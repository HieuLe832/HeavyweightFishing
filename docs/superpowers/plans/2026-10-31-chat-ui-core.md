# Chat UI, Slice 1 (Core Chat Loop) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Register, verify, log in, list conversations, open one, read history, send and receive text in real time, in an original ocean-and-fishing theme.

**Architecture:** A client-side Next.js app. Framework-free modules (`api.ts`, `chat-connection.ts`, `chat-state.ts`) hold all logic and are unit-tested; thin React providers and components sit on top. The backend gets one change: a CORS allowlist for REST and the hub.

**Tech Stack:** Next.js 16 (App Router, Tailwind 4, already scaffolded in `frontend/`), TypeScript, `@microsoft/signalr`, Vitest + Testing Library + jsdom. Backend: ASP.NET Core CORS.

**Spec:** `docs/superpowers/specs/2026-10-31-chat-ui-core-design.md`

## Global Constraints

- Out of scope: contacts, creating chats, attachments, avatars, typing, presence, reactions, edit/delete UI, mute, reports, password reset, settings.
- Access token in memory only; refresh token in `localStorage`. Concurrent 401s share one `POST /auth/refresh`. No `dangerouslySetInnerHTML`.
- API base URL from `NEXT_PUBLIC_API_URL` (default `http://localhost:5000` in `config.ts`; check `backend/src/Chat.Api/Properties/launchSettings.json` for the real dev port and use it).
- Errors shown by `code`, never raw server text. Codes in play: `invalid_credentials`, `email_not_verified`, `account_banned`, `email_taken`, `username_taken`, `invalid_code`, `validation_failed`, `rate_limited`, `blocked`, `user_not_found`, `session_ended`, `conversation_not_found`.
- Hub: `/hubs/chat` with `?access_token=`; commands `SendMessage(conversationId, clientMsgId, body, replyToId?)`, `MarkRead(conversationId, upToSeq)`; events `message.created|edited|deleted` (a message), `receipt.updated`. Message shape: `{ id, conversationId, seq, senderId?, clientMsgId, type: "text"|"media"|"system", body?, replyToId?, createdAt, editedAt?, deletedAt?, reactions[], attachments[] }`. Conversation summary: `{ id, type: "direct"|"group", title?, peer?: {id, username, displayName, avatarMediaId?}, lastSeq, unread, mutedUntil?, role, updatedAt, avatarMediaId? }`. History: `GET /conversations/{id}/messages?before=&after=&limit=` → `{ messages, hasMore }`, ascending `seq`. Login/refresh → `{ accessToken, expiresIn, refreshToken, user: {id, username, email, displayName, role} }`.
- Body 1-4000 characters after trim. `prefers-reduced-motion` stops animations. Text contrast WCAG AA.
- Next.js 16 differs from older versions and `next.config.ts` sets `cacheComponents` and `partialPrefetching`. Before writing routing/layout code, read the Next docs shipped in `frontend/node_modules/next/dist/docs/` if present (else the installed package types) rather than relying on memory. Pages that use browser APIs are client components.
- Never push. Commits carry no `Co-Authored-By` or generated-by lines. Identity `hieu` / `lehoanghieu02206@gmail.com`; if `git config` differs, stop and tell the user.

## Review Focus

- A refresh token that was already used (second tab): the client logs out cleanly instead of looping. Test in Task 3.
- Two tabs open: both receive the same events and neither duplicates its own sent message. Test in Task 5.
- A very long unbroken string (4000 chars, no spaces) and a message with only newlines/emoji: the bubble wraps and the trimmed-empty message is not sent. Test in Task 7.
- The socket drops mid-send: the message shows `failed` with Retry and the retry reuses the `clientMsgId`. Test in Tasks 5 and 7.
- A message from a conversation not yet in the list (new chat made elsewhere): the list refetches instead of dropping it. Test in Task 5.

---

### Task 1: CORS in the API

**Files:**
- Modify: `backend/src/Chat.Api/Program.cs`, `backend/src/Chat.Api/appsettings.Development.json`
- Create: `backend/src/Chat.Api/CorsOptions.cs` (`public class CorsOptions { const string Section = "Cors"; string[] AllowedOrigins = []; }`)
- Test: `backend/tests/Chat.Api.Tests/CorsTests.cs`

**Interfaces:** Produces policy name `"web"`, applied with `app.UseCors("web")` before authentication.

- [ ] **Step 1: Write failing tests** (`ApiFixture` with `SettingsAsync` override giving `Cors:AllowedOrigins:0 = http://localhost:3000`; a small `CorsApiFixture : ApiFixture` subclass):
  - `A_preflight_from_an_allowed_origin_gets_the_cors_headers` (`OPTIONS /conversations` with `Origin`, `Access-Control-Request-Method: GET`, `Access-Control-Request-Headers: authorization`): 204, `Access-Control-Allow-Origin: http://localhost:3000`, allowed headers include authorization.
  - `A_hub_negotiate_from_an_allowed_origin_gets_the_header` (`POST /hubs/chat/negotiate?negotiateVersion=1` with Origin and a valid token) and it includes `Access-Control-Allow-Origin`.
  - `Another_origin_gets_no_allow_origin_header`.
  - `With_no_allowed_origins_nobody_gets_the_header` (default fixture).
- [ ] **Step 2: Run to verify they fail.** Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter CorsTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** `AddCors` with policy `web`: `WithOrigins(options.AllowedOrigins)`, `AllowAnyHeader`, `AllowAnyMethod`, no credentials. SignalR's negotiate needs `x-signalr-user-agent`, covered by `AllowAnyHeader`. Development settings: `"Cors": { "AllowedOrigins": ["http://localhost:3000"] }`. Place `UseCors` before `UseAuthentication`.
- [ ] **Step 4: Run the full suite.** Run: `cd backend && dotnet test`. Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat(api): CORS allowlist for the web client"
```

### Task 2: Frontend tooling and theme tokens

**Files:**
- Modify: `frontend/package.json` (scripts `test`, deps), `frontend/src/app/globals.css`, `frontend/src/app/layout.tsx`, `frontend/src/app/page.tsx` (becomes a redirect shell; replaced in Task 7)
- Create: `frontend/vitest.config.ts`, `frontend/vitest.setup.ts`, `frontend/.env.example` (`NEXT_PUBLIC_API_URL=`), `frontend/src/lib/config.ts`, `frontend/src/components/{Button,Field,WaveBackground,Bobber}.tsx`
- Test: `frontend/src/components/Button.test.tsx`

**Interfaces:**
- Produces: `config.apiUrl: string`; `<Button variant="primary"|"ghost" busy?>`, `<Field label error ...inputProps>`, `<Bobber count>` (renders nothing at 0, `99+` over 99), `<WaveBackground />`.
- Produces CSS variables `--water-deep`, `--water-mid`, `--sand`, `--cream`, `--coral`, `--gold`, `--ink` exposed through Tailwind `@theme` as `bg-water-deep` etc.

- [ ] **Step 1: Install tooling.** In `frontend/`: `npm i @microsoft/signalr` and `npm i -D vitest @vitejs/plugin-react jsdom @testing-library/react @testing-library/user-event @testing-library/jest-dom`. Add `"test": "vitest run"`.
- [ ] **Step 2: Write failing tests** `Button.test.tsx`: `renders its label and fires onClick`, `is disabled and not clickable while busy`, `Bobber renders nothing at 0 and 99+ above 99`.
- [ ] **Step 3: Run to verify they fail.** Run: `cd frontend && npm test`. Expected: FAIL (modules missing).
- [ ] **Step 4: Implement** the config, components and theme. Theme per spec: deep-teal gradient background, sand/cream cards, coral accent, gold primary buttons with a pressed offset shadow, a rounded display font via `next/font` for headings, wave motif drawn in inline SVG, `@media (prefers-reduced-motion: reduce)` disables the wave and bobber animation. Remove the dark-mode media query of the scaffold (the theme is one look). Set `metadata` title to "Câu Cá Vạn Cân Chat"; all copy is English, so `lang` stays `en`.
- [ ] **Step 5: Verify.** Run: `cd frontend && npm test && npm run lint && npm run build`. Expected: all pass.
- [ ] **Step 6: Commit**

```bash
git add frontend
git commit -m "feat(web): test tooling and ocean theme tokens"
```

### Task 3: API client with token refresh

**Files:**
- Create: `frontend/src/lib/api.ts`, `frontend/src/lib/token-store.ts`
- Test: `frontend/src/lib/api.test.ts`

**Interfaces:**
- Produces: `class ApiError extends Error { status: number; code: string; fields?: Record<string,string[]> }`.
- Produces: `interface TokenStore { getAccess(): string|null; setAccess(t: string|null): void; getRefresh(): string|null; setRefresh(t: string|null): void }` and `createLocalTokenStore(): TokenStore` (refresh in `localStorage` key `refreshToken`, access in memory).
- Produces: `createApi(opts: { baseUrl: string; tokens: TokenStore; fetch?: typeof fetch; onSessionEnded: () => void }): { request<T>(method: string, path: string, body?: unknown): Promise<T>; login(identifier, password): Promise<AuthResponse>; register(...); verifyEmail(email, code); resendVerification(email); logout(); me(); refresh(): Promise<void> }` plus types `AuthResponse`, `User` named as in Global Constraints.

- [ ] **Step 1: Write failing tests** with an injected fake `fetch`:
  - `sends the bearer token and a JSON body`
  - `turns a problem+json response into ApiError with status and code` (and `fields` from `errors`)
  - `a non-JSON error body (HTML from a proxy) becomes ApiError code "unknown"`
  - `on 401 it refreshes once and retries the request with the new token`
  - `concurrent 401s trigger a single refresh call`
  - `when refresh fails with 401 it clears both tokens and calls onSessionEnded` and `a reused refresh token does not loop` (two requests, one refresh attempt each, no recursion)
  - `login stores both tokens`; `204 responses resolve to undefined`
- [ ] **Step 2: Run to verify they fail.** Run: `cd frontend && npx vitest run src/lib/api.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.** A shared in-flight refresh promise; `/auth/refresh`, `/auth/login`, `/auth/register` and `/auth/verify-email` are never retried on 401.
- [ ] **Step 4: Run to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat(web): typed API client with single-flight token refresh"
```

### Task 4: Chat connection (SignalR wrapper)

**Files:**
- Create: `frontend/src/lib/chat-connection.ts`, `frontend/src/lib/types.ts` (message and conversation types from Global Constraints)
- Test: `frontend/src/lib/chat-connection.test.ts`

**Interfaces:**
- Produces: `interface HubLike { start(): Promise<void>; stop(): Promise<void>; invoke<T>(method: string, ...args: unknown[]): Promise<T>; on(event: string, cb: (...args: any[]) => void): void; onreconnected(cb: () => void): void; onclose(cb: () => void): void }`.
- Produces: `createChatConnection(opts: { hubUrl: string; getAccessToken: () => string|null; hub?: HubLike }): ChatConnection` where `ChatConnection` has `start()`, `stop()`, `sendMessage(conversationId, clientMsgId, body): Promise<Message>`, `markRead(conversationId, upToSeq): Promise<void>`, `subscribe(handlers: { onMessageCreated(m), onMessageEdited(m), onMessageDeleted(m), onReconnected(), onStatus(s: "connected"|"reconnecting"|"disconnected") }): () => void`.
- The default hub is built from `HubConnectionBuilder` with `accessTokenFactory` and `withAutomaticReconnect()`; tests pass a fake `HubLike`.

- [ ] **Step 1: Write failing tests** with a fake hub:
  - `sendMessage invokes SendMessage with the three ids and body and returns the stored message`
  - `a hub error "…HubException: rate_limited" becomes ApiError with code rate_limited`
  - `events are forwarded to subscribers and unsubscribe stops them`
  - `reconnecting reports status and onReconnected fires after the hub reconnects`
  - `close reports disconnected`
- [ ] **Step 2: Run to verify they fail.** Run: `cd frontend && npx vitest run src/lib/chat-connection.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement.** Parse the code with `/HubException:\s*(\w+)\s*$/`.
- [ ] **Step 4: Run to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat(web): SignalR chat connection wrapper"
```

### Task 5: Chat state reducer

**Files:**
- Create: `frontend/src/lib/chat-state.ts`
- Test: `frontend/src/lib/chat-state.test.ts`

**Interfaces:**
- Consumes: `Message`, `ConversationSummary` from `types.ts` (Task 4).
- Produces: `ChatState { conversations: Record<string, ConversationSummary>; order: string[]; messages: Record<string, ChatMessage[]>; openId: string|null; status }` where `ChatMessage = Message & { local?: "sending"|"failed" }`.
- Produces: `type Action` = `conversationsLoaded(list)` | `conversationsAppended(list)` | `historyLoaded(conversationId, messages, mode: "latest"|"older"|"gap")` | `messageCreated(message, meId)` | `messageEdited(m)` | `messageDeleted(m)` | `sendStarted(conversationId, clientMsgId, body, meId)` | `sendAcked(message)` | `sendFailed(conversationId, clientMsgId)` | `opened(conversationId|null)` | `markedRead(conversationId)` | `status(s)`; `reducer(state, action): ChatState`; `initialState`; selectors `latestSeq(state, id)`, `needsConversationRefetch(state, message)`.
- Rules: messages sorted by `seq`, pending locals after the last known; merge by `id` or `clientMsgId` (an ack/event for a local replaces it, never duplicates); events for a conversation not in `conversations` set a `missingConversation` flag via `needsConversationRefetch`; `messageCreated` moves the conversation to the top of `order`, updates `lastSeq`/`updatedAt`, and adds 1 to `unread` unless it is open, from `meId`, or `type === "system"`; `opened`/`markedRead` set `unread` to 0; edits and deletes replace by `id`; a deleted message keeps its `seq` as a tombstone.

- [ ] **Step 1: Write failing tests**, one per rule above plus: `ack then the same message event does not duplicate`, `event then the ack does not duplicate`, `sendFailed then sendStarted with the same clientMsgId leaves one message`, `gap fill merges and keeps order`, `older page prepends`, `an event for an unknown conversation is flagged`, `duplicate event is ignored`.
- [ ] **Step 2: Run to verify they fail.** Run: `cd frontend && npx vitest run src/lib/chat-state.test.ts`. Expected: FAIL.
- [ ] **Step 3: Implement** as a pure reducer without mutation.
- [ ] **Step 4: Run to verify they pass.** Expected: PASS.
- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat(web): chat state reducer"
```

### Task 6: Auth provider, login and register pages

**Files:**
- Create: `frontend/src/lib/auth.tsx` (`AuthProvider`, `useAuth`), `frontend/src/lib/errors.ts` (`messageFor(code: string): string`), `frontend/src/app/login/page.tsx`, `frontend/src/app/register/page.tsx`
- Modify: `frontend/src/app/layout.tsx` (wrap in `AuthProvider`)
- Test: `frontend/src/lib/errors.test.ts`, `frontend/src/app/login/login.test.tsx`, `frontend/src/app/register/register.test.tsx`

**Interfaces:**
- Consumes: `createApi`, `createLocalTokenStore` (Task 3).
- Produces: `useAuth(): { user: User|null; ready: boolean; api: Api; login(id, pw): Promise<void>; logout(): Promise<void> }`. On mount, if a refresh token exists it calls `api.refresh()` then `api.me()`; `ready` is true once that settles.

- [ ] **Step 1: Write failing tests:** `messageFor` maps each code in Global Constraints to friendly copy and unknown codes to a generic line; login page `shows the mapped message for invalid_credentials`, `links to verification when email_not_verified`, `navigates to / on success`; register page `shows field errors from validation_failed`, `moves to the code step after success`, `verifies the code, then logs in`, `resend calls resend-verification`.
- [ ] **Step 2: Run to verify they fail.** Run: `cd frontend && npm test`. Expected: FAIL.
- [ ] **Step 3: Implement** the provider, pages and error copy; use the theme components; a visible fishing scene (inline SVG) on the auth pages. Mock `next/navigation` in tests.
- [ ] **Step 4: Run tests, lint and build.** Run: `cd frontend && npm test && npm run lint && npm run build`. Expected: pass.
- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat(web): login and register with email verification"
```

### Task 7: Chat shell, list, conversation view, composer

**Files:**
- Create: `frontend/src/lib/chat.tsx` (`ChatProvider`, `useChat`), `frontend/src/app/(app)/layout.tsx`, `frontend/src/app/(app)/page.tsx`, `frontend/src/app/(app)/c/[id]/page.tsx`, `frontend/src/components/{ConversationList,MessageList,MessageBubble,Composer}.tsx`, `frontend/src/lib/format.ts` (`conversationTitle`, `dayLabel`, `systemNoticeText`)
- Delete: `frontend/src/app/page.tsx` (the `(app)` group now owns `/`)
- Test: `frontend/src/components/Composer.test.tsx`, `MessageList.test.tsx`, `ConversationList.test.tsx`, `frontend/src/lib/format.test.ts`, `frontend/src/lib/chat.test.tsx`

**Interfaces:**
- Consumes: `useAuth` (Task 6), `createChatConnection` (Task 4), `reducer` and selectors (Task 5).
- Produces: `useChat(): { state; open(id); loadOlder(id); send(id, body); retry(id, clientMsgId) }`. `ChatProvider` loads `GET /conversations?limit=50` (an array), connects the hub, on `onReconnected` refetches the list and the open chat via `?after=<latestSeq>` repeating while `hasMore`, calls `markRead` (debounced 300 ms) when the open chat's newest message changes and `document.visibilityState === "visible"`, and refetches the list when `needsConversationRefetch` is true.

- [ ] **Step 1: Write failing tests:**
  - `format`: direct chat title is the peer's display name, group uses `title`, fallback `Chat`; `systemNoticeText` renders `member_added`, `member_removed`, `member_left`, `renamed` from their JSON bodies and a safe fallback for unknown JSON or bad JSON.
  - `Composer`: Enter sends and clears; Shift+Enter inserts a newline; whitespace-only and newline-only do not send; text over 4000 is blocked with the counter shown; disabled with a "Reconnecting…" note when `connected` is false.
  - `MessageList`: groups by day and consecutive sender; own messages are right-aligned; a 4000-character unbroken string renders inside the bubble with wrapping class; `failed` messages show Retry which calls `onRetry(clientMsgId)`; tombstones render "Message deleted"; user text is rendered as text (a `<img onerror>` string stays literal); a "new messages" button appears when scrolled up and a message arrives.
  - `ConversationList`: orders as given, shows `Bobber` with unread, highlights the open one.
  - `chat.test.tsx` with a fake connection and fake API: initial load; a `message.created` event for another chat bumps unread and reorders; sending shows the message immediately then replaces it on ack without duplication; a failed send then Retry reuses the `clientMsgId`; reconnect triggers list refetch and gap fill; `markRead` is called once for a burst of messages.
- [ ] **Step 2: Run to verify they fail.** Run: `cd frontend && npm test`. Expected: FAIL.
- [ ] **Step 3: Implement.** Two panes from `md` up (list left, chat right); below `md` the list is `/` and the chat is `/c/[id]` with a back link. Auth guard in `(app)/layout.tsx`: when `ready && !user` redirect to `/login`. Empty state on `/` says that chats started from the API appear here. Scroll behaviour: stick to bottom when already within 80 px of it, else show the "new messages" button; loading older pages keeps the scroll anchor.
- [ ] **Step 4: Run tests, lint and build.** Run: `cd frontend && npm test && npm run lint && npm run build`. Expected: pass.
- [ ] **Step 5: Commit**

```bash
git add frontend
git commit -m "feat(web): conversation list, chat view and composer"
```

### Task 8: End-to-end check and docs

**Files:**
- Modify: `frontend/README.md` (env var, commands), `docs/README.md` ("Connecting": the `Cors:AllowedOrigins` setting), `CLAUDE.md` (commands, "Next steps": slice 1 done, list what is left for the UI)

- [ ] **Step 1: Run the stack.** `docker compose up -d --wait`; `cd backend && dotnet run --project src/Chat.Api`; `cd frontend && npm run dev`.
- [ ] **Step 2: Manual check with two browser profiles.** Register and verify two users (codes are in Mailpit at `http://localhost:8025`); create a direct chat through the API (`POST /conversations/direct` with the first user's token and the second user's id); then in the browser: both see the chat, messages sent from either side appear on the other within a second, unread bobbers and ordering update, reload keeps the session, killing the API shows "Reconnecting…" and a restart recovers with no gap or duplicate. Fix anything found with a failing test first.
- [ ] **Step 3: Final checks.** Run: `cd backend && dotnet test` and `cd frontend && npm test && npm run lint && npm run build`. Expected: all pass.
- [ ] **Step 4: Update the docs** as listed and commit

```bash
git add docs CLAUDE.md frontend/README.md
git commit -m "docs: chat UI slice 1"
```
