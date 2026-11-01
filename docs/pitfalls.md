# Pitfalls

Things that look fine and break later. Many are pinned by tests in
`backend/tests/Chat.Data.Tests`; the rest are conventions to keep in mind.

## Database

- **`messages.seq` needs the row lock.** Allocate it by locking the `conversations` row, bumping
  `last_seq`, and using the result in the same transaction. `MAX(seq) + 1` races: two senders get the
  same number and one fails on the `(conversation_id, seq)` unique key.
- **`users` and `media` reference each other.** `users.avatar_media_id` points at `media`, and
  `media.uploader_id` points at `users`. EF Core adds one of the two foreign keys after both tables
  exist. Hand-written SQL has to do the same.
- **System messages are not deduplicated.** `sender_id` is NULL for them, and PostgreSQL treats NULLs
  as distinct in unique constraints, so `(sender_id, client_msg_id)` never collides. Generate system
  messages inside the transaction that causes them, once.
- **Enum labels are created alphabetically.** EF Core's migration creates PostgreSQL enum labels in
  sorted order, not the ERD's order. Never compare enum values with `<` or `>`, or sort by an enum column
  and expect a meaningful order.
- **Enums must be mapped twice.** Map them on `NpgsqlDataSourceBuilder` and on
  `UseNpgsql(o => o.MapEnum<T>())`. With only the data source, EF Core falls back to `integer` columns
  and the first migration silently creates the wrong schema. `ChatDataExtensions` does both.
- **`citext` must exist before the tables.** The migration creates the extension, so the database user
  needs permission to run `CREATE EXTENSION`, which managed PostgreSQL services often restrict.
- **`audit_logs` rejects UPDATE, DELETE and TRUNCATE.** A trigger enforces it. Do not "fix" an entry,
  and do not wipe the table between tests; use a fresh database.
- **Almost everything is `ON DELETE RESTRICT`.** Only `refresh_tokens.session_id` cascades. Deleting
  a user, conversation or message fails while rows refer to it. This is deliberate: reports and the
  audit log must not lose their targets.
- **Timestamps are `timestamptz`.** Npgsql only accepts `DateTimeOffset` with offset zero for them. Pass
  UTC values.

## Auth and moderation

- **A ban must revoke every session.** Setting `users.status = banned` is not enough; 6.4 also revokes
  all rows in `sessions`, otherwise a banned user keeps a valid access token until it expires.
  `ModerationService.BanCoreAsync` does both in one transaction.
- **Refresh-token reuse revokes the session.** A token with `used_at` set that is presented again means
  theft; revoke the whole session, not just that token. `SignInService.RefreshAsync` does this.
- **Refresh calls from one client must not overlap.** Two parallel refreshes with the same token look like
  reuse: one wins, the other fails and the session is revoked. Clients serialise their refresh calls, across
  browser tabs too: tabs share the refresh token, so the web client holds a Web Lock (`chat-auth-refresh`) and
  reads the token inside it.
- **Every authenticated request reads `sessions`.** `JwtBearerSetup` checks the session after validating
  the JWT; that lookup is what makes logout, password reset and bans immediate. Do not cache it without
  a short TTL, and do not skip it for "cheap" endpoints.
- **Verification codes are HMACs, not plain hashes.** Six digits are brute-forceable from a leaked hash,
  so `CodeService` keys the hash with `Auth:CodeKey` and allows 5 attempts. Rotating the key invalidates
  every unused code.
- **A locked account answers like a wrong password.** Do not return a distinct "locked" error: it would tell
  a guesser when a guess is right.
- **Access-token expiry follows `TimeProvider`.** The default JwtBearer lifetime check reads the system
  clock, so `JwtBearerSetup` supplies its own validator. Keep it if tests move the fake clock.

## Conversations and contacts

- **A block must look like a missing user.** `UserAccess` answers `user_not_found` for a user who blocked the
  viewer, in lookups, search, contacts, direct chats and group adds. A distinct error would tell the blocked
  person they were blocked. Only the blocker themselves sees `blocked`.
- **Group changes lock the conversation row.** `GroupService.MutateAsync` runs `SELECT ... FOR UPDATE` on the
  `conversations` row first. Without it, the owner and the only admin leaving at the same moment can leave the
  group with no owner. New group operations go through `MutateAsync`.
- **`direct_key` is lower-case `D`-format ids joined by `:`.** Build it with `ConversationService.DirectKey` only;
  a differently formatted key makes a second chat for the same pair, since the unique index compares text.
- **Leaving keeps the member row.** `left_at` is set, not the row deleted (messages and receipts refer to it).
  Every read of members must filter `left_at IS NULL`; `ConversationReader` does.
- **Blocking does not end existing chats.** It only stops new direct chats and group adds. Message delivery
  between blocked users has to be refused in 4.1.

## Messaging

- **Publish events after the commit.** `MessageBroadcaster` runs once the transaction is committed, never inside it;
  otherwise a rolled-back change is still announced. A delivery failure is logged and swallowed, since the change happened.
- **The socket outlives the access token.** JWT validation runs only on connect, so `CommandValidator.CheckCallerAsync`
  re-reads the session and user status on every hub command. Keep it first in every new hub method.
- **Clear the change tracker before taking the `seq`.** The validator loads the conversation in the same scope, so its
  `last_seq` is stale once `MessageWriter` holds the lock; reusing it hands out a duplicate `seq`.
- **Hub errors keep the `user_not_found` / `blocked` split.** The same rule as REST: do not collapse them, or a block shows.
- **`HubException` carries the code.** Anything else thrown in a hub reaches the client as a generic error.
- **`Clients.Users` needs `SubClaimUserIdProvider`.** JWT claims are not remapped, so the default provider finds no user id
  and events silently go nowhere.
- **Deleted text is nulled, not hidden.** Do not add code that relies on the body of a deleted message; evidence lives in the
  report snapshot (6.1).
- **A system notice is written inside the group change.** Use `MessageWriter.WriteSystem` from `GroupService.MutateAsync`,
  never a second transaction (system messages are not deduplicated).

## Real-time and presence

- **Presence leases, not flags.** A connection is online only while its lease is live; `PresenceRefresher` renews this node's
  leases. Never store "online = true" without an expiry: a node that dies would leave its users online forever.
- **A crash sends no `presence.changed`.** Events are hints; clients reconcile with `GET /conversations/{id}/presence`.
- **Presence audience excludes blocks both ways.** `PresenceAudience` is the only place that decides who sees presence; sending
  it to the whole conversation would show a blocker's comings and goings to the person they blocked.
- **Presence must not break messaging.** `PresenceService` catches store errors. Do not call `IPresenceStore` directly from a
  command path.
- **Do not put message text on the bus.** The event has ids; the consumer reads the row, so a delete in between wins and no
  private text sits in a queue.
- **Publish to the bus after the commit,** like every event, and swallow the failure: push is best effort.
- **A topic exchange drops messages with no queue.** Queues are declared by `RabbitMqConsumer`. Publishing before any consumer has
  started (or with a handler removed) loses the event; that is fine for push, not for anything that must not be lost.
- **`SetTyping` renews, it does not toggle.** Keep the key's TTL short; a stuck typing indicator means a client stopped sending.
- **Push re-checks everything.** `PushDispatcher` re-reads membership, mute, user status, deletion and presence, because the event
  may have sat in a queue. Do not trust `offlineUserIds` as a final answer.
- **Push is best effort and has no retry.** Retrying the event re-notifies devices that already got it. Clear tokens the provider
  rejects; do not throw for a single bad device.
- **A timeout is a `TaskCanceledException`.** HttpClient reports its timeout as an `OperationCanceledException`, so a filter like
  `when (e is not OperationCanceledException)` lets a slow provider escape. In a RabbitMQ handler that leaves the message
  unacknowledged, and after `prefetch` of them the consumer stops. Filter on the token: `when (!ct.IsCancellationRequested)`.
- **A push preview leaves the system.** The text goes to FCM / APNs / Web Push. Do not add more than the 100-character preview, and
  do not log it.
- **The per-user rate limiters are per node.** With several nodes the real limit is the limit times the node count.

## Moderation

- **A ban must also stop live events.** An open socket outlives its token, and fan-out goes by user id, so `MessageBroadcaster` only
  sends to members whose account is `active`. Keep that join when changing it.
- **Reports must work through a block.** 6.1 checks membership only. Do not reuse `CommandValidator.CheckMemberAsync` for it: it refuses a
  blocker in a blocked direct chat, and that is exactly who needs to report.
- **Evidence is read-audited.** Only `GET /admin/reports/{id}` returns evidence, and it writes `report.view_evidence`. Do not add a second
  way to read evidence without the audit entry.
- **Only `AuditWriter` inserts audit rows,** inside the caller's transaction. The log's time is the database's `now()`; the fake test
  clock does not move it.
- **Admin role is read from the database on every admin call.** A token's `role` claim can be minutes stale.
- **A report is deduplicated by a query, not a constraint.** Two identical reports sent at the same instant can both be stored; there is
  no unique index to stop it (that would be a schema change).

## Media

- **Never trust the client's content type or file name.** `MediaSniffer` decides from the bytes. SVG and HTML are not types: markup
  is stored as `text/plain` and served with `nosniff` as a download. Do not add a type that browsers render without also thinking
  about script.
- **A header is not a decode.** Dimensions come from the header and may lie. Serve with the locked-down headers in `MediaEndpoints`
  and never inline anything but images; if you ever resize or re-encode, do it in a sandbox.
- **Authorise on every download.** Access follows membership and message deletion, so do not hand out long-lived presigned URLs.
  A forbidden file and a missing file must give the same `media_not_found`.
- **`invalid_attachment` hides who owns what.** A missing file and someone else's file give the same error.
- **Deleting a message does not delete the bytes.** Rows and objects stay (reports may need them); a cleanup job is future work.
- **`Chat.Api.Media` would clash with the `Media` entity,** so the code is in `Chat.Api.Files`.
- **S3-compatible stores and checksums.** `S3ObjectStore` turns off the SDK's default checksum trailers (`WHEN_REQUIRED`) because
  RustFS-style servers do not all support them.
- **RustFS needs a writable `/data` it owns.** A `tmpfs` mount owned by root makes it exit with "Permission denied"; use a volume.

## Tooling



- **Edit `.drawio` files, never the PNGs.** Regenerate the PNG after every diagram change (see
  CLAUDE.md).
- **`npm ci --dry-run` empties `node_modules`** with npm 9. Use `npm install` to check the lockfile.
- **MinIO has no official image any more**, so Compose uses RustFS for S3-compatible storage. Pin a
  version before relying on it outside development.
- **Compose credentials are dev-only.** Ports bind to `127.0.0.1`. Do not reuse them anywhere else.
