using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Integrava
{
    /// <summary>
    /// UnityWebRequest transport for player builds. The Core client stays free of UnityEngine
    /// so it can be tested with dotnet on Linux.
    /// </summary>
    public sealed class IntegravaUnityTransport : IIntegravaTransport
    {
        const int PollIntervalMs = 50;

        public async Task<IntegravaHttpResponse> SendAsync(IntegravaHttpRequest request, CancellationToken cancellationToken)
        {
            IntegravaUnityMainThread.ThrowIfOffMainThread();
            if (request == null) throw new ArgumentNullException("request");
            var method = string.IsNullOrEmpty(request.Method) ? "GET" : request.Method;
            var web = new UnityWebRequest(request.Url, method);
            try
            {
                web.downloadHandler = new DownloadHandlerBuffer();
                if (!string.IsNullOrEmpty(request.Body))
                {
                    web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body));
                    web.SetRequestHeader("Content-Type", "application/json");
                }
                if (!string.IsNullOrEmpty(request.IdentifyToken))
                    web.SetRequestHeader("X-Integrava-Identify-Token", request.IdentifyToken);
                if (request.TimeoutSeconds > 0) web.timeout = request.TimeoutSeconds;
                var operation = web.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        web.Abort();
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    try
                    {
                        await Task.Delay(PollIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!operation.isDone) web.Abort();
                        throw;
                    }
                }
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                if (web.result == UnityWebRequest.Result.ConnectionError
                    || web.result == UnityWebRequest.Result.DataProcessingError)
                {
                    return new IntegravaHttpResponse
                    {
                        StatusCode = 0,
                        TransportError = "Network request failed."
                    };
                }
                var body = web.downloadHandler == null ? "" : web.downloadHandler.text;
                return new IntegravaHttpResponse
                {
                    StatusCode = (int)web.responseCode,
                    Body = body
                };
            }
            finally
            {
                web.Dispose();
            }
        }
    }
}
