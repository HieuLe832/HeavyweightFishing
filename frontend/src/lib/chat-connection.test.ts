import { describe, expect, it, vi } from "vitest";
import { ApiError } from "./api";
import { createChatConnection, type HubLike } from "./chat-connection";
import type { Message } from "./types";

class FakeHub implements HubLike {
  handlers = new Map<string, (...args: unknown[]) => void>();
  reconnecting: (() => void)[] = [];
  reconnected: (() => void)[] = [];
  closed: (() => void)[] = [];
  invocations: { method: string; args: unknown[] }[] = [];
  result: unknown = undefined;
  failure: Error | null = null;
  startFailure: Error | null = null;

  async start() {
    if (this.startFailure) throw this.startFailure;
  }
  async stop() {}
  async invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    this.invocations.push({ method, args });
    if (this.failure) throw this.failure;
    return this.result as T;
  }
  on(event: string, cb: (...args: unknown[]) => void) {
    this.handlers.set(event, cb);
  }
  onreconnecting(cb: () => void) {
    this.reconnecting.push(cb);
  }
  onreconnected(cb: () => void) {
    this.reconnected.push(cb);
  }
  onclose(cb: () => void) {
    this.closed.push(cb);
  }
  emit(event: string, ...args: unknown[]) {
    this.handlers.get(event)?.(...args);
  }
}

const message = (over: Partial<Message> = {}): Message => ({
  id: "m1", conversationId: "c1", seq: 1, senderId: "u1", clientMsgId: "k1", type: "text", body: "hi",
  createdAt: "2026-01-01T00:00:00Z", reactions: [], attachments: [], ...over,
});

function setup() {
  const hub = new FakeHub();
  const connection = createChatConnection({ hubUrl: "http://api.test/hubs/chat", getAccessToken: () => "tok", hub });
  const handlers = {
    onMessageCreated: vi.fn(), onMessageEdited: vi.fn(), onMessageDeleted: vi.fn(), onReconnected: vi.fn(), onStatus: vi.fn(),
  };
  return { hub, connection, handlers };
}

describe("commands", () => {
  it("sendMessage invokes SendMessage with the ids and body and returns the stored message", async () => {
    const { hub, connection } = setup();
    hub.result = message({ seq: 7 });

    const stored = await connection.sendMessage("c1", "k1", "hi");

    expect(stored.seq).toBe(7);
    expect(hub.invocations).toEqual([{ method: "SendMessage", args: ["c1", "k1", "hi", null] }]);
  });

  it("markRead invokes MarkRead", async () => {
    const { hub, connection } = setup();

    await connection.markRead("c1", 9);

    expect(hub.invocations).toEqual([{ method: "MarkRead", args: ["c1", 9] }]);
  });

  it("a hub error ending in HubException: code becomes an ApiError with that code", async () => {
    const { hub, connection } = setup();
    hub.failure = new Error("An unexpected error occurred invoking 'SendMessage' on the server. HubException: rate_limited");

    const error = await connection.sendMessage("c1", "k1", "hi").catch((e) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error.code).toBe("rate_limited");
  });

  it("any other failure (the socket is down) becomes an ApiError with code connection_lost", async () => {
    const { hub, connection } = setup();
    hub.failure = new Error("Cannot send data if the connection is not in the 'Connected' State.");

    const error = await connection.sendMessage("c1", "k1", "hi").catch((e) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect(error.code).toBe("connection_lost");
  });
});

describe("events", () => {
  it("are forwarded to subscribers, and unsubscribe stops them", () => {
    const { hub, connection, handlers } = setup();
    const unsubscribe = connection.subscribe(handlers);

    hub.emit("message.created", message());
    hub.emit("message.edited", message({ body: "edited" }));
    hub.emit("message.deleted", message({ deletedAt: "2026-01-01T00:01:00Z" }));
    unsubscribe();
    hub.emit("message.created", message({ id: "m2" }));

    expect(handlers.onMessageCreated).toHaveBeenCalledTimes(1);
    expect(handlers.onMessageEdited).toHaveBeenCalledWith(expect.objectContaining({ body: "edited" }));
    expect(handlers.onMessageDeleted).toHaveBeenCalledOnce();
  });

  it("one bad subscriber does not stop the others", () => {
    const { hub, connection, handlers } = setup();
    connection.subscribe({ ...handlers, onMessageCreated: () => { throw new Error("boom"); } });
    const second = { ...handlers, onMessageCreated: vi.fn() };
    connection.subscribe(second);

    hub.emit("message.created", message());

    expect(second.onMessageCreated).toHaveBeenCalledOnce();
  });
});

describe("status", () => {
  it("is connected after start, reconnecting while the hub retries, and onReconnected fires afterwards", async () => {
    const { hub, connection, handlers } = setup();
    connection.subscribe(handlers);

    await connection.start();
    hub.reconnecting.forEach((cb) => cb());
    hub.reconnected.forEach((cb) => cb());

    expect(handlers.onStatus.mock.calls.map((c) => c[0])).toEqual(["connected", "reconnecting", "connected"]);
    expect(handlers.onReconnected).toHaveBeenCalledOnce();
  });

  it("is disconnected when the hub closes", async () => {
    const { hub, connection, handlers } = setup();
    connection.subscribe(handlers);
    await connection.start();

    hub.closed.forEach((cb) => cb());

    expect(handlers.onStatus).toHaveBeenLastCalledWith("disconnected");
  });

  it("reports disconnected and rethrows when start fails", async () => {
    const { hub, connection, handlers } = setup();
    connection.subscribe(handlers);
    hub.startFailure = new Error("401");

    await expect(connection.start()).rejects.toThrow("401");

    expect(handlers.onStatus).toHaveBeenLastCalledWith("disconnected");
  });
});
