# Character avatars from an optional Avatar Provider, and the profile card — design

Date: 2026-11-01

## Goal

1. A user can pick a character avatar from a pack, besides uploading a picture.
2. The pack lives in its own container, the **Avatar Provider**. The API does not need it to start or run: it stores which
   avatar a user picked (metadata) and fetches the image from the provider when asked. While the provider is down the
   avatars do not show; when it comes back they show again, and nobody's choice is lost.
3. The web app gets a profile card in the bottom-left corner of the conversation list. Clicking it shows the profile and
   lets the user change their avatar.

Out of scope: group avatars from the pack, more profile editing (display name, password), contacts.

## Decisions

- **The API proxies the images** (`GET /avatars/{key}`). The browser never talks to the provider, so its address stays
  internal and the API checks every key.
- **No server-side cache of images.** Each request goes to the provider; down means `503`. Browsers keep an image for five
  minutes (`Cache-Control: private, max-age=300`) and revalidate with the ETag. The catalog is cached in memory for 60 seconds
  after a successful fetch; a failed fetch is not cached.
- **Picking needs the provider.** `PUT /users/me/avatar { presetKey }` checks the key against the catalog: provider down is
  `503 avatar_provider_unavailable`, a key not in the catalog is `404 avatar_not_found`. A stored key is never cleared because
  the provider is down or the key left the catalog.
- **Bearer token required** for `GET /avatars` and `GET /avatars/{key}`, like `/media`. The web app fetches the bytes with
  its token and shows them through an object URL.
- **One avatar per user.** `users.avatar_media_id` (an upload) and the new `users.avatar_preset_key` are mutually exclusive;
  setting one clears the other.

## Avatar Provider (external entity)

A static file server. The contract is all the API relies on, so any server that keeps it can replace this one:

- `GET /catalog.json` → `{ "avatars": [{ "key": "07-curly-blond", "file": "07-curly-blond.webp" }] }`
- `GET /{file}` → the image bytes.

Keys match `^[a-z0-9-]{1,64}$`. The implementation is `avatar-provider/` at the repository root: `nginx:alpine` serving the
`.webp` files, with `catalog.json` generated from the file names at image build time, so adding a file needs no catalog edit.
The 37 files and `SOURCES.md` move there from `frontend/public/avatars/` (the art belongs to its creators and stays a
development-only pack). It runs in `docker-compose.yml` on `127.0.0.1:18090` and in `compose.app.yml` as `avatar-provider`;
the API does not depend on it.

## Data model

- `users.avatar_preset_key varchar(64) NULL`.
- `CHECK ck_users_one_avatar (avatar_media_id IS NULL OR avatar_preset_key IS NULL)`.
- No catalog table: the catalog belongs to the provider.
- Migration `AddUserAvatarPreset`. ERD, README (D1 row and design notes) and `ErdSchemaTests` change together; a schema test
  checks the constraint.

## API

| Route | Does |
|-------|------|
| `GET /avatars` | `{ avatars: [{ key }] }` from the (cached) catalog. Provider down: `503 avatar_provider_unavailable`. Not configured: `{ avatars: [] }` |
| `GET /avatars/{key}` | The image, proxied. Bad key or not in the catalog: `404 avatar_not_found`. Provider down or slow: `503`. Bytes that are not PNG, JPEG, GIF or WebP, or larger than `MaxBytes`: `502 avatar_provider_invalid` |
| `PUT /users/me/avatar` | Exactly one of `{ mediaId }` or `{ presetKey }`, else `400 validation_failed`. `presetKey` checked as above; sets it and clears `avatar_media_id`. `mediaId` as today, and clears `avatar_preset_key`. `204` |
| `DELETE /users/me/avatar` | Clears both |

- Image responses carry the same locked-down headers as `/media` (`nosniff`, `default-src 'none'; sandbox`, `inline`), the
  type the sniffer found (never the provider's header), an ETag (SHA-256 of the bytes) and `304` on `If-None-Match`.
- `avatarPresetKey` appears wherever a user's `avatarMediaId` does: `UserProfile`, `PersonBrief` (peer), `MemberSummary`.
  `UserSummary` (login, `/auth/me`, `PATCH /users/me`) gains both `avatarMediaId` and `avatarPresetKey`, so the app knows
  its own avatar.
- Settings `AvatarProvider`: `BaseUrl` (empty turns the feature off), `TimeoutSeconds` (2), `CatalogCacheSeconds` (60),
  `MaxBytes` (2 MiB). Development: `http://localhost:18090`; `compose.app.yml`: `http://avatar-provider`.
- Code: `Chat.Api/Avatars/` holds `AvatarProviderOptions`, `AvatarCatalog` (singleton: HTTP client, catalog cache on the
  injected `TimeProvider`, image fetch) and `AvatarEndpoints`. `MediaService.SetUserAvatarAsync` takes either kind.

## Web app

- Types: `User` gains `avatarMediaId` and `avatarPresetKey`; `PersonBrief` gains `avatarPresetKey`.
- `api.ts`: `uploadMedia(file)` (multipart), `setAvatar({ mediaId } | { presetKey })`, `clearAvatar()`, `avatarPresets()`,
  `getBlob(path)`; all go through the existing refresh-on-401 path.
- `auth.tsx`: `reloadUser()` re-reads `/auth/me` after a change.
- `useAuthedImage(path)`: fetches with the token, returns an object URL or `null` on any failure; one shared cache per path
  for the session, cleared on sign-out.
- `Avatar`: picks `/media/{id}` or `/avatars/{key}`, falls back to the initial (today's look) when there is none or it fails.
  That fallback is how "provider down hides avatars" shows.
- `ConversationList` uses `Avatar` (the peer for a direct chat, the group's own avatar for a group).
- `ProfileCard` at the bottom of the sidebar: avatar, display name, `@username`. Clicking opens a popover
  (`role="dialog"`, closes on Escape or an outside click, focus returns to the button) with a large avatar, display name,
  username and email; **Characters** (a grid from `GET /avatars`, the current one marked; `503` shows "Character avatars are
  offline right now."); **Upload a picture**; **Remove avatar** (only when there is one); **Sign out**, which moves here from
  the header.
- `frontend/public/avatars/` is removed.

## Errors

`avatar_provider_unavailable` (503), `avatar_not_found` (404), `avatar_provider_invalid` (502) join the API's error codes.
The web app shows provider and upload errors inside the popover; an avatar image that fails just shows the initial.

## Diagrams and docs

- `01-context`: Avatar Provider as an external entity; flows `catalog & image requests` (out) and
  `avatar catalog, avatar images` (in); the User flows gain `avatar choice` / `avatar catalog, avatar images`.
- `02-dfd-level-1`: the same two flows on 5.0; User ↔ 5.0 labels as above; 5.0 → D1 `avatar id` becomes
  `avatar id or preset key`. 5.0 has no level 2.
- `04-erd`: the new column and the check.
- README: external entities, D1 row, ERD notes, "API: profile", "API: media" (routes and an "Avatar Provider" part).
  `pitfalls.md`: never clear a stored key because the provider failed; the two avatar columns exclude each other.
  CLAUDE.md infrastructure line; `frontend/README.md`.

## Testing

- API, with a fake provider (an `HttpMessageHandler` the fixture controls): catalog listing and its cache; provider down
  (list, image, pick all `503`, and the stored key still in `/auth/me`); unknown or malformed key; bytes that are not an
  image (`502`); `PUT` with both or neither field; switching preset ↔ upload clears the other; ETag and `304`;
  `avatarPresetKey` in the profile, peer and member list; not configured gives an empty list.
- Schema: `ErdSchemaTests` column list; the check rejects both columns set.
- Web (vitest): `ProfileCard` open and close, picking a preset calls `setAvatar` then `reloadUser`, upload success and error,
  provider offline message; `Avatar` falls back to the initial; `api` `uploadMedia` and `getBlob` (with a 401 refresh).
