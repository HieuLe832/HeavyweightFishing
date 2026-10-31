# Chat UI, slice 1: the core chat loop — design

Date: 2026-10-31

## Goal

A signed-in person can register, log in, see their conversations, open one, read history, send text and receive
messages from others in real time. The look is derived from the fishing game *Câu Cá Vạn Cân* using original art.

**Out of scope for this slice** (later slices): contacts and blocking, creating direct and group chats from the UI,
attachments and avatars, typing and presence, reactions, editing and deleting, mute, reports, password reset and settings.
Because there is no "new chat" yet, a conversation exists only if it was created through the API; the empty state says so.

## Decisions

**Plain client app, no state library.** Typed `fetch` client, a `ChatConnection` wrapper over `@microsoft/signalr`, and
React context plus reducers for auth and chat state. Alternatives rejected: TanStack Query (more machinery than four
screens need) and cookie sessions through Next route handlers (a backend-for-frontend the API was not designed around).
Pages are client components; Next.js is used for routing, fonts and the build.

**Tokens.** The access token (15 minutes) lives in memory. The refresh token lives in `localStorage` and rotates on every
refresh. This keeps the existing API as is. The cost is that an XSS bug could read the refresh token; mitigations are no
`dangerouslySetInnerHTML` anywhere, message text rendered as text, and a strict CSP header set in `next.config.ts`.
On a 401 the client refreshes once and retries; if that fails it clears the tokens and goes to `/login`. Concurrent 401s
share one refresh call (refresh tokens are single-use, and reuse revokes the session).

**CORS (backend change).** The API has none today, so a browser on `:3000` cannot call it or open the hub. Add
`Cors:AllowedOrigins` (array); Development sets `http://localhost:3000`. The policy allows the origins with credentials
off (tokens are not cookies), any header and method, and applies to REST and the hub (`/hubs/chat` negotiate and
WebSocket). Empty list outside Development means no cross-origin access. The frontend reads the API base URL from
`NEXT_PUBLIC_API_URL`.

**Optimistic send.** The composer creates a local message `{ clientMsgId (uuid), status: sending }`, calls `SendMessage`,
and on the ack replaces it with the stored message. The same `clientMsgId` arrives back in the `message.created` event
for the sender's own devices; messages merge by `id` or by `clientMsgId`, so nothing doubles. A failure marks the message
`failed` with Retry, which resends the same `clientMsgId` (the API dedupes).

**Consistency.** Messages are ordered by `seq`; pending local messages sit after the last known one. On (re)connect the
client fetches the conversation list and, for the open chat, `GET …/messages?after=<lastSeq>` to fill the gap, repeating
while `hasMore`. Edits and deletes made offline are not replayed by the API, so the open page is refetched after a reconnect.

**Read state.** While a chat is open and the tab is visible, the client calls `MarkRead(conversationId, lastSeq)` when
the newest message changes (debounced 300 ms). The unread badge in the list is `unread` from the API, cleared locally when
the chat opens and increased locally by events for conversations that are not open.

**Theme (presentation only).** Tokens as CSS variables in `globals.css` and Tailwind `@theme`: deep-teal water gradient
background, sand and cream cards, coral accent, gold for primary actions. Chunky rounded buttons with a pressed offset
shadow, a bobber as the unread badge, wave dividers and a small SVG scene on the login and empty states, a rounded display
font for headings (loaded with `next/font`) and the existing sans for text. All art is drawn in CSS and inline SVG; no game
assets. Respect `prefers-reduced-motion` (waves and bobbing stop). Text contrast meets WCAG AA. No theme words appear in API
calls or data.

## Structure

```
frontend/src/
  app/
    layout.tsx              fonts, theme, <Providers>
    login/page.tsx          login
    register/page.tsx       register + verify-email step
    (app)/layout.tsx        auth guard, shell with list + outlet
    (app)/page.tsx          empty state ("pick a chat")
    (app)/c/[id]/page.tsx   the open conversation
  lib/
    config.ts               API base URL
    api.ts                  fetch wrapper: bearer, refresh-on-401, problem+json -> ApiError(code)
    auth.tsx                AuthProvider: tokens, user, login/logout/register
    chat-connection.ts      SignalR wrapper: connect, reconnect hook, typed events, typed commands
    chat-state.ts           reducer: conversations, messages by conversation, pending, unread
    chat.tsx                ChatProvider: wires connection + state + REST
  components/               ConversationList, MessageList, MessageBubble, Composer, WaveBackground, Bobber, Button, Field
```

`api.ts`, `chat-connection.ts` and `chat-state.ts` have no React in them, so they are tested without rendering.

## Screens

- **Login:** email-or-username and password. Errors by `code` (`invalid_credentials`, `account_locked`, `email_not_verified`
  which links to the verify step), never raw server text.
- **Register:** email, username, display name, password, then the 6-digit code and resend. After verifying, log in.
- **Shell:** two panes from `md` up; below it, the list and the chat are separate routes with a back button.
- **List:** title (the peer's display name for direct chats), last-activity time, unread bobber. Newest activity first;
  an event moves a conversation to the top. Paging by `before` on scroll.
- **Chat:** messages grouped by day and sender, own messages on the right, system notices as centered text rendered from their
  JSON (`member_added`, …). History loads older pages when scrolled to the top, keeping the scroll position. The view sticks
  to the bottom when already at the bottom, otherwise shows a "new messages" button. Composer: Enter sends, Shift+Enter is a
  newline, 4000-character limit with the counter near it, disabled while disconnected with a "Reconnecting…" banner.
- **Errors:** rate limited, blocked and session ended show inline messages; `session_ended` logs out.

## Testing

- `api.ts`: bearer header, single shared refresh on concurrent 401s, logout when refresh fails, `ApiError` from problem+json;
- `chat-state.ts`: ordering by `seq`, optimistic merge by `clientMsgId` and by `id`, duplicate event, failed then retried,
  unread rules, conversation reordering, gap fill;
- `chat-connection.ts`: with a fake hub, commands and events map to the typed API; reconnect triggers the resync callback;
- components (Testing Library): login error mapping, composer keys and limit, message list grouping and scroll behaviour;
- backend: a CORS test in `Chat.Api.Tests` (allowed origin gets the headers on a REST call and on the hub negotiate; another
  origin does not);
- then `npm run lint`, `npm run build`, and a manual run against the Compose stack with two browser sessions chatting.

Tooling: Vitest, `@testing-library/react`, `jsdom`; `@microsoft/signalr` as the only new runtime dependency.

## Docs to update with the code

`frontend/README.md` (env var, commands), README "Connecting" note on CORS, CLAUDE.md commands and "Next steps".
