import { describe, expect, it } from "vitest";
import { initialState, latestSeq, needsConversationRefetch, reducer, type Action, type ChatState } from "./chat-state";
import type { ConversationSummary, Message } from "./types";

const ME = "me";

const conv = (id: string, over: Partial<ConversationSummary> = {}): ConversationSummary => ({
  id, type: "direct", peer: { id: "p-" + id, username: "p", displayName: "Peer " + id }, lastSeq: 0, unread: 0, role: "member",
  updatedAt: "2026-01-01T00:00:00Z", ...over,
});

const msg = (conversationId: string, seq: number, over: Partial<Message> = {}): Message => ({
  id: `m-${conversationId}-${seq}`, conversationId, seq, senderId: "peer", clientMsgId: `k-${conversationId}-${seq}`, type: "text", body: `body ${seq}`,
  createdAt: `2026-01-01T00:00:${String(seq).padStart(2, "0")}Z`, reactions: [], attachments: [], ...over,
});

const run = (state: ChatState, ...actions: Action[]) => actions.reduce(reducer, state);
const withConvs = (...convs: ConversationSummary[]) => run(initialState, { type: "conversationsLoaded", conversations: convs });
const seqs = (state: ChatState, id: string) => (state.messages[id] ?? []).map((m) => m.seq);

describe("conversations", () => {
  it("are ordered by latest activity", () => {
    const state = withConvs(conv("a", { updatedAt: "2026-01-01T00:00:00Z" }), conv("b", { updatedAt: "2026-01-02T00:00:00Z" }));

    expect(state.order).toEqual(["b", "a"]);
  });

  it("a new message moves its conversation to the top and updates lastSeq", () => {
    const state = run(withConvs(conv("a", { updatedAt: "2026-01-02T00:00:00Z" }), conv("b")), { type: "messageCreated", message: msg("b", 1), meId: ME });

    expect(state.order).toEqual(["b", "a"]);
    expect(state.conversations.b.lastSeq).toBe(1);
  });

  it("appended pages merge into the list", () => {
    const state = run(withConvs(conv("a")), { type: "conversationsAppended", conversations: [conv("b", { updatedAt: "2025-12-01T00:00:00Z" })] });

    expect(state.order).toEqual(["a", "b"]);
  });

  it("an event for a conversation that is not in the list is flagged for a refetch", () => {
    const state = run(withConvs(conv("a")), { type: "messageCreated", message: msg("zzz", 1), meId: ME });

    expect(state.order).toEqual(["a"]);
    expect(needsConversationRefetch(state, msg("zzz", 1))).toBe(true);
    expect(needsConversationRefetch(state, msg("a", 1))).toBe(false);
    expect(run(state, { type: "conversationsLoaded", conversations: [conv("a"), conv("zzz")] }).missingConversation).toBe(false);
  });
});

describe("unread", () => {
  it("counts a message from someone else in a conversation that is not open", () => {
    const state = run(withConvs(conv("a")), { type: "messageCreated", message: msg("a", 1), meId: ME });

    expect(state.conversations.a.unread).toBe(1);
  });

  it("does not count my own message, a system notice, or a message in the open conversation", () => {
    const state = run(withConvs(conv("a"), conv("b")), { type: "opened", conversationId: "b" },
      { type: "messageCreated", message: msg("a", 1, { senderId: ME }), meId: ME },
      { type: "messageCreated", message: msg("a", 2, { type: "system", senderId: null }), meId: ME },
      { type: "messageCreated", message: msg("b", 1), meId: ME });

    expect(state.conversations.a.unread).toBe(0);
    expect(state.conversations.b.unread).toBe(0);
  });

  it("opening or marking a conversation read clears it", () => {
    const base = withConvs(conv("a", { unread: 3 }), conv("b", { unread: 2 }));

    expect(run(base, { type: "opened", conversationId: "a" }).conversations.a.unread).toBe(0);
    expect(run(base, { type: "markedRead", conversationId: "b" }).conversations.b.unread).toBe(0);
  });

  it("a duplicate event is ignored and does not count twice", () => {
    const m = msg("a", 1);
    const state = run(withConvs(conv("a")), { type: "messageCreated", message: m, meId: ME }, { type: "messageCreated", message: m, meId: ME });

    expect(state.conversations.a.unread).toBe(1);
    expect(seqs(state, "a")).toEqual([1]);
  });
});

describe("messages", () => {
  it("are ordered by seq whatever order they arrive in", () => {
    const state = run(withConvs(conv("a")),
      { type: "messageCreated", message: msg("a", 3), meId: ME },
      { type: "messageCreated", message: msg("a", 1), meId: ME },
      { type: "messageCreated", message: msg("a", 2), meId: ME });

    expect(seqs(state, "a")).toEqual([1, 2, 3]);
  });

  it("the latest page, an older page and a gap fill merge in order without duplicates", () => {
    const state = run(withConvs(conv("a")),
      { type: "historyLoaded", conversationId: "a", messages: [msg("a", 5), msg("a", 6)], mode: "latest", hasMore: true },
      { type: "historyLoaded", conversationId: "a", messages: [msg("a", 3), msg("a", 4)], mode: "older", hasMore: false },
      { type: "historyLoaded", conversationId: "a", messages: [msg("a", 6), msg("a", 7)], mode: "gap" });

    expect(seqs(state, "a")).toEqual([3, 4, 5, 6, 7]);
    expect(state.hasMoreOlder.a).toBe(false);
  });

  it("an edit replaces the message and a delete leaves a tombstone at the same seq", () => {
    const state = run(withConvs(conv("a")), { type: "messageCreated", message: msg("a", 1), meId: ME },
      { type: "messageEdited", message: msg("a", 1, { body: "fixed", editedAt: "2026-01-01T00:05:00Z" }) });
    expect(state.messages.a[0].body).toBe("fixed");

    const deleted = run(state, { type: "messageDeleted", message: msg("a", 1, { body: null, deletedAt: "2026-01-01T00:06:00Z" }) });
    expect(deleted.messages.a[0].deletedAt).toBe("2026-01-01T00:06:00Z");
    expect(seqs(deleted, "a")).toEqual([1]);
  });

  it("latestSeq is the highest confirmed seq", () => {
    const state = run(withConvs(conv("a")), { type: "messageCreated", message: msg("a", 4), meId: ME });

    expect(latestSeq(state, "a")).toBe(4);
    expect(latestSeq(state, "nope")).toBe(0);
  });
});

describe("sending", () => {
  const start = (state: ChatState, clientMsgId = "k1", body = "hello") =>
    reducer(state, { type: "sendStarted", conversationId: "a", clientMsgId, body, meId: ME, createdAt: "2026-01-01T01:00:00Z" });

  it("shows the message at once, after the confirmed ones", () => {
    const state = start(run(withConvs(conv("a")), { type: "messageCreated", message: msg("a", 1), meId: ME }));

    const last = state.messages.a.at(-1)!;
    expect(last.local).toBe("sending");
    expect(last.body).toBe("hello");
    expect(last.senderId).toBe(ME);
    expect(state.messages.a).toHaveLength(2);
  });

  it("the ack replaces the local message with the stored one", () => {
    const stored = msg("a", 1, { senderId: ME, clientMsgId: "k1", body: "hello" });
    const state = run(start(withConvs(conv("a"))), { type: "sendAcked", message: stored });

    expect(state.messages.a).toHaveLength(1);
    expect(state.messages.a[0].local).toBeUndefined();
    expect(state.messages.a[0].seq).toBe(1);
    expect(state.conversations.a.lastSeq).toBe(1);
  });

  it("the ack then the same message as an event does not duplicate, and does not count as unread", () => {
    const stored = msg("a", 1, { senderId: ME, clientMsgId: "k1" });
    const state = run(start(withConvs(conv("a"))), { type: "sendAcked", message: stored }, { type: "messageCreated", message: stored, meId: ME });

    expect(state.messages.a).toHaveLength(1);
    expect(state.conversations.a.unread).toBe(0);
  });

  it("the event then the ack does not duplicate", () => {
    const stored = msg("a", 1, { senderId: ME, clientMsgId: "k1" });
    const state = run(start(withConvs(conv("a"))), { type: "messageCreated", message: stored, meId: ME }, { type: "sendAcked", message: stored });

    expect(state.messages.a).toHaveLength(1);
    expect(state.messages.a[0].local).toBeUndefined();
  });

  it("a failed send is marked, and starting it again with the same clientMsgId leaves one message", () => {
    const failed = run(start(withConvs(conv("a"))), { type: "sendFailed", conversationId: "a", clientMsgId: "k1" });
    expect(failed.messages.a[0].local).toBe("failed");

    const retried = start(failed);
    expect(retried.messages.a).toHaveLength(1);
    expect(retried.messages.a[0].local).toBe("sending");
  });

  it("a send that already succeeded is not marked failed by a late error", () => {
    const stored = msg("a", 1, { senderId: ME, clientMsgId: "k1" });
    const state = run(start(withConvs(conv("a"))), { type: "sendAcked", message: stored }, { type: "sendFailed", conversationId: "a", clientMsgId: "k1" });

    expect(state.messages.a[0].local).toBeUndefined();
  });
});

describe("status", () => {
  it("is stored", () => {
    expect(reducer(initialState, { type: "status", status: "reconnecting" }).status).toBe("reconnecting");
  });
});
