import type { ConnectionStatus, ConversationSummary, Message } from "./types";

/** A message as shown: `local` marks one that the server has not confirmed yet. */
export type ChatMessage = Message & { local?: "sending" | "failed" };

export interface ChatState {
  conversations: Record<string, ConversationSummary>;
  /** Conversation ids, newest activity first. */
  order: string[];
  /** Per conversation: confirmed messages by seq, then unconfirmed local ones in the order they were sent. */
  messages: Record<string, ChatMessage[]>;
  /** Per conversation: whether older history exists on the server. */
  hasMoreOlder: Record<string, boolean>;
  openId: string | null;
  status: ConnectionStatus;
  /** True once the list has been fetched, so an empty list can be told from one still loading. */
  listLoaded: boolean;
  /** An event arrived for a conversation the list does not have: refetch the list. */
  missingConversation: boolean;
}

export const initialState: ChatState = {
  conversations: {},
  order: [],
  messages: {},
  hasMoreOlder: {},
  openId: null,
  status: "disconnected",
  listLoaded: false,
  missingConversation: false,
};

export type Action =
  | { type: "conversationsLoaded"; conversations: ConversationSummary[] }
  | { type: "conversationsAppended"; conversations: ConversationSummary[] }
  | { type: "historyLoaded"; conversationId: string; messages: Message[]; mode: "latest" | "older" | "gap"; hasMore?: boolean }
  | { type: "messageCreated"; message: Message; meId: string }
  | { type: "messageEdited"; message: Message }
  | { type: "messageDeleted"; message: Message }
  | { type: "sendStarted"; conversationId: string; clientMsgId: string; body: string; meId: string; createdAt: string }
  | { type: "sendAcked"; message: Message }
  | { type: "sendFailed"; conversationId: string; clientMsgId: string }
  | { type: "opened"; conversationId: string | null }
  | { type: "markedRead"; conversationId: string }
  | { type: "status"; status: ConnectionStatus };

export const latestSeq = (state: ChatState, conversationId: string): number =>
  (state.messages[conversationId] ?? []).reduce((max, m) => (m.local ? max : Math.max(max, m.seq)), 0);

export const needsConversationRefetch = (state: ChatState, message: Message): boolean => !(message.conversationId in state.conversations);

const byActivity = (conversations: Record<string, ConversationSummary>) => (a: string, b: string) =>
  conversations[b].updatedAt.localeCompare(conversations[a].updatedAt);

/** Puts confirmed messages in seq order and keeps the unconfirmed ones after them. */
function arrange(list: ChatMessage[]): ChatMessage[] {
  const confirmed = list.filter((m) => !m.local).sort((a, b) => a.seq - b.seq);
  return [...confirmed, ...list.filter((m) => m.local)];
}

/** Adds a confirmed message, or replaces the one it stands for: the same id, or the local message with the same clientMsgId. */
function upsert(list: ChatMessage[], message: Message): { list: ChatMessage[]; added: boolean } {
  const index = list.findIndex((m) => m.id === message.id || (m.local && m.clientMsgId === message.clientMsgId));
  if (index === -1) return { list: arrange([...list, message]), added: true };
  const replaced = [...list];
  replaced[index] = message;
  // The same message may also be present as a local copy when the event beat the ack, so drop any other local twin.
  return { list: arrange(replaced.filter((m, i) => i === index || !(m.local && m.clientMsgId === message.clientMsgId))), added: list[index].local !== undefined };
}

function withConversationActivity(state: ChatState, message: Message, moveToTop: boolean): ChatState {
  const conversation = state.conversations[message.conversationId];
  if (!conversation) return state;
  const updated: ConversationSummary = {
    ...conversation,
    lastSeq: Math.max(conversation.lastSeq, message.seq),
    updatedAt: message.createdAt > conversation.updatedAt ? message.createdAt : conversation.updatedAt,
  };
  return {
    ...state,
    conversations: { ...state.conversations, [message.conversationId]: updated },
    order: moveToTop ? [message.conversationId, ...state.order.filter((id) => id !== message.conversationId)] : state.order,
  };
}

function setUnread(state: ChatState, conversationId: string, unread: number): ChatState {
  const conversation = state.conversations[conversationId];
  if (!conversation || conversation.unread === unread) return state;
  return { ...state, conversations: { ...state.conversations, [conversationId]: { ...conversation, unread } } };
}

function mergeConversations(state: ChatState, incoming: ConversationSummary[], replace: boolean): ChatState {
  const conversations = replace ? {} : { ...state.conversations };
  for (const c of incoming) conversations[c.id] = c.id === state.openId ? { ...c, unread: 0 } : c;
  return { ...state, conversations, order: Object.keys(conversations).sort(byActivity(conversations)), listLoaded: true, missingConversation: false };
}

export function reducer(state: ChatState, action: Action): ChatState {
  switch (action.type) {
    case "conversationsLoaded":
      return mergeConversations(state, action.conversations, true);

    case "conversationsAppended":
      return mergeConversations(state, action.conversations, false);

    case "historyLoaded": {
      let list = state.messages[action.conversationId] ?? [];
      for (const m of action.messages) list = upsert(list, m).list;
      return {
        ...state,
        messages: { ...state.messages, [action.conversationId]: list },
        hasMoreOlder: action.mode === "gap" || action.hasMore === undefined ? state.hasMoreOlder : { ...state.hasMoreOlder, [action.conversationId]: action.hasMore },
      };
    }

    case "messageCreated": {
      const { message } = action;
      const { list, added } = upsert(state.messages[message.conversationId] ?? [], message);
      let next: ChatState = { ...state, messages: { ...state.messages, [message.conversationId]: list } };
      if (needsConversationRefetch(state, message)) return { ...next, missingConversation: true };
      if (!added) return next; // a duplicate event
      next = withConversationActivity(next, message, true);
      const counts = message.type !== "system" && message.senderId !== action.meId && message.conversationId !== state.openId;
      return counts ? setUnread(next, message.conversationId, next.conversations[message.conversationId].unread + 1) : next;
    }

    case "messageEdited":
    case "messageDeleted": {
      const list = state.messages[action.message.conversationId];
      if (!list?.some((m) => m.id === action.message.id)) return state;
      return { ...state, messages: { ...state.messages, [action.message.conversationId]: list.map((m) => (m.id === action.message.id ? action.message : m)) } };
    }

    case "sendStarted": {
      const list = state.messages[action.conversationId] ?? [];
      const existing = list.find((m) => m.clientMsgId === action.clientMsgId);
      if (existing && !existing.local) return state;
      if (existing) {
        return { ...state, messages: { ...state.messages, [action.conversationId]: list.map((m) => (m === existing ? { ...m, local: "sending" as const } : m)) } };
      }
      const local: ChatMessage = {
        id: `local:${action.clientMsgId}`, conversationId: action.conversationId, seq: 0, senderId: action.meId, clientMsgId: action.clientMsgId,
        type: "text", body: action.body, createdAt: action.createdAt, reactions: [], attachments: [], local: "sending",
      };
      return { ...state, messages: { ...state.messages, [action.conversationId]: [...list, local] } };
    }

    case "sendAcked": {
      const { list } = upsert(state.messages[action.message.conversationId] ?? [], action.message);
      return withConversationActivity({ ...state, messages: { ...state.messages, [action.message.conversationId]: list } }, action.message, true);
    }

    case "sendFailed": {
      const list = state.messages[action.conversationId];
      if (!list?.some((m) => m.local && m.clientMsgId === action.clientMsgId)) return state;
      return { ...state, messages: { ...state.messages, [action.conversationId]: list.map((m) => (m.local && m.clientMsgId === action.clientMsgId ? { ...m, local: "failed" as const } : m)) } };
    }

    case "opened":
      return action.conversationId ? setUnread({ ...state, openId: action.conversationId }, action.conversationId, 0) : { ...state, openId: null };

    case "markedRead":
      return setUnread(state, action.conversationId, 0);

    case "status":
      return { ...state, status: action.status };
  }
}
