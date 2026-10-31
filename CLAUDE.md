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
