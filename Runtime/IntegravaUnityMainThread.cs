using System.Threading;
using UnityEngine;

namespace Integrava
{
    /// <summary>
    /// UnityWebRequest and PlayerPrefs are main-thread APIs. Capture the player thread
    /// before the first scene loads (Unity 2021.3).
    /// </summary>
    public static class IntegravaUnityMainThread
    {
        static int _mainThreadId;
        static bool _captured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Capture()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _captured = true;
        }

        public static void ThrowIfOffMainThread()
        {
            if (!_captured)
            {
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;
                _captured = true;
                return;
            }
            if (Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            {
                throw new IntegravaException(
                    IntegravaErrorCode.BadRequest,
                    0,
                    "Integrava Unity calls must run on the main thread.");
            }
        }
    }
}
