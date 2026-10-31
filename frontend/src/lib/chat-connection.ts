import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { ApiError } from "./api";
import type { ConnectionStatus, Message } from "./types";

/** The part of a SignalR connection this app uses, so tests can stand in a fake. */
export interface HubLike {
  start(): Promise<void>;
  stop(): Promise<void>;
  invoke<T>(method: string, ...args: unknown[]): Promise<T>;
  on(event: string, callback: (...args: any[]) => void): void; // eslint-disable-line @typescript-eslint/no-explicit-any
  onreconnecting(callback: () => void): void;
  onreconnected(callback: () => void): void;
  onclose(callback: () => void): void;
}

export interface ChatHandlers {
  onMessageCreated(message: Message): void;
  onMessageEdited(message: Message): void;
  onMessageDeleted(message: Message): void;
  /** The socket came back after a drop: events in between were missed, so refetch. */
  onReconnected(): void;
  onStatus(status: ConnectionStatus): void;
}

export interface ChatConnection {
  start(): Promise<void>;
  stop(): Promise<void>;
  sendMessage(conversationId: string, clientMsgId: string, body: string): Promise<Message>;
  markRead(conversationId: string, upToSeq: number): Promise<void>;
  subscribe(handlers: ChatHandlers): () => void;
}

export interface ChatConnectionOptions {
  hubUrl: string;
  getAccessToken: () => string | null;
  hub?: HubLike;
}

function defaultHub({ hubUrl, getAccessToken }: ChatConnectionOptions): HubLike {
  // The hub reads the token from the query string (a browser WebSocket cannot set headers); the factory runs on every reconnect.
  // Credentials stay off: the API's CORS policy does not allow them, and the token is not a cookie.
  const connection = new HubConnectionBuilder()
    .withUrl(hubUrl, { accessTokenFactory: () => getAccessToken() ?? "", withCredentials: false })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
  return {
    start: () => connection.start(),
    stop: () => connection.stop(),
    invoke: (method, ...args) => connection.invoke(method, ...args),
    on: (event, callback) => connection.on(event, callback),
    onreconnecting: (callback) => connection.onreconnecting(() => callback()),
    onreconnected: (callback) => connection.onreconnected(() => callback()),
    onclose: (callback) => connection.onclose(() => callback()),
  };
}

/** A hub failure carries the API's error code at the end of its message: "...HubException: rate_limited". */
function toApiError(error: unknown): ApiError {
  const message = error instanceof Error ? error.message : String(error);
  const code = /HubException:\s*(\w+)\s*$/.exec(message)?.[1];
  return new ApiError(0, code ?? "connection_lost", message);
}

export function createChatConnection(options: ChatConnectionOptions): ChatConnection {
  const hub = options.hub ?? defaultHub(options);
  const subscribers = new Set<ChatHandlers>();

  const each = (call: (handlers: ChatHandlers) => void) => {
    for (const handlers of subscribers) {
      try {
        call(handlers);
      } catch (e) {
        console.error("chat subscriber failed", e);
      }
    }
  };

  hub.on("message.created", (m: Message) => each((h) => h.onMessageCreated(m)));
  hub.on("message.edited", (m: Message) => each((h) => h.onMessageEdited(m)));
  hub.on("message.deleted", (m: Message) => each((h) => h.onMessageDeleted(m)));
  hub.onreconnecting(() => each((h) => h.onStatus("reconnecting")));
  hub.onreconnected(() => {
    each((h) => h.onStatus("connected"));
    each((h) => h.onReconnected());
  });
  hub.onclose(() => each((h) => h.onStatus("disconnected")));

  async function invoke<T>(method: string, ...args: unknown[]): Promise<T> {
    try {
      return await hub.invoke<T>(method, ...args);
    } catch (e) {
      throw toApiError(e);
    }
  }

  return {
    async start() {
      try {
        await hub.start();
      } catch (e) {
        each((h) => h.onStatus("disconnected"));
        throw e;
      }
      each((h) => h.onStatus("connected"));
    },
    stop: () => hub.stop(),
    sendMessage: (conversationId, clientMsgId, body) => invoke<Message>("SendMessage", conversationId, clientMsgId, body, null),
    markRead: (conversationId, upToSeq) => invoke<void>("MarkRead", conversationId, upToSeq),
    subscribe(handlers) {
      subscribers.add(handlers);
      return () => {
        subscribers.delete(handlers);
      };
    },
  };
}
