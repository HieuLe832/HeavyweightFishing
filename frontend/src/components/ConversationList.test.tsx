import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import type { ConversationSummary } from "@/lib/types";
import { ConversationList } from "./ConversationList";

const NOW = new Date(2026, 5, 15, 12, 0, 0);
const conv = (id: string, over: Partial<ConversationSummary> = {}): ConversationSummary => ({
  id, type: "direct", peer: { id: "p" + id, username: "u" + id, displayName: "Peer " + id }, lastSeq: 1, unread: 0, role: "member",
  updatedAt: new Date(2026, 5, 15, 9, 5).toISOString(), ...over,
});

describe("ConversationList", () => {
  it("lists the conversations in the order given, as links to their chats", () => {
    render(<ConversationList items={[conv("b"), conv("a")]} openId={null} now={NOW} hasMore={false} onLoadMore={vi.fn()} />);

    const links = screen.getAllByRole("link");
    expect(links.map((l) => l.textContent)).toEqual([expect.stringContaining("Peer b"), expect.stringContaining("Peer a")]);
    expect(links[0]).toHaveAttribute("href", "/c/b");
  });

  it("shows the unread bobber and the time", () => {
    render(<ConversationList items={[conv("a", { unread: 3 })]} openId={null} now={NOW} hasMore={false} onLoadMore={vi.fn()} />);

    const row = screen.getByRole("link");
    expect(within(row).getByLabelText("3 unread")).toBeInTheDocument();
    expect(within(row).getByText("09:05")).toBeInTheDocument();
  });

  it("marks the open conversation", () => {
    render(<ConversationList items={[conv("a"), conv("b")]} openId="b" now={NOW} hasMore={false} onLoadMore={vi.fn()} />);

    expect(screen.getByRole("link", { name: /Peer b/ })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("link", { name: /Peer a/ })).not.toHaveAttribute("aria-current");
  });

  it("says so when there are no conversations", () => {
    render(<ConversationList items={[]} openId={null} now={NOW} hasMore={false} onLoadMore={vi.fn()} />);

    expect(screen.getByText(/no conversations yet/i)).toBeInTheDocument();
  });

  it("offers more only when the server has more", async () => {
    const onLoadMore = vi.fn();
    const { rerender } = render(<ConversationList items={[conv("a")]} openId={null} now={NOW} hasMore onLoadMore={onLoadMore} />);

    await userEvent.click(screen.getByRole("button", { name: /show more/i }));
    expect(onLoadMore).toHaveBeenCalledOnce();

    rerender(<ConversationList items={[conv("a")]} openId={null} now={NOW} hasMore={false} onLoadMore={onLoadMore} />);
    expect(screen.queryByRole("button", { name: /show more/i })).not.toBeInTheDocument();
  });
});
