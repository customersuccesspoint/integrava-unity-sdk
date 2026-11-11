# Integrava Unity SDK (beta)

HTTP client for Integrava, an AI customer support platform, from a Unity project. This is not a native iOS, Android, React Native, or Flutter package. There is no websocket. Chat replies use the same long poll as the website widget.

The server secret key (`integrava_sk_…`) stays on your server. The player holds the public widget token and a short-lived identify token. A busy widget returns HTTP 200 with `chatLimited`. A blocked sender returns HTTP 200 with `chatUnavailable`. The sample shows that reply and does not add a waiting message. When history sets `needsReplyRetry`, the sample calls `POST /api/public/chat/retry`.

## Install

Unity 2021.3 or newer. In Package Manager, choose Add package from git URL:

```
https://github.com/customersuccesspoint/integrava-unity-sdk.git#v0.1.2
```

`package.json` is at the repository root. The `#v0.1.2` tag is this beta. Pin that tag when you ship.

The package name is `ai.integrava.unity`. An OpenUPM listing is a separate step. There is no release tarball.

## What you wire up

1. Create a widget in Embed Assistant and copy its public token.
2. On your server, mint identify tokens with `POST /api/v1/identify-token` and the secret key. The sample server reads `INTEGRAVA_SECRET_KEY` (optional `INTEGRAVA_API_BASE`, `INTEGRAVA_MINT_PASSWORD`). See `Samples~/Chat/backend/mint-identify-token.mjs`.
3. In the sample scene `Samples~/Chat/IntegravaChat`, set the API base URL, widget token, workspace slug, and your mint URL.
4. Press Play. The AI notice comes from the widget config. Help lists published Help Center articles.

Import the sample from Package Manager, Integrava Unity SDK (beta), Samples.

The sample stores the conversation id in PlayerPrefs. The key is a hash of the API host and the widget token, so staging and production do not share a session, and the token is not the key. Backgrounding the app stops the long poll. It starts again when the player returns. Unloading the scene cancels requests that are still in flight. The Unity transport and PlayerPrefs store must be called from the main thread.

## Input

The chat sample supports Active Input Handling set to Input Manager (Old), Input System Package, or Both.

- Input Manager (Old) only: the sample creates an EventSystem with StandaloneInputModule. This is the usual setup on Unity 2021.3.
- Input System Package only: this is the default for a new Unity 6 project. When `com.unity.inputsystem` is installed, the sample creates an EventSystem with InputSystemUIInputModule and does not call `UnityEngine.Input`.
- Both: the sample uses InputSystemUIInputModule when that package is installed.

The sample assembly does not reference `com.unity.inputsystem`. A project without that package still compiles, and the sample uses StandaloneInputModule.

## iOS and Android

You build and sign the player in Unity. Integrava does not code-sign this package. iOS exports need a Mac, Xcode, and your Apple Developer account. Android APK or AAB builds use your keystore. Turnstile stays on the website widget only. Standalone, iOS, and Android players send no browser Origin, so chat requires an identify token. WebGL is a browser: allowlist that host. Per-IP and per-token rate limits still apply.

## Not in this version

Attachments, an offline queue, push notifications, and localized UI strings.
