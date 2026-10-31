# Project init and database schema — design

Date: 2026-10-31

## Goal

Create the repository skeleton for the chat app (backend, frontend, local infrastructure) and implement
the database schema as the first deliverable. The schema must match `docs/diagrams/04-erd.drawio`
(tables for data stores D1–D8; D9 Presence Cache is Redis, not a table).

No features in this round: no auth endpoints, no chat UI, no real-time hub.

## Scope

In:
- `backend/`: ASP.NET Core 10 solution with entities, EF Core configuration, and the initial migration.
- `frontend/`: Next.js (TypeScript) skeleton that builds and starts.
- `docker-compose.yml` at the repo root with the supporting services.
- Schema integration test against a real PostgreSQL.
- CLAUDE.md: reformat the todo notes into Stack / Next steps / Pitfalls sections.
- `docs/pitfalls.md`: details behind the Pitfalls section.
- ERD change: add `conversations.last_seq` (see Decisions). Update the `.drawio`, regenerate the PNG,
  and update `docs/README.md`.

Out: any endpoint, UI screen, message broker consumer, or push/email integration.

## Repository layout

```
backend/        ASP.NET Core 10 solution
frontend/       Next.js + TypeScript
docker-compose.yml
docs/
```

The game theme stays out of table, class, and API names (CLAUDE.md scope rule).

## Compose services

| Service | Purpose |
|---------|---------|
| PostgreSQL 17 | The 14 tables. Extension `citext`; types `inet`, `jsonb`, native enums |
| Redis | D9 presence/typing keys; later the SignalR backplane |
| RabbitMQ | Later: decouples 4.3 fan-out from 4.4 push and from email sending |
| MinIO | Object storage for D7 file bytes |
| Mailpit | Dev SMTP inbox |

Backend and frontend get Dockerfiles; during development they run on the host against compose services.

## Schema implementation

- EF Core code-first with Npgsql. One entity class and one `IEntityTypeConfiguration` per ERD table.
- Snake_case table and column names so the SQL reads like the ERD.
- Native PostgreSQL enums for `role`, `status`, `type`, `purpose`, `reason`, `push_platform`, and the
  other enum columns in the ERD.
- One initial migration. Primary keys, unique keys (including composites `UK1`/`UK2`), and foreign keys
  exactly as drawn.
- `direct_key` is unique and nullable (NULL for groups). `(sender_id, client_msg_id)` is unique.
- Check constraints: a contact or block cannot target oneself; `direct_key` is set iff `type = direct`.
- `audit_logs` is append-only, enforced in the database by a trigger that rejects UPDATE and DELETE.

### Decisions

1. **`messages.seq` allocation.** Add `conversations.last_seq bigint NOT NULL DEFAULT 0`. The send
   transaction locks the conversation row (`SELECT ... FOR UPDATE`), increments `last_seq`, and uses the
   result as `seq`. This gives gap-free per-conversation ordering without retry loops. It is a change to
   the ERD, so the diagram, PNG, and README are updated in the same change.
2. **Circular FK.** `users.avatar_media_id` and `conversations.avatar_media_id` reference `media`, and
   `media.uploader_id` references `users`. The migration creates the tables first and adds the avatar
   FKs afterwards.
3. **Delete behaviour.** `refresh_tokens.session_id` cascades. All other foreign keys are RESTRICT, so
   deleting data never silently removes moderation evidence or history.

## Verification

An integration test starts PostgreSQL in a container, applies the migration, and asserts against
`information_schema` / `pg_catalog`:
- every ERD table and column exists with the drawn type and nullability;
- every unique constraint and foreign key exists;
- the check constraints and the `audit_logs` trigger reject invalid writes;
- `seq` allocation under concurrent inserts into one conversation yields no gaps or duplicates.

`frontend/` must pass `npm run build`; `docker compose up` must bring all services to healthy.

## Pitfalls to document

- `messages.seq` needs the row lock; reading `MAX(seq)` is racy.
- The `users` / `media` foreign-key cycle (Decisions 2).
- System messages have `sender_id` NULL, and PostgreSQL treats NULLs as distinct in unique constraints,
  so `(sender_id, client_msg_id)` does not dedupe them.
- Banning a user must revoke every session in D2 (flow 6.4 to D2), or banned users keep valid tokens.
- Citext needs the extension created before the migration runs.
- Native enums in EF Core must be registered on the data source and in the model, or inserts fail at runtime.
- Edit `.drawio` files, never the PNGs; regenerate PNGs after every diagram change.
