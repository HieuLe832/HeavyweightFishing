/** The API's shapes, as the client reads them. Dates are ISO strings. */

export interface PersonBrief {
  id: string;
  username: string;
  displayName: string;
  avatarMediaId?: string | null;
}

export interface ConversationSummary {
  id: string;
  type: "direct" | "group";
  title?: string | null;
  peer?: PersonBrief | null;
  lastSeq: number;
  unread: number;
  mutedUntil?: string | null;
  role: string;
  updatedAt: string;
  avatarMediaId?: string | null;
}

export interface Attachment {
  mediaId: string;
  mimeType: string;
  sizeBytes: number;
  width?: number | null;
  height?: number | null;
}

export interface Reaction {
  emoji: string;
  count: number;
  mine: boolean;
}

export interface Message {
  id: string;
  conversationId: string;
  seq: number;
  senderId?: string | null;
  clientMsgId: string;
  type: "text" | "media" | "system";
  body?: string | null;
  replyToId?: string | null;
  createdAt: string;
  editedAt?: string | null;
  deletedAt?: string | null;
  reactions: Reaction[];
  attachments: Attachment[];
}

export interface MessagePage {
  messages: Message[];
  hasMore: boolean;
}

export type ConnectionStatus = "connected" | "reconnecting" | "disconnected";
