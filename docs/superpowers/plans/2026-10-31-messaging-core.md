# Messaging Core (4.0 slice 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Members send, edit, delete and react to text messages over a SignalR hub, read history over REST, and see system notices for group changes, with blocked users refused.

**Architecture:** Plain services over `ChatDbContext`. One write path, `MessageWriter`, serves the hub (own transaction) and `GroupService` (inside its existing locked transaction). Events go out after commit through `MessageBroadcaster`, which sends to the conversation's active members with `Clients.Users`, no hub groups.

**Tech Stack:** .NET 10, ASP.NET Core SignalR (shared framework), EF Core/Npgsql, xUnit, `Microsoft.AspNetCore.SignalR.Client` 10.0.14 (tests only).

**Spec:** [docs/superpowers/specs/2026-10-31-messaging-core-design.md](../specs/2026-10-31-messaging-core-design.md)

## Global Constraints

- No schema change. Do not touch `Chat.Data`, migrations, the ERD or `ErdSchemaTests`.
- Hub path `/hubs/chat`; hub methods `SendMessage`, `EditMessage`, `DeleteMessage`, `React`; events `message.created`, `message.edited`, `message.deleted`, `message.reaction`. JSON is camelCase.
- Hub failures are `HubException(code)` with the `AuthError.Code`; REST failures use `ProblemResults.Problem`.
- Body: trimmed, 1–4000 characters. Emoji: 1–32 characters. History `limit` 1–50, default 50.
- Rate limit: 20 messages per 10 seconds per user, in memory, using `TimeProvider`.
- Blocks in a direct chat: other member blocked the caller → `user_not_found`; caller blocked them → `blocked`. Applies to send, edit, delete, react; not to history.
- Publish events only after the transaction commits. A broadcast failure is logged and swallowed; the command already succeeded.
- All timestamps come from `TimeProvider` (UTC). Tests use `ApiFixture.Time`.
- Commits: author `hieu <lehoanghieu02206@gmail.com>`, no `Co-Authored-By` or attribution lines, never `--no-verify`, never `git push`.
- Run tests from `backend/` with `dotnet test --filter <Class>`; they start PostgreSQL through Docker.

## Review Focus

1. `clientMsgId` of `Guid.Empty` → `validation_failed` (Task 2).
2. The same `clientMsgId` reused by the same user in a *different* conversation → `conflict`, never the other conversation's message (Task 2).
3. Body of exactly 4000 characters accepted, 4001 and whitespace-only rejected (Task 2).
4. Edit, delete or react on a `system` message → `message_not_found` (Tasks 4, 5).
5. History cursor edges: `limit` 0 or 51, `before` with `after`, `after` past the last `seq` (empty page, `hasMore` false) (Task 6).

## File Structure

New, under `backend/src/Chat.Api/Messaging/`: `ChatHub.cs`, `CommandValidator.cs`, `MessageWriter.cs`, `MessageBroadcaster.cs`, `MessageEvents.cs`, `MessagingOptions.cs`, `MessageRateLimiter.cs`, `MessageReader.cs`, `HistoryEndpoints.cs`, `SubClaimUserIdProvider.cs`.
Modified: `Program.cs`, `Auth/AuthError.cs`, `Auth/JwtBearerSetup.cs`, `Conversations/GroupService.cs`, `Chat.Api.Tests.csproj`, `ApiFixture.cs`, docs.
New tests under `backend/tests/Chat.Api.Tests/`: `HubTestHelpers.cs`, `HubConnectTests.cs`, `SendMessageTests.cs`, `GuardTests.cs`, `EditDeleteTests.cs`, `ReactionTests.cs`, `HistoryTests.cs`, `SystemNoticeTests.cs`.

---

### Task 1: Hub skeleton, auth and test harness

**Files:**
- Create: `Messaging/ChatHub.cs`, `Messaging/SubClaimUserIdProvider.cs`, `Messaging/MessagingOptions.cs`, `Messaging/MessageEvents.cs` (DTO only for now)
- Modify: `Program.cs`, `Auth/JwtBearerSetup.cs`, `Auth/AuthError.cs`, `Chat.Api.Tests.csproj`, `ApiFixture.cs`
- Create: `tests/Chat.Api.Tests/HubTestHelpers.cs`, `tests/Chat.Api.Tests/HubConnectTests.cs`

**Interfaces:**
- Produces:
  - `AuthError.MessageNotFound` (404 `message_not_found`), `MessageDeleted` (409 `message_deleted`), `RateLimited` (429 `rate_limited`), `UnsupportedType` (400 `unsupported_type`), `Invalid` (400 `validation_failed`).
  - `MessagingOptions { int MaxBodyLength = 4000; int RateLimitCount = 20; int RateLimitWindowSeconds = 10; int MaxEmojiLength = 32; }`, section `"Messaging"`.
  - `record MessageDto(Guid Id, Guid ConversationId, long Seq, Guid? SenderId, Guid ClientMsgId, string Type, string? Body, Guid? ReplyToId, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt, List<ReactionCount> Reactions)` and `record ReactionCount(string Emoji, int Count, bool Mine)`. `Type` is the lower-case enum name.
  - Test helpers (in `HubTestHelpers`): `Task<HubConnection> ApiFixture.ConnectAsync(Person who)`; `Task<Guid> ApiFixture.OpenDirectAsync(Person a, Person b)`; `Task<Guid> ApiFixture.CreateGroupAsync(Person owner, params Person[] members)`; `Inbox` class: `Inbox Listen(HubConnection c)` registers handlers for the four events, `Task<JsonElement> Next(string eventName, TimeSpan? timeout = null)` (default 5 s, fails the test on timeout) and `Task ExpectNone(string eventName, TimeSpan? wait = null)` (default 300 ms).
  - `static async Task AssertHubErrorAsync(Func<Task> call, string code)` asserting a `HubException` whose `Message == code`.

- [ ] **Step 1: Add the client package and harness.** Add `Microsoft.AspNetCore.SignalR.Client` 10.0.14 to `Chat.Api.Tests.csproj`. In `ApiFixture` expose `public HubConnection NewHub(string accessToken)` building a connection to `http://localhost/hubs/chat` with `AccessTokenProvider`, `HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler()`, `Transports = WebSockets` and `WebSocketFactory` using `_factory.Server.CreateWebSocketClient()`. Write `HubTestHelpers` per the Interfaces block (`ConnectAsync` = `NewHub(who.Token)` then `StartAsync`; `OpenDirectAsync`/`CreateGroupAsync` call the existing `/conversations/direct` and `/conversations/group` endpoints and read `conversation.id`).

- [ ] **Step 2: Write the failing tests** in `HubConnectTests`:
  - `Valid_token_connects` — `ConnectAsync(person)` ends in `HubConnectionState.Connected`.
  - `Missing_token_is_rejected` — `NewHub("")` `StartAsync` throws.
  - `Revoked_session_is_rejected_on_connect` — log the person out (`POST /auth/logout` as that person), then `NewHub(person.Token).StartAsync()` throws.

- [ ] **Step 3: Run** `dotnet test --filter HubConnectTests`. Expected: FAIL (hub route does not exist).

- [ ] **Step 4: Implement.**
  - `ChatHub : Hub` with `[Authorize]`, empty for now.
  - `SubClaimUserIdProvider : IUserIdProvider` returns `connection.User?.FindFirst("sub")?.Value` (the JWT is mapped with `MapInboundClaims = false`, so the default provider would find nothing).
  - Program.cs: `AddSignalR()`, `AddSingleton<IUserIdProvider, SubClaimUserIdProvider>()`, options `Messaging` bound with `AddOptions<MessagingOptions>().BindConfiguration("Messaging")`, `app.MapHub<ChatHub>("/hubs/chat")` after `UseAuthorization`.
  - `JwtBearerSetup`: add `OnMessageReceived` that, when the request path starts with `/hubs/chat` and `access_token` is in the query string, sets `context.Token`. Keep the existing `OnTokenValidated`.
  - Add the five `AuthError` entries and `MessagingOptions`, `MessageDto`, `ReactionCount`.

- [ ] **Step 5: Run** `dotnet test --filter HubConnectTests`. Expected: 3 passed. Then `dotnet test` once to confirm nothing else broke.

- [ ] **Step 6: Commit** — `git add -A backend && git commit -m "feat(api): chat hub skeleton with JWT auth and test harness (4.0)"`.

---

### Task 2: Send a message (4.1, 4.2, 4.3)

**Files:**
- Create: `Messaging/CommandValidator.cs`, `Messaging/MessageWriter.cs`, `Messaging/MessageBroadcaster.cs`
- Modify: `Messaging/ChatHub.cs`, `Messaging/MessageEvents.cs`, `Program.cs` (register all three as scoped, the broadcaster as scoped over `IHubContext<ChatHub>`)
- Test: `tests/Chat.Api.Tests/SendMessageTests.cs`

**Interfaces:**
- Consumes: Task 1 items; `ConversationReader.FindAsync(userId, conversationId, ct) -> Membership?`; `UserAccess.HasBlockedAsync(blockerId, blockedId, ct)`.
- Produces:
  - `CommandValidator`: `Task<AuthError?> CheckSendAsync(ClaimsPrincipal caller, Guid conversationId, Guid clientMsgId, string? body, Guid? replyToId, CancellationToken ct)` and `Task<Result<Membership>> CheckMemberAsync(Guid userId, Guid conversationId, CancellationToken ct)` (membership + block rules, reused by Tasks 4–5), plus `static string? NormalizeBody(string? body, int max)` returning the trimmed body or null when empty/too long.
  - `MessageWriter`: `Task<Result<SendResult>> SendAsync(Guid senderId, Guid conversationId, Guid clientMsgId, string body, Guid? replyToId, CancellationToken ct)` with `record SendResult(MessageDto Message, bool Created)`; `MessageDto ToDto(Message m, IReadOnlyList<ReactionCount>? reactions = null)`.
  - `MessageBroadcaster`: `Task PublishAsync(string eventName, object payload, Guid conversationId, IEnumerable<Guid>? extraRecipients, CancellationToken ct)` — loads `user_id` of active members, calls `Clients.Users(ids).SendAsync(eventName, payload)`, catches and logs any exception.
  - Hub: `Task<MessageDto> SendMessage(Guid conversationId, Guid clientMsgId, string body, Guid? replyToId)`.

- [ ] **Step 1: Write the failing tests** in `SendMessageTests` (all via `ConnectAsync` + `Inbox`):
  - `Send_returns_the_stored_message_and_both_members_receive_it` — alice sends "hello " in a direct chat with bob; returned `body == "hello"`, `seq == 1`, `type == "text"`; bob's and alice's own connection both get `message.created` with the same `id`.
  - `Senders_other_device_also_receives_it` — alice has two connections; the second gets the event.
  - `Second_message_gets_next_seq` and `Concurrent_sends_get_distinct_gapless_seq` — 3 users × 10 sends in a group with `Task.WhenAll`; resulting seqs are exactly 1..30.
  - `Retry_with_same_clientMsgId_returns_original_and_broadcasts_once` — second call returns same `id`/`seq`; `ExpectNone("message.created")` after draining the first.
  - `Concurrent_retry_of_same_clientMsgId_creates_one_row` — two parallel calls, one DB row (`api.WithDbAsync`), both return the same id.
  - `Sender_cursors_move_to_the_new_seq` — DB check of `last_read_seq == last_delivered_seq == seq` for the sender; the other member's stay 0.
  - `Reply_to_message_in_same_conversation_works_and_other_conversation_is_rejected` — `validation_failed`.
  - Rejections via `AssertHubErrorAsync`: non-member → `conversation_not_found`; member who left → `conversation_not_found`; empty text and whitespace-only → `validation_failed`; body of 4000 chars OK, 4001 → `validation_failed` (Review Focus 3); `Guid.Empty` clientMsgId → `validation_failed` (Review Focus 1); same clientMsgId reused in a second conversation by the same user → `conflict` and no row created there (Review Focus 2).
  - `Blocks_in_a_direct_chat_are_refused_in_both_directions` — with `PUT /blocks/{id}`: after bob blocks alice, alice's send throws `user_not_found`; after alice blocks bob, alice's send throws `blocked` and bob's send throws `user_not_found`. A group chat with a block between two members still delivers.

- [ ] **Step 2: Run** `dotnet test --filter SendMessageTests`. Expected: FAIL (no `SendMessage` hub method).

- [ ] **Step 3: Implement `CommandValidator.CheckMemberAsync`** — `ConversationReader.FindAsync` (null → `ConversationNotFound`); for a direct conversation find the other active member and apply the block rule from Global Constraints (other blocked caller → `UserNotFound`, checked first; caller blocked other → `Blocked`).

- [ ] **Step 4: Implement `CheckSendAsync` and `NormalizeBody`** — order: clientMsgId non-empty, body normalised against `MessagingOptions.MaxBodyLength`, `CheckMemberAsync`, `replyToId` (if set) exists in the same conversation. All input failures are `AuthError.Invalid`.

- [ ] **Step 5: Implement `MessageWriter.SendAsync`** — (a) look up an existing row by `(senderId, clientMsgId)`: same conversation → return it with `Created = false`; different conversation → `AuthError.Conflict`. (b) Otherwise begin a transaction, `SELECT 1 FROM conversations WHERE id = {id} FOR UPDATE` (same pattern as `GroupService.MutateAsync`), load the conversation, `LastSeq++`, add the `Message` (`Type = Text`, `CreatedAt = time.GetUtcNow()`), set the sender's `LastReadSeq` and `LastDeliveredSeq` to the new seq, bump `UpdatedAt`, save, commit. (c) On `PostgresException` `UniqueViolation` for the client-id index, roll back, re-run step (a) and return that row with `Created = false`.

- [ ] **Step 6: Implement `MessageBroadcaster` and `ChatHub.SendMessage`** — the hub calls `CheckSendAsync` then `SendAsync`; any `AuthError` → `throw new HubException(error.Code)`. When `Created`, publish `message.created` after the writer returns. Return the DTO either way.

- [ ] **Step 7: Run** `dotnet test --filter SendMessageTests`. Expected: all pass.

- [ ] **Step 8: Commit** — `feat(api): send messages over the chat hub (4.1, 4.2, 4.3)`.

---

### Task 3: Session re-check and rate limit

**Files:**
- Create: `Messaging/MessageRateLimiter.cs`
- Modify: `Messaging/CommandValidator.cs`, `Messaging/ChatHub.cs`, `Program.cs` (singleton limiter)
- Test: `tests/Chat.Api.Tests/GuardTests.cs`

**Interfaces:**
- Consumes: `CommandValidator.CheckSendAsync`, `CheckMemberAsync` from Task 2.
- Produces: `MessageRateLimiter.TryAcquire(Guid userId) -> bool` (singleton, sliding window from `MessagingOptions`, clock from `TimeProvider`); `CommandValidator.CheckCallerAsync(ClaimsPrincipal caller, CancellationToken ct) -> Task<AuthError?>` returns a new `AuthError.SessionEnded` (401, `session_ended`) when the session (`sid` claim) is revoked or expired, or the user is not `active`; otherwise null.

- [ ] **Step 1: Write the failing tests** in `GuardTests`:
  - `Command_fails_after_logout_on_an_open_connection` — connect, send OK, `POST /auth/logout`, next `SendMessage` throws `session_ended`.
  - `Command_fails_after_the_user_is_banned` — set `users.status = banned` through `WithDbAsync`; next send throws `session_ended`.
  - `Twenty_sends_pass_and_the_21st_is_rate_limited` — then `api.Time.Advance(11 s)` and a send succeeds again.
  - `Rate_limit_is_per_user` — one user at the limit does not block another.

- [ ] **Step 2: Run** `dotnet test --filter GuardTests`. Expected: FAIL.

- [ ] **Step 3: Implement** `CheckCallerAsync` (one query on `sessions` joined with `users`) and call it first in every hub method (add it to the hub's existing `SendMessage`; Tasks 4–5 call it too). Implement `MessageRateLimiter` and call `TryAcquire` in `SendMessage` after validation succeeds, before the writer; failure → `AuthError.RateLimited`. Add `AuthError.SessionEnded`.

- [ ] **Step 4: Run** `dotnet test --filter "GuardTests|SendMessageTests"`. Expected: pass (no send test sends more than 20 messages as one user inside 10 fake-clock seconds; keep it that way by spreading load across users, not by raising the limit).

- [ ] **Step 5: Commit** — `feat(api): re-check the session on each hub command and rate-limit sends`.

---

### Task 4: Edit and delete

**Files:**
- Modify: `Messaging/MessageWriter.cs`, `Messaging/ChatHub.cs`
- Test: `tests/Chat.Api.Tests/EditDeleteTests.cs`

**Interfaces:**
- Consumes: `CommandValidator.CheckCallerAsync`, `CheckMemberAsync`, `NormalizeBody`; `MessageWriter.ToDto`; `MessageBroadcaster.PublishAsync`.
- Produces: `MessageWriter.EditAsync(Guid userId, Guid messageId, string body, CancellationToken ct) -> Task<Result<MessageDto>>`; `DeleteAsync(Guid userId, Guid messageId, CancellationToken ct) -> Task<Result<MessageDto>>`; a private finder shared with Task 5 that returns the message only if it exists, the caller is an active member of its conversation, and its `type` is not `system` (otherwise `AuthError.MessageNotFound`). Hub: `Task<MessageDto> EditMessage(Guid messageId, string body)`, `Task<MessageDto> DeleteMessage(Guid messageId)`.

- [ ] **Step 1: Write the failing tests** in `EditDeleteTests`:
  - `Sender_edits_and_everyone_gets_message_edited` — `editedAt` set, `body` trimmed.
  - `Another_member_cannot_edit_or_delete` → `message_not_found` (same code as a missing id).
  - `Edit_of_deleted_message_fails` → `message_deleted`; edit with empty/oversize body → `validation_failed`.
  - `Delete_sets_deleted_at_and_nulls_the_body_in_the_database` — DB check; event `message.deleted` has `body == null`, `seq` unchanged.
  - `Delete_twice_is_idempotent` — second call returns the tombstone, no second event.
  - `System_message_cannot_be_edited_or_deleted` → `message_not_found` (Review Focus 4; insert the system row directly with `WithDbAsync`).
  - `Left_member_cannot_edit_their_old_message` → `conversation_not_found`/`message_not_found` (assert `message_not_found`).
  - `Edit_in_a_blocked_direct_chat_is_refused` — `user_not_found` / `blocked` as for send.
  - `Edits_do_not_consume_the_send_rate_limit`.

- [ ] **Step 2: Run** `dotnet test --filter EditDeleteTests`. Expected: FAIL.

- [ ] **Step 3: Implement** the finder, `EditAsync` (sender only; `deleted_at != null` → `MessageDeleted`; set `Body`, `EditedAt`), `DeleteAsync` (sender only; if already deleted return the existing tombstone without re-publishing; else set `DeletedAt`, `Body = null`) and the two hub methods, which publish `message.edited` / `message.deleted` after saving. Block rules via `CheckMemberAsync`.

- [ ] **Step 4: Run** `dotnet test --filter "EditDeleteTests|SendMessageTests"`. Expected: pass.

- [ ] **Step 5: Commit** — `feat(api): edit and delete messages (4.2)`.

---

### Task 5: Reactions

**Files:**
- Modify: `Messaging/MessageWriter.cs`, `Messaging/ChatHub.cs`
- Test: `tests/Chat.Api.Tests/ReactionTests.cs`

**Interfaces:**
- Consumes: Task 4's finder.
- Produces: `MessageWriter.SetReactionAsync(Guid userId, Guid messageId, string emoji, bool on, CancellationToken ct) -> Task<Result<ReactionChange>>` with `record ReactionChange(Guid ConversationId, bool Changed)`; hub `Task React(Guid messageId, string emoji, bool on)` publishing `message.reaction` `{ conversationId, messageId, userId, emoji, on }` only when `Changed`.

- [ ] **Step 1: Write the failing tests** in `ReactionTests`:
  - `React_on_broadcasts_to_all_members_and_stores_one_row`.
  - `React_on_twice_is_idempotent` — one row, one event.
  - `React_off_removes_it`; `React_off_when_absent_sends_nothing`.
  - `Reaction_on_deleted_message_fails` → `message_deleted`.
  - `Reaction_on_system_message_fails` → `message_not_found` (Review Focus 4).
  - `Empty_or_33_char_emoji_fails` → `validation_failed`; 32 chars OK.
  - `Non_member_cannot_react` → `message_not_found`.
  - `Reaction_in_blocked_direct_chat_is_refused`.

- [ ] **Step 2: Run** `dotnet test --filter ReactionTests`. Expected: FAIL.

- [ ] **Step 3: Implement** `SetReactionAsync` (insert or delete the `(message, user, emoji)` row; `Changed` tells whether a row was added/removed) and `ChatHub.React`.

- [ ] **Step 4: Run** `dotnet test --filter ReactionTests`. Expected: pass.

- [ ] **Step 5: Commit** — `feat(api): message reactions (4.2)`.

---

### Task 6: History and sync (4.6)

**Files:**
- Create: `Messaging/HistoryEndpoints.cs`
- Create: `Messaging/MessageReader.cs` (the query; register scoped in `Program.cs`)
- Modify: `Program.cs` (`app.MapHistory()`)
- Test: `tests/Chat.Api.Tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `ConversationReader.FindAsync`, `MessageDto`, `AuthError.Invalid`.
- Produces: `GET /conversations/{id:guid}/messages?before=&after=&limit=` (authorised) returning `{ messages: MessageDto[], hasMore: bool }` ascending by `seq`. `MessageReader.PageAsync(Guid userId, Guid conversationId, long? before, long? after, int limit, CancellationToken ct) -> Task<Result<MessagePage>>` with `record MessagePage(List<MessageDto> Messages, bool HasMore)`; `Reactions[].Mine` is set from `userId`.

- [ ] **Step 1: Write the failing tests** in `HistoryTests` (seed 120 messages through the hub or directly through `WithDbAsync`):
  - `Latest_page_is_the_last_50_in_ascending_order_with_hasMore`.
  - `Before_returns_the_page_just_older_than_seq`; `After_returns_newer_messages_oldest_first`.
  - `After_past_the_last_seq_is_empty_and_hasMore_false` (Review Focus 5).
  - `Limit_0_and_51_are_rejected` → 400 `validation_failed`; `Before_with_after` → 400 `validation_failed`; negative cursors → 400 (Review Focus 5).
  - `Deleted_messages_are_tombstones` — `deletedAt` set, `body` null, `seq` present.
  - `Reactions_are_aggregated_with_mine`.
  - `Non_member_and_left_member_get_404_conversation_not_found`.
  - `History_still_works_between_blocked_users`.
  - `Needs_authentication` → 401.

- [ ] **Step 2: Run** `dotnet test --filter HistoryTests`. Expected: FAIL (404 route).

- [ ] **Step 3: Implement** the validation (limit 1–50, default 50; cursors ≥ 0; not both), the query (latest: `seq` descending take `limit+1`, then reverse; `before`: `seq < before` descending; `after`: `seq > after` ascending; `hasMore` from the extra row), reaction aggregation in one extra query, and the endpoint.

- [ ] **Step 4: Run** `dotnet test --filter HistoryTests`. Expected: pass.

- [ ] **Step 5: Commit** — `feat(api): message history and sync endpoint (4.6)`.

---

### Task 7: System notices from group changes (3.0 → 4.2)

**Files:**
- Modify: `Messaging/MessageWriter.cs`, `Conversations/GroupService.cs`
- Test: `tests/Chat.Api.Tests/SystemNoticeTests.cs`

**Interfaces:**
- Consumes: `MessageBroadcaster.PublishAsync` (with `extraRecipients`).
- Produces: `MessageWriter.WriteSystem(Conversation conversation, string kind, Guid? actorId, Guid? userId, string? title = null) -> Message` — **does not save**: bumps `conversation.LastSeq`, adds a `Message` with `Type = System`, `SenderId = null`, fresh `ClientMsgId`, JSON body `{"kind":…,"actorId":…,"userId":…}` (`title` for `renamed`; omit null properties), `CreatedAt` from `TimeProvider`. Kinds: `member_added`, `member_removed`, `member_left`, `renamed`.
- `GroupService` constructor gains `MessageWriter` and `MessageBroadcaster`; `MutateAsync`'s delegate gets a notice sink (the implementer picks the shape, e.g. a second parameter) so `RenameAsync`, `AddMembersAsync` (one notice per user actually added), `RemoveMemberAsync`, `LeaveAsync` record notices. `SetRoleAsync` records none. After `CommitAsync`, `MutateAsync` publishes `message.created` for each notice to the active members plus any user removed/left in that change.

- [ ] **Step 1: Write the failing tests** in `SystemNoticeTests` (use `WithDbAsync` to read `messages`; connect hubs for delivery):
  - `Rename_stores_one_system_message` — `type == system`, `sender_id` null, body JSON `kind == "renamed"`, `title` new, `actorId` owner, `seq` = previous last + 1; members receive `message.created`.
  - `Add_stores_one_notice_per_added_user`; re-adding a current member stores none.
  - `Remove_stores_member_removed` and the removed user's connection also receives it; later group messages do not reach them.
  - `Leave_stores_member_left_with_no_actor` and the leaver receives it.
  - `Role_change_stores_nothing`.
  - `Failed_change_stores_nothing` — a member (not admin) renaming gets 403 and no new message/`last_seq` change.
  - `Notices_get_gapless_seq_alongside_user_messages` — interleave sends and renames concurrently; seqs 1..n with no gaps.
  - `Existing_group_tests_still_pass` — run the whole `GroupTests` class.

- [ ] **Step 2: Run** `dotnet test --filter SystemNoticeTests`. Expected: FAIL.

- [ ] **Step 3: Implement** `WriteSystem`, the `GroupService` changes and post-commit publishing. The notice rows are added to the same `DbContext` before the existing `SaveChangesAsync`, so they commit atomically with the change.

- [ ] **Step 4: Run** `dotnet test --filter "SystemNoticeTests|GroupTests|ConversationTests"`. Expected: pass.

- [ ] **Step 5: Commit** — `feat(api): system notices for group changes (3.0, 4.2)`.

---

### Task 8: Docs and full verification

**Files:**
- Modify: `docs/README.md`, `docs/pitfalls.md`, `CLAUDE.md`; `docs/diagrams/03b-dfd-level-2-messaging.drawio` only if it disagrees.

- [ ] **Step 1:** Add an "API: Messaging" section to `docs/README.md` in the style of the other "API: …" sections: hub URL and token query-string rule, the four methods with arguments, the four events and payloads, notice body kinds, the history endpoint, error codes, and the known limit (offline edits/deletes/reactions are not replayed by `after`). Correct the notes at README lines ~220–221 (the sender's own cursor now moves; system notices are written).
- [ ] **Step 2:** Add pitfalls: publish only after commit; the socket outlives the token so every command re-checks the session; keep the `user_not_found` / `blocked` split in hub errors; deleted text is nulled (reports keep their own snapshot); `HubException` messages are the error codes; `Clients.Users` needs `SubClaimUserIdProvider`.
- [ ] **Step 3:** Compare `03b-dfd-level-2-messaging.drawio` with the flows built (3.0 → 4.2 notices, 4.1 → 4.2/4.6, 4.2 → 4.3). If they agree, say so in the commit message; if not, edit the `.drawio` and regenerate the PNG as described in the project `CLAUDE.md`.
- [ ] **Step 4:** Update `CLAUDE.md`: remove step 1 from "Next steps" and replace with the remaining slices (Redis backplane + presence + RabbitMQ fan-out + 4.5; push 4.4), then media and moderation, then the chat UI; add 4.1, 4.2, 4.3 (single node), 4.6 to the "Done" line.
- [ ] **Step 5:** Run `cd backend && dotnet test`. Expected: all tests pass (report counts).
- [ ] **Step 6: Commit** — `docs: document messaging core (4.0 slice 1)`.
