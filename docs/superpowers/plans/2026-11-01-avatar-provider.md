# Character Avatars from an Optional Avatar Provider — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Users pick a character avatar served by an optional Avatar Provider container (or upload one), and the web app shows a profile card with an avatar changer in the bottom-left corner.

**Architecture:** The API stores `users.avatar_preset_key` and proxies `GET /avatars/{key}` to a static provider (`catalog.json` + files), with a 60 s catalog cache and no image cache; provider down is `503` and never clears a stored key. The web app fetches avatar bytes with the bearer token into object URLs and falls back to the initial.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, EF Core (Npgsql), xUnit; Next.js App Router, React 19, Tailwind, Vitest + Testing Library; nginx:alpine.

**Spec:** `docs/superpowers/specs/2026-11-01-avatar-provider-design.md`

## Global Constraints

- Preset keys match `^[a-z0-9-]{1,64}$`; column `users.avatar_preset_key varchar(64) NULL`; check `ck_users_one_avatar`.
- Error codes: `avatar_provider_unavailable` 503, `avatar_not_found` 404, `avatar_provider_invalid` 502.
- Settings `AvatarProvider`: `BaseUrl` (empty = off), `TimeoutSeconds` 2, `CatalogCacheSeconds` 60, `MaxBytes` 2 MiB.
- Image responses: `Cache-Control: private, max-age=300`, `X-Content-Type-Options: nosniff`,
  `Content-Security-Policy: default-src 'none'; sandbox`, `Content-Disposition: inline`, ETag = quoted lowercase hex SHA-256.
- The API never depends on the provider at start-up (`depends_on` excludes it; no health check on it).
- The game theme stays out of API names. UI copy is English.
- Provider port `127.0.0.1:18090`; compose service name `avatar-provider`.

## Review Focus

- Provider answers `200` with HTML or a broken catalog (not JSON, missing `avatars`): treat as unavailable (`503`), never crash — test in Task 2.
- Catalog lists a key that does not match the pattern (or a file with `../`): skip that entry — test in Task 2.
- Provider is slow (longer than `TimeoutSeconds`): `503` rather than a hung request — test in Task 2.
- A user whose stored key left the catalog: profile still returns the key, image is `404`, picking another works — test in Task 3.
- Signing out and in as someone else in the same tab must not show the previous user's cached images — test in Task 5.

---

### Task 1: Schema — `avatar_preset_key` and the one-avatar check

**Files:**
- Modify: `backend/src/Chat.Data/Entities/Users.cs` (add `public string? AvatarPresetKey { get; set; }` after `AvatarMediaId`)
- Modify: `backend/src/Chat.Data/Configurations/UserConfigurations.cs` (`HasMaxLength(64)`, `ToTable("users", t => t.HasCheckConstraint("ck_users_one_avatar", "avatar_media_id IS NULL OR avatar_preset_key IS NULL"))`)
- Create: migration `AddUserAvatarPreset` under `backend/src/Chat.Data/Migrations/`
- Modify: `backend/tests/Chat.Data.Tests/ErdSchemaTests.cs` (users column list gains `"avatar_preset_key varchar?"` in the type notation the file already uses for `varchar`; if the file has a check-constraint list, add `ck_users_one_avatar`)
- Modify: `backend/tests/Chat.Data.Tests/SchemaBehaviourTests.cs`
- Modify: `docs/diagrams/04-erd.drawio` (row `avatar_preset_key` / `varchar(64) NULL` under `avatar_media_id`, and a note "CHECK: avatar_media_id or avatar_preset_key, not both"), `docs/README.md` (D1 row: "avatar choice (an upload or a preset key)"; ERD design note "**One avatar.** …catalog belongs to the Avatar Provider, so there is no table for it")

**Interfaces:**
- Produces: `User.AvatarPresetKey : string?`

- [ ] **Step 1: Write the failing tests** — in `ErdSchemaTests` the expected users columns include `avatar_preset_key`; in `SchemaBehaviourTests` add `A_user_cannot_have_an_uploaded_and_a_preset_avatar_at_once`: insert a user with both `avatar_media_id` (a real media row) and `avatar_preset_key = 'x'`, assert `PostgresException` with `ConstraintName == "ck_users_one_avatar"`.
- [ ] **Step 2: Run** `cd backend && dotnet test tests/Chat.Data.Tests` — expect both to FAIL.
- [ ] **Step 3: Implement** the entity and configuration, then `dotnet ef migrations add AddUserAvatarPreset --project src/Chat.Data --output-dir Migrations`.
- [ ] **Step 4: Run** `dotnet test tests/Chat.Data.Tests` — PASS.
- [ ] **Step 5: ERD and README** as listed under Files.
- [ ] **Step 6: Commit** `feat(data): users.avatar_preset_key with a one-avatar check`.

### Task 2: Avatar Provider client and `GET /avatars`, `GET /avatars/{key}`

**Files:**
- Create: `backend/src/Chat.Api/Avatars/AvatarProviderOptions.cs` (section `AvatarProvider`, defaults from Global Constraints)
- Create: `backend/src/Chat.Api/Avatars/AvatarCatalog.cs`
- Create: `backend/src/Chat.Api/Avatars/AvatarEndpoints.cs`
- Modify: `backend/src/Chat.Api/Auth/AuthError.cs` (three errors)
- Modify: `backend/src/Chat.Api/Program.cs` (options, `AddHttpClient(AvatarCatalog.HttpClientName)` with `BaseAddress` and `Timeout`, singleton `AvatarCatalog`, `app.MapAvatars()`, a start-up log line "AvatarProvider:BaseUrl is not configured: character avatars are off." when empty)
- Modify: `backend/src/Chat.Api/Files/MediaEndpoints.cs` (extract the shared header block of `Serve` so avatars reuse it; avatars pass `max-age=300`)
- Modify: `backend/src/Chat.Api/appsettings.Development.json` (`"AvatarProvider": { "BaseUrl": "http://localhost:18090" }`)
- Create: `backend/tests/Chat.Api.Tests/FakeAvatarProvider.cs`
- Modify: `backend/tests/Chat.Api.Tests/ApiFixture.cs` (`public FakeAvatarProvider Avatars { get; } = new();`, setting `AvatarProvider:BaseUrl = http://avatars.test`, primary handler of the named client = `Avatars`)
- Create: `backend/tests/Chat.Api.Tests/AvatarTests.cs`

**Interfaces:**
- Produces:
  - `AuthError.AvatarProviderUnavailable` (503, `avatar_provider_unavailable`, "Character avatars are unavailable right now."), `AuthError.AvatarNotFound` (404, `avatar_not_found`, "No such avatar."), `AuthError.AvatarProviderInvalid` (502, `avatar_provider_invalid`, "The avatar provider sent something that is not an image.")
  - `public sealed class AvatarCatalog` with `const string HttpClientName = "avatar-provider"`, `bool Enabled`, `Task<Result<IReadOnlyList<string>>> KeysAsync(CancellationToken)`, `Task<AuthError?> CheckKeyAsync(string key, CancellationToken)`, `Task<Result<MediaDownload>> OpenAsync(string key, CancellationToken)`, `static bool IsValidKey(string? key)`
  - `FakeAvatarProvider : HttpMessageHandler` with `bool Down`, `TimeSpan Delay`, `int CatalogRequests`, `void Set(string key, byte[] bytes)`, `void SetRawCatalog(string body, string contentType)`, `void Clear()`
- Consumes: `MediaSniffer.Detect`, `MediaDownload` from `Chat.Api.Files`

- [ ] **Step 1: Write the failing tests** in `AvatarTests` (fixture `ApiFixture`, call `api.Avatars.Clear()` at the start of each test):
  - `The_catalog_lists_the_providers_keys` — `Set("01-a", Png())`, `Set("02-b", WebPLossy())`; `GET /avatars` → 200, `avatars[*].key == ["01-a","02-b"]`.
  - `The_catalog_is_cached_for_a_minute` — two `GET /avatars` → `CatalogRequests == 1`; `api.Time.Advance(61 s)`; third call → 2.
  - `An_avatar_image_is_proxied_with_locked_down_headers` — 200, content type `image/png`, the four headers and `Cache-Control` from Global Constraints, ETag present; same request with `If-None-Match` = that ETag → 304.
  - `A_key_outside_the_catalog_or_malformed_is_avatar_not_found` — `"zz-none"`, `"Bad_Key"`, `"..%2Fsecret"` → 404 `avatar_not_found`.
  - `A_provider_that_is_down_gives_503_everywhere` — `Down = true` (after the cache has expired): list and image → 503 `avatar_provider_unavailable`.
  - `A_provider_that_answers_garbage_is_unavailable` — `SetRawCatalog("<html>", "text/html")` → list 503; `SetRawCatalog("{\"avatars\":[{\"key\":\"../x\",\"file\":\"../x.webp\"},{\"key\":\"ok\",\"file\":\"ok.webp\"}]}")` → only `ok`.
  - `A_slow_provider_times_out` — `Delay = 3 s` with `AvatarProvider:TimeoutSeconds` at its default 2 → 503 (use a real-time delay; keep it to one test).
  - `Bytes_that_are_not_an_image_are_avatar_provider_invalid` — `Set("t", Text())` → image 502 `avatar_provider_invalid`.
  - `Without_a_token_avatars_are_401` — anonymous `GET /avatars` → 401.
  - `Without_a_base_url_the_feature_is_off` — an `AvatarCatalog` built directly with empty `BaseUrl`: `Enabled` false, `KeysAsync` empty, `CheckKeyAsync` → `AvatarProviderUnavailable`.
- [ ] **Step 2: Run** `dotnet test tests/Chat.Api.Tests --filter AvatarTests` — FAIL (routes missing).
- [ ] **Step 3: Implement.** `AvatarCatalog` keeps `(DateTimeOffset fetchedAt, Dictionary<string,string> keyToFile)` under a lock, refreshes from `catalog.json` when older than `CatalogCacheSeconds` on the injected `TimeProvider`, caches only successes, drops entries whose key fails `IsValidKey` or whose file contains `/` or `..`. Any `HttpRequestException`, `TaskCanceledException` (not the caller's token), non-2xx or JSON error → `AvatarProviderUnavailable`. `OpenAsync` reads at most `MaxBytes + 1` bytes; over the limit or `MediaSniffer.Detect` null / not image → `AvatarProviderInvalid`. Not `Enabled`: `KeysAsync` returns an empty list, `CheckKeyAsync`/`OpenAsync` return `AvatarProviderUnavailable`. Endpoints `GET /avatars` → `{ avatars: [{ key }] }` and `GET /avatars/{key}`, both `RequireAuthorization()`.
- [ ] **Step 4: Run** the filter again — PASS. Then the full `dotnet test` — PASS.
- [ ] **Step 5: Commit** `feat(api): proxy character avatars from an optional avatar provider`.

### Task 3: Picking a preset, and `avatarPresetKey` in every profile shape

**Files:**
- Modify: `backend/src/Chat.Api/Files/MediaEndpoints.cs` (`AvatarRequest(Guid? MediaId, string? PresetKey)`; `PUT /users/me/avatar` validation: exactly one → else `ProblemResults.Invalid` on `mediaId`/`presetKey`)
- Modify: `backend/src/Chat.Api/Files/MediaService.cs` (`SetUserAvatarAsync(Guid userId, Guid? mediaId, string? presetKey, CancellationToken ct)`; check via `AvatarCatalog.CheckKeyAsync`; one `ExecuteUpdateAsync` setting both columns)
- Modify: `backend/src/Chat.Api/Auth/AuthRequests.cs` (`UserSummary(..., string Role, Guid? AvatarMediaId, string? AvatarPresetKey)`), `Auth/SignInService.cs` (`Summary`)
- Modify: `backend/src/Chat.Api/Users/UserRequests.cs` + `UserService.cs` (`UserProfile` gains `string? AvatarPresetKey` last)
- Modify: `backend/src/Chat.Api/Conversations/ConversationRequests.cs` + `ConversationReader.cs` (`PersonBrief`, `MemberSummary` gain `string? AvatarPresetKey` last)
- Modify: `backend/tests/Chat.Api.Tests/AvatarTests.cs`

**Interfaces:**
- Consumes: `AvatarCatalog.CheckKeyAsync` (Task 2), `User.AvatarPresetKey` (Task 1)
- Produces: JSON fields `avatarPresetKey` on `/auth/me`, login `user`, `PATCH /users/me`, `GET /users/{id}`, search, `peer`, members; `avatarMediaId` on `UserSummary`.

- [ ] **Step 1: Write the failing tests** in `AvatarTests`:
  - `Picking_a_preset_shows_in_every_profile_shape` — Alice and Bob in a direct chat; Alice `PUT { presetKey: "01-a" }` → 204; `/auth/me` has `avatarPresetKey == "01-a"` and `avatarMediaId` null; Bob sees it in `GET /users/{alice}`, in `GET /conversations` `peer`, and in `GET /conversations/{id}` members.
  - `A_preset_and_an_upload_replace_each_other` — set upload → preset → `avatarMediaId` null; set upload again → `avatarPresetKey` null; `DELETE` → both null.
  - `Picking_needs_the_provider_and_a_known_key` — unknown key → 404 `avatar_not_found`; `Down = true` → 503; neither field or both fields → 400 `validation_failed`.
  - `A_stored_preset_survives_the_provider_going_away` — pick `"01-a"`, then `Down = true`, advance 61 s: `/auth/me` still `"01-a"`, image 503; then `Clear()` and `Set("02-b", …)` (key left the catalog): `/auth/me` still `"01-a"`, image 404, picking `"02-b"` works.
- [ ] **Step 2: Run** `--filter AvatarTests` — the four FAIL.
- [ ] **Step 3: Implement** as listed under Files. Fix the existing callers of `SetUserAvatarAsync` (DELETE passes `null, null`).
- [ ] **Step 4: Run** the full `dotnet test` — PASS (existing `MediaUploadTests` avatar tests must still pass).
- [ ] **Step 5: Commit** `feat(api): pick a preset avatar; profiles carry avatarPresetKey`.

### Task 4: The `avatar-provider` container

**Files:**
- Move: `frontend/public/avatars/*` → `avatar-provider/avatars/` (`git mv`, keeps `SOURCES.md`)
- Create: `avatar-provider/Dockerfile`, `avatar-provider/nginx.conf`, `avatar-provider/README.md`
- Modify: `docker-compose.yml` (service `avatar-provider`, `build: ./avatar-provider`, `127.0.0.1:18090:80`, healthcheck `wget -qO- http://127.0.0.1/catalog.json`), `compose.app.yml` (`AvatarProvider__BaseUrl: http://avatar-provider` on `api`; no `depends_on`)

**Interfaces:**
- Produces: the provider contract from the spec on port 80 of the container.

- [ ] **Step 1: Dockerfile** — `FROM nginx:alpine`; copy `avatars/*.webp` to `/usr/share/nginx/html/`; a `RUN` that writes `catalog.json` from the file names (`{"avatars":[{"key":"<name without .webp>","file":"<name>"},…]}`, sorted); copy `nginx.conf` that serves only `GET`, sets `application/json` for the catalog and turns `autoindex` off.
- [ ] **Step 2: Verify** — `docker compose up -d --build avatar-provider --wait`; `curl -s 127.0.0.1:18090/catalog.json | head -c 200` shows 37 entries' start; `curl -sI 127.0.0.1:18090/01-cap-wink.webp` is 200 `image/webp`.
- [ ] **Step 3: Verify end to end** — run the API (`dotnet run --project src/Chat.Api`), sign in a test user with curl, `GET /avatars` lists 37 keys, `GET /avatars/01-cap-wink` 200; `docker compose stop avatar-provider` → 503; start again → 200.
- [ ] **Step 4: Commit** `feat(docker): avatar-provider container serving the character pack`.

### Task 5: Web — API calls, authed images, `Avatar`, conversation list

**Files:**
- Modify: `frontend/src/lib/api.ts`, `frontend/src/lib/api.test.ts`
- Modify: `frontend/src/lib/types.ts`
- Modify: `frontend/src/lib/auth.tsx`
- Create: `frontend/src/lib/authed-image.tsx`, `frontend/src/lib/authed-image.test.tsx`
- Create: `frontend/src/components/Avatar.tsx`, `frontend/src/components/Avatar.test.tsx`
- Modify: `frontend/src/components/ConversationList.tsx`, `ConversationList.test.tsx`

**Interfaces:**
- Produces:
  - `User` gains `avatarMediaId?: string | null; avatarPresetKey?: string | null`; `PersonBrief` gains `avatarPresetKey?: string | null`.
  - `Api.uploadMedia(file: Blob): Promise<{ id: string; mimeType: string }>`, `Api.setAvatar(choice: { mediaId: string } | { presetKey: string }): Promise<void>`, `Api.clearAvatar(): Promise<void>`, `Api.avatarPresets(): Promise<string[]>`, `Api.getBlob(path: string): Promise<Blob>`
  - `AuthValue.reloadUser(): Promise<void>`
  - `avatarPath(who: { avatarMediaId?: string | null; avatarPresetKey?: string | null }): string | null` and `useAuthedImage(path: string | null): string | null` in `authed-image.tsx`; `clearAuthedImages(): void` (revokes every object URL)
  - `<Avatar name: string; mediaId?: string | null; presetKey?: string | null; size?: "sm" | "md" | "lg" />` — an `img` with `alt=""` when the image loaded, else the initial in the current circle style; `aria-hidden` either way.
- [ ] **Step 1: Write the failing tests:**
  - `api.test.ts`: `uploadMedia` sends `POST /media` with a `FormData` body holding `file`, the bearer header and **no** `Content-Type` header; `getBlob` on 401 refreshes once and retries, returning the blob; `setAvatar({ presetKey: "x" })` sends `PUT /users/me/avatar` `{"presetKey":"x"}`.
  - `authed-image.test.tsx`: `avatarPath` prefers `/media/{id}`, then `/avatars/{key}`, else `null`; `useAuthedImage` returns an object URL after the blob resolves, `null` when `getBlob` rejects, fetches a path once for two hooks; after `clearAuthedImages()` the next render fetches again (the sign-out case in Review Focus).
  - `Avatar.test.tsx`: shows the initial when there is no avatar; shows the initial when the image fails; shows an `img` when it loads.
  - `ConversationList.test.tsx`: a direct chat whose peer has `avatarPresetKey` requests `/avatars/{key}` (mock `useAuthedImage`'s fetcher through the auth context).
- [ ] **Step 2: Run** `cd frontend && npm test` — the new tests FAIL.
- [ ] **Step 3: Implement.** `uploadMedia` and `getBlob` reuse the refresh-on-401 path of `request` (refactor `send` to accept a `FormData` body and a `blob` response mode rather than duplicating it). `useAuthedImage` reads `api` from `useAuth()`; the module cache is `Map<string, Promise<string | null>>`. `logout` in `auth.tsx` calls `clearAuthedImages()`; `reloadUser` sets `user` from `api.me()`.
- [ ] **Step 4: Run** `npm test && npm run lint` — PASS.
- [ ] **Step 5: Commit** `feat(web): avatars from uploads and the character pack`.

### Task 6: Web — the profile card

**Files:**
- Create: `frontend/src/components/ProfileCard.tsx`, `frontend/src/components/ProfileCard.test.tsx`
- Modify: `frontend/src/app/(app)/layout.tsx` (header keeps only "Chats"; `<ProfileCard />` at the bottom of the `aside`)

**Interfaces:**
- Consumes: `useAuth()` (`user`, `api`, `reloadUser`, `logout`), `Avatar`, `Api.avatarPresets/setAvatar/clearAvatar/uploadMedia`, `ApiError`, `errorMessage` helpers in `lib/errors.ts`
- Produces: `<ProfileCard />` (no props)

- [ ] **Step 1: Write the failing tests** (render inside an `AuthContext.Provider` with a fake `AuthValue`):
  - opens a dialog named "Profile" on clicking the card button showing display name, `@username` and email; Escape closes it and focus returns to the button.
  - lists presets as buttons named by key (e.g. "Use 01-a"), the current one has `aria-pressed="true"`; clicking another calls `setAvatar({ presetKey })` then `reloadUser`.
  - when `avatarPresets` rejects with `ApiError(503, "avatar_provider_unavailable")` it shows "Character avatars are offline right now." and no preset buttons.
  - choosing a file in "Upload a picture" calls `uploadMedia` then `setAvatar({ mediaId })`; an `ApiError(413, "file_too_large")` shows its message in the dialog.
  - "Remove avatar" appears only when the user has an avatar and calls `clearAvatar`.
  - "Sign out" calls `logout`.
- [ ] **Step 2: Run** `npm test` — FAIL.
- [ ] **Step 3: Implement** with the comic style of `Button` and the sidebar (ink border, hard shadow). The popover opens upwards from the card, `max-h` with scroll for the grid. Presets load when the dialog opens.
- [ ] **Step 4: Run** `npm test && npm run lint && npm run build` — PASS.
- [ ] **Step 5: Verify in the browser** — infra plus provider up, API and `npm run dev`; sign in, open the card, pick a character, see it in the card and (from a second account) in the conversation list; stop the provider and reload: initials, the dialog says offline; start it: avatars return.
- [ ] **Step 6: Commit** `feat(web): profile card with an avatar picker`.

### Task 7: Diagrams and docs

**Files:**
- Modify: `docs/diagrams/01-context-dfd-level-0.drawio`, `docs/diagrams/02-dfd-level-1.drawio` (flows and labels from the spec's "Diagrams and docs"; the provider sits right of 5.0 below the store column so no flow crosses a store)
- Regenerate: the PNGs of 01, 02 and 04 with `node scripts/export-diagrams.mjs`
- Modify: `docs/README.md` (external entities table; "API: profile" bullet; "API: media" route table and an **Avatar Provider** bullet: contract, settings, 503 behaviour, cache, byte check), `docs/pitfalls.md` (two bullets), `CLAUDE.md` (infrastructure line, Next steps: "avatars (own)" done), `frontend/README.md`

- [ ] **Step 1: Edit the diagrams**, export, look at the PNGs.
- [ ] **Step 2: Edit the docs.**
- [ ] **Step 3: Verify** — `cd backend && dotnet test` and `cd frontend && npm test && npm run lint && npm run build` all PASS.
- [ ] **Step 4: Commit** `docs: avatar provider in the DFDs, README and pitfalls`.
