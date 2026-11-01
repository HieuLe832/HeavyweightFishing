import { act, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, type Api } from "./api";
import { AuthContext, type AuthValue } from "./auth";
import { latestSeq } from "./chat-state";
import { ChatProvider, useChat, type ChatValue } from "./chat";
import type { ChatConnection, ChatHandlers } from "./chat-connection";
import type { ConversationSummary, Message } from "./types";

const ME = "me";
const conv = (id: string, over: Partial<ConversationSummary> = {}): ConversationSummary => ({
  id, type: "direct", peer: { id: "peer-" + id, username: "p", displayName: "Peer " + id }, lastSeq: 0, unread: 0, role: "member",
  updatedAt: "2026-01-01T00:00:00Z", ...over,
});
const msg = (conversationId: string, seq: number, over: Partial<Message> = {}): Message => ({
  id: `m-${conversationId}-${seq}`, conversationId, seq, senderId: "peer-" + conversationId, clientMsgId: `k-${conversationId}-${seq}`, type: "text", body: `body ${seq}`,
  createdAt: `2026-01-02T00:00:${String(seq).padStart(2, "0")}Z`, reactions: [], attachments: [], ...over,
});

class FakeConnection implements ChatConnection {
  handlers: ChatHandlers[] = [];
  start = vi.fn(async () => {});
  stop = vi.fn(async () => {});
  sendMessage = vi.fn(async (conversationId: string, clientMsgId: string, body: string): Promise<Message> =>
    msg(conversationId, 1, { id: "stored-" + clientMsgId, clientMsgId, senderId: ME, body }));
  markRead = vi.fn(async () => {});
  subscribe(h: ChatHandlers) {
    this.handlers.push(h);
    return () => { this.handlers = this.handlers.filter((x) => x !== h); };
  }
  emit<K extends keyof ChatHandlers>(name: K, ...args: Parameters<ChatHandlers[K]>) {
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    for (const h of this.handlers) (h[name] as any)(...args);
  }
}

type Route = (method: string, path: string) => unknown;

function setup(route: Route) {
  const connection = new FakeConnection();
  const request = vi.fn(async (method: string, path: string) => route(method, path));
  const api = { request, accessToken: () => "tok", refresh: vi.fn(async () => {}) } as unknown as Api;
  const auth = { user: { id: ME, username: "me", email: "me@example.com", displayName: "Me", role: "user" }, ready: true, api, login: vi.fn(), logout: vi.fn(), reloadUser: vi.fn() } as AuthValue;
  const latest: { chat: ChatValue } = { chat: undefined as unknown as ChatValue };
  function Probe() {
    latest.chat = useChat();
    return null;
  }
  const view = render(
    <AuthContext.Provider value={auth}>
      <ChatProvider createConnection={() => connection}><Probe /></ChatProvider>
    </AuthContext.Provider>,
  );
  return { connection, request, latest, api, unmount: view.unmount };
}

const paths = (request: ReturnType<typeof vi.fn>) => request.mock.calls.map((c) => c[1] as string);
const flush = () => act(async () => { await Promise.resolve(); await Promise.resolve(); });

const standardRoute = (extra: Route = () => undefined): Route => (method, path) => {
  if (path.startsWith("/conversations?")) return [conv("a", { updatedAt: "2026-01-01T00:00:00Z" }), conv("b", { updatedAt: "2026-01-02T00:00:00Z" })];
  if (/^\/conversations\/[^/]+$/.test(path)) return { conversation: conv("a"), members: [{ userId: "peer-a", displayName: "Ann Peer" }, { userId: ME, displayName: "Me" }] };
  if (path.includes("/messages?after=")) return { messages: [], hasMore: false };
  if (path.includes("/messages")) return { messages: [msg("a", 1), msg("a", 2)], hasMore: true };
  return extra(method, path);
};

describe("ChatProvider", () => {
  beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
  afterEach(() => vi.useRealTimers());

  it("loads the conversations and connects", async () => {
    const { connection, latest } = setup(standardRoute());
    await flush();

    expect(latest.chat.state.order).toEqual(["b", "a"]);
    expect(connection.start).toHaveBeenCalledOnce();
    expect(latest.chat.state.status).toBe("disconnected"); // the fake never reports connected until it emits
  });

  it("a message in another conversation raises its unread count and moves it to the top", async () => {
    const { connection, latest } = setup(standardRoute());
    await flush();

    act(() => connection.emit("onMessageCreated", msg("a", 1)));

    expect(latest.chat.state.order[0]).toBe("a");
    expect(latest.chat.state.conversations.a.unread).toBe(1);
  });

  it("an event for a conversation the list lacks refetches the list", async () => {
    const { connection, request } = setup(standardRoute());
    await flush();
    const before = paths(request).filter((p) => p.startsWith("/conversations?")).length;

    act(() => connection.emit("onMessageCreated", msg("zzz", 1)));
    await flush();

    expect(paths(request).filter((p) => p.startsWith("/conversations?")).length).toBe(before + 1);
  });

  it("opening a conversation loads its latest page and the member names", async () => {
    const { latest, request } = setup(standardRoute());
    await flush();

    act(() => latest.chat.open("a"));
    await flush();

    expect(paths(request)).toContain("/conversations/a/messages?limit=50");
    expect(latest.chat.state.messages.a.map((m) => m.seq)).toEqual([1, 2]);
    expect(latest.chat.state.hasMoreOlder.a).toBe(true);
    expect(latest.chat.nameOf("peer-a")).toBe("Ann Peer");
    expect(latest.chat.nameOf("nobody")).toBe("Someone");
  });

  it("loadOlder asks for the page before the first message", async () => {
    const { latest, request } = setup(standardRoute());
    await flush();
    act(() => latest.chat.open("a"));
    await flush();

    await act(async () => { await latest.chat.loadOlder("a"); });

    expect(paths(request)).toContain("/conversations/a/messages?before=1&limit=50");
  });

  it("sending shows the message at once and the ack replaces it without a duplicate, even if the event follows", async () => {
    const { connection, latest } = setup(standardRoute());
    await flush();
    let release!: () => void;
    connection.sendMessage.mockImplementationOnce(async (conversationId, clientMsgId, body) => {
      await new Promise<void>((r) => { release = r; });
      return msg(conversationId, 3, { id: "stored-1", clientMsgId, senderId: ME, body });
    });

    act(() => latest.chat.send("a", "hello"));
    expect(latest.chat.state.messages.a.map((m) => [m.body, m.local])).toEqual([["hello", "sending"]]);

    await act(async () => release());
    const stored = latest.chat.state.messages.a[0];
    expect(stored.local).toBeUndefined();
    act(() => connection.emit("onMessageCreated", stored));

    expect(latest.chat.state.messages.a).toHaveLength(1);
    expect(latest.chat.state.conversations.a.unread).toBe(0);
  });

  it("a failed send is marked failed, and Retry resends the same clientMsgId", async () => {
    const { connection, latest } = setup(standardRoute());
    await flush();
    connection.sendMessage.mockRejectedValueOnce(new ApiError(0, "connection_lost", "down"));

    act(() => latest.chat.send("a", "hello"));
    await flush();
    const failed = latest.chat.state.messages.a[0];
    expect(failed.local).toBe("failed");

    act(() => latest.chat.retry("a", failed.clientMsgId));
    await flush();

    expect(connection.sendMessage).toHaveBeenNthCalledWith(2, "a", failed.clientMsgId, "hello");
    expect(latest.chat.state.messages.a).toHaveLength(1);
    expect(latest.chat.state.messages.a[0].local).toBeUndefined();
  });

  it("after a reconnect it refetches the list and fills the gap of the open conversation", async () => {
    const { connection, latest, request } = setup(standardRoute());
    await flush();
    act(() => latest.chat.open("a"));
    await flush();
    request.mockClear();

    act(() => connection.emit("onReconnected"));
    await flush();

    const calls = paths(request);
    expect(calls.some((p) => p.startsWith("/conversations?"))).toBe(true);
    expect(calls).toContain("/conversations/a/messages?after=2&limit=50");
  });

  it("marks the open conversation read once for a burst of messages", async () => {
    const { connection, latest } = setup(standardRoute());
    await flush();
    act(() => connection.emit("onStatus", "connected"));
    act(() => latest.chat.open("a"));
    await flush();
    connection.markRead.mockClear();

    act(() => {
      connection.emit("onMessageCreated", msg("a", 3));
      connection.emit("onMessageCreated", msg("a", 4));
      connection.emit("onMessageCreated", msg("a", 5));
    });
    await act(async () => { await vi.advanceTimersByTimeAsync(400); });

    expect(connection.markRead).toHaveBeenCalledTimes(1);
    expect(connection.markRead).toHaveBeenCalledWith("a", 5);
    expect(latestSeq(latest.chat.state, "a")).toBe(5);
  });

  it("does not mark read while the tab is hidden", async () => {
    const { connection, latest } = setup(standardRoute());
    await flush();
    act(() => connection.emit("onStatus", "connected"));
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "hidden" });
    act(() => latest.chat.open("a"));
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(400); });

    expect(connection.markRead).not.toHaveBeenCalled();
    Object.defineProperty(document, "visibilityState", { configurable: true, value: "visible" });
  });

  it("starts the connection again after it drops", async () => {
    const { connection } = setup(standardRoute());
    await flush();
    expect(connection.start).toHaveBeenCalledTimes(1);

    act(() => connection.emit("onStatus", "disconnected"));
    await act(async () => { await vi.advanceTimersByTimeAsync(1500); });

    expect(connection.start).toHaveBeenCalledTimes(2);
  });

  it("after a failed start it retries once, and still restarts when the connection later drops", async () => {
    const { connection } = setup(standardRoute());
    await flush();
    connection.start.mockClear();
    // Like the real connection: a failed start reports "disconnected" before it rejects.
    connection.start.mockImplementationOnce(async () => {
      connection.emit("onStatus", "disconnected");
      throw new Error("server down");
    });
    act(() => connection.emit("onStatus", "disconnected"));
    await act(async () => { await vi.advanceTimersByTimeAsync(1100); }); // the restart, which fails
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); }); // one retry, which works

    expect(connection.start).toHaveBeenCalledTimes(2);

    act(() => connection.emit("onStatus", "disconnected"));
    await act(async () => { await vi.advanceTimersByTimeAsync(1500); });

    expect(connection.start).toHaveBeenCalledTimes(3);
  });

  it("does not start the connection again after it unmounts with a restart pending", async () => {
    const { connection, unmount } = setup(standardRoute());
    await flush();
    connection.start.mockImplementation(async () => {
      connection.emit("onStatus", "disconnected");
      throw new Error("server down");
    });
    act(() => connection.emit("onStatus", "disconnected"));
    await act(async () => { await vi.advanceTimersByTimeAsync(1100); });
    const before = connection.start.mock.calls.length;

    unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });

    expect(connection.start).toHaveBeenCalledTimes(before);
  });

  it("loads more conversations after the last one of the previous page, by time and id", async () => {
    const page = [conv("b", { updatedAt: "2026-01-02T00:00:00Z" }), conv("a", { updatedAt: "2026-01-01T00:00:00Z" })];
    const { latest, request } = setup((method, path) => (path.startsWith("/conversations?") ? page : standardRoute()(method, path)));
    await flush();

    await act(async () => { await latest.chat.loadMoreConversations(); });

    expect(paths(request)).toContain(`/conversations?limit=50&before=${encodeURIComponent("2026-01-01T00:00:00Z")}&beforeId=a`);
  });

  it("stops asking for newer messages when the server claims more but sends nothing newer", async () => {
    const { latest, request } = setup((method, path) => {
      if (path.includes("/messages?after=")) return { messages: [msg("a", 2)], hasMore: true };
      return standardRoute()(method, path);
    });
    await flush();
    act(() => latest.chat.open("a"));
    await flush();
    request.mockClear();

    act(() => latest.chat.open("a"));
    await flush();

    expect(paths(request).filter((p) => p.includes("after=2"))).toHaveLength(1);
  });

  it("stops the connection and unsubscribes when it unmounts", async () => {
    const { connection, unmount } = setup(standardRoute());
    await flush();
    expect(connection.handlers).toHaveLength(1);

    unmount();

    expect(connection.stop).toHaveBeenCalledOnce();
    expect(connection.handlers).toHaveLength(0);
  });
});
