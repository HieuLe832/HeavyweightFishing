# Câu Cá Vạn Cân Chat

A chat app whose frontend look is derived from the Vietnamese fishing game *Câu Cá Vạn Cân*.

## Scope

- It is a chat app, **not social media**: direct and group conversations, real-time messages,
  attachments, contacts, blocking, moderation. No feeds, posts, followers, or public timelines.
  Push back on features that drift that way.
- The game theme is presentation only. It must not leak into the data model or the API names.

## Docs

- `docs/README.md` is the index. It explains every diagram and the design decisions behind the schema.
- `docs/diagrams/*.drawio` are the source of truth. Each has a `.png` export next to it.
- Diagrams use **light mode** (white background, light fills, dark text). DFDs use Gane & Sarson
  notation and the ERD uses crow's foot.
- Keep the levels consistent when changing anything:
  - DFD levels stay balanced. A flow in or out of a process must appear, split up, in its level-2 diagram.
  - DFD data stores D1–D8 map to ERD tables; the ERD header colour shows which store a table belongs to.
  - D9 Presence Cache is Redis, not a table.
- After editing a `.drawio` file, regenerate its PNG. Start `jgraph/drawio` in Docker on
  `127.0.0.1:18080`, then run `node scripts/export-diagrams.mjs [name-filter]`. The script uses the
  local Google Chrome headless and needs Node >= 22. Stop the container afterwards.

## Stack

- **Backend:** C# on .NET 10, ASP.NET Core, EF Core (Npgsql). Solution in `backend/`:
  `Chat.Data` (entities, EF configuration, migrations), `Chat.Api`, `Chat.Data.Tests`, `Chat.Api.Tests`.
- **Frontend:** TypeScript, Next.js (App Router), Tailwind. In `frontend/`.
- **Infrastructure:** `docker-compose.yml` runs PostgreSQL 17, Redis (D9 and the real-time backplane),
  RabbitMQ (fan-out, push, email), an S3-compatible object store (D7 bytes), and Mailpit (dev email).
  Backend and frontend run on the host during development.
- Any other stack is fine if it runs from Docker.

## Commands

```bash
docker compose up -d --wait                     # infrastructure
cd backend && dotnet test                       # schema and API tests (start their own PostgreSQL via Docker)
cd backend && dotnet run --project src/Chat.Api # API, applies migrations in Development
cd frontend && npm install && npm run dev
```

After changing entities, add a migration:
`dotnet ef migrations add <Name> --project src/Chat.Data --output-dir Migrations`.

## Schema rules

- The schema must match `docs/diagrams/04-erd.drawio`. `ErdSchemaTests` holds the expected tables,
  columns, keys and foreign keys. Change the ERD, the README, and that test together.
- Read [docs/pitfalls.md](docs/pitfalls.md) before touching the schema, auth, or `messages`.

## Next steps

1. Messaging, next slices (4.0). Slice 2: Redis backplane and presence (D9), RabbitMQ fan-out, and 4.5 receipts /
   typing / presence. Slice 3: 4.4 push notifications.
2. Media upload (5.0) and moderation (6.0).
3. Chat UI with the game-inspired theme.

Skipped for now: Sign in with Google (1.6). Done: processes 1.1–1.5, 1.7, 2.0 and 3.0, and the first slice of 4.0
(4.1, 4.2, 4.6 and a single-node 4.3: send, edit, delete, react, history, system notices); see the "API: …" sections
in `docs/README.md`.
