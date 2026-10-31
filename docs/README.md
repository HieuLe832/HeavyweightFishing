# Câu Cá Vạn Cân Chat — design docs

A chat app: one-to-one and group conversations, real-time delivery, media attachments, and basic
moderation. The frontend borrows its look from the fishing game *Câu Cá Vạn Cân*. That theme is
presentation only, so it does not appear in the diagrams below.

**It is not a social network.** There are no feeds, posts, followers, likes on profiles, or public
timelines. Users find each other by username, add them as contacts, and talk.

All diagrams are draw.io files in [diagrams/](diagrams/), each with a light-mode PNG export next to it.

| # | Diagram | Source |
|---|---------|--------|
| 1 | Context diagram (DFD level 0) | [01-context-dfd-level-0.drawio](diagrams/01-context-dfd-level-0.drawio) |
| 2 | DFD level 1 | [02-dfd-level-1.drawio](diagrams/02-dfd-level-1.drawio) |
| 3a | DFD level 2: 1.0 Authenticate & Authorize | [03a-dfd-level-2-auth.drawio](diagrams/03a-dfd-level-2-auth.drawio) |
| 3b | DFD level 2: 4.0 Exchange Messages | [03b-dfd-level-2-messaging.drawio](diagrams/03b-dfd-level-2-messaging.drawio) |
| 3c | DFD level 2: 6.0 Moderate & Audit | [03c-dfd-level-2-moderation.drawio](diagrams/03c-dfd-level-2-moderation.drawio) |
| 4 | Entity relationship diagram | [04-erd.drawio](diagrams/04-erd.drawio) |

## Notation

The DFDs use **Gane & Sarson** notation:

- **External entities** are yellow boxes.
- **Processes** are blue rounded boxes with the process number in the header.
- **Data stores** are green open-ended boxes with the store id. A store or entity drawn a second time
  to avoid long lines is marked as a duplicate: an extra bar for stores, a corner stroke for entities.
- **Data flows** are solid arrows.
- **The dashed purple arrow** is the *verified principal* (user id, role) that 1.0 hands to the other
  processes after checking a request's access token.

Levels are balanced: every flow entering or leaving a process on one level appears, split into
finer flows, on the level below.

The ERD uses **crow's-foot** notation. Each table's header colour matches the DFD data store it belongs to.

## 1. Context diagram (DFD level 0)

![Context diagram](diagrams/01-context-dfd-level-0.png)

| External entity | Exchanges with the system |
|-----------------|---------------------------|
| User | Everything a chat client does: account, contacts, conversations, messages, files, abuse reports |
| Admin | Moderation console: reviews reports, bans or unbans users, reads the audit log |
| Google Identity | "Sign in with Google" (OAuth 2.0 / OpenID Connect code exchange) |
| Email Service | Verification and password-reset emails |
| Push Notification Service | New-message alerts for users with no live connection (FCM, APNs, Web Push) |

## 2. DFD level 1

![DFD level 1](diagrams/02-dfd-level-1.png)

| Process | Does |
|---------|------|
| 1.0 Authenticate & Authorize | Sign-up, sign-in, tokens, password reset, and the access check on every request |
| 2.0 Manage Profile & Contacts | Profile edits, user search, contact list, blocking |
| 3.0 Manage Conversations | Direct chats and groups, members and roles, rename, leave |
| 4.0 Exchange Messages | Send, edit and react; real-time delivery; receipts, typing, presence; history; push |
| 5.0 Manage Media | Upload, validate and serve files for attachments and avatars |
| 6.0 Moderate & Audit | Abuse reports, bans (which revoke sessions), audit log |

| Store | Holds | Tables (see ERD) |
|-------|-------|------------------|
| D1 Users | Accounts, credentials, linked Google identities | `users`, `oauth_identities` |
| D2 Sessions & Devices | One row per signed-in device, refresh tokens, push tokens | `sessions`, `refresh_tokens` |
| D3 Verification Codes | Email-verification and password-reset codes (hashed) | `verification_codes` |
| D4 Contacts & Blocks | Who a user saved and who they blocked | `contacts`, `blocks` |
| D5 Conversations & Members | Chats, membership, roles, mute, read and delivered cursors | `conversations`, `conversation_members` |
| D6 Messages & Reactions | Messages, attachments links, reactions | `messages`, `message_attachments`, `message_reactions` |
| D7 Media Files | File metadata (PostgreSQL) plus the bytes (object storage) | `media` |
| D8 Reports & Audit Log | Abuse reports with an evidence snapshot, admin action log | `reports`, `audit_logs` |
| D9 Presence Cache | Online status, live connections, typing (Redis keys with a TTL) | none |

## 3. DFD level 2

The processes with real internal logic are decomposed: auth, messaging, and moderation. Processes
2.0, 3.0 and 5.0 are mostly validate-then-read/write and are already fully described at level 1.

### 3a. Process 1.0 Authenticate & Authorize

![DFD level 2 for 1.0](diagrams/03a-dfd-level-2-auth.png)

| Sub-process | Does |
|-------------|------|
| 1.1 Register Account | Checks the email and username are free, stores a *pending* account with a password hash, emails a verification code |
| 1.2 Verify Email | Checks the code (limited attempts, expiry), marks it used, activates the account |
| 1.3 Reset Password | Emails a reset code; on a valid code stores the new hash and asks 1.5 to revoke every session |
| 1.4 Sign In with Password | Checks the hash and account status, counts failures, locks the account after repeated failures |
| 1.5 Manage Sessions & Tokens | Creates a session per device; issues a short-lived access token and a rotating refresh token; on refresh re-reads role and status; on refresh-token reuse revokes the whole session; signs out |
| 1.6 Sign In with Google | Exchanges the authorization code for an ID token, then finds the linked account, links by verified email, or creates a new one |
| 1.7 Authorize Request | Verifies the access token (a JWT with user id, role and session id) and that its session is not revoked, then passes the verified principal to 2.0–6.0 or rejects with 401/403 |

Banning a user in 6.0 revokes their sessions in D2, so both 1.5 and 1.7 reject them straight away.

### 3b. Process 4.0 Exchange Messages

![DFD level 2 for 4.0](diagrams/03b-dfd-level-2-messaging.png)

| Sub-process | Does |
|-------------|------|
| 4.1 Validate Command | Checks membership and role, blocks (direct chats), attachment ownership and readiness, and rate limits; routes the command to 4.2, 4.5 or 4.6 |
| 4.2 Store Message | Assigns the next per-conversation `seq`, drops duplicates by `client_msg_id`, applies edits, deletes and reactions (sender-only edits), stores system notices from 3.0 |
| 4.3 Fan Out Events | Sends each event to every connected device of every member, which includes the sender's own ack; passes offline members on to 4.4 |
| 4.4 Send Push Notifications | Skips muted members and sends a short preview to each device's push token |
| 4.5 Track Receipts, Typing & Presence | Moves each member's read / delivered cursor, keeps online and typing state in Redis, and emits those events through 4.3 |
| 4.6 Serve History & Sync | Returns message pages before or after a `seq`, so a reconnecting client fills exactly the gap it missed |

### 3c. Process 6.0 Moderate & Audit

![DFD level 2 for 6.0](diagrams/03c-dfd-level-2-moderation.png)

| Sub-process | Does |
|-------------|------|
| 6.1 Submit Report | Any signed-in user reports a user or message. Checks the reporter is a member of that message's conversation, then stores the report with a snapshot of the message as evidence |
| 6.2 Review Reports | Lists open reports with their evidence and the users involved. Every view of evidence is sent to 6.3, because admins are reading private messages |
| 6.3 Record Audit Entry | The only writer of the audit log. It records who did what to which target; entries are never updated or deleted |
| 6.4 Apply Moderation Decision | Bans or unbans a user and actions or dismisses a report. A ban sets the account status and revokes every session, so the user is signed out everywhere at once |
| 6.5 Query Audit Log | Lets admins search the audit log by actor, action and date |

6.2, 6.4 and 6.5 need `role = admin` in the verified principal; 6.1 is open to every user.

## 4. ERD

![ERD](diagrams/04-erd.png)

Design decisions worth knowing:

- **Message ordering and idempotency.** `messages.seq` is unique per conversation and gives a total
  order for sync. `(sender_id, client_msg_id)` is unique, so a client can retry a send without
  creating duplicates.
- **Allocating `seq`.** `conversations.last_seq` is the last number handed out. The send transaction
  locks the conversation row, increments `last_seq`, and uses the result as the message's `seq`, so
  numbers never repeat or skip. Do not derive the next `seq` from `MAX(seq)`.
- **One direct chat per pair.** `conversations.direct_key` (the two user ids, sorted) is unique for
  direct chats and NULL for groups.
- **Receipts as cursors.** `conversation_members.last_read_seq` and `last_delivered_seq` replace a
  per-message receipt table. That is one row per member instead of one per member per message.
- **Refresh-token rotation.** Each refresh marks the old `refresh_tokens` row `used_at` and issues a
  new one in the same session. Presenting a used token revokes the session.
- **System notices** ("Lan joined the group") are messages with `type = system` and `sender_id` NULL.
- **Report evidence** is a JSON snapshot of the reported message, so deleting the message does not
  destroy the evidence.
- **Admins** are users with `role = admin`. There is no separate admin table.

## Pitfalls

Traps in the schema and the stack are listed in [pitfalls.md](pitfalls.md).

## Editing the diagrams

The `.drawio` files are the source of truth. Edit them in draw.io: the desktop app, the VS Code
extension, or the official Docker image in a browser.

```bash
docker run -d --rm --name drawio -p 127.0.0.1:18080:8080 jgraph/drawio
# edit at http://127.0.0.1:18080 (File > Open from > Device)
```

To regenerate the PNGs in light mode, run the export script with that container running. It drives
draw.io's own export page through a local headless Chrome and needs Node 22 or newer.

```bash
node scripts/export-diagrams.mjs          # all diagrams
node scripts/export-diagrams.mjs erd      # only files whose name contains "erd"
docker stop drawio
```
