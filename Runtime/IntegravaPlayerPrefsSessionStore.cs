using UnityEngine;

namespace Integrava
{
    public sealed class IntegravaPlayerPrefsSessionStore : IIntegravaSessionStore
    {
        public string Load(string storageKey)
        {
            IntegravaUnityMainThread.ThrowIfOffMainThread();
            if (string.IsNullOrEmpty(storageKey)) return "";
            return PlayerPrefs.GetString(storageKey, "");
        }

        public void Save(string storageKey, string sessionId)
        {
            IntegravaUnityMainThread.ThrowIfOffMainThread();
            if (string.IsNullOrEmpty(storageKey)) return;
            if (string.IsNullOrEmpty(sessionId))
            {
                Clear(storageKey);
                return;
            }
            PlayerPrefs.SetString(storageKey, sessionId);
            PlayerPrefs.Save();
        }

        public void Clear(string storageKey)
        {
            IntegravaUnityMainThread.ThrowIfOffMainThread();
            if (string.IsNullOrEmpty(storageKey)) return;
            PlayerPrefs.DeleteKey(storageKey);
            PlayerPrefs.Save();
        }
    }
}
