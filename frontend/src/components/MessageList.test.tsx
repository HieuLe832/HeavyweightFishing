import { fireEvent, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ChatMessage } from "@/lib/chat-state";
import { MessageList } from "./MessageList";

const ME = "me";
const NOW = new Date(2026, 5, 15, 12, 0, 0);
const at = (day: number, hour: number) => new Date(2026, 5, day, hour, 0).toISOString();

const msg = (seq: number, over: Partial<ChatMessage> = {}): ChatMessage => ({
  id: `m${seq}`, conversationId: "c", seq, senderId: "peer", clientMsgId: `k${seq}`, type: "text", body: `message ${seq}`, createdAt: at(15, 9),
  reactions: [], attachments: [], ...over,
});

const names: Record<string, string> = { me: "Me", peer: "Bình", other: "Cường" };

function renderList(messages: ChatMessage[], props: Partial<React.ComponentProps<typeof MessageList>> = {}) {
  const handlers = { onLoadOlder: vi.fn(), onRetry: vi.fn() };
  const view = render(
    <MessageList messages={messages} meId={ME} nameOf={(id) => names[id] ?? "Someone"} showSenders={false} hasMoreOlder={false} now={NOW} {...handlers} {...props} />,
  );
  return { ...view, ...handlers };
}

/** jsdom has no layout, so the scroll metrics the component reads are set by hand. */
function scrollMetrics(el: HTMLElement, m: { scrollHeight: number; clientHeight: number; scrollTop: number }) {
  Object.defineProperty(el, "scrollHeight", { configurable: true, value: m.scrollHeight });
  Object.defineProperty(el, "clientHeight", { configurable: true, value: m.clientHeight });
  el.scrollTop = m.scrollTop;
}

describe("MessageList", () => {
  it("separates days and aligns my messages to the right", () => {
    renderList([msg(1, { createdAt: at(14, 9) }), msg(2, { senderId: ME, createdAt: at(15, 9) })]);

    expect(screen.getByText("Yesterday")).toBeInTheDocument();
    expect(screen.getByText("Today")).toBeInTheDocument();
    expect(screen.getByText("message 2").closest("[data-mine]")).toHaveAttribute("data-mine", "true");
    expect(screen.getByText("message 1").closest("[data-mine]")).toHaveAttribute("data-mine", "false");
  });

  it("names the sender once per run in a group, but not in a direct chat", () => {
    const messages = [msg(1, { senderId: "peer" }), msg(2, { senderId: "peer" }), msg(3, { senderId: "other" })];
    const { unmount } = renderList(messages, { showSenders: true });
    expect(screen.getAllByText("Bình")).toHaveLength(1);
    expect(screen.getAllByText("Cường")).toHaveLength(1);
    unmount();

    renderList(messages, { showSenders: false });
    expect(screen.queryByText("Bình")).not.toBeInTheDocument();
  });

  it("wraps a 4000-character unbroken string inside the bubble", () => {
    renderList([msg(1, { body: "x".repeat(4000) })]);

    expect(screen.getByText("x".repeat(4000))).toHaveClass("break-words");
  });

  it("renders user text as text, never as markup", () => {
    renderList([msg(1, { body: '<img src=x onerror="alert(1)"> <b>bold</b>' })]);

    expect(screen.getByText('<img src=x onerror="alert(1)"> <b>bold</b>')).toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
    expect(document.querySelector("b")).toBeNull();
  });

  it("shows a tombstone for a deleted message", () => {
    renderList([msg(1, { body: null, deletedAt: at(15, 10) })]);

    expect(screen.getByText("Message deleted")).toBeInTheDocument();
  });

  it("renders system notices from their JSON as centered text", () => {
    renderList([msg(1, { type: "system", senderId: null, body: JSON.stringify({ kind: "member_left", userId: "peer" }) })]);

    expect(screen.getByText("Bình left")).toBeInTheDocument();
  });

  it("shows Sending and a Retry button for a failed message", async () => {
    const { onRetry } = renderList([msg(1, { senderId: ME, local: "sending" }), msg(2, { senderId: ME, local: "failed", clientMsgId: "k-failed" })]);

    expect(screen.getByText(/sending/i)).toBeInTheDocument();
    await userEvent.click(screen.getByRole("button", { name: /retry/i }));
    expect(onRetry).toHaveBeenCalledWith("k-failed");
  });

  it("offers older messages only when there are some, and asks for them", async () => {
    const { onLoadOlder, unmount } = renderList([msg(1)], { hasMoreOlder: true });
    await userEvent.click(screen.getByRole("button", { name: /earlier messages/i }));
    expect(onLoadOlder).toHaveBeenCalledOnce();
    unmount();

    renderList([msg(1)], { hasMoreOlder: false });
    expect(screen.queryByRole("button", { name: /earlier messages/i })).not.toBeInTheDocument();
  });

  it("shows a new-messages button when a message arrives while scrolled up, and scrolls down when it is clicked", async () => {
    const first = [msg(1), msg(2)];
    const { rerender } = renderList(first);
    const log = screen.getByRole("log");
    scrollMetrics(log, { scrollHeight: 1000, clientHeight: 300, scrollTop: 100 });
    fireEvent.scroll(log);

    rerender(<MessageList messages={[...first, msg(3)]} meId={ME} nameOf={(id) => names[id] ?? "Someone"} showSenders={false} hasMoreOlder={false} now={NOW} onLoadOlder={vi.fn()} onRetry={vi.fn()} />);

    const button = await screen.findByRole("button", { name: /new messages/i });
    scrollMetrics(log, { scrollHeight: 1200, clientHeight: 300, scrollTop: 100 });
    await userEvent.click(button);
    expect(log.scrollTop).toBe(1200);
    expect(screen.queryByRole("button", { name: /new messages/i })).not.toBeInTheDocument();
  });

  it("does not show the button when the person is already at the bottom", () => {
    const first = [msg(1)];
    const { rerender } = renderList(first);
    const log = screen.getByRole("log");
    scrollMetrics(log, { scrollHeight: 1000, clientHeight: 300, scrollTop: 690 });
    fireEvent.scroll(log);

    rerender(<MessageList messages={[...first, msg(2)]} meId={ME} nameOf={(id) => names[id] ?? "Someone"} showSenders={false} hasMoreOlder={false} now={NOW} onLoadOlder={vi.fn()} onRetry={vi.fn()} />);

    expect(screen.queryByRole("button", { name: /new messages/i })).not.toBeInTheDocument();
  });
});
