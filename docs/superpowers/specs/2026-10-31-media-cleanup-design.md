# Media cleanup and the 6.0 diagram (follow-ups) — design

Date: 2026-10-31

## Goal

Two loose ends from "Next steps":

1. Bytes of media that nothing uses any more stay in the object store forever (a deleted message's files, a replaced
   avatar, an upload that was never attached). A background job removes them.
2. The 6.0 level-2 diagram (`03c`) does not show that 6.1 reads D1 (the target user) and the block list (D4).

No schema change, no API change.

## Decisions

**What counts as unused.** A `media` row is deleted when all of these hold:

- older than the grace period (`Media:Cleanup:GraceDays`, default 7): an upload may be waiting for its `SendMedia`, and
  the media design lets the uploader attach a file again later;
- not the `avatar_media_id` of any user or conversation;
- not attached to any live message (`message_attachments` joined to `messages.deleted_at is null`);
- not attached to a message that has a report (`reports.message_id`). The report's evidence snapshot lists the
  attachment ids, and a moderator must still be able to open the file.

Everything else stays, whatever its `status` (`pending` and `rejected` uploads older than the grace period go too).

**The row is claimed first, the bytes are deleted second.** In one transaction the job deletes the `message_attachments`
rows of dead messages and the `media` rows, with `DELETE … RETURNING storage_key` under `FOR UPDATE SKIP LOCKED` on the
candidate rows, then commits and deletes the bytes. A crash between the two leaks bytes (harmless, logged) instead of
leaving a row that points at nothing. `SKIP LOCKED` makes several API nodes safe without a leader election.
An object store failure for one key is logged and skipped; that key is not retried (a later full-bucket sweep is out of scope).

**Known limit.** A `SendMedia` that validates a file just before the job claims it can hit the foreign key and fail
with a 500. It needs a file older than the grace period that was unused until that moment, so it is rare; the client can
retry after uploading again.

## Components

```
Chat.Api/Files/
  MediaCleanupService.cs   BackgroundService: periodic tick, batch loop
  MediaCleanupOptions.cs   Media:Cleanup  { Enabled (true), IntervalMinutes (60), GraceDays (7), BatchSize (200) }
```

`MediaCleanupService` runs `RunOnceAsync(now)` on a `PeriodicTimer` (the `TimeProvider` is injected, as in
`PresenceRefresher`) and repeats within a tick until a batch comes back smaller than `BatchSize`. `RunOnceAsync` is public
so tests call it directly. Disabled by `Enabled = false`; the API test host disables it so tests control the clock.

## Diagram

`03c-dfd-level-2-moderation.drawio` gets two flows into 6.1: `reported user` from D1 and `block list` from D4 (duplicate
data store symbols as the other flows use). The README table row for 6.1 already says what it checks; add "and that the
target is visible to them (D1) and not behind a block (D4)" so text and picture agree. The level-1 diagram is unchanged:
6.0 already reads those stores there. Regenerate the PNG with `scripts/export-diagrams.mjs`.

## Testing

Against real PostgreSQL and the in-memory object store:

- unused and old: row and bytes gone;
- inside the grace period: kept;
- user avatar, group avatar: kept; a replaced avatar's old file: removed;
- attached to a live message: kept; only to a deleted message: removed along with the attachment rows;
- attached to a deleted message that has a report: kept;
- `pending` and `rejected` old uploads: removed;
- batching: more candidates than `BatchSize` are all removed in one tick;
- an object store that throws for one key: the others are still removed and the row of the failing key is gone too (logged).

## Docs to update with the code

README "API: media" (one bullet on cleanup and its settings), 3c table row for 6.1, CLAUDE.md "Next steps".
