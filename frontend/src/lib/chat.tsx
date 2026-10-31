"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import { useAuth } from "./auth";
import { createChatConnection, type ChatConnection, type ChatConnectionOptions } from "./chat-connection";
import { initialState, latestSeq, needsConversationRefetch, reducer, type ChatState } from "./chat-state";
import { config } from "./config";
import type { ConversationSummary, MessagePage } from "./types";

const PAGE = 50;
const RESTART_DELAYS = [1000, 2000, 5000, 10000];
const MARK_READ_DELAY = 300;

export interface ChatValue {
  state: ChatState;
  meId: string;
  hasMoreConversations: boolean;
  /** Which conversation is on screen (null for none). Loads its messages and members. */
  open(conversationId: string | null): void;
  loadOlder(conversationId: string): Promise<void>;
  loadMoreConversations(): Promise<void>;
  send(conversationId: string, body: string): void;
  retry(conversationId: string, clientMsgId: string): void;
  /** A person's display name, or "Someone" when it is not known. */
  nameOf(userId: string): string;
}

export const ChatContext = createContext<ChatValue | null>(null);

export function useChat(): ChatValue {
  const value = useContext(ChatContext);
  if (!value) throw new Error("useChat must be used inside <ChatProvider>");
  return value;
}

interface Props {
  children: ReactNode;
  /** Tests pass a fake connection. */
  createConnection?: (options: ChatConnectionOptions) => ChatConnection;
}

export function ChatProvider({ children, createConnection = createChatConnection }: Props) {
  const { user, api } = useAuth();
  const meId = user!.id; // rendered only for a signed-in person
  const [state, dispatch] = useReducer(reducer, initialState);
  const [memberNames, setMemberNames] = useState<Record<string, string>>({});
  const [hasMoreConversations, setHasMoreConversations] = useState(false);
  const [visible, setVisible] = useState(() => typeof document === "undefined" || document.visibilityState === "visible");

  const stateRef = useRef(state);
  const connectionRef = useRef<ChatConnection | null>(null);
  const membersLoaded = useRef(new Set<string>());
  const lastMarked = useRef<Record<string, number>>({});
  useEffect(() => {
    stateRef.current = state;
  });

  const loadConversations = useCallback(async () => {
    const list = await api.request<ConversationSummary[]>("GET", `/conversations?limit=${PAGE}`);
    dispatch({ type: "conversationsLoaded", conversations: list });
    setHasMoreConversations(list.length === PAGE);
  }, [api]);

  /** Fetches what a conversation has after the newest message held, until caught up, then refreshes the latest page (edits and deletes are not replayed). */
  const syncConversation = useCallback(async (conversationId: string) => {
    let after = latestSeq(stateRef.current, conversationId);
    if (after === 0) {
      const page = await api.request<MessagePage>("GET", `/conversations/${conversationId}/messages?limit=${PAGE}`);
      dispatch({ type: "historyLoaded", conversationId, messages: page.messages, mode: "latest", hasMore: page.hasMore });
      return;
    }
    for (;;) {
      const page = await api.request<MessagePage>("GET", `/conversations/${conversationId}/messages?after=${after}&limit=${PAGE}`);
      dispatch({ type: "historyLoaded", conversationId, messages: page.messages, mode: "gap" });
      const newest = page.messages.at(-1)?.seq ?? after;
      if (!page.hasMore || newest <= after) break; // stop if the server claims more but gives nothing newer
      after = newest;
    }
    const latest = await api.request<MessagePage>("GET", `/conversations/${conversationId}/messages?limit=${PAGE}`);
    dispatch({ type: "historyLoaded", conversationId, messages: latest.messages, mode: "gap" });
  }, [api]);

  const resync = useCallback(async () => {
    await loadConversations();
    const openId = stateRef.current.openId;
    if (openId) await syncConversation(openId);
  }, [loadConversations, syncConversation]);

  // One connection for as long as the person is signed in. If it drops for good, start it again with a growing pause.
  useEffect(() => {
    const connection = createConnection({ hubUrl: `${config.apiUrl}/hubs/chat`, getAccessToken: () => api.accessToken() });
    connectionRef.current = connection;
    let stopped = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;

    const connect = async () => {
      try {
        await connection.start();
        attempt = 0;
        await resync();
      } catch {
        if (stopped) return;
        const delay = RESTART_DELAYS[Math.min(attempt++, RESTART_DELAYS.length - 1)];
        timer = setTimeout(async () => {
          // The access token may have expired while the socket was down; a refused refresh signs the person out.
          await api.refresh().catch(() => {});
          if (!stopped) void connect();
        }, delay);
      }
    };

    const unsubscribe = connection.subscribe({
      onMessageCreated: (message) => {
        dispatch({ type: "messageCreated", message, meId });
        if (needsConversationRefetch(stateRef.current, message)) void loadConversations().catch(() => {});
      },
      onMessageEdited: (message) => dispatch({ type: "messageEdited", message }),
      onMessageDeleted: (message) => dispatch({ type: "messageDeleted", message }),
      onReconnected: () => void resync().catch(() => {}),
      onStatus: (status) => {
        dispatch({ type: "status", status });
        if (status === "disconnected" && !stopped && timer === undefined) {
          timer = setTimeout(() => {
            timer = undefined;
            void connect();
          }, RESTART_DELAYS[0]);
        }
      },
    });
    void connect();

    return () => {
      stopped = true;
      clearTimeout(timer);
      unsubscribe();
      void connection.stop();
      connectionRef.current = null;
    };
  }, [api, createConnection, meId, resync, loadConversations]);

  useEffect(() => {
    const onVisibility = () => setVisible(document.visibilityState === "visible");
    document.addEventListener("visibilitychange", onVisibility);
    return () => document.removeEventListener("visibilitychange", onVisibility);
  }, []);

  // Tell the server what has been read: after a short pause, once, for the newest message of the open conversation.
  const openId = state.openId;
  const newest = openId ? latestSeq(state, openId) : 0;
  const connected = state.status === "connected";
  useEffect(() => {
    if (!openId || newest === 0 || !connected || !visible || (lastMarked.current[openId] ?? 0) >= newest) return;
    const timer = setTimeout(() => {
      if (document.visibilityState !== "visible") return;
      connectionRef.current
        ?.markRead(openId, newest)
        .then(() => {
          lastMarked.current[openId] = newest;
          dispatch({ type: "markedRead", conversationId: openId });
        })
        .catch(() => {});
    }, MARK_READ_DELAY);
    return () => clearTimeout(timer);
  }, [openId, newest, connected, visible]);

  const open = useCallback((conversationId: string | null) => {
    dispatch({ type: "opened", conversationId });
    if (!conversationId) return;
    void syncConversation(conversationId).catch(() => {});
    if (!membersLoaded.current.has(conversationId)) {
      membersLoaded.current.add(conversationId);
      api.request<{ members: { userId: string; displayName: string }[] }>("GET", `/conversations/${conversationId}`)
        .then((detail) => setMemberNames((names) => ({ ...names, ...Object.fromEntries(detail.members.map((m) => [m.userId, m.displayName])) })))
        .catch(() => membersLoaded.current.delete(conversationId));
    }
  }, [api, syncConversation]);

  const loadOlder = useCallback(async (conversationId: string) => {
    const first = (stateRef.current.messages[conversationId] ?? []).find((m) => !m.local);
    if (!first) return;
    const page = await api.request<MessagePage>("GET", `/conversations/${conversationId}/messages?before=${first.seq}&limit=${PAGE}`);
    dispatch({ type: "historyLoaded", conversationId, messages: page.messages, mode: "older", hasMore: page.hasMore });
  }, [api]);

  const loadMoreConversations = useCallback(async () => {
    const { order, conversations } = stateRef.current;
    const oldest = order.length ? conversations[order[order.length - 1]] : undefined;
    if (!oldest) return;
    const list = await api.request<ConversationSummary[]>("GET", `/conversations?limit=${PAGE}&before=${encodeURIComponent(oldest.updatedAt)}`);
    dispatch({ type: "conversationsAppended", conversations: list });
    setHasMoreConversations(list.length === PAGE);
  }, [api]);

  const deliver = useCallback(async (conversationId: string, clientMsgId: string, body: string) => {
    try {
      const message = await connectionRef.current!.sendMessage(conversationId, clientMsgId, body);
      dispatch({ type: "sendAcked", message });
    } catch {
      dispatch({ type: "sendFailed", conversationId, clientMsgId });
    }
  }, []);

  const send = useCallback((conversationId: string, body: string) => {
    const clientMsgId = crypto.randomUUID();
    dispatch({ type: "sendStarted", conversationId, clientMsgId, body, meId, createdAt: new Date().toISOString() });
    void deliver(conversationId, clientMsgId, body);
  }, [deliver, meId]);

  const retry = useCallback((conversationId: string, clientMsgId: string) => {
    const failed = (stateRef.current.messages[conversationId] ?? []).find((m) => m.local === "failed" && m.clientMsgId === clientMsgId);
    if (!failed?.body) return;
    dispatch({ type: "sendStarted", conversationId, clientMsgId, body: failed.body, meId, createdAt: failed.createdAt });
    void deliver(conversationId, clientMsgId, failed.body);
  }, [deliver, meId]);

  const names = useMemo(() => {
    const all: Record<string, string> = { [meId]: user!.displayName };
    for (const c of Object.values(state.conversations)) if (c.peer) all[c.peer.id] = c.peer.displayName;
    return { ...all, ...memberNames };
  }, [state.conversations, memberNames, meId, user]);
  const nameOf = useCallback((userId: string) => names[userId] ?? "Someone", [names]);

  const value = useMemo<ChatValue>(
    () => ({ state, meId, hasMoreConversations, open, loadOlder, loadMoreConversations, send, retry, nameOf }),
    [state, meId, hasMoreConversations, open, loadOlder, loadMoreConversations, send, retry, nameOf],
  );
  return <ChatContext.Provider value={value}>{children}</ChatContext.Provider>;
}
