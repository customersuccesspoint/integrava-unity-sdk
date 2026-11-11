using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Integrava.Tests
{
    public sealed class IntegravaJsonTests
    {
        [Fact]
        public void ParsesNestedHelpTreeAndEscapes()
        {
            var raw = "{\"ok\":true,\"n\":2,\"note\":null,\"items\":[\"a\\nb\",{\"slug\":\"reset\"}]}";
            var json = IntegravaJson.Parse(raw);
            Assert.True(json.GetBool("ok", false));
            Assert.Equal(2, json.GetInt("n", 0));
            Assert.Equal(IntegravaJson.Kind.Null, json.Get("note").Type);
            Assert.Equal("a\nb", json.Get("items").Items[0].StringValue);
            Assert.Equal("reset", json.Get("items").Items[1].GetString("slug", ""));
        }

        [Fact]
        public void QuoteRoundTripsControlCharacters()
        {
            var quoted = IntegravaJson.Quote("say \"hi\"\n<path>");
            var parsed = IntegravaJson.Parse("{\"m\":" + quoted + "}");
            Assert.Equal("say \"hi\"\n<path>", parsed.GetString("m", ""));
        }

        [Fact]
        public void StripHtmlRemovesTagsAndDecodesEntities()
        {
            Assert.Equal("Cats & dogs", IntegravaJson.StripHtml("<p>Cats &amp; dogs</p>"));
        }
    }

    public sealed class IntegravaClientTests
    {
        const string Widget = "widget-token-0123456789abcdef0123456789";
        const string Jwt = "header.payload.sig";
        const string BaseUrl = "https://api.integrava.ai/";

        [Fact]
        public async Task SendPostsBodyAndHeaderWithoutPuttingTokenInTheUrl()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(200, "{\"reply\":\"hello\",\"sessionId\":\"sess-1\",\"newConversation\":true,\"messages\":[{\"id\":\"m1\",\"role\":\"assistant\",\"content\":\"hello\",\"aiGenerated\":true}]}");
            };
            var client = Client(transport, Jwt);
            var result = await client.SendMessageAsync("Need help", CancellationToken.None);
            Assert.Equal("hello", result.Reply);
            Assert.Equal("sess-1", result.SessionId);
            Assert.Equal("sess-1", client.SessionId);
            Assert.True(result.Messages[0].AiGenerated);
            var call = transport.Calls[0];
            Assert.Equal("POST", call.Method);
            Assert.DoesNotContain("identifyToken=", call.Url);
            Assert.DoesNotContain(Jwt, call.Url);
            Assert.Contains("\"message\":\"Need help\"", call.Body);
            Assert.Contains("\"identifyToken\":\"" + Jwt + "\"", call.Body);
            Assert.Equal(Jwt, call.IdentifyToken);
            Assert.Contains("\"clientMessageId\":", call.Body);
        }

        [Fact]
        public async Task IdentifyInvalidRefreshesOnceThenSucceeds()
        {
            var tokens = new List<string>();
            var transport = new FakeTransport();
            transport.Handler = delegate (IntegravaHttpRequest request)
            {
                if (request.IdentifyToken == "stale")
                    return Json(403, "{\"error\":\"Identify token is invalid or expired\",\"code\":\"IDENTIFY_INVALID\"}");
                return Json(200, "{\"assistantName\":\"Ada\",\"aiDisclosure\":{\"enabled\":true,\"text\":\"You are chatting with an AI.\"},\"aiEnabled\":true}");
            };
            var client = Client(transport, "unused", delegate (bool force, CancellationToken ct)
            {
                var token = force ? "fresh" : "stale";
                tokens.Add(token);
                return Task.FromResult(token);
            });
            var config = await client.GetWidgetConfigAsync(CancellationToken.None);
            Assert.Equal("You are chatting with an AI.", config.AiDisclosureText);
            Assert.True(config.AiDisclosureEnabled);
            Assert.Equal(new[] { "stale", "fresh" }, tokens);
            Assert.Equal(2, transport.Calls.Count);
        }

        [Fact]
        public async Task IdentifyInvalidDoesNotRefreshForever()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(403, "{\"error\":\"Identify token is invalid or expired\",\"code\":\"IDENTIFY_INVALID\"}");
            };
            var calls = 0;
            var client = Client(transport, Jwt, delegate
            {
                calls++;
                return Task.FromResult(Jwt);
            });
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.IdentifyInvalid, error.Code);
            Assert.Equal(2, calls);
            Assert.DoesNotContain(Jwt, error.Message);
        }

        [Fact]
        public async Task RateLimitRetriesAndBadRequestDoesNot()
        {
            var delays = new List<int>();
            var transport = new FakeTransport();
            var hits = 0;
            transport.Handler = delegate
            {
                hits++;
                if (hits == 1) return Json(429, "{\"error\":\"Rate limit exceeded\"}");
                return Json(200, "{\"reply\":\"ok\",\"sessionId\":\"sess-2\",\"messages\":[]}");
            };
            var client = Client(transport, Jwt, null, (ms, ct) =>
            {
                delays.Add(ms);
                return Task.CompletedTask;
            });
            var result = await client.SendMessageAsync("hi", CancellationToken.None);
            Assert.Equal("ok", result.Reply);
            Assert.Equal(new[] { 500 }, delays);
            Assert.Equal("sess-2", client.SessionId);

            transport.Calls.Clear();
            transport.Handler = delegate { return Json(400, "{\"error\":\"Message is too long.\"}"); };
            var bad = await Assert.ThrowsAsync<IntegravaException>(() => client.SendMessageAsync("nope", CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.BadRequest, bad.Code);
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task ServerErrorIsNotRetried()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate { return Json(500, "{\"error\":\"boom\"}"); };
            var client = Client(transport, Jwt);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.SendMessageAsync("hi", CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.Server, error.Code);
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task RetryKeepsTheSameClientMessageId()
        {
            var transport = new FakeTransport();
            var hits = 0;
            transport.Handler = delegate
            {
                hits++;
                if (hits == 1) return Json(503, "{\"error\":\"unavailable\"}");
                return Json(200, "{\"reply\":\"ok\",\"sessionId\":\"sess-3\",\"messages\":[]}");
            };
            var client = Client(transport, Jwt, null, (ms, ct) => Task.CompletedTask);
            await client.SendMessageAsync("hi", CancellationToken.None);
            var first = ClientMessageId(transport.Calls[0].Body);
            var second = ClientMessageId(transport.Calls[1].Body);
            Assert.Equal(first, second);
            Assert.False(string.IsNullOrEmpty(first));
        }

        [Fact]
        public async Task HistoryClampsWaitAndOmitsIdentifyTokenFromTheUrl()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(200, "{\"messages\":[],\"messageCount\":0,\"lastMessageId\":null,\"changed\":false,\"ticketClosed\":false,\"ticketStatus\":null}");
            };
            var store = new MemorySessionStore();
            store.Save(IntegravaSessionKeys.ForWidgetToken(BaseUrl, Widget), "sess-9");
            var client = Client(transport, Jwt, null, null, store);
            var history = await client.PollHistoryAsync(99999, 3, "m9", CancellationToken.None);
            Assert.False(history.Changed);
            var url = transport.Calls[0].Url;
            Assert.Contains("waitMs=15000", url);
            Assert.Contains("sessionId=sess-9", url);
            Assert.DoesNotContain("identifyToken", url);
            Assert.DoesNotContain(Jwt, url);
            Assert.Equal(Jwt, transport.Calls[0].IdentifyToken);
            Assert.Null(transport.Calls[0].Body);
        }

        [Fact]
        public async Task HistoryWithoutASessionDoesNotCallTheApi()
        {
            var transport = new FakeTransport();
            var client = Client(transport, Jwt);
            var history = await client.PollHistoryAsync(1000, 0, "", CancellationToken.None);
            Assert.Empty(history.Messages);
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task MismatchClearsTheStoredSession()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(403, "{\"error\":\"Identify token does not match this conversation\",\"code\":\"IDENTIFY_MISMATCH\"}");
            };
            var store = new MemorySessionStore();
            store.Save(IntegravaSessionKeys.ForWidgetToken(BaseUrl, Widget), "other-user");
            var client = Client(transport, Jwt, null, null, store);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.PollHistoryAsync(0, 0, "", CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.IdentifyMismatch, error.Code);
            Assert.Equal("", client.SessionId);
        }

        [Fact]
        public async Task OriginRejectionIsNotRetried()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(403, "{\"error\":\"Origin not allowed\",\"code\":\"ORIGIN_NOT_ALLOWED\"}");
            };
            var client = Client(transport, Jwt);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.OriginRejected, error.Code);
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task HelpListFlattensArticlesAndReadStripsHtml()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate (IntegravaHttpRequest request)
            {
                Assert.Equal("", request.IdentifyToken);
                Assert.DoesNotContain("widgetToken", request.Url);
                if (request.Url.Contains("/articles/"))
                    return Json(200, "{\"article\":{\"title\":\"Reset\",\"slug\":\"reset\",\"bodyHtml\":\"<p>Use &amp; save</p>\"}}");
                return Json(200, "{\"categories\":[{\"title\":\"Account\",\"sections\":[{\"title\":\"Login\",\"pages\":[{\"id\":\"p1\",\"title\":\"Reset\",\"slug\":\"reset\"}]}]}]}");
            };
            var client = Client(transport, Jwt);
            var list = await client.ListArticlesAsync(CancellationToken.None);
            Assert.Equal("Reset", list[0].Title);
            Assert.Equal("Account", list[0].CategoryTitle);
            Assert.Equal("Login", list[0].SectionTitle);
            var article = await client.GetArticleAsync("reset", CancellationToken.None);
            Assert.Equal("Use & save", article.BodyText);
            Assert.Contains("<p>", article.BodyHtml);
        }

        [Fact]
        public async Task HelpDoesNotRunWhenSlugIsMissing()
        {
            var transport = new FakeTransport();
            var options = Options(transport, Jwt);
            options.WorkspaceSlug = "  ";
            var client = new IntegravaClient(options);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.ListArticlesAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.BadRequest, error.Code);
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public void SessionKeyIsPerHostAndDoesNotContainTheWidgetToken()
        {
            var production = IntegravaSessionKeys.ForWidgetToken(BaseUrl, Widget);
            var sameHost = IntegravaSessionKeys.ForWidgetToken("https://api.integrava.ai", Widget);
            var staging = IntegravaSessionKeys.ForWidgetToken("https://staging.example", Widget);
            Assert.StartsWith("integrava.session.", production);
            Assert.Equal(production, sameHost);
            Assert.NotEqual(production, staging);
            Assert.DoesNotContain(Widget, production);
            Assert.DoesNotContain("integrava.ai", production);
            Assert.DoesNotContain("staging.example", staging);
        }

        [Fact]
        public async Task SessionsStaySeparateWhenTheSameTokenUsesTwoHosts()
        {
            var store = new MemorySessionStore();
            var productionTransport = new FakeTransport();
            productionTransport.Handler = delegate
            {
                return Json(200, "{\"reply\":\"ok\",\"sessionId\":\"sess-a\",\"messages\":[]}");
            };
            var productionOptions = Options(productionTransport, Jwt, null, store);
            var production = new IntegravaClient(productionOptions);
            await production.SendMessageAsync("hi", CancellationToken.None);

            var stagingTransport = new FakeTransport();
            var stagingOptions = Options(stagingTransport, Jwt, null, store);
            stagingOptions.BaseUrl = "https://staging.example";
            var staging = new IntegravaClient(stagingOptions);
            Assert.Equal("sess-a", production.SessionId);
            Assert.Equal("", staging.SessionId);
            var history = await staging.PollHistoryAsync(0, 0, "", CancellationToken.None);
            Assert.Empty(history.Messages);
            Assert.Empty(stagingTransport.Calls);
        }

        [Fact]
        public async Task TransportIntegravaExceptionIsNotRetriedAsNetwork()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                throw new IntegravaException(IntegravaErrorCode.BadRequest, 0, "Integrava Unity calls must run on the main thread.");
            };
            var client = Client(transport, Jwt);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.BadRequest, error.Code);
            Assert.Contains("main thread", error.Message);
            Assert.Single(transport.Calls);
        }

        [Fact]
        public async Task CancellationDuringBackoffIsNotRetried()
        {
            var transport = new FakeTransport();
            var hits = 0;
            transport.Handler = delegate
            {
                hits++;
                return new IntegravaHttpResponse { StatusCode = 0, TransportError = "down" };
            };
            var cts = new CancellationTokenSource();
            var client = Client(transport, Jwt, null, delegate (int ms, CancellationToken ct)
            {
                cts.Cancel();
                return Task.Delay(ms, ct);
            });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendMessageAsync("hi", cts.Token));
            Assert.Equal(1, hits);
        }

        [Fact]
        public void RefusesAServerSecretAsTheWidgetToken()
        {
            var options = Options(new FakeTransport(), Jwt);
            options.WidgetToken = "integrava_sk_not_for_the_client";
            Assert.Throws<ArgumentException>(() => new IntegravaClient(options));
        }

        [Fact]
        public async Task RefusesAServerSecretFromTheIdentifyCallback()
        {
            var transport = new FakeTransport();
            var client = Client(transport, Jwt, delegate
            {
                return Task.FromResult("integrava_sk_leaked");
            });
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.IdentifyInvalid, error.Code);
            Assert.Empty(transport.Calls);
            Assert.DoesNotContain("integrava_sk_", error.Message);
        }

        [Fact]
        public async Task CallbackIntegravaExceptionIsPreserved()
        {
            var transport = new FakeTransport();
            var client = Client(transport, Jwt, delegate
            {
                return Task.FromException<string>(new IntegravaException(IntegravaErrorCode.Network, 502, "Identify mint failed (HTTP 502)."));
            });
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.Network, error.Code);
            Assert.Equal("Identify mint failed (HTTP 502).", error.Message);
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task CallbackExceptionDoesNotLeakTheSecret()
        {
            var transport = new FakeTransport();
            var client = Client(transport, Jwt, delegate
            {
                return Task.FromException<string>(new InvalidOperationException("leaked integrava_sk_supersecret"));
            });
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.IdentifyRequired, error.Code);
            Assert.DoesNotContain("integrava_sk_", error.Message);
            Assert.DoesNotContain("supersecret", error.Message);
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task ChatLimitAndUnavailableStayOnTheResult()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(200, "{\"reply\":\"We're very busy right now. Please try again later.\",\"chatLimited\":true,\"sessionId\":\"sess-lim\",\"messages\":[],\"waitingForAgent\":false}");
            };
            var client = Client(transport, Jwt);
            var limited = await client.SendMessageAsync("hi", CancellationToken.None);
            Assert.True(limited.ChatLimited);
            Assert.False(limited.ChatUnavailable);
            Assert.Equal("We're very busy right now. Please try again later.", limited.Reply);
            Assert.Empty(limited.Messages);
            Assert.Equal("", client.SessionId);
            Assert.DoesNotContain("escalateToAgent", transport.Calls[0].Body);

            transport.Handler = delegate
            {
                return Json(200, "{\"reply\":\"This chat is unavailable right now.\",\"chatUnavailable\":true,\"messages\":[],\"waitingForAgent\":false}");
            };
            var blocked = await client.SendMessageAsync("hi", CancellationToken.None);
            Assert.True(blocked.ChatUnavailable);
            Assert.False(blocked.Banned);
            Assert.Equal("This chat is unavailable right now.", blocked.Reply);
        }

        [Fact]
        public async Task HistoryReadsRetryFlagAndRotatesSession()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(200, "{\"messages\":[{\"id\":\"u1\",\"role\":\"user\",\"content\":\"hi\"}],\"messageCount\":1,\"lastMessageId\":\"u1\",\"changed\":true,\"ticketClosed\":false,\"ticketStatus\":\"BOT_CONVERSATION\",\"needsReplyRetry\":true,\"sessionId\":\"sess-next\"}");
            };
            var store = new MemorySessionStore();
            store.Save(IntegravaSessionKeys.ForWidgetToken(BaseUrl, Widget), "sess-old");
            var client = Client(transport, Jwt, null, null, store);
            var history = await client.PollHistoryAsync(0, 0, "", CancellationToken.None);
            Assert.True(history.NeedsReplyRetry);
            Assert.True(history.SessionRotated);
            Assert.Equal("sess-next", client.SessionId);
            Assert.Equal("BOT_CONVERSATION", history.TicketStatus);
            Assert.False(history.WaitingForAgent);
        }

        [Fact]
        public async Task WaitingStatusComesFromTheServerFlag()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(200, "{\"reply\":\"\",\"sessionId\":\"sess-w\",\"waitingForAgent\":true,\"ticketStatus\":\"WAITING_FOR_AGENT\",\"messages\":[{\"id\":\"u1\",\"role\":\"user\",\"content\":\"help\"}]}");
            };
            var client = Client(transport, Jwt);
            var result = await client.SendMessageAsync("help", CancellationToken.None);
            Assert.True(result.WaitingForAgent);
            Assert.Equal("WAITING_FOR_AGENT", result.TicketStatus);
            Assert.Equal("help", result.Messages[0].Content);
        }

        [Fact]
        public async Task RetryPostsTheSessionAndDoesNotAskForAnAgent()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate (IntegravaHttpRequest request)
            {
                if (request.Url.Contains("/api/public/chat/retry"))
                    return Json(200, "{\"ok\":true,\"completed\":true,\"reply\":\"done\",\"needsReplyRetry\":false,\"messages\":[{\"id\":\"a1\",\"role\":\"assistant\",\"content\":\"done\"}]}");
                return Json(200, "{\"messages\":[],\"messageCount\":0,\"changed\":false,\"ticketStatus\":\"BOT_CONVERSATION\",\"needsReplyRetry\":false}");
            };
            var store = new MemorySessionStore();
            store.Save(IntegravaSessionKeys.ForWidgetToken(BaseUrl, Widget), "sess-r");
            var client = Client(transport, Jwt, null, null, store);
            var retried = await client.RetryAssistantReplyAsync(CancellationToken.None);
            Assert.True(retried.RetryCompleted);
            Assert.Equal("done", retried.Reply);
            var call = transport.Calls[0];
            Assert.Contains("/api/public/chat/retry", call.Url);
            Assert.Contains("\"sessionId\":\"sess-r\"", call.Body);
            Assert.DoesNotContain("escalateToAgent", call.Body);
            Assert.Equal(Jwt, call.IdentifyToken);
        }

        [Fact]
        public async Task EmptyMessageDoesNotCallTheApi()
        {
            var transport = new FakeTransport();
            var client = Client(transport, Jwt);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.SendMessageAsync("  ", CancellationToken.None));
            Assert.Equal(IntegravaErrorCode.BadRequest, error.Code);
            Assert.Empty(transport.Calls);
        }

        [Fact]
        public async Task ErrorTextThatEchoesTheTokenIsRedacted()
        {
            var transport = new FakeTransport();
            transport.Handler = delegate
            {
                return Json(403, "{\"error\":\"bad " + Jwt + "\",\"code\":\"IDENTIFY_INVALID\"}");
            };
            var client = Client(transport, Jwt);
            var error = await Assert.ThrowsAsync<IntegravaException>(() => client.GetWidgetConfigAsync(CancellationToken.None));
            Assert.DoesNotContain(Jwt, error.Message);
            Assert.Contains("[token]", error.Message);
        }

        [Fact]
        public async Task NetworkFailureUsesBackoff()
        {
            var delays = new List<int>();
            var transport = new FakeTransport();
            var hits = 0;
            transport.Handler = delegate
            {
                hits++;
                if (hits < 3) return new IntegravaHttpResponse { StatusCode = 0, TransportError = "down" };
                return Json(200, "{\"reply\":\"ok\",\"sessionId\":\"sess-4\",\"messages\":[]}");
            };
            var client = Client(transport, Jwt, null, (ms, ct) =>
            {
                delays.Add(ms);
                return Task.CompletedTask;
            });
            await client.SendMessageAsync("hi", CancellationToken.None);
            Assert.Equal(new[] { 500, 1000 }, delays);
        }

        static string ClientMessageId(string body)
        {
            var json = IntegravaJson.Parse(body);
            return json.GetString("clientMessageId", "");
        }

        static IntegravaHttpResponse Json(int status, string body)
        {
            return new IntegravaHttpResponse { StatusCode = status, Body = body };
        }

        static IntegravaClient Client(
            FakeTransport transport,
            string token,
            Func<bool, CancellationToken, Task<string>> provider = null,
            Func<int, CancellationToken, Task> delay = null,
            IIntegravaSessionStore store = null)
        {
            var options = Options(transport, token, provider, store);
            return new IntegravaClient(options, delay);
        }

        static IntegravaClientOptions Options(
            FakeTransport transport,
            string token,
            Func<bool, CancellationToken, Task<string>> provider = null,
            IIntegravaSessionStore store = null)
        {
            return new IntegravaClientOptions
            {
                BaseUrl = BaseUrl,
                WidgetToken = Widget,
                WorkspaceSlug = "acme",
                Transport = transport,
                SessionStore = store,
                IdentifyTokenProvider = provider ?? ((force, ct) => Task.FromResult(token))
            };
        }

        sealed class FakeTransport : IIntegravaTransport
        {
            public readonly List<IntegravaHttpRequest> Calls = new List<IntegravaHttpRequest>();
            public Func<IntegravaHttpRequest, IntegravaHttpResponse> Handler;

            public Task<IntegravaHttpResponse> SendAsync(IntegravaHttpRequest request, CancellationToken cancellationToken)
            {
                Calls.Add(request);
                return Task.FromResult(Handler(request));
            }
        }
    }
}
