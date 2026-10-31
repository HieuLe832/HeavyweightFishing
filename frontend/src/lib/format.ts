import type { ConversationSummary } from "./types";

const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

export function conversationTitle(conversation: ConversationSummary): string {
  const title = conversation.type === "direct" ? conversation.peer?.displayName : conversation.title;
  return title?.trim() || "Chat";
}

const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();

function shortDate(date: Date, now: Date): string {
  const base = `${date.getDate()} ${MONTHS[date.getMonth()]}`;
  return date.getFullYear() === now.getFullYear() ? base : `${base} ${date.getFullYear()}`;
}

/** The divider text above a run of messages from one day. */
export function dayLabel(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  if (sameDay(date, now)) return "Today";
  const yesterday = new Date(now);
  yesterday.setDate(now.getDate() - 1);
  return sameDay(date, yesterday) ? "Yesterday" : shortDate(date, now);
}

/** The clock time for today, the short date for any other day. */
export function timeLabel(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  if (!sameDay(date, now)) return shortDate(date, now);
  return `${String(date.getHours()).padStart(2, "0")}:${String(date.getMinutes()).padStart(2, "0")}`;
}

/** The text of a group notice. The API sends JSON; the client words it, since the wording is presentation. */
export function systemNoticeText(body: string | null | undefined, nameOf: (userId: string) => string): string {
  const fallback = "The group changed";
  if (!body) return fallback;
  let notice: { kind?: string; actorId?: string; userId?: string; title?: string };
  try {
    notice = JSON.parse(body);
  } catch {
    return fallback;
  }
  if (typeof notice !== "object" || notice === null) return fallback;
  const actor = notice.actorId ? nameOf(notice.actorId) : "Someone";
  const target = notice.userId ? nameOf(notice.userId) : "someone";
  switch (notice.kind) {
    case "member_added":
      return `${actor} added ${target}`;
    case "member_removed":
      return `${actor} removed ${target}`;
    case "member_left":
      return `${target} left`;
    case "renamed":
      return `${actor} renamed the group to “${notice.title ?? ""}”`;
    default:
      return fallback;
  }
}
