# Media Cleanup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A background job deletes media rows and object-store bytes that nothing uses any more, (the 6.0 diagram redraw is deferred and only recorded).

**Architecture:** `MediaCleanupService` is a `BackgroundService` whose public `RunOnceAsync` claims unused `media` rows in one transaction (`FOR UPDATE SKIP LOCKED`), commits, then deletes their bytes. No schema or API change.

**Tech Stack:** .NET 10, EF Core (Npgsql, raw SQL for the claim), xUnit with `ApiFixture` (real PostgreSQL, `FakeTimeProvider`, `InMemoryObjectStore`).

**Spec:** `docs/superpowers/specs/2026-10-31-media-cleanup-design.md`

## Global Constraints

- Unused = older than `Media:Cleanup:GraceDays` (default 7) AND not `users.avatar_media_id` AND not `conversations.avatar_media_id` AND not attached to a live message (`messages.deleted_at is null`) AND not attached to a message that has a `reports` row. Any `status`.
- Options `Media:Cleanup`: `Enabled` (true), `IntervalMinutes` (60), `GraceDays` (7), `BatchSize` (200).
- Claim rows first (delete `message_attachments` of dead messages, then `media`, `RETURNING storage_key`), commit, then delete bytes. Bytes failure for one key: log and continue.
- The clock is the injected `TimeProvider` (`media.created_at` is set from it by `MediaService`).
- Never push. Commits carry no `Co-Authored-By` or generated-by lines. Identity is `hieu` / `lehoanghieu02206@gmail.com`; if `git config` differs, stop and tell the user.

## Review Focus

- A file attached to a live message in a conversation the uploader since left: kept (any live message keeps it). Test in Task 1.
- The same file attached to both a deleted and a live message: kept, and the dead attachment row is untouched. Test in Task 1.
- A second node running at the same time: no double delete and no error (`SKIP LOCKED`). Test in Task 1.
- Empty database, nothing to clean: returns 0 and does not touch the store. Test in Task 1.
- `Enabled=false` registers no timer loop (tests rely on this). Test in Task 2.

---

### Task 1: `MediaCleanupService.RunOnceAsync`

**Files:**
- Create: `backend/src/Chat.Api/Files/MediaCleanupOptions.cs`
- Create: `backend/src/Chat.Api/Files/MediaCleanupService.cs`
- Test: `backend/tests/Chat.Api.Tests/MediaCleanupTests.cs`

**Interfaces:**
- Produces: `public class MediaCleanupOptions { const string Section = "Media:Cleanup"; bool Enabled = true; int IntervalMinutes = 60; int GraceDays = 7; int BatchSize = 200; }`
- Produces: `public sealed class MediaCleanupService(IServiceScopeFactory scopes, IObjectStore store, IOptions<MediaCleanupOptions> options, TimeProvider time, ILogger<MediaCleanupService> log) : BackgroundService` with `public Task<int> RunOnceAsync(CancellationToken ct = default)`: runs batches until one returns fewer than `BatchSize` rows, returns the number of media rows removed.
- Consumes: `IObjectStore.DeleteAsync(string key, CancellationToken)`, `ChatDbContext` (scoped), test helpers `UploadedAsync`, `SendMediaAsync`, `ConnectAsync`, `OpenDirectAsync`, `SendMessageTests.SendAsync`, hub `DeleteMessage`, `ReportedMessageAsync`, `api.WithDbAsync`, `api.Time`.

- [ ] **Step 1: Write the failing tests** in `MediaCleanupTests` (`IClassFixture<ApiFixture>`). Each test uploads with `api.UploadedAsync`, advances `api.Time.Advance(TimeSpan.FromDays(8))` unless the test is about the grace period, calls `await Cleanup.RunOnceAsync()` where `Cleanup = api.Services.GetRequiredService<MediaCleanupService>()`, and checks the `media` row via `api.WithDbAsync(db => db.Media.AnyAsync(m => m.Id == id))` and the bytes via `api.Services.GetRequiredService<InMemoryObjectStore>()` (`GetAsync(storageKey)`; read `StorageKey` before cleanup). Names and assertions:
  - `An_old_unattached_upload_is_removed_with_its_bytes`: returns 1; row gone; bytes null.
  - `An_upload_inside_the_grace_period_is_kept`: advance 6 days; returns 0; row and bytes present.
  - `A_user_avatar_is_kept_and_a_replaced_avatar_is_removed`: `PUT /users/me/avatar` with file A, then file B; after cleanup A gone, B kept.
  - `A_group_avatar_is_kept`: `PUT /conversations/{id}/avatar`.
  - `A_file_on_a_live_message_is_kept`.
  - `A_file_only_on_a_deleted_message_is_removed_with_its_attachment_rows`: send with `SendMediaAsync`, `DeleteMessage`; `db.MessageAttachments` has no row for that media afterwards.
  - `A_file_on_a_deleted_message_that_was_reported_is_kept`: report the message first (`ReportedMessageAsync`, reporter = the other member), then delete it.
  - `A_file_on_both_a_deleted_and_a_live_message_is_kept_with_both_rows`.
  - `Pending_and_rejected_old_uploads_are_removed`: set `Status` via `ExecuteUpdateAsync`.
  - `More_candidates_than_BatchSize_are_all_removed_in_one_run`: seed 5 uploads with a service built via `ActivatorUtilities.CreateInstance<MediaCleanupService>(api.Services, Options.Create(new MediaCleanupOptions { BatchSize = 2 }))`; returns 5.
  - `A_failing_object_store_delete_still_removes_the_other_files_and_rows`: wrap the store in a test `ThrowingObjectStore(key)` passed to `ActivatorUtilities.CreateInstance`; all rows gone, no exception.
  - `Nothing_to_clean_returns_zero`.
  - `Two_runs_at_once_remove_each_file_once`: `Task.WhenAll` of two services; sum of results equals the number of seeded files.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter MediaCleanupTests`
Expected: compile error, `MediaCleanupService` not found.

- [ ] **Step 3: Implement options and service.** The claim is one raw SQL batch through `db.Database.SqlQuery<string>` inside `BeginTransactionAsync`: a CTE selects candidate ids `FROM media m WHERE m.created_at < @cutoff AND NOT EXISTS (users avatar) AND NOT EXISTS (conversations avatar) AND NOT EXISTS (message_attachments a JOIN messages g ON g.id = a.message_id WHERE a.media_id = m.id AND (g.deleted_at IS NULL OR EXISTS (SELECT 1 FROM reports r WHERE r.message_id = g.id))) ORDER BY m.created_at LIMIT @batch FOR UPDATE OF m SKIP LOCKED`; then `DELETE FROM message_attachments WHERE media_id IN (…)`, then `DELETE FROM media WHERE id IN (…) RETURNING storage_key`. Run these as separate statements in the transaction (candidate ids selected first into a list). After commit, delete each key with try/catch logging a warning. `ExecuteAsync` is added in Task 2.

- [ ] **Step 4: Run to verify they pass**

Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter MediaCleanupTests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add backend/src/Chat.Api/Files backend/tests/Chat.Api.Tests/MediaCleanupTests.cs
git commit -m "feat(api): media cleanup service (RunOnceAsync)"
```

### Task 2: Run it on a timer and wire it up

**Files:**
- Modify: `backend/src/Chat.Api/Files/MediaCleanupService.cs` (add `ExecuteAsync`)
- Modify: `backend/src/Chat.Api/Program.cs` (near the other `Media` registrations)
- Modify: `backend/tests/Chat.Api.Tests/ApiFixture.cs` (`Factory.ConfigureWebHost`)
- Test: `backend/tests/Chat.Api.Tests/MediaCleanupTests.cs`

**Interfaces:**
- Consumes: `MediaCleanupService.RunOnceAsync` (Task 1).
- Produces: registration `AddOptions<MediaCleanupOptions>().BindConfiguration(MediaCleanupOptions.Section)`, `AddSingleton<MediaCleanupService>()`, and `AddHostedService(sp => sp.GetRequiredService<MediaCleanupService>())`.

- [ ] **Step 1: Write the failing test** `The_timer_removes_unused_files_when_enabled`: build a second `Factory`-style host is heavy, so instead construct the service with `Enabled = true, IntervalMinutes = 1` and the fixture's `FakeTimeProvider`, call `StartAsync(CancellationToken.None)`, advance `api.Time` by 1 minute (the `PeriodicTimer` takes the `TimeProvider`, as in `PresenceRefresher`), wait up to 5 s for the row to disappear, then `StopAsync`. A second test `Disabled_service_never_runs`: `Enabled = false`, start, advance, assert the row still exists.

- [ ] **Step 2: Run to verify they fail**

Run: `cd backend && dotnet test tests/Chat.Api.Tests --filter MediaCleanupTests`
Expected: FAIL (service does nothing on start).

- [ ] **Step 3: Implement `ExecuteAsync`** following `PresenceRefresher`: return immediately when `!Enabled`; loop on `PeriodicTimer(TimeSpan.FromMinutes(IntervalMinutes), time)`; catch non-cancellation exceptions per tick and log (a failed tick must not stop the loop). Register in `Program.cs`. In `ApiFixture.Factory.ConfigureWebHost` add `builder.UseSetting("Media:Cleanup:Enabled", "false")` so the hosted instance stays idle during other tests.

- [ ] **Step 4: Run the whole backend suite**

Run: `cd backend && dotnet test`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add backend
git commit -m "feat(api): run media cleanup periodically"
```

### Task 3: Docs (the diagram is deferred)

The owner decided not to touch the diagrams in this work; a later review redraws them.

**Files:**
- Modify: `docs/README.md` ("API: media" gets one bullet about cleanup and its four settings)
- Modify: `CLAUDE.md` ("Next steps": remove the "job that deletes object-store bytes" half of the follow-up item and add the cleanup job to the "Done" sentence; KEEP the diagram half as its own item: "Redraw `03c` so 6.1 reads D1 (`reported user`) and D4 (`block list`), add the matching words to the 6.1 row of the README 3c table, check `02` shows the same, regenerate PNGs. Deliberately not done yet.")

**Interfaces:** none.

- [ ] **Step 1: Edit README and CLAUDE.md** as listed. Do not edit any `.drawio` or `.png` and do not change the 6.1 table row.
- [ ] **Step 2: Commit**

```bash
git add docs CLAUDE.md
git commit -m "docs: document media cleanup; record the pending 6.0 diagram redraw"
```
