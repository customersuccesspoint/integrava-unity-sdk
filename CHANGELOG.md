# Changelog

## 0.1.2

- The chat sample tries LegacyRuntime.ttf first, then Arial.ttf, and still builds the UI if neither built-in font loads. Unity 6 throws when code requests Arial.ttf.
- The chat sample adds InputSystemUIInputModule when the Input System is enabled and com.unity.inputsystem is installed, and StandaloneInputModule otherwise. Input System only, the legacy Input Manager only, and Both are supported. The sample does not call UnityEngine.Input while the legacy Input Manager is off.

## 0.1.1

- Added .meta files so Unity imports the scripts and assembly definitions from a Git package.
- A busy widget returns HTTP 200 with `chatLimited` and the reply text. The sample keeps the visitor line and shows that reply.
- A blocked sender returns HTTP 200 with `chatUnavailable`. The sample shows that reply and stops polling.
- History `needsReplyRetry` calls `POST /api/public/chat/retry` once. The client does not send `escalateToAgent`.
- Waiting for a person is a status line. The sample does not add its own waiting message.
- A new `sessionId` on history replaces the stored session.

## 0.1.0

- First beta. Widget config, public chat, history long poll, and Help Center reads.
