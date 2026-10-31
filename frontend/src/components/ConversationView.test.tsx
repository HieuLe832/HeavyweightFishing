import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ChatContext, type ChatValue } from "@/lib/chat";
import { initialState, reducer, type ChatState } from "@/lib/chat-state";
import type { ConversationSummary, Message } from "@/lib/types";
import { ConversationView } from "./ConversationView";

const conv: ConversationSummary = { id: "c1", type: "direct", peer: { id: "p1", username: "bin", displayName: "Bình" }, lastSeq: 1, unread: 0, role: "member", updatedAt: "2026-01-01T00:00:00Z" };
const message: Message = { id: "m1", conversationId: "c1", seq: 1, senderId: "p1", clientMsgId: "k1", type: "text", body: "Chào bạn", createdAt: "2026-01-01T00:00:00Z", reactions: [], attachments: [] };

function renderView(state: ChatState, overrides: Partial<ChatValue> = {}) {
  const chat: ChatValue = {
    state, meId: "me", hasMoreConversations: false, open: vi.fn(), loadOlder: vi.fn(), loadMoreConversations: vi.fn(), send: vi.fn(), retry: vi.fn(),
    nameOf: (id) => (id === "p1" ? "Bình" : "Someone"), ...overrides,
  };
  const view = render(<ChatContext.Provider value={chat}><ConversationView id="c1" /></ChatContext.Provider>);
  return { chat, ...view };
}

const loaded = (status: ChatState["status"] = "connected") =>
  reducer(reducer(reducer(initialState, { type: "conversationsLoaded", conversations: [conv] }), { type: "historyLoaded", conversationId: "c1", messages: [message], mode: "latest", hasMore: false }), { type: "status", status });

describe("ConversationView", () => {
  it("shows the title, the messages and the composer", () => {
    renderView(loaded());

    expect(screen.getByRole("heading", { name: "Bình" })).toBeInTheDocument();
    expect(screen.getByText("Chào bạn")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /message/i })).toBeEnabled();
  });

  it("tells the chat provider which conversation is open, and that none is when it goes away", () => {
    const { chat, unmount } = renderView(loaded());
    expect(chat.open).toHaveBeenCalledWith("c1");

    unmount();

    expect(chat.open).toHaveBeenLastCalledWith(null);
  });

  it("sends what is typed to this conversation", async () => {
    const { chat } = renderView(loaded());

    await userEvent.type(screen.getByRole("textbox", { name: /message/i }), "xin chào{Enter}");

    expect(chat.send).toHaveBeenCalledWith("c1", "xin chào");
  });

  it("says the conversation is not available once the list is loaded without it", () => {
    renderView(reducer(initialState, { type: "conversationsLoaded", conversations: [] }));

    expect(screen.getByText(/no longer available/i)).toBeInTheDocument();
    expect(screen.queryByRole("textbox")).not.toBeInTheDocument();
  });

  it("shows a loading note while the list has not loaded", () => {
    renderView(initialState);

    expect(screen.getByText(/loading/i)).toBeInTheDocument();
  });

  it("links back to the list", () => {
    renderView(loaded());

    expect(screen.getByRole("link", { name: /back/i })).toHaveAttribute("href", "/");
  });

  it("disables the composer while the connection is down", () => {
    renderView(loaded("reconnecting"));

    expect(screen.getByRole("textbox", { name: /message/i })).toBeDisabled();
  });
});
