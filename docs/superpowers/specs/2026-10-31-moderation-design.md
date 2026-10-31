# Moderation and audit (6.0) — design

Date: 2026-10-31

## Goal

Any user can report a message or a user. Admins review reports, ban or unban accounts, and search an audit log
of everything they did. A ban signs the user out everywhere and stops their live events at once. No schema change:
`reports` and `audit_logs` (D8, with the append-only trigger) already exist, and 6.1 to 6.5 are already drawn.

**Out of scope:** an admin UI, bootstrapping the first admin (promote an account with SQL:
`UPDATE users SET role = 'admin' WHERE username = '…'`), moderator deletion of messages, report notifications to
admins, appeals, automatic bans.

## Decisions

**Reporting must work through a block.** The ordinary message checks refuse a blocker in a blocked direct chat. A
user who blocked an abuser still needs to report them, so 6.1 checks membership only (active member of the message's
conversation), not blocks.

**Evidence is a snapshot.** At report time the API stores the message as JSON (id, conversation, `seq`, sender, type,
body, reply target, timestamps, attachment metadata) and the conversation's id, type and title. Deleting the message
later does not touch the evidence. A message already deleted cannot be reported (`message_deleted`): its text is gone,
so report the user instead.

**Reading evidence is auditable.** Admins read private messages, so `GET /admin/reports/{id}` (the only call that
returns evidence) writes an audit entry every time. The report list leaves evidence out. This is a deliberate small
change from the diagram's "lists open reports with their evidence": the list is a queue, the detail is the evidence.

**The audit log has one writer, and the database stamps the time (`now()`, not the app clock).** `AuditWriter` is the only code that inserts into `audit_logs`. It adds the entry to the
caller's unit of work, so an action and its audit entry commit together or not at all. The database rejects updates and
deletes (pitfalls).

**Admin role is re-read per request.** A demoted admin keeps `role = admin` in an access token for up to 15 minutes, so the
admin endpoints check `users.role` and `status` in the database on every call (an endpoint filter), on top of the JWT policy.

**A ban cuts live delivery.** Besides revoking sessions, `MessageBroadcaster` only sends events to members whose account is
`active`, so a banned user's already-open socket stops receiving at once on every node, even if the client ignores the
close. The hub's per-command session check already stops them from sending.

## Components

```
Chat.Api/Moderation/
  ModerationOptions.cs   report limits
  AuditWriter.cs         6.3: the only writer of audit_logs
  ReportService.cs       6.1 submit, with evidence snapshot
  ModerationService.cs   6.2 review, 6.4 decide / ban / unban, 6.5 query
  ModerationEndpoints.cs POST /reports, /admin/…
```

### 6.1 Submit a report

`POST /reports` `{ messageId?, targetUserId?, reason, details? }`, any signed-in user.
`reason` is `spam`, `harassment` or `other`; `details` at most 1000 characters. Exactly the cases:

- `messageId` set: the message must exist, not be a system notice, and the caller must be an active member of its
  conversation (else `message_not_found`). Its sender is the target; `targetUserId`, if also given, must be that sender
  (else `validation_failed`). Your own message is `invalid_target`. A deleted one is `message_deleted`.
- only `targetUserId`: must be a user the caller can see (a user who blocked the caller looks missing: `user_not_found`),
  not the caller (`invalid_target`). Evidence is the user's id, username and display name.
- neither: `validation_failed`.

A second open report by the same caller about the same message (or, for user reports, the same user with no message)
returns the existing report with `200` instead of creating another. 10 reports per hour per user (`rate_limited`).
`201 { id, status, createdAt }`.

### 6.2 Review (admin)

- `GET /admin/reports?status=&limit=&after=`: reports oldest first (a queue). `status` is `open` (default), `actioned`,
  `dismissed` or `all`; `limit` 1–50 (default 20); `after` is the id of the last report seen. Rows:
  `{ id, reason, status, details?, messageId?, createdAt, reporter, target, resolvedAt?, resolvedBy? }` with `reporter` /
  `target` as `{ id, username, displayName, status }`. No evidence.
- `GET /admin/reports/{id}`: the same plus `evidence`. Writes audit `report.view_evidence`.

### 6.4 Decide (admin)

- `POST /admin/reports/{id}/resolve` `{ decision: "action" | "dismiss", ban?: bool }`. Only an open report
  (`report_resolved`, 409, otherwise). Sets status, `resolved_by`, `resolved_at`. `ban: true` (only with `action`) also bans
  the target, in the same transaction. Audit `report.action` / `report.dismiss` (and `user.ban`).
- `POST /admin/users/{id}/ban` `{ reason? }`: status → `banned`, every unrevoked session revoked with reason `banned`
  (`sessions` rows, so tokens and refresh both die). Banning yourself or another admin is `forbidden_target` (403). Already
  banned: no-op, no second audit entry. Audit `user.ban`, with the reason in the metadata.
- `POST /admin/users/{id}/unban`: back to `active` if the email was verified, else `pending`. Not banned: no-op. Audit `user.unban`.

### 6.5 Query the audit log (admin)

`GET /admin/audit?actorId=&action=&from=&to=&limit=&before=`: newest first, `limit` 1–100 (default 50), `before` is the
id of the last entry seen, `from` / `to` are timestamps (inclusive / exclusive). Entries:
`{ id, actorId, action, targetType, targetId, metadata, ipAddress, createdAt }`.

Actions written: `report.view_evidence`, `report.action`, `report.dismiss`, `user.ban`, `user.unban`.

## Errors

New: `report_resolved` (409), `forbidden_target` (403), `admin_only` (403, from the filter). Reuses `message_not_found`,
`message_deleted`, `user_not_found`, `invalid_target`, `rate_limited`, `validation_failed`.

## Testing

- report a message: evidence content, still there after the message is deleted, works for a member who blocked the sender,
  non-member / system / own / deleted / unknown refused, target mismatch, duplicate returns the existing one, rate limit;
- report a user: visible user, blocked-by-user looks missing, self refused, bad reason, long details, nothing to report;
- admin gate: no token 401, ordinary user 403, an admin demoted in the database is refused with the same token;
- review: queue order and paging, status filter, no evidence in the list, evidence in the detail with an audit entry per view;
- decide: action / dismiss, twice → 409, `ban: true` bans in one go, a failed ban rolls back the resolution;
- ban: status, all sessions revoked and the token stops working, refresh and login refused, cannot ban self or an admin,
  idempotent, unban restores `active` or `pending`, a banned user's open socket stops receiving events and cannot send;
- audit: every admin action leaves one entry with actor, target, IP and metadata; filters and paging; the log cannot be changed;

## Docs to update with the code

README "API: moderation" section; pitfalls (the ban line moves from "not built yet" to done; evidence reads are audited); CLAUDE.md.
