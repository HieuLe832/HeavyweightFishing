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
  all rows in `sessions`, otherwise a banned user keeps a valid access token until it expires. *(Not
  built yet.)*
- **Refresh-token reuse revokes the session.** A token with `used_at` set that is presented again means
  theft; revoke the whole session, not just that token. *(Not built yet.)*

## Tooling

- **Edit `.drawio` files, never the PNGs.** Regenerate the PNG after every diagram change (see
  CLAUDE.md).
- **`npm ci --dry-run` empties `node_modules`** with npm 9. Use `npm install` to check the lockfile.
- **MinIO has no official image any more**, so Compose uses RustFS for S3-compatible storage. Pin a
  version before relying on it outside development.
- **Compose credentials are dev-only.** Ports bind to `127.0.0.1`. Do not reuse them anywhere else.
