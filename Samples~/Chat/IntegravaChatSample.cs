using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Networking;
using UnityEngine.UI;

namespace Integrava.Sample
{
    /// <summary>
    /// Restylable uGUI chat built at runtime. Drop this on an empty GameObject.
    /// Colors, font, and font size are public so a studio can restyle them.
    /// The server secret key is intentionally not a field.
    /// </summary>
    public sealed class IntegravaChatSample : MonoBehaviour
    {
        const int WebPollIntervalMs = 50;

        [Header("Integrava")]
        public string baseUrl = "https://api.integrava.ai";
        public string widgetToken = "";
        public string workspaceSlug = "";

        [Header("Identify token")]
        [Tooltip("Your server route that returns { identifyToken }. It must hold the secret key. This is not the Integrava API.")]
        public string identifyMintUrl = "";
        [Tooltip("Your player id, sent only to your mint server.")]
        public string externalUserId = "player-1";
        [Tooltip("Optional password your mint server expects. Not the Integrava secret key.")]
        public string mintPassword = "";
        [Tooltip("Editor only. A short-lived identify token. Leave empty in player builds.")]
        public string editorIdentifyToken = "";

        [Header("Style")]
        public Font font;
        public int fontSize = 16;
        public Color backgroundColor = new Color(0.08f, 0.09f, 0.14f, 1f);
        public Color panelColor = new Color(0.12f, 0.14f, 0.2f, 1f);
        public Color textColor = new Color(0.93f, 0.94f, 0.96f, 1f);
        public Color noticeColor = new Color(0.72f, 0.78f, 0.95f, 1f);
        public Color buttonColor = new Color(0.35f, 0.28f, 0.75f, 1f);
        public Color inputColor = new Color(0.16f, 0.17f, 0.24f, 1f);

        public int pollWaitMs = 12000;

        Text _notice;
        Text _transcript;
        InputField _input;
        Text _status;
        IntegravaClient _client;
        CancellationTokenSource _lifetime;
        CancellationTokenSource _poll;
        GameObject _canvasObject;
        GameObject _eventSystem;
        string _cachedToken = "";
        float _cachedUntil;
        int _knownCount;
        int _viewGeneration;
        int _pollGeneration;
        string _lastMessageId = "";
        bool _showingHelp;
        bool _alive;
        bool _started;
        bool _paused;
        bool _sending;
        bool _pollRunning;
        bool _retryRunning;
        bool _chatUnavailable;
        bool _createdEventSystem;

        bool InBackground
        {
            get { return _paused; }
        }

        void Awake()
        {
            _alive = true;
            BuildUi();
        }

        async void Start()
        {
            _lifetime = new CancellationTokenSource();
            try
            {
                if (LooksLikeSecret(widgetToken) || LooksLikeSecret(editorIdentifyToken) || LooksLikeSecret(mintPassword))
                {
                    SetStatus("Refusing to start. A field contains a server secret key (integrava_sk_). Put that key only on your server.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(widgetToken))
                {
                    SetStatus("Set the public widget token on this component.");
                    return;
                }
                _client = new IntegravaClient(new IntegravaClientOptions
                {
                    BaseUrl = baseUrl,
                    WidgetToken = widgetToken.Trim(),
                    WorkspaceSlug = workspaceSlug == null ? "" : workspaceSlug.Trim(),
                    IdentifyTokenProvider = ProvideIdentifyToken,
                    SessionStore = new IntegravaPlayerPrefsSessionStore(),
                    Transport = new IntegravaUnityTransport()
                });
                var config = await _client.GetWidgetConfigAsync(_lifetime.Token);
                if (!_alive) return;
                _started = true;
                if (_notice != null)
                    _notice.text = config.AiDisclosureEnabled ? config.AiDisclosureText : "";
                SetStatus(string.IsNullOrEmpty(_client.SessionId) ? "Ready." : "Restored session.");
                StartPollLoop();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }
        }

        void OnDestroy()
        {
            _alive = false;
            StopPollLoop();
            if (_lifetime != null)
            {
                _lifetime.Cancel();
                _lifetime = null;
            }
            if (_canvasObject != null)
            {
                Destroy(_canvasObject);
                _canvasObject = null;
            }
            if (_createdEventSystem && _eventSystem != null)
            {
                Destroy(_eventSystem);
                _eventSystem = null;
            }
        }

        void OnApplicationPause(bool paused)
        {
            _paused = paused;
            ApplyBackground();
        }

        void OnApplicationFocus(bool focused)
        {
            // Focus also fires once at startup. Pause is the mobile background signal.
            // Starting another poll from focus would run two loops, and treating an
            // unfocused Game view as background would stop the poll in the editor.
            if (!focused || !_paused) return;
            _paused = false;
            ApplyBackground();
        }

        void ApplyBackground()
        {
            if (!_alive || !_started) return;
            if (InBackground) StopPollLoop();
            else StartPollLoop();
        }

        void StartPollLoop()
        {
            if (!_alive || !_started || InBackground || _chatUnavailable || _client == null || _lifetime == null) return;
            if (_lifetime.IsCancellationRequested || _pollRunning) return;
            var generation = ++_pollGeneration;
            var poll = new CancellationTokenSource();
            _poll = poll;
            _pollRunning = true;
            RunPollLoop(generation, poll);
        }

        void StopPollLoop()
        {
            _pollGeneration++;
            _pollRunning = false;
            var poll = _poll;
            _poll = null;
            if (poll != null && !poll.IsCancellationRequested) poll.Cancel();
        }

        async void RunPollLoop(int generation, CancellationTokenSource pollSource)
        {
            CancellationTokenSource linked = null;
            try
            {
                if (_lifetime == null) return;
                linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, pollSource.Token);
                await PollLoop(generation, linked.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }
            finally
            {
                DisposeSource(linked);
                DisposeSource(pollSource);
                if (generation == _pollGeneration) _pollRunning = false;
            }
        }

        static void DisposeSource(CancellationTokenSource source)
        {
            if (source == null) return;
            try
            {
                source.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public async void OnSendClicked()
        {
            if (!_alive || _sending || _client == null || _input == null || _lifetime == null) return;
            if (_lifetime.IsCancellationRequested) return;
            var text = _input.text == null ? "" : _input.text.Trim();
            if (text.Length == 0) return;
            _input.text = "";
            _sending = true;
            _viewGeneration++;
            AppendLine("You", text);
            SetStatus("Sending…");
            try
            {
                var result = await _client.SendMessageAsync(text, _lifetime.Token);
                if (!_alive) return;
                _viewGeneration++;
                ApplyChatResult(result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }
            finally
            {
                _sending = false;
            }
        }

        public async void OnHelpClicked()
        {
            if (!_alive || _client == null || _lifetime == null || _lifetime.IsCancellationRequested) return;
            _showingHelp = !_showingHelp;
            if (!_showingHelp)
            {
                SetStatus("Chat.");
                return;
            }
            SetStatus("Loading articles…");
            try
            {
                var articles = await _client.ListArticlesAsync(_lifetime.Token);
                if (!_alive || !_showingHelp) return;
                var builder = new StringBuilder();
                if (articles.Count == 0) builder.Append("No published articles.");
                for (var i = 0; i < articles.Count; i++)
                {
                    var article = articles[i];
                    builder.Append(article.CategoryTitle).Append(" / ").Append(article.Title).Append("\n");
                    if (i == 0 && !string.IsNullOrEmpty(article.Slug))
                    {
                        var full = await _client.GetArticleAsync(article.Slug, _lifetime.Token);
                        if (!_alive || !_showingHelp) return;
                        builder.Append(full.BodyText).Append("\n");
                    }
                }
                if (_alive && _transcript != null) _transcript.text = builder.ToString();
                SetStatus("Help Center. Press Help again for chat.");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                ReportFault(ex);
            }
        }

        async Task PollLoop(int generation, CancellationToken cancellationToken)
        {
            var backoff = 500;
            while (_alive && generation == _pollGeneration && !cancellationToken.IsCancellationRequested)
            {
                if (_showingHelp || string.IsNullOrEmpty(_client.SessionId))
                {
                    await Task.Delay(400, cancellationToken);
                    continue;
                }
                try
                {
                    var viewAtStart = _viewGeneration;
                    var history = await _client.PollHistoryAsync(pollWaitMs, _knownCount, _lastMessageId, cancellationToken);
                    if (!_alive || generation != _pollGeneration) return;
                    if (history.ChatUnavailable)
                    {
                        MarkChatUnavailable(null);
                        return;
                    }
                    if (history.SessionRotated)
                    {
                        _knownCount = 0;
                        _lastMessageId = "";
                        ReplaceTranscript(null);
                        continue;
                    }
                    if (_showingHelp || _sending || viewAtStart != _viewGeneration) continue;
                    var incoming = history.Messages == null ? 0 : history.Messages.Count;
                    var serverEmpty = incoming == 0 && history.MessageCount == 0;
                    if (!serverEmpty && (history.Changed || history.MessageCount != _knownCount))
                    {
                        ReplaceTranscript(history.Messages);
                        _knownCount = history.MessageCount;
                        _lastMessageId = history.LastMessageId ?? "";
                    }
                    if (serverEmpty)
                        await Task.Delay(1000, cancellationToken);
                    if (history.Changed || history.WaitingForAgent || !string.IsNullOrEmpty(history.TicketStatus))
                        NoteConversation(history.WaitingForAgent, history.TicketStatus, false);
                    if (history.NeedsReplyRetry && !_retryRunning)
                    {
                        _retryRunning = true;
                        try
                        {
                            var retried = await _client.RetryAssistantReplyAsync(cancellationToken);
                            if (!_alive || generation != _pollGeneration) return;
                            if (retried.ChatUnavailable)
                            {
                                MarkChatUnavailable(retried.Reply);
                                return;
                            }
                            if (retried.Messages != null && retried.Messages.Count > 0)
                            {
                                ReplaceTranscript(retried.Messages);
                                _knownCount = retried.Messages.Count;
                                _lastMessageId = retried.Messages[retried.Messages.Count - 1].Id;
                            }
                            NoteConversation(retried.WaitingForAgent, retried.TicketStatus, retried.ChatLimited);
                        }
                        finally
                        {
                            _retryRunning = false;
                        }
                    }
                    backoff = 500;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IntegravaException ex)
                {
                    if (ex.Code == IntegravaErrorCode.Cancelled || !_alive) return;
                    if (!_showingHelp) SetStatus(ex.Code + ": " + ex.Message);
                    await Task.Delay(backoff, cancellationToken);
                    backoff = backoff * 2;
                    if (backoff > 8000) backoff = 8000;
                }
            }
        }

        async Task<string> ProvideIdentifyToken(bool forceRefresh, CancellationToken cancellationToken)
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_cachedToken) && Time.realtimeSinceStartup < _cachedUntil)
                return _cachedToken;
            if (!string.IsNullOrWhiteSpace(identifyMintUrl))
                return await MintFromServer(cancellationToken);
            if (!string.IsNullOrWhiteSpace(editorIdentifyToken))
            {
                _cachedToken = editorIdentifyToken.Trim();
                _cachedUntil = Time.realtimeSinceStartup + 14f * 60f;
                return _cachedToken;
            }
            throw new IntegravaException(
                IntegravaErrorCode.IdentifyRequired,
                0,
                "Set an identify mint URL, or an editor identify token.");
        }

        async Task<string> MintFromServer(CancellationToken cancellationToken)
        {
            IntegravaUnityMainThread.ThrowIfOffMainThread();
            var payload = "{\"externalUserId\":" + IntegravaJson.Quote(externalUserId ?? "") + "}";
            var web = new UnityWebRequest(identifyMintUrl.Trim(), "POST");
            try
            {
                web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload));
                web.downloadHandler = new DownloadHandlerBuffer();
                web.SetRequestHeader("Content-Type", "application/json");
                if (!string.IsNullOrEmpty(mintPassword))
                    web.SetRequestHeader("X-Integrava-Mint-Password", mintPassword);
                web.timeout = 20;
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
                        await Task.Delay(WebPollIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        if (!operation.isDone) web.Abort();
                        throw;
                    }
                }
                if (cancellationToken.IsCancellationRequested)
                    cancellationToken.ThrowIfCancellationRequested();
                var body = web.downloadHandler == null ? "" : web.downloadHandler.text;
                if (web.result != UnityWebRequest.Result.Success)
                {
                    throw new IntegravaException(
                        IntegravaErrorCode.Network,
                        (int)web.responseCode,
                        "Identify mint failed (HTTP " + (int)web.responseCode + ").");
                }
                var json = IntegravaJson.Parse(body);
                var token = json.GetString("identifyToken", "");
                if (token.Length == 0)
                {
                    throw new IntegravaException(IntegravaErrorCode.InvalidResponse, (int)web.responseCode, "Identify mint response had no identifyToken.");
                }
                if (LooksLikeSecret(token))
                {
                    throw new IntegravaException(IntegravaErrorCode.IdentifyInvalid, 0, "Mint server returned a server secret key.");
                }
                var seconds = json.GetInt("expiresInSeconds", 15 * 60);
                if (seconds < 90) seconds = 90;
                _cachedUntil = Time.realtimeSinceStartup + (seconds - 60);
                _cachedToken = token;
                return token;
            }
            finally
            {
                web.Dispose();
            }
        }

        void ApplyChatResult(IntegravaChatResult result)
        {
            if (result == null) return;
            if (result.ChatUnavailable)
            {
                MarkChatUnavailable(result.Reply);
                return;
            }
            if (result.Messages != null && result.Messages.Count > 0)
            {
                ReplaceTranscript(result.Messages);
                _knownCount = result.Messages.Count;
                _lastMessageId = result.Messages[result.Messages.Count - 1].Id;
            }
            else if (!string.IsNullOrEmpty(result.Reply))
            {
                AppendLine("Assistant", result.Reply);
            }
            NoteConversation(result.WaitingForAgent, result.TicketStatus, result.ChatLimited);
        }

        void MarkChatUnavailable(string reply)
        {
            _chatUnavailable = true;
            StopPollLoop();
            if (!string.IsNullOrEmpty(reply)) AppendLine("Assistant", reply);
            else AppendLine("Assistant", "This chat is unavailable right now.");
            SetStatus("This chat is unavailable right now.");
        }

        void NoteConversation(bool waitingForAgent, string ticketStatus, bool chatLimited)
        {
            if (chatLimited)
            {
                SetStatus("This widget is not accepting another new chat right now.");
                return;
            }
            if (waitingForAgent || ticketStatus == "WAITING_FOR_AGENT" || ticketStatus == "PENDING")
            {
                SetStatus("A team member will respond shortly. You can still send a message.");
                return;
            }
            if (ticketStatus == "CLOSED" || ticketStatus == "BOT_CLOSED")
            {
                SetStatus("This conversation is closed. Send a message to start a new request.");
                return;
            }
            if (ticketStatus == "SOLVED" || ticketStatus == "BOT_SOLVED")
            {
                SetStatus("This conversation was marked as solved. Send a message to start a new request.");
                return;
            }
            SetStatus("Sent.");
        }

        void ReplaceTranscript(List<IntegravaMessage> messages)
        {
            if (!_alive || _transcript == null) return;
            var builder = new StringBuilder();
            if (messages != null)
            {
                for (var i = 0; i < messages.Count; i++)
                {
                    var message = messages[i];
                    var who = message.Role == "user" ? "You" : (string.IsNullOrEmpty(message.AuthorName) ? "Assistant" : message.AuthorName);
                    builder.Append(who).Append(": ").Append(message.Content).Append("\n\n");
                }
            }
            _transcript.text = builder.ToString();
        }

        void AppendLine(string who, string text)
        {
            if (!_alive || _transcript == null) return;
            _transcript.text = (_transcript.text ?? "") + who + ": " + text + "\n\n";
        }

        void SetStatus(string text)
        {
            if (!_alive || _status == null) return;
            _status.text = text ?? "";
        }

        void ReportFault(Exception ex)
        {
            if (!_alive || ex == null) return;
            var integrava = ex as IntegravaException;
            var message = ex.Message ?? "";
            if (!string.IsNullOrEmpty(widgetToken)) message = message.Replace(widgetToken, "[widget]");
            if (!string.IsNullOrEmpty(_cachedToken)) message = message.Replace(_cachedToken, "[token]");
            if (LooksLikeSecret(message)) message = "Request failed.";
            if (integrava != null) SetStatus(integrava.Code + ": " + message);
            else SetStatus(message);
        }

        static bool LooksLikeSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf("integrava_sk_", StringComparison.Ordinal) >= 0
                || value.IndexOf("opsflow_sk_", StringComparison.Ordinal) >= 0;
        }

        static Font TryBuiltinFont(string resourceName)
        {
            if (string.IsNullOrEmpty(resourceName)) return null;
            try
            {
                return Resources.GetBuiltinResource<Font>(resourceName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static void AddInputModule(GameObject eventSystem)
        {
            // New Unity 6 projects enable the Input System only. StandaloneInputModule
            // reads UnityEngine.Input and throws on that setting. Both still has legacy
            // input, so StandaloneInputModule is a safe fallback if the package is missing.
#if ENABLE_INPUT_SYSTEM && INTEGRAVA_HAS_INPUT_SYSTEM
            if (TryAddInputSystemUiModule(eventSystem)) return;
#elif ENABLE_INPUT_SYSTEM
            // The version define did not match this package version. Still avoid UnityEngine.Input.
            if (TryAddInputSystemUiModule(eventSystem)) return;
#endif
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            return;
#endif
            eventSystem.AddComponent<StandaloneInputModule>();
        }

#if ENABLE_INPUT_SYSTEM
        static bool TryAddInputSystemUiModule(GameObject eventSystem)
        {
            try
            {
                var assembly = Assembly.Load("Unity.InputSystem");
                var moduleType = assembly.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
                if (moduleType == null) return false;
                var module = eventSystem.AddComponent(moduleType);
                if (module == null) return false;
                try
                {
                    var assign = moduleType.GetMethod("AssignDefaultActions", BindingFlags.Instance | BindingFlags.Public);
                    if (assign != null && assign.GetParameters().Length == 0)
                        assign.Invoke(module, null);
                }
                catch (Exception)
                {
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
#endif

        void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            try
            {
                _eventSystem = new GameObject("EventSystem");
                _eventSystem.AddComponent<EventSystem>();
                AddInputModule(_eventSystem);
                _createdEventSystem = true;
            }
            catch (Exception)
            {
                _createdEventSystem = _eventSystem != null;
            }
        }

        void BuildUi()
        {
            if (font == null)
            {
                font = TryBuiltinFont("LegacyRuntime.ttf");
                if (font == null) font = TryBuiltinFont("Arial.ttf");
            }

            EnsureEventSystem();

            _canvasObject = new GameObject("IntegravaCanvas");
            var canvas = _canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = _canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            _canvasObject.AddComponent<GraphicRaycaster>();

            var panel = CreateRect("Panel", _canvasObject.transform);
            var panelImage = panel.gameObject.AddComponent<Image>();
            panelImage.color = panelColor;
            Stretch(panel, 40, 40, 40, 40);

            _notice = CreateText("Notice", panel, noticeColor, fontSize - 2);
            Anchor(_notice.rectTransform, 0, 1, 1, 1, 16, -48, -16, -12);

            _transcript = CreateText("Transcript", panel, textColor, fontSize);
            _transcript.alignment = TextAnchor.UpperLeft;
            Anchor(_transcript.rectTransform, 0, 0, 1, 1, 16, 88, -16, -56);

            var inputObject = CreateRect("Input", panel);
            var inputImage = inputObject.gameObject.AddComponent<Image>();
            inputImage.color = inputColor;
            Anchor(inputObject, 0, 0, 1, 0, 16, 16, -220, 56);
            _input = inputObject.gameObject.AddComponent<InputField>();
            var inputText = CreateText("InputText", inputObject, textColor, fontSize);
            Stretch(inputText.rectTransform, 8, 6, 8, 6);
            _input.textComponent = inputText;
            _input.lineType = InputField.LineType.SingleLine;

            var send = CreateButton("Send", panel, "Send", OnSendClicked);
            Anchor(send, 1, 0, 1, 0, -204, 16, -112, 56);
            var help = CreateButton("Help", panel, "Help", OnHelpClicked);
            Anchor(help, 1, 0, 1, 0, -100, 16, -16, 56);

            _status = CreateText("Status", panel, noticeColor, fontSize - 2);
            Anchor(_status.rectTransform, 0, 0, 1, 0, 16, 60, -220, 84);

            var camera = Camera.main;
            if (camera != null) camera.backgroundColor = backgroundColor;
        }

        RectTransform CreateButton(string name, Transform parent, string label, UnityEngine.Events.UnityAction click)
        {
            var rect = CreateRect(name, parent);
            var image = rect.gameObject.AddComponent<Image>();
            image.color = buttonColor;
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(click);
            var text = CreateText("Label", rect, textColor, fontSize);
            text.text = label;
            text.alignment = TextAnchor.MiddleCenter;
            Stretch(text.rectTransform, 4, 4, 4, 4);
            return rect;
        }

        Text CreateText(string name, Transform parent, Color color, int size)
        {
            var rect = CreateRect(name, parent);
            var text = rect.gameObject.AddComponent<Text>();
            text.font = font;
            text.fontSize = size;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        static RectTransform CreateRect(string name, Transform parent)
        {
            var obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            return obj.AddComponent<RectTransform>();
        }

        static void Stretch(RectTransform rect, float left, float bottom, float right, float top)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(-right, -top);
        }

        static void Anchor(RectTransform rect, float minX, float minY, float maxX, float maxY, float left, float bottom, float right, float top)
        {
            rect.anchorMin = new Vector2(minX, minY);
            rect.anchorMax = new Vector2(maxX, maxY);
            rect.offsetMin = new Vector2(left, bottom);
            rect.offsetMax = new Vector2(right, top);
        }
    }
}
