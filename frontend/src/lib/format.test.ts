import { describe, expect, it } from "vitest";
import { conversationTitle, dayLabel, systemNoticeText, timeLabel } from "./format";
import type { ConversationSummary } from "./types";

const base: ConversationSummary = { id: "c", type: "direct", lastSeq: 0, unread: 0, role: "member", updatedAt: "2026-01-01T00:00:00Z" };
const names: Record<string, string> = { u1: "Ann", u2: "Bình" };
const nameOf = (id: string) => names[id] ?? "Someone";

describe("conversationTitle", () => {
  it("is the peer's display name in a direct chat", () => {
    expect(conversationTitle({ ...base, peer: { id: "p", username: "p", displayName: "Nguyễn Văn A" } })).toBe("Nguyễn Văn A");
  });

  it("is the title of a group", () => {
    expect(conversationTitle({ ...base, type: "group", title: "Weekend anglers" })).toBe("Weekend anglers");
  });

  it("falls back to Chat", () => {
    expect(conversationTitle(base)).toBe("Chat");
    expect(conversationTitle({ ...base, type: "group", title: "  " })).toBe("Chat");
  });
});

describe("dayLabel", () => {
  const now = new Date(2026, 5, 15, 12, 0, 0);

  it("says Today and Yesterday", () => {
    expect(dayLabel(new Date(2026, 5, 15, 8, 0).toISOString(), now)).toBe("Today");
    expect(dayLabel(new Date(2026, 5, 14, 23, 59).toISOString(), now)).toBe("Yesterday");
  });

  it("gives the date for older days, with the year when it differs", () => {
    expect(dayLabel(new Date(2026, 5, 1, 8, 0).toISOString(), now)).toBe("1 Jun");
    expect(dayLabel(new Date(2025, 11, 31, 8, 0).toISOString(), now)).toBe("31 Dec 2025");
  });
});

describe("timeLabel", () => {
  const now = new Date(2026, 5, 15, 12, 0, 0);

  it("is the clock time today and the short date otherwise", () => {
    expect(timeLabel(new Date(2026, 5, 15, 9, 5).toISOString(), now)).toBe("09:05");
    expect(timeLabel(new Date(2026, 5, 1, 9, 5).toISOString(), now)).toBe("1 Jun");
  });
});

describe("systemNoticeText", () => {
  it("renders each kind of group notice", () => {
    expect(systemNoticeText(JSON.stringify({ kind: "member_added", actorId: "u1", userId: "u2" }), nameOf)).toBe("Ann added Bình");
    expect(systemNoticeText(JSON.stringify({ kind: "member_removed", actorId: "u1", userId: "u2" }), nameOf)).toBe("Ann removed Bình");
    expect(systemNoticeText(JSON.stringify({ kind: "member_left", userId: "u2" }), nameOf)).toBe("Bình left");
    expect(systemNoticeText(JSON.stringify({ kind: "renamed", actorId: "u1", title: "Night crew" }), nameOf)).toBe("Ann renamed the group to “Night crew”");
  });

  it("uses a safe fallback for an unknown kind, bad JSON, or no body", () => {
    expect(systemNoticeText(JSON.stringify({ kind: "something_new" }), nameOf)).toBe("The group changed");
    expect(systemNoticeText("not json", nameOf)).toBe("The group changed");
    expect(systemNoticeText(null, nameOf)).toBe("The group changed");
    expect(systemNoticeText("42", nameOf)).toBe("The group changed");
  });
});
