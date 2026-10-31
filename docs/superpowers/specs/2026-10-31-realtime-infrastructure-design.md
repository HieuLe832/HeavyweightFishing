# Real-time infrastructure (4.0, slice 2) — design

Date: 2026-10-31

## Goal

Run the chat hub on more than one API node, keep online / typing state in Redis (D9), track read and delivered
receipts, and hand "someone is offline" to a durable queue so push (slice 3) can consume it. No schema change:
the ERD, `ErdSchemaTests` and the DFDs already show these flows (4.3 → 4.4, 4.5 ↔ D9, receipts on D5).

Covers: the Redis backplane, D9 presence, RabbitMQ publishing for offline recipients, and 4.5 receipts /
typing / presence.

**Out of scope:** the push consumer and sending (slice 3), media messages, frontend, last-seen timestamps.

## Decisions

**Two transports, two jobs.** Live sockets get events through SignalR on the Redis backplane: low latency, no
persistence. Work that must not be lost when nobody is listening (push) goes through RabbitMQ: durable, retried,
dead-lettered. The roadmap's "RabbitMQ fan-out" is the second one; delivering to open sockets over RabbitMQ as
well would duplicate the backplane for no gain.

**Everything has an in-process fallback.** With `Redis:ConnectionString` empty the API uses an in-memory
presence store and no backplane; with `RabbitMq:Uri` empty it uses an in-process event bus. Tests and a
one-node dev setup need no containers; `appsettings.Development.json` points both at Compose. The API logs a
warning at start when either is missing. Redis and RabbitMQ tests use Testcontainers.

**Presence must never break messaging.** Every presence call is wrapped: a Redis error is logged, and the caller
carries on (a send still works; offline members are then assumed offline, so they get a push rather than silence).

## Components

```
Chat.Api/Realtime/
  IPresenceStore.cs         D9 contract
  InMemoryPresenceStore.cs  single node and tests, expiry follows TimeProvider
  RedisPresenceStore.cs     sorted-set leases + typing keys
  PresenceService.cs        4.5: connect / disconnect / online / typing; swallows store errors; emits presence.changed
  ConnectionRegistry.cs     connections held by this node
  PresenceRefresher.cs      BackgroundService, renews this node's leases every 20 s
  IEventBus.cs              publish durable events; IBusHandler for consumers
  InMemoryEventBus.cs       calls handlers inline, keeps what it published (tests)
  RabbitMqEventBus.cs       topic exchange chat.events, persistent messages, publisher confirms
  RabbitMqConsumer.cs       BackgroundService: one queue + dead-letter queue per IBusHandler
  RealtimeOptions.cs        Redis / RabbitMq sections
Chat.Api/Messaging/
  ReceiptService.cs         4.5: advance read / delivered cursors
  PresenceEndpoints.cs      GET /conversations/{id}/presence
```

### Presence (D9)

A user is online while at least one connection holds a live lease.

- Redis key `presence:{userId}`: sorted set, member = connection id, score = lease expiry (unix ms). Lease 60 s.
  `PresenceRefresher` re-scores this node's connections every 20 s, so a crashed node's connections expire by
  themselves. A Lua script makes "drop expired, count, add" atomic, so exactly one connect sees the offline→online
  edge.
- Redis key `typing:{conversationId}:{userId}`: `SET … EX 5`. Each `SetTyping(true)` renews it.
- A crashed node emits no `presence.changed` for its users; the lease just runs out. Presence is a hint, and
  clients reconcile with `GET /conversations/{id}/presence`.

`OnConnectedAsync` registers the connection and, on the offline→online edge, sends `presence.changed`
`{ userId, online: true }`; `OnDisconnectedAsync` does the reverse.

**Audience.** Presence goes to people who share an active conversation with the user, excluding anyone with a
block either way with them. (A block must look like a missing user: the blocked person must not see the
blocker come and go.) The user's own other devices do not receive it.

### Receipts

`MarkDelivered(conversationId, upToSeq)` and `MarkRead(conversationId, upToSeq)` move the caller's cursors on
`conversation_members` forward only. Read implies delivered. `upToSeq` above `conversations.last_seq` is
`validation_failed`. Nothing changed (a stale or repeated call) → no event. Otherwise `receipt.updated`
`{ conversationId, userId, lastDeliveredSeq, lastReadSeq }` goes to every active member, the caller's other
devices included, so their unread counts follow. Blocked direct chats refuse receipts like any command.

`MemberSummary` in `GET /conversations/{id}` gains `lastDeliveredSeq` and `lastReadSeq` so a client can draw
receipts without waiting for events.

### Typing

`SetTyping(conversationId, typing)`: stores or clears the key and sends `typing` `{ conversationId, userId,
typing }` to the other active members. Clients repeat `true` about every 3 s while typing and hide the
indicator after 6 s without one. At most 10 typing calls per 10 s per user; extra calls are dropped silently
(not an error). Sending a message clears the sender's typing key without an event; clients clear on
`message.created`.

### Offline hand-off (4.3 → 4.4)

After a user message commits, `MessageBroadcaster.PublishCreatedAsync` delivers `message.created`, then asks
`PresenceService` which other active members have no live lease and publishes to the bus, routing key
`message.created`, body `{ messageId, conversationId, seq, senderId, offlineUserIds }`. No message text goes
into the queue: the consumer reads the row, so a delete in between wins. System notices are not published.
Muted members are not filtered here: DFD 4.4 owns the mute check.

RabbitMQ: durable topic exchange `chat.events`, persistent messages, publisher confirms. Each `IBusHandler`
gets durable queue `chat.<name>` bound to its routing key and a dead-letter queue `chat.<name>.dead`. A handler
that throws → `nack` without requeue → dead letter. A publish failure is logged and swallowed: push is best effort,
and the message is already stored.

### Backplane

`AddSignalR().AddStackExchangeRedis(...)` when Redis is configured, with channel prefix `chat`. Fan-out still
looks recipients up per event (slice 1), now across nodes. `MessageRateLimiter` stays per node.

## Errors

No new codes. Hub methods reuse `session_ended`, `conversation_not_found`, `user_not_found` / `blocked`,
`validation_failed`.

## Testing

`Chat.Api.Tests`, in-memory presence and bus unless stated:

- receipts: forward only, read implies delivered, above `last_seq` rejected, stale call sends no event, event
  reaches the caller's other device, non-member and blocked refused, `GET` members show the cursors;
- typing: other members get it, the typer does not, `false` clears, throttle drops extras, non-member refused;
- presence: first connection emits `presence.changed` online, a second device does not, last disconnect emits
  offline, lease expiry with the fake clock, blocked pair sees nothing, `GET …/presence` lists online and typing;
- offline hand-off: offline members are published, online ones and the sender are not, system notices are not;
- store errors: a failing presence store does not fail connect, send or receipts.

With containers (Testcontainers):

- `RedisPresenceStore` against real Redis: edge detection, expiry, concurrent connects see one edge, typing;
- two API nodes on one Redis and one database: a message sent through node 1 reaches a client on node 2, and
  presence set on node 1 is visible from node 2;
- `RabbitMqEventBus` + `RabbitMqConsumer`: a published event reaches the handler; a throwing handler's message
  lands in the dead-letter queue.

## Docs to update with the code

README: new "API: receipts, typing and presence" section and the hub events; update the slice note in
"API: messaging". `pitfalls.md`: presence leases, the audience rule, publish-after-commit also covers the bus.
`CLAUDE.md`: next steps. `docker-compose.yml` needs no change. `Realtime` options go in `appsettings`.

## Tasks

1. Packages, options, `IPresenceStore` + in-memory store, `PresenceService`, registry, refresher.
2. Hub lifecycle → presence; `presence.changed`; `GET …/presence`.
3. `ReceiptService`, `MarkDelivered` / `MarkRead`, `receipt.updated`, member cursors in the detail response.
4. Typing.
5. `IEventBus`, in-memory bus, offline hand-off in the broadcaster.
6. Redis presence store and backplane, RabbitMQ bus and consumer, with container tests.
7. Docs.
