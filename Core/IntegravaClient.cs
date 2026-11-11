using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Integrava
{
    public enum IntegravaErrorCode
    {
        None = 0,
        Network,
        RateLimited,
        Unauthorized,
        OriginRejected,
        IdentifyRequired,
        IdentifyInvalid,
        IdentifyMismatch,
        NotFound,
        BadRequest,
        PaymentRequired,
        InvalidResponse,
        Server,
        Cancelled
    }

    public sealed class IntegravaException : Exception
    {
        public IntegravaErrorCode Code { get; private set; }
        public int StatusCode { get; private set; }

        public IntegravaException(IntegravaErrorCode code, int statusCode, string message)
            : base(message)
        {
            Code = code;
            StatusCode = statusCode;
        }
    }

    public sealed class IntegravaHttpRequest
    {
        public string Method;
        public string Url;
        public string Body;
        public string IdentifyToken;
        public int TimeoutSeconds;
    }

    public sealed class IntegravaHttpResponse
    {
        public int StatusCode;
        public string Body;
        public string TransportError;
    }

    public interface IIntegravaTransport
    {
        Task<IntegravaHttpResponse> SendAsync(IntegravaHttpRequest request, CancellationToken cancellationToken);
    }

    public interface IIntegravaSessionStore
    {
        string Load(string storageKey);
        void Save(string storageKey, string sessionId);
        void Clear(string storageKey);
    }

    public sealed class MemorySessionStore : IIntegravaSessionStore
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        public string Load(string storageKey)
        {
            string value;
            if (storageKey != null && _values.TryGetValue(storageKey, out value)) return value ?? "";
            return "";
        }

        public void Save(string storageKey, string sessionId)
        {
            if (string.IsNullOrEmpty(storageKey)) return;
            if (string.IsNullOrEmpty(sessionId))
            {
                _values.Remove(storageKey);
                return;
            }
            _values[storageKey] = sessionId;
        }

        public void Clear(string storageKey)
        {
            if (storageKey != null) _values.Remove(storageKey);
        }
    }

    /// <summary>
    /// PlayerPrefs key for one API host and widget token. Neither value is written into the key.
    /// A trailing slash on the host does not change the key. Two hosts do not share a session.
    /// </summary>
    public static class IntegravaSessionKeys
    {
        public static string ForWidgetToken(string baseUrl, string widgetToken)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                hash = Mix(hash, NormalizeBaseUrl(baseUrl));
                hash = Mix(hash, widgetToken ?? "");
                return "integrava.session." + hash.ToString("x16");
            }
        }

        static ulong Mix(ulong hash, string value)
        {
            unchecked
            {
                hash ^= (ulong)(uint)value.Length;
                hash *= 1099511628211UL;
                for (var i = 0; i < value.Length; i++)
                {
                    hash ^= value[i];
                    hash *= 1099511628211UL;
                }
                return hash;
            }
        }

        static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return "";
            return baseUrl.Trim().TrimEnd('/');
        }
    }

    public sealed class IntegravaMessage
    {
        public string Id;
        public string Role;
        public string Content;
        public string CreatedAt;
        public string AuthorName;
        public bool AiGenerated;
    }

    public sealed class IntegravaWidgetConfig
    {
        public string AssistantName;
        public bool AiDisclosureEnabled;
        public string AiDisclosureText;
        public bool AiEnabled;
    }

    public sealed class IntegravaChatResult
    {
        public string Reply;
        public string SessionId;
        public bool NewConversation;
        /// <summary>Older responses. Current chat uses ChatUnavailable.</summary>
        public bool Banned;
        public bool ChatLimited;
        public bool ChatUnavailable;
        public bool WaitingForAgent;
        public bool NeedsReplyRetry;
        public bool RetryCompleted;
        public string TicketStatus;
        public List<IntegravaMessage> Messages;
    }

    public sealed class IntegravaHistory
    {
        public List<IntegravaMessage> Messages;
        public int MessageCount;
        public string LastMessageId;
        public bool Changed;
        public bool TicketClosed;
        public string TicketStatus;
        public bool ChatUnavailable;
        public bool WaitingForAgent;
        public bool NeedsReplyRetry;
        public string SessionId;
        public bool SessionRotated;
    }

    public sealed class IntegravaArticleSummary
    {
        public string Id;
        public string Title;
        public string Slug;
        public string CategoryTitle;
        public string SectionTitle;
    }

    public sealed class IntegravaArticle
    {
        public string Title;
        public string Slug;
        public string BodyHtml;
        public string BodyText;
    }

    public sealed class IntegravaClientOptions
    {
        public string BaseUrl;
        public string WidgetToken;
        public string WorkspaceSlug;
        /// <summary>
        /// Called before each authenticated request. forceRefresh is true after the
        /// server rejected the previous token, so a cached token must be replaced.
        /// Return the identify token only. Never a server secret key.
        /// </summary>
        public Func<bool, CancellationToken, Task<string>> IdentifyTokenProvider;
        public IIntegravaSessionStore SessionStore;
        public IIntegravaTransport Transport;
        public int MaxAttempts = 4;
    }

    /// <summary>
    /// HTTP client for the public chat, history long poll, widget config, and Help Center.
    /// The server secret key is not a parameter and is never sent.
    /// </summary>
    public sealed class IntegravaClient
    {
        public const int MaxWaitMs = 15000;

        readonly IntegravaClientOptions _options;
        readonly IIntegravaSessionStore _store;
        readonly string _storageKey;
        readonly Func<int, CancellationToken, Task> _delay;

        public IntegravaClient(IntegravaClientOptions options)
            : this(options, null)
        {
        }

        public IntegravaClient(IntegravaClientOptions options, Func<int, CancellationToken, Task> delay)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new ArgumentException("BaseUrl is required.");
            if (string.IsNullOrWhiteSpace(options.WidgetToken)) throw new ArgumentException("WidgetToken is required.");
            if (options.Transport == null) throw new ArgumentException("Transport is required.");
            if (options.IdentifyTokenProvider == null) throw new ArgumentException("IdentifyTokenProvider is required.");
            if (LooksLikeServerSecret(options.WidgetToken))
                throw new ArgumentException("WidgetToken must be the public widget token, not a server secret key.");
            _options = options;
            _store = options.SessionStore ?? new MemorySessionStore();
            _storageKey = IntegravaSessionKeys.ForWidgetToken(options.BaseUrl, options.WidgetToken.Trim());
            _delay = delay ?? ((ms, ct) => Task.Delay(ms, ct));
            if (_options.MaxAttempts < 1) _options.MaxAttempts = 1;
        }

        public string SessionId
        {
            get { return _store.Load(_storageKey) ?? ""; }
        }

        public void ClearSession()
        {
            _store.Clear(_storageKey);
        }

        public async Task<IntegravaWidgetConfig> GetWidgetConfigAsync(CancellationToken cancellationToken)
        {
            var json = await RequestAsync(
                "GET",
                "/api/public/widget-config?widgetToken=" + Escape(_options.WidgetToken.Trim()),
                null,
                true,
                30,
                cancellationToken);
            var disclosure = json.Get("aiDisclosure");
            return new IntegravaWidgetConfig
            {
                AssistantName = json.GetString("assistantName", ""),
                AiDisclosureEnabled = disclosure != null && disclosure.GetBool("enabled", false),
                AiDisclosureText = disclosure == null ? "" : disclosure.GetString("text", ""),
                AiEnabled = json.GetBool("aiEnabled", false)
            };
        }

        public async Task<IntegravaChatResult> SendMessageAsync(string message, CancellationToken cancellationToken)
        {
            var text = message == null ? "" : message.Trim();
            if (text.Length == 0)
                throw new IntegravaException(IntegravaErrorCode.BadRequest, 400, "Message is required.");
            var clientMessageId = Guid.NewGuid().ToString("N");
            var json = await RequestAsync(
                "POST",
                "/api/public/chat",
                delegate (string token)
                {
                    var sessionId = _store.Load(_storageKey) ?? "";
                    var builder = new StringBuilder();
                    builder.Append("{\"widgetToken\":").Append(IntegravaJson.Quote(_options.WidgetToken.Trim()));
                    builder.Append(",\"message\":").Append(IntegravaJson.Quote(text));
                    if (sessionId.Length > 0)
                        builder.Append(",\"sessionId\":").Append(IntegravaJson.Quote(sessionId));
                    if (!string.IsNullOrEmpty(token))
                        builder.Append(",\"identifyToken\":").Append(IntegravaJson.Quote(token));
                    builder.Append(",\"clientMessageId\":").Append(IntegravaJson.Quote(clientMessageId));
                    builder.Append('}');
                    return builder.ToString();
                },
                true,
                60,
                cancellationToken);
            return ReadChatResult(json);
        }

        /// <summary>
        /// Asks the server to finish a bot reply that was stored without an answer.
        /// Call this when history NeedsReplyRetry is true. Does not send escalateToAgent.
        /// </summary>
        public async Task<IntegravaChatResult> RetryAssistantReplyAsync(CancellationToken cancellationToken)
        {
            var sessionId = _store.Load(_storageKey) ?? "";
            if (sessionId.Length == 0)
                throw new IntegravaException(IntegravaErrorCode.BadRequest, 400, "No conversation to retry.");
            var json = await RequestAsync(
                "POST",
                "/api/public/chat/retry",
                delegate (string token)
                {
                    var builder = new StringBuilder();
                    builder.Append("{\"widgetToken\":").Append(IntegravaJson.Quote(_options.WidgetToken.Trim()));
                    builder.Append(",\"sessionId\":").Append(IntegravaJson.Quote(sessionId));
                    if (!string.IsNullOrEmpty(token))
                        builder.Append(",\"identifyToken\":").Append(IntegravaJson.Quote(token));
                    builder.Append('}');
                    return builder.ToString();
                },
                true,
                60,
                cancellationToken);
            return ReadChatResult(json);
        }

        public async Task<IntegravaHistory> PollHistoryAsync(
            int waitMs,
            int knownMessageCount,
            string lastMessageId,
            CancellationToken cancellationToken)
        {
            var sessionId = _store.Load(_storageKey) ?? "";
            if (sessionId.Length == 0)
            {
                return new IntegravaHistory
                {
                    Messages = new List<IntegravaMessage>(),
                    MessageCount = 0,
                    LastMessageId = "",
                    Changed = false,
                    TicketClosed = false,
                    TicketStatus = ""
                };
            }
            if (waitMs < 0) waitMs = 0;
            if (waitMs > MaxWaitMs) waitMs = MaxWaitMs;
            var url = "/api/public/chat/history?widgetToken=" + Escape(_options.WidgetToken.Trim())
                + "&sessionId=" + Escape(sessionId)
                + "&waitMs=" + waitMs.ToString()
                + "&messageCount=" + knownMessageCount.ToString();
            if (!string.IsNullOrEmpty(lastMessageId))
                url += "&lastMessageId=" + Escape(lastMessageId);
            var timeout = 20 + (waitMs / 1000);
            var json = await RequestAsync("GET", url, null, true, timeout, cancellationToken);
            var ticketStatus = json.GetString("ticketStatus", "");
            var nextSession = json.GetString("sessionId", "");
            var rotated = nextSession.Length > 0 && nextSession != sessionId;
            if (rotated) _store.Save(_storageKey, nextSession);
            return new IntegravaHistory
            {
                Messages = ReadMessages(json.Get("messages")),
                MessageCount = json.GetInt("messageCount", 0),
                LastMessageId = json.GetString("lastMessageId", ""),
                Changed = json.GetBool("changed", false),
                TicketClosed = json.GetBool("ticketClosed", false),
                TicketStatus = ticketStatus,
                ChatUnavailable = json.GetBool("chatUnavailable", false),
                WaitingForAgent = ReadWaiting(json, ticketStatus),
                NeedsReplyRetry = json.GetBool("needsReplyRetry", false),
                SessionId = rotated ? nextSession : sessionId,
                SessionRotated = rotated
            };
        }

        public async Task<List<IntegravaArticleSummary>> ListArticlesAsync(CancellationToken cancellationToken)
        {
            var slug = RequireWorkspaceSlug();
            var json = await RequestAsync(
                "GET",
                "/api/public/help/" + Escape(slug),
                null,
                false,
                30,
                cancellationToken);
            var list = new List<IntegravaArticleSummary>();
            var categories = json.Get("categories");
            if (categories == null || categories.Type != IntegravaJson.Kind.Array || categories.Items == null)
                return list;
            for (var c = 0; c < categories.Items.Count; c++)
            {
                var category = categories.Items[c];
                var categoryTitle = category.GetString("title", "");
                var sections = category.Get("sections");
                if (sections == null || sections.Items == null) continue;
                for (var s = 0; s < sections.Items.Count; s++)
                {
                    var section = sections.Items[s];
                    var sectionTitle = section.GetString("title", "");
                    var pages = section.Get("pages");
                    if (pages == null || pages.Items == null) continue;
                    for (var p = 0; p < pages.Items.Count; p++)
                    {
                        var page = pages.Items[p];
                        list.Add(new IntegravaArticleSummary
                        {
                            Id = page.GetString("id", ""),
                            Title = page.GetString("title", ""),
                            Slug = page.GetString("slug", ""),
                            CategoryTitle = categoryTitle,
                            SectionTitle = sectionTitle
                        });
                    }
                }
            }
            return list;
        }

        public async Task<IntegravaArticle> GetArticleAsync(string pageSlug, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(pageSlug))
                throw new IntegravaException(IntegravaErrorCode.BadRequest, 400, "Article slug is required.");
            var slug = RequireWorkspaceSlug();
            var json = await RequestAsync(
                "GET",
                "/api/public/help/" + Escape(slug) + "/articles/" + Escape(pageSlug.Trim()),
                null,
                false,
                30,
                cancellationToken);
            var article = json.Get("article");
            if (article == null)
                throw new IntegravaException(IntegravaErrorCode.InvalidResponse, 200, "Article response was not valid.");
            var html = article.GetString("bodyHtml", "");
            return new IntegravaArticle
            {
                Title = article.GetString("title", ""),
                Slug = article.GetString("slug", ""),
                BodyHtml = html,
                BodyText = IntegravaJson.StripHtml(html)
            };
        }

        async Task<IntegravaJson> RequestAsync(
            string method,
            string pathAndQuery,
            Func<string, string> bodyForToken,
            bool authenticate,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            var attempt = 0;
            var refreshes = 0;
            var delayMs = 500;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var token = "";
                if (authenticate)
                {
                    try
                    {
                        var provided = await _options.IdentifyTokenProvider(refreshes > 0, cancellationToken);
                        token = provided == null ? "" : provided.Trim();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (IntegravaException)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        throw new IntegravaException(
                            IntegravaErrorCode.IdentifyRequired,
                            0,
                            "Identify token callback failed.");
                    }
                    if (LooksLikeServerSecret(token))
                    {
                        throw new IntegravaException(
                            IntegravaErrorCode.IdentifyInvalid,
                            0,
                            "Identify callback returned a server secret key. Return an identify token instead.");
                    }
                }

                var httpRequest = new IntegravaHttpRequest
                {
                    Method = method,
                    Url = Combine(_options.BaseUrl, pathAndQuery),
                    Body = bodyForToken == null ? null : bodyForToken(token),
                    IdentifyToken = authenticate ? token : "",
                    TimeoutSeconds = timeoutSeconds
                };
                IntegravaHttpResponse response;
                try
                {
                    response = await _options.Transport.SendAsync(httpRequest, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IntegravaException)
                {
                    throw;
                }
                catch (Exception)
                {
                    response = new IntegravaHttpResponse
                    {
                        StatusCode = 0,
                        TransportError = "Network request failed."
                    };
                }

                if (response != null && response.StatusCode >= 200 && response.StatusCode < 300)
                {
                    try
                    {
                        return IntegravaJson.Parse(response.Body ?? "");
                    }
                    catch (FormatException)
                    {
                        throw new IntegravaException(
                            IntegravaErrorCode.InvalidResponse,
                            response.StatusCode,
                            "Response was not valid JSON.");
                    }
                }

                var error = MapError(response, token, _options.WidgetToken);
                if ((error.Code == IntegravaErrorCode.IdentifyInvalid || error.Code == IntegravaErrorCode.IdentifyRequired)
                    && authenticate
                    && refreshes < 1)
                {
                    refreshes++;
                    continue;
                }
                if (error.Code == IntegravaErrorCode.IdentifyMismatch)
                {
                    _store.Clear(_storageKey);
                    throw error;
                }
                if (IsRetryable(error) && attempt < _options.MaxAttempts - 1)
                {
                    attempt++;
                    await _delay(delayMs, cancellationToken);
                    delayMs = delayMs * 2;
                    if (delayMs > 8000) delayMs = 8000;
                    continue;
                }
                throw error;
            }
        }

        static bool IsRetryable(IntegravaException error)
        {
            // Chat retries reuse the same clientMessageId, so a stored response is
            // replayed instead of starting another model turn. HTTP 500 is not retried.
            if (error.Code == IntegravaErrorCode.Network || error.Code == IntegravaErrorCode.RateLimited)
                return true;
            return error.StatusCode == 409 || error.StatusCode == 502 || error.StatusCode == 503;
        }

        static IntegravaException MapError(IntegravaHttpResponse response, string identifyToken, string widgetToken)
        {
            if (response == null || response.StatusCode == 0)
            {
                return new IntegravaException(IntegravaErrorCode.Network, 0, "Network request failed.");
            }
            var code = "";
            var errorText = "";
            if (!string.IsNullOrEmpty(response.Body))
            {
                try
                {
                    var json = IntegravaJson.Parse(response.Body);
                    code = json.GetString("code", "");
                    errorText = json.GetString("error", "");
                }
                catch (FormatException)
                {
                    errorText = "";
                }
            }
            var message = string.IsNullOrEmpty(errorText) ? "HTTP " + response.StatusCode : errorText;
            message = Redact(message, identifyToken, widgetToken);
            var kind = IntegravaErrorCode.Server;
            if (code == "ORIGIN_NOT_ALLOWED") kind = IntegravaErrorCode.OriginRejected;
            else if (code == "IDENTIFY_REQUIRED") kind = IntegravaErrorCode.IdentifyRequired;
            else if (code == "IDENTIFY_INVALID") kind = IntegravaErrorCode.IdentifyInvalid;
            else if (code == "IDENTIFY_MISMATCH") kind = IntegravaErrorCode.IdentifyMismatch;
            else if (response.StatusCode == 401) kind = IntegravaErrorCode.Unauthorized;
            else if (response.StatusCode == 404) kind = IntegravaErrorCode.NotFound;
            else if (response.StatusCode == 400 || response.StatusCode == 413) kind = IntegravaErrorCode.BadRequest;
            else if (response.StatusCode == 402) kind = IntegravaErrorCode.PaymentRequired;
            else if (response.StatusCode == 429) kind = IntegravaErrorCode.RateLimited;
            else if (response.StatusCode >= 500) kind = IntegravaErrorCode.Server;
            return new IntegravaException(kind, response.StatusCode, message);
        }

        static string Redact(string message, string identifyToken, string widgetToken)
        {
            if (string.IsNullOrEmpty(message)) return "";
            if (!string.IsNullOrEmpty(identifyToken)) message = message.Replace(identifyToken, "[token]");
            if (!string.IsNullOrEmpty(widgetToken)) message = message.Replace(widgetToken, "[widget]");
            return message;
        }

        static bool LooksLikeServerSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf("integrava_sk_", StringComparison.Ordinal) >= 0
                || value.IndexOf("opsflow_sk_", StringComparison.Ordinal) >= 0;
        }

        string RequireWorkspaceSlug()
        {
            var slug = _options.WorkspaceSlug == null ? "" : _options.WorkspaceSlug.Trim();
            if (slug.Length == 0)
                throw new IntegravaException(IntegravaErrorCode.BadRequest, 400, "Workspace slug is required.");
            return slug;
        }

        IntegravaChatResult ReadChatResult(IntegravaJson json)
        {
            var sessionIdFromServer = json.GetString("sessionId", "");
            // A limit or unavailable response can name a session that was never stored.
            // Saving it makes history poll a conversation that does not exist.
            if (sessionIdFromServer.Length > 0
                && !json.GetBool("chatLimited", false)
                && !json.GetBool("chatUnavailable", false))
            {
                _store.Save(_storageKey, sessionIdFromServer);
            }
            var ticketStatus = json.GetString("ticketStatus", "");
            return new IntegravaChatResult
            {
                Reply = json.GetString("reply", ""),
                SessionId = sessionIdFromServer,
                NewConversation = json.GetBool("newConversation", false),
                Banned = json.GetBool("banned", false),
                ChatLimited = json.GetBool("chatLimited", false),
                ChatUnavailable = json.GetBool("chatUnavailable", false),
                WaitingForAgent = ReadWaiting(json, ticketStatus),
                NeedsReplyRetry = json.GetBool("needsReplyRetry", false),
                RetryCompleted = json.GetBool("completed", false),
                TicketStatus = ticketStatus,
                Messages = ReadMessages(json.Get("messages"))
            };
        }

        static bool ReadWaiting(IntegravaJson json, string ticketStatus)
        {
            if (json != null && json.GetBool("waitingForAgent", false)) return true;
            return ticketStatus == "WAITING_FOR_AGENT" || ticketStatus == "PENDING";
        }

        static List<IntegravaMessage> ReadMessages(IntegravaJson node)
        {
            var list = new List<IntegravaMessage>();
            if (node == null || node.Type != IntegravaJson.Kind.Array || node.Items == null) return list;
            for (var i = 0; i < node.Items.Count; i++)
            {
                var item = node.Items[i];
                list.Add(new IntegravaMessage
                {
                    Id = item.GetString("id", ""),
                    Role = item.GetString("role", ""),
                    Content = item.GetString("content", ""),
                    CreatedAt = item.GetString("createdAt", ""),
                    AuthorName = item.GetString("authorName", ""),
                    AiGenerated = item.GetBool("aiGenerated", false)
                });
            }
            return list;
        }

        static string Combine(string baseUrl, string pathAndQuery)
        {
            var root = baseUrl.Trim().TrimEnd('/');
            if (pathAndQuery.Length == 0 || pathAndQuery[0] != '/') pathAndQuery = "/" + pathAndQuery;
            return root + pathAndQuery;
        }

        static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? "");
        }
    }
}
