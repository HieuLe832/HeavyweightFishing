# Frontend

Next.js 16 (App Router, Cache Components) + TypeScript + Tailwind 4. A client-side chat app for the API in `backend/`.

```bash
npm install
cp .env.example .env.local   # NEXT_PUBLIC_API_URL, default http://localhost:5036
npm run dev                  # http://localhost:3000
npm test                     # Vitest + Testing Library
npm run lint
npm run build
```

The API must allow this origin: `Cors:AllowedOrigins` is `http://localhost:3000` in Development (see "API: messaging" in
`docs/README.md`). Dev email (the verification code) is in Mailpit at http://localhost:8025.

## Layout

- `src/lib/api.ts`: typed `fetch` client. The access token stays in memory, the refresh token in `localStorage`; concurrent 401s share
  one refresh.
- `src/lib/chat-connection.ts`: the SignalR hub behind a small interface. `chat-state.ts` is the pure reducer (ordering, optimistic
  send, unread). `chat.tsx` wires them together: loading, reconnect and gap fill, marking read.
- `src/components/`: presentation. The ocean theme is CSS variables in `src/app/globals.css` plus inline SVG; it never reaches the API.
- `src/app/`: `login`, `register`, and the signed-in shell in `(app)` (`/` and `/c/[id]`).

## What this slice does not do

Contacts and blocking, starting direct or group chats, attachments and avatars, typing and presence, reactions, edit and delete,
mute, reports and settings. A conversation shows up in the list once it has been created through the API.
