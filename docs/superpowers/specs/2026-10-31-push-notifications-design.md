# Push notifications (4.4, slice 3) — design

Date: 2026-10-31

## Goal

Members with no live connection get a short push notification on each of their devices when a user message
arrives in a conversation they have not muted. Builds on slice 2: 4.3 already publishes
`{ messageId, conversationId, seq, senderId, offlineUserIds }` to RabbitMQ (`message.created`). No schema change:
`sessions.push_platform` / `push_token` already exist (D2).

**Out of scope:** the provider adapters for FCM, APNs and Web Push (they need credentials from the operator; see
Decisions), notification settings beyond mute, badge counts, collapsing several messages into one notification.

## Decisions

**Provider adapters are an interface plus a logging sender.** `IPushSender` has one implementation per platform.
This slice ships `LoggingPushSender` for all three, which only logs. Real adapters need FCM service-account
credentials, an APNs key and VAPID keys, none of which exist in the repo, and cannot be tested here; they are a
drop-in `IPushSender` registration. The API logs a warning at start while the logging sender is in use.

**Best effort, no retry.** A failed send to one device is logged and does not stop the others. Retrying the whole
event would re-notify the devices that succeeded. A rejected token (`InvalidToken`) is cleared from the session.
The handler itself only throws on infrastructure errors (database down), which dead-letters the event.

**Checks happen at dispatch time, not publish time.** 4.3 only says who was offline. 4.4 re-reads the world because
the event may wait in a queue: the message must still exist and not be deleted, the member must still be active in
the conversation, not muted, the user active, and not online *now* (a user who just connected reads the message live).

**Preview.** Title is the sender's display name, plus ` · <group title>` in a group. Body is the message text cut at
100 characters (with `…`); a message with no text (media) says `Sent an attachment`. The push service sees this
text; that is what the context diagram already shows (a short preview). System notices are never pushed.

## Components

```
Chat.Api/Push/
  IPushSender.cs        PushMessage, PushResult, IPushSender (one per platform)
  LoggingPushSender.cs  default, logs only
  PushDispatcher.cs     IBusHandler "push" on routing key message.created: the 4.4 logic
  PushEndpoints.cs      PUT / DELETE /auth/sessions/current/push-token
```

### Register a device

`PUT /auth/sessions/current/push-token` `{ platform: "fcm" | "apns" | "web", token }` stores them on the caller's
session (replacing any earlier token). `DELETE` clears them. A token is 1–4096 characters, no whitespace
(`validation_failed` otherwise). A token already stored on another session of the same user moves to this one, so one
physical device never gets two notifications after a re-login. Logout revokes the session, which ends its pushes.

### Dispatch

For each event: load the message; stop if missing, deleted or `system`. Then, for the offline user ids that are
still active members of the conversation with `muted_until` null or in the past and `users.status = active`, minus
those online now: take their non-revoked, unexpired sessions that hold a token and send one `PushMessage`
`{ title, body, conversationId, messageId, seq }` per session through the sender for its platform.

## Errors

Nothing new for hub or history. The token endpoint uses `validation_failed` and 401 like every authenticated route.

## Testing

`Chat.Api.Tests`, with a recording `IPushSender` in place of the logging one:

- an offline member with a token is notified once per device (two sessions → two pushes), with title and body;
- not notified: the online member, the sender, a muted member (until a future date; notified again once the mute
  lapsed), a member who left, a banned user, a user with only revoked sessions, a session with no token;
- group titles show in the notification title, long text is cut, media-less messages say `Sent an attachment`;
- a message deleted before dispatch sends nothing; a system notice sends nothing;
- an `InvalidToken` result clears that session's token; one failing device does not stop the others;
- the token endpoint: set, replace, clear, validation, 401 without auth, token moves between sessions of one user;
- end to end through the hub: sending to an offline member produces the push (in-memory bus runs the handler).

## Docs to update with the code

README "API: push" section, DFD check (4.3 → 4.4 and 4.4 → push service are already drawn), pitfalls, CLAUDE.md.
