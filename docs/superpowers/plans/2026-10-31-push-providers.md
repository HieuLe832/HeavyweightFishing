# Push Provider Adapters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Real FCM, APNs and Web Push senders, each enabled by configuration, with the logging sender kept for any platform that has no credentials.

**Architecture:** One `IPushSender` per platform in `Chat.Api/Push/`, each built on an injectable `HttpClient` (so a fake `HttpMessageHandler` tests them). `PushRegistration.AddPushSenders` reads the `Push` section and registers the real or the logging sender per platform. `PushDispatcher` is untouched.

**Tech Stack:** .NET 10, `HttpClient`, `System.Security.Cryptography.ECDsa` (APNs JWT), `Google.Apis.Auth` (FCM access tokens), `WebPush` NuGet package (VAPID + RFC 8291). Resolve current versions with `dotnet add package`.

**Spec:** `docs/superpowers/specs/2026-10-31-push-providers-design.md`

## Global Constraints

- Config keys: `Push:Fcm:ServiceAccountJsonPath`; `Push:Apns:KeyPath|KeyId|TeamId|BundleId|Sandbox`; `Push:Web:PublicKey|PrivateKey|Subject`. Nothing set for a platform: logging sender. A platform partly set: start fails with `InvalidOperationException` naming the missing keys. Secrets are never committed.
- Result mapping: FCM 404 `UNREGISTERED` or 400 `INVALID_ARGUMENT` whose message mentions the token, APNs 410 or 400 `BadDeviceToken`/`Unregistered`, Web 404/410 → `PushResult.InvalidToken`. 2xx → `Sent`. Anything else throws.
- Payload fields: title, body, and `conversationId`, `messageId`, `seq` as strings. APNs uses `aps.alert` and `thread-id` = conversation id.
- Web token = base64url of `{"endpoint":…,"keys":{"p256dh":…,"auth":…}}`; undecodable → `InvalidToken`.
- APNs JWT (ES256, header `kid`, claims `iss` = team id and `iat`) is cached 50 minutes using the injected `TimeProvider`.
- The notification text is never logged.
- Never push. Commits carry no `Co-Authored-By` or generated-by lines. Identity `hieu` / `lehoanghieu02206@gmail.com`; if `git config` differs, stop and tell the user.

## Review Focus

- A push token with a reason body that is not JSON (proxy error page): throws, does not clear the token. Test in Task 2 (APNs) and Task 3 (FCM).
- APNs 403 `ExpiredProviderToken` (clock skew): throws, token kept. Test in Task 2.
- A Web token that is valid base64url but not the expected JSON: `InvalidToken`. Test in Task 4.
- A multi-byte (Vietnamese) title and body survive in each provider body. Test in Tasks 2-4.
- The API still starts with every `Push` key unset. Test in Task 1.

---

### Task 1: Options and registration

**Files:**
- Create: `backend/src/Chat.Api/Push/PushOptions.cs`
- Create: `backend/src/Chat.Api/Push/PushRegistration.cs`
- Modify: `backend/src/Chat.Api/Program.cs` (replace the three `AddSingleton<IPushSender>(… LoggingPushSender …)` lines and the "No push provider adapter" warning)
- Test: `backend/tests/Chat.Api.Tests/PushRegistrationTests.cs`

**Interfaces:**
- Produces: `public static class PushRegistration { public static IServiceCollection AddPushSenders(this IServiceCollection services, IConfiguration configuration); }`. Registers exactly one `IPushSender` for each of `PushPlatform.Fcm`, `Apns`, `Web`; `HttpClient`s come from `IHttpClientFactory`.
- Produces: `PushOptions` with nested `FcmOptions`, `ApnsOptions`, `WebOptions` and `IReadOnlyList<string> Missing(string platform)`.
- Produces (used by Tasks 2-4): constructors `FcmPushSender(HttpClient http, FcmCredentials credentials, IAccessTokenProvider tokens)`, `ApnsPushSender(HttpClient http, ApnsOptions options, TimeProvider time)`, `WebPushSender(WebPushClient client, WebOptions options)`. The registration builds them with the real pieces; tests build them directly.

- [ ] **Step 1: Write failing tests** with an in-memory `ConfigurationBuilder`:
  - `With_no_push_settings_all_three_platforms_use_the_logging_sender`: each `IPushSender` from `AddPushSenders(...).BuildServiceProvider()` is a `LoggingPushSender`; platforms are exactly Fcm, Apns, Web.
  - `A_complete_apns_section_registers_the_apns_sender` (write a real P-256 key to a temp `.p8` with `ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportPkcs8PrivateKeyPem()`): Apns is `ApnsPushSender`, the other two stay logging.
  - `A_half_filled_apns_section_throws_naming_the_missing_keys`: only `KeyId` and `TeamId` set; message contains `Push:Apns:KeyPath` and `Push:Apns:BundleId`.
  - `A_half_filled_web_section_throws`: only `PublicKey` set; message names `Push:Web:PrivateKey` and `Push:Web:Subject`.
  - `A_missing_fcm_file_throws`: path set, file absent; message contains the path.
- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter PushRegistrationTests`
Expected: compile error.

- [ ] **Step 3: Implement options and registration.** Register `LoggingPushSender` per platform when its section is empty. For a complete section, register the real sender: until Tasks 2-4 exist, register `LoggingPushSender` for that platform and mark the one test that expects `ApnsPushSender` with `[Fact(Skip = "Task 2")]`; Task 2 swaps in the real branch and removes the skip. Log, per platform, which sender is active; the old single warning moves here and names only the platforms that fell back. In `Program.cs` call `builder.Services.AddPushSenders(builder.Configuration)` and delete the old warning line.
- [ ] **Step 4: Run the registration tests and the full suite**

Run: `cd backend && dotnet test`
Expected: PASS (existing push tests still pass; `ApiFixture` already replaces all `IPushSender`s).

- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat(api): configure push senders per platform"
```

### Task 2: APNs sender

**Files:**
- Create: `backend/src/Chat.Api/Push/ApnsPushSender.cs`, `backend/src/Chat.Api/Push/ApnsJwt.cs`
- Modify: `backend/src/Chat.Api/Push/PushRegistration.cs` (real APNs branch; remove the skip from Task 1)
- Test: `backend/tests/Chat.Api.Tests/ApnsPushSenderTests.cs`, `backend/tests/Chat.Api.Tests/FakeHttp.cs` (a `FakeHandler : HttpMessageHandler` that records requests and returns queued responses)

**Interfaces:**
- Produces: `ApnsJwt(ECDsa key, string keyId, string teamId, TimeProvider time)` with `string Token()` (cached 50 minutes).
- Produces: `ApnsPushSender : IPushSender` (`Platform = PushPlatform.Apns`).
- Produces: `FakeHandler` with `Queue(HttpStatusCode, string? body)`, `IReadOnlyList<HttpRequestMessage> Requests` and bodies captured as strings. Tasks 3-4 reuse it.

- [ ] **Step 1: Write failing tests:**
  - `The_request_goes_to_the_device_path_with_apns_headers`: path `/3/device/<token>`, host `api.push.apple.com` (`api.sandbox.push.apple.com` when `Sandbox`), `apns-topic` = bundle id, `apns-push-type: alert`, `apns-priority: 10`, `Authorization: bearer <jwt>`, version 2.0.
  - `The_body_carries_alert_thread_id_and_ids`: JSON has `aps.alert.title/body` (with `"Nguyễn Văn A"` and `"Chào bạn 🎣"` intact), `aps.thread-id` = conversation id, `conversationId`, `messageId`, `seq` as strings.
  - `The_jwt_has_kid_iss_and_iat_and_verifies_with_the_public_key`, `The_jwt_is_reused_for_50_minutes_then_renewed` (use `FakeTimeProvider`).
  - `Result_mapping`: `[Theory]` rows 200 → `Sent`; 410 `{"reason":"Unregistered"}`, 400 `BadDeviceToken` → `InvalidToken`; 403 `ExpiredProviderToken`, 429, 500, 400 with `PayloadTooLarge`, 502 with an HTML body → throws `HttpRequestException`.
- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter "ApnsPushSenderTests|PushRegistrationTests"`
Expected: FAIL / compile error.

- [ ] **Step 3: Implement.** `ApnsJwt` builds `base64url(header).base64url(claims).base64url(ECDsa.SignData(..., SHA256, DSASignatureFormat.IeeeP1363FormatFixedFieldConcatenation))`. The sender sets `Version = HttpVersion.Version20` with `VersionPolicy = RequestVersionExact`. Parse the reason leniently: a body that is not JSON counts as no reason (so it throws). Registration reads the `.p8` with `ECDsa.ImportFromPem`.
- [ ] **Step 4: Run to verify they pass** — same command; Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat(api): APNs push sender"
```

### Task 3: FCM sender

**Files:**
- Create: `backend/src/Chat.Api/Push/FcmPushSender.cs`, `backend/src/Chat.Api/Push/FcmCredentials.cs`
- Modify: `backend/src/Chat.Api/Push/PushRegistration.cs`, `backend/src/Chat.Api/Chat.Api.csproj` (`Google.Apis.Auth`)
- Test: `backend/tests/Chat.Api.Tests/FcmPushSenderTests.cs`

**Interfaces:**
- Produces: `public interface IAccessTokenProvider { Task<string> GetAsync(CancellationToken ct); }` with the real `GoogleAccessTokenProvider(string serviceAccountJsonPath)` (scope `https://www.googleapis.com/auth/firebase.messaging`, via `GoogleCredential.FromFile(path).CreateScoped(...)`). `FcmCredentials(string ProjectId)` is read from the file's `project_id`.
- Consumes: `FakeHandler` (Task 2).

- [ ] **Step 1: Write failing tests:**
  - `The_request_posts_to_the_project_send_url_with_a_bearer_token`: `https://fcm.googleapis.com/v1/projects/<id>/messages:send`, `Authorization: Bearer <fake token>`.
  - `The_body_has_token_notification_and_string_data` (Vietnamese text intact; `seq` is a string).
  - `Result_mapping` `[Theory]`: 200 → `Sent`; 404 with `{"error":{"status":"NOT_FOUND","details":[{"errorCode":"UNREGISTERED"}]}}` → `InvalidToken`; 400 `INVALID_ARGUMENT` with message `The registration token is not a valid FCM registration token` → `InvalidToken`; 400 `INVALID_ARGUMENT` with message `Invalid JSON payload` → throws; 401, 429, 503 and a 502 HTML body → throws.
  - `The_access_token_is_requested_per_send_not_cached_by_the_sender` (the provider caches).
- [ ] **Step 2: Run to verify they fail.** Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter FcmPushSenderTests`. Expected: compile error.
- [ ] **Step 3: Implement** `FcmPushSender : IPushSender` (`Platform = PushPlatform.Fcm`) and the registration branch (project id from the JSON; missing file already covered by Task 1's test).
- [ ] **Step 4: Run FCM and registration tests.** Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat(api): FCM push sender"
```

### Task 4: Web Push sender

**Files:**
- Create: `backend/src/Chat.Api/Push/WebPushSender.cs`
- Modify: `backend/src/Chat.Api/Push/PushRegistration.cs`, `backend/src/Chat.Api/Chat.Api.csproj` (`WebPush`)
- Test: `backend/tests/Chat.Api.Tests/WebPushSenderTests.cs`

**Interfaces:**
- Produces: `WebPushSender(WebPushClient client, WebOptions options) : IPushSender` (`Platform = PushPlatform.Web`); a static `WebPushSender.Encode(string endpoint, string p256dh, string auth)` returns the base64url token (used by tests and documented for the client).
- Consumes: `FakeHandler` (Task 2) given to `new WebPushClient(new HttpClient(handler))`.

- [ ] **Step 1: Write failing tests** (generate VAPID keys and a subscription key pair in the test with `VapidHelper.GenerateVapidKeys()` and `ECDiffieHellman` P-256 + 16 random auth bytes):
  - `A_valid_subscription_is_posted_to_its_endpoint_with_vapid_and_an_encrypted_body` (headers `Authorization: vapid …`, `Content-Encoding: aes128gcm`, `TTL`; body is not plaintext).
  - `Result_mapping`: 201 → `Sent`; 404 and 410 → `InvalidToken`; 429 and 500 → throws.
  - `An_undecodable_token_is_InvalidToken`: `"not base64!"`; valid base64url of `{"foo":1}`; valid JSON with an `http://` endpoint (reject non-https endpoints as invalid, so a token cannot aim the server at an internal address).
  - `Encode_then_decode_round_trips`.
- [ ] **Step 2: Run to verify they fail.** Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter WebPushSenderTests`. Expected: compile error.
- [ ] **Step 3: Implement** the sender and the registration branch.
- [ ] **Step 4: Run the full suite.** Run: `cd backend && dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat(api): Web Push sender"
```

### Task 5: Docs

**Files:**
- Modify: `docs/README.md` ("API: push": providers, the three config sections, the Web token format, and a plain sentence that delivery to real devices is untested), `CLAUDE.md` ("Next steps" and the "Done" paragraph), (`appsettings.json` stays unchanged: the keys are documented in the README).

- [ ] **Step 1: Edit the docs** as listed.
- [ ] **Step 2: Run** `cd backend && dotnet test` — Expected: PASS.
- [ ] **Step 3: Commit**

```bash
git add docs CLAUDE.md backend
git commit -m "docs: push provider configuration"
```
