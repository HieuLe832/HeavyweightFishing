# Messaging core (4.0, slice 1) — design

Date: 2026-10-31

## Goal

Members of a direct or group chat send, edit, delete and react to text messages over a SignalR hub and see
them live. History and gap sync are served over REST. Group changes in 3.0 write system notices. Messages
between blocked users are refused. No schema change, so the ERD, `ErdSchemaTests` and the diagrams stay as
they are.

This is slice 1 of process 4.0. It covers 4.1 Validate Command, 4.2 Store Message and 4.6 Serve History &
Sync, plus a single-node version of 4.3 Fan Out Events.

**Out of scope (later slices):**
- Slice 2: Redis backplane and presence (D9), RabbitMQ fan-out, 4.5 receipts / typing / presence.
- Slice 3: 4.4 push notifications.
- Media messages (needs 5.0; `type = media` is rejected), moderator deletes (6.0), per-IP limits,
  frontend screens.

## Approach

Plain services over `ChatDbContext`, like the existing API. Commands arrive as hub methods; history is a
REST endpoint. One write path (`MessageWriter`) serves the hub and `GroupService`, so a system notice is
stored in the same transaction as the group change that causes it, as `docs/pitfalls.md` requires.

```
Chat.Api/Messaging/
  ChatHub.cs            SendMessage, EditMessage, DeleteMessage, React   (entry to 4.1)
  CommandValidator.cs   4.1: session, membership, blocks, input, rate limit
  MessageWriter.cs      4.2: seq allocation, dedupe, edit, delete, reactions, system notices
  MessageBroadcaster.cs 4.3 (single node): sends events to the active members of a conversation
  MessageEvents.cs      MessageDto and the event shapes
  MessagingOptions.cs   body length, rate limit
  HistoryEndpoints.cs   4.6: GET /conversations/{id}/messages
  SubClaimUserIdProvider.cs  maps the JWT `sub` to the SignalR user id
```

### Hub

`/hubs/chat`, authenticated with the existing JWT. Browsers cannot set headers on a WebSocket, so
`JwtBearerSetup` also accepts `access_token` in the query string, for this path only. The session check it
already does (pitfalls: "Every authenticated request reads `sessions`") runs on connect.

A hub connection outlives the token, so `CommandValidator` re-reads the session (`sid` claim) and user status
on **every command**. Logout, password reset and a ban therefore stop commands at once; the open socket
itself is closed by the client, or by slice 2 when presence lands.

Failures are thrown as `HubException` whose message is the error `code` (same codes as the REST API).

| Method | Arguments | Returns |
|---|---|---|
| `SendMessage` | `conversationId`, `clientMsgId`, `body`, `replyToId?` | `MessageDto` (the stored message, also the ack) |
| `EditMessage` | `messageId`, `body` | `MessageDto` |
| `DeleteMessage` | `messageId` | `MessageDto` (tombstone) |
| `React` | `messageId`, `emoji`, `on` | nothing |

`React` sets state (`on = true` adds, `false` removes) rather than toggling, so a retry cannot flip it.

### Events

Sent to every connected device of every active member, including the sender's other devices:

| Event | Payload |
|---|---|
| `message.created` | `MessageDto` (also for system notices) |
| `message.edited` | `MessageDto` |
| `message.deleted` | `MessageDto` (tombstone) |
| `message.reaction` | `{ conversationId, messageId, userId, emoji, on }` |

`MessageDto`: `id, conversationId, seq, senderId?, clientMsgId, type, body?, replyToId?, createdAt,
editedAt?, deletedAt?, reactions[]` where `reactions` is `[{ emoji, count, mine }]` (`mine` only in REST
history; events carry the raw `message.reaction` instead).

### Fan-out without hub groups

Recipients are looked up per event: `SELECT user_id FROM conversation_members WHERE conversation_id = @id
AND left_at IS NULL`, then `Clients.Users(ids)`. This needs no connection-to-group bookkeeping, and a member
who leaves or is removed stops receiving events the moment the query runs, with nothing to keep in sync. One
cheap indexed query per event is acceptable for slice 1; the broadcaster is an interface so slice 2 can swap
the transport (backplane / RabbitMQ) without touching callers.

Events are published **after** the transaction commits, never inside it.

### Send flow

1. `ChatHub.SendMessage` → `CommandValidator`:
   - session still valid and user `active`;
   - caller is an active member of the conversation (else `conversation_not_found`);
   - direct chat with a block either way: if the other member blocked the caller → `user_not_found`; if the
     caller blocked them → `blocked`. Applies to send, edit, delete and react. Reading history still works;
   - `type` is text: `body` trimmed, 1–4000 characters; `replyToId`, if set, is a message in the same
     conversation;
   - rate limit: 20 messages per 10 seconds per user, in memory (`Messaging:` options) → `rate_limited`.
2. `MessageWriter.SendAsync`, one transaction:
   - `SELECT ... FOR UPDATE` on the conversation row, `last_seq + 1`, insert;
   - dedupe: an existing row for `(sender_id, client_msg_id)` is returned as is, with no new `seq` and no
     broadcast. A unique-key race on that index is caught and answered the same way;
   - the sender's own `last_read_seq` and `last_delivered_seq` move to the new `seq` (they have read their
     own message; other members' cursors wait for 4.5);
   - `conversations.updated_at` is bumped.
3. After commit, `MessageBroadcaster` sends `message.created`. The hub method returns the same `MessageDto`.

### Edit, delete, react

- **Edit:** sender only, text messages only, not deleted. Sets `body` and `edited_at`. Same body rules.
  Otherwise `message_not_found` (also for other people's messages, so ids cannot be probed) or
  `message_deleted`.
- **Delete:** sender only. Soft delete: `deleted_at` is set and `body` is set to NULL, so deleted text is
  gone from the database. Reports keep their own snapshot as evidence (6.1). The row and its `seq` stay, so
  history has no gaps. Idempotent.
- **React:** any active member, on a message that is not deleted. `emoji` is non-empty and at most 32
  characters. Idempotent by `(message_id, user_id, emoji)`.

### System notices (3.0 → 4.2)

`GroupService.MutateAsync` already holds the conversation lock. It now also calls
`MessageWriter.WriteSystemAsync` for each notice, in the same `SaveChanges`, and returns the written
messages to its caller, which broadcasts them after commit.

| Group change | Notice `body` (JSON) |
|---|---|
| Members added | one per added user: `{"kind":"member_added","actorId":…,"userId":…}` |
| Member removed | `{"kind":"member_removed","actorId":…,"userId":…}` |
| Member left | `{"kind":"member_left","userId":…}` |
| Renamed | `{"kind":"renamed","actorId":…,"title":"…"}` |

`type = system`, `sender_id = NULL`, a fresh `client_msg_id`. The body is structured data, not display
text, so names and language stay in the client. Role changes and owner hand-over write no notice. Removed
and departing members also receive the notice that ends their membership; everyone else gets recipients from
the post-commit member list.

### History and sync (4.6)

`GET /conversations/{id}/messages?before={seq}&after={seq}&limit={n}` (authenticated; caller must be an
active member, else `conversation_not_found`).

- No cursor: the latest page. `before`: the page just older than `seq`. `after`: the messages newer than
  `seq`, oldest first, which a reconnecting client uses to fill its gap. `before` and `after` together → 400.
- `limit` 1–50, default 50. Always returned in ascending `seq`. The response carries `hasMore`.
- Deleted messages come back as tombstones (`deletedAt` set, `body` null).
- Messages from before the caller's `joined_at` are not hidden; a member who rejoins a group sees the whole
  history (same as the DB design, which has no per-member visibility).

**Known limit:** `seq` exists only for new messages. Edits, deletes and reactions that happen while a client
is offline are not replayed by `after`. The client refetches the page it is showing. Replaying them would
need an `updated_seq` column, which is a schema change and is left for a later slice.

## Errors

Reuses `AuthError` and adds `message_not_found` (404), `message_deleted` (409), `rate_limited` (429),
`unsupported_type` (400) and `validation_failed` (400, for empty or oversize bodies, bad emoji, bad
history cursors). REST returns `application/problem+json` with `code`; the hub throws
`HubException(code)`.

## Testing

Extend `Chat.Api.Tests` (real PostgreSQL via the shared fixture, `WebApplicationFactory`, and
`Microsoft.AspNetCore.SignalR.Client` over the in-memory test server):

- send: ack equals the stored row; both members' connections receive `message.created`; sender's second
  device receives it too;
- `seq`: concurrent sends from several users produce distinct, gapless numbers;
- dedupe: a retry with the same `clientMsgId` returns the original and broadcasts once;
- rejections: non-member, left member, blocked in each direction (error codes differ as specified),
  oversize or empty body, `media` type, rate limit;
- edit / delete / react: ownership, deleted-message rules, idempotency, body cleared on delete;
- stale session: after logout the next command fails;
- history: paging with `before` / `after`, `limit`, tombstones, non-member 404;
- notices: add, remove, leave, rename each store one `system` message with the right body, with a
  `seq` in order, delivered to the right people; a failed group change stores none;
- a member removed from a group stops receiving events.

`ErdSchemaTests` is unchanged.

## Docs to update with the code

- `docs/README.md`: an "API: Messaging" section (hub methods, events, history, notice bodies, known limit);
  fix the "Nothing moves `last_read_seq` yet" note (the sender's own cursor now moves) and the "System
  notices are not written yet" note.
- `docs/pitfalls.md`: publish only after commit; the socket outlives the token, so check the session per
  command; blocked-user errors in the hub must keep the `user_not_found` / `blocked` split; deleted text is
  nulled.
- `CLAUDE.md`: update "Next steps" and the "Done" line.
- Check `03b-dfd-level-2-messaging.drawio` against the 3.0 → 4.2 notice flow and the 4.1 → 4.3 events; edit
  and re-export the PNG only if it disagrees.
