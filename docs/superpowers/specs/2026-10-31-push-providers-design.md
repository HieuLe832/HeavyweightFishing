# Push provider adapters (4.4, slice 4) — design

Date: 2026-10-31

## Goal

Replace the logging sender with real FCM, APNs and Web Push senders, each switched on by configuration. With no
credentials for a platform, that platform keeps the logging sender, so the API still starts with nothing configured.
`PushDispatcher`, the token endpoints and the event are unchanged.

**Out of scope:** the mobile and browser clients that obtain tokens (the web client comes with the UI later), topics,
badge counts, silent pushes, retries.

**Cannot be verified here.** There are no credentials, so delivery to a real device is untested. The adapters are tested
against fake HTTP servers and fixed vectors. The README says so.

## Decisions

**Dependencies.** FCM through `FirebaseAdmin` (handles the OAuth token for a service account). Web Push through the
`WebPush` package (VAPID signing and RFC 8291 encryption are easy to get wrong by hand). APNs by hand over `HttpClient`
on HTTP/2 with an ES256 provider token: the key is a `.p8` file, the JWT is 20 lines with `ECDsa`, and the available
packages are unmaintained. Exact package versions are checked when the plan is written.

**Configuration** under `Push` in `appsettings.json` (secrets via environment or user secrets, never committed):

| Key | Meaning |
|-----|---------|
| `Push:Fcm:ServiceAccountJsonPath` | FCM service account file. Unset: logging sender for `fcm` |
| `Push:Apns:KeyPath`, `KeyId`, `TeamId`, `BundleId`, `Sandbox` | APNs token auth. All of the first four needed, else logging for `apns` |
| `Push:Web:PublicKey`, `PrivateKey`, `Subject` | VAPID keys and a `mailto:` or `https:` contact. Unset: logging for `web` |

A half-filled section fails start with a message naming the missing keys, rather than silently falling back.
The API logs, per platform, which sender is active.

**Web tokens.** The stored token has no whitespace and fits 4096 characters (`validation_failed` otherwise). A browser
push subscription is JSON, so the client sends it as base64url of `{"endpoint","keys":{"p256dh","auth"}}`; the sender
decodes it. A token that does not decode is `InvalidToken`.

**Payload.** All three carry the `PushMessage` fields: title, body, and `conversationId`, `messageId`, `seq` as string data
so the app can open the chat. FCM: a notification plus data; APNs: `aps.alert` with `thread-id` = conversation id, plus the
ids as custom keys; Web Push: a JSON body the service worker shows. The preview text is already cut by the dispatcher.

**Result mapping.** Only "this token is dead" maps to `InvalidToken` (the dispatcher then clears it); every other failure
throws, which the dispatcher logs and skips.

| Provider answer | Result |
|-----------------|--------|
| FCM `UNREGISTERED`, `INVALID_ARGUMENT` on the token | `InvalidToken` |
| APNs 410, or 400 `BadDeviceToken` / `Unregistered` | `InvalidToken` |
| Web Push 404 or 410 | `InvalidToken` |
| 2xx | `Sent` |
| anything else (auth, 429, 5xx, network) | exception |

## Components

```
Chat.Api/Push/
  PushOptions.cs        Push section, validation of half-filled sections
  FcmPushSender.cs
  ApnsPushSender.cs     ApnsJwt (token cached for 50 minutes) + HttpClient
  WebPushSender.cs
  PushRegistration.cs   AddPushSenders(): one real or logging sender per platform
```

`Program.cs` replaces its three `LoggingPushSender` lines with `AddPushSenders(configuration)`. The "logging sender in use"
warning moves into it and names only the platforms that fell back.

## Testing

- options: complete, empty and half-filled sections; the registration picks real or logging per platform;
- APNs: JWT header and claims, caching and renewal, request path, headers (`apns-topic`, `apns-push-type`, `apns-priority`),
  body, and every row of the result table, against a fake HTTP/2 handler;
- FCM: message mapping and the `FirebaseMessagingException` codes, through an `IFirebaseMessagingClient` seam over the SDK;
- Web: base64url decoding (valid, garbage, wrong shape), 404 / 410 mapping, through a fake `HttpMessageHandler`;
- the dispatcher still clears a token on `InvalidToken` (existing test) with each real sender replaced by a fake.

## Docs to update with the code

README "API: push" (providers, configuration, the untested-delivery note), CLAUDE.md "Next steps" and the push sentence in
the "Done" paragraph.
