"use client";

import { Fragment, useLayoutEffect, useRef, useState, type UIEvent } from "react";
import type { ChatMessage } from "@/lib/chat-state";
import { dayLabel } from "@/lib/format";
import { Button } from "./Button";
import { MessageBubble } from "./MessageBubble";

interface Props {
  messages: ChatMessage[];
  meId: string;
  nameOf: (userId: string) => string;
  /** Show who sent each run of messages (groups; in a direct chat there is only one other person). */
  showSenders: boolean;
  hasMoreOlder: boolean;
  onLoadOlder: () => void;
  onRetry: (clientMsgId: string) => void;
  now?: Date;
}

const NEAR_BOTTOM = 80;
const NEAR_TOP = 40;

export function MessageList({ messages, meId, nameOf, showSenders, hasMoreOlder, onLoadOlder, onRetry, now = new Date() }: Props) {
  const logRef = useRef<HTMLDivElement>(null);
  const atBottom = useRef(true);
  const olderFrom = useRef<number | null>(null); // scrollHeight when older messages were requested
  const [view, setView] = useState<{ atBottom: boolean; seenId?: string }>({ atBottom: true });

  const firstId = messages[0]?.id;
  const last = messages.at(-1);
  const lastId = last?.id;
  const lastIsMine = last?.senderId === meId;

  useLayoutEffect(() => {
    const el = logRef.current;
    if (!el) return;
    if (olderFrom.current !== null) {
      // Older messages were added above: keep what the person was reading where it was.
      el.scrollTop += el.scrollHeight - olderFrom.current;
      olderFrom.current = null;
      return;
    }
    if (atBottom.current || lastIsMine) {
      el.scrollTop = el.scrollHeight;
      atBottom.current = true;
    }
  }, [firstId, lastId, lastIsMine]);

  function requestOlder() {
    if (olderFrom.current !== null || !hasMoreOlder) return;
    olderFrom.current = logRef.current?.scrollHeight ?? 0;
    onLoadOlder();
  }

  function onScroll(event: UIEvent<HTMLDivElement>) {
    const el = event.currentTarget;
    const near = el.scrollHeight - el.scrollTop - el.clientHeight <= NEAR_BOTTOM;
    atBottom.current = near;
    setView((v) => (near ? (v.atBottom ? v : { atBottom: true }) : v.atBottom ? { atBottom: false, seenId: lastId } : v));
    if (el.scrollTop < NEAR_TOP) requestOlder();
  }

  function scrollToEnd() {
    const el = logRef.current;
    if (el) el.scrollTop = el.scrollHeight;
    atBottom.current = true;
    setView({ atBottom: true });
  }

  const unseen = !view.atBottom && view.seenId !== lastId;
  const rows = messages.map((message, i) => {
    const previous = messages[i - 1];
    const day = dayLabel(message.createdAt, now);
    const newDay = !previous || dayLabel(previous.createdAt, now) !== day;
    const newRun = newDay || previous.type === "system" || message.type === "system" || previous.senderId !== message.senderId;
    return { message, day, newDay, newRun };
  });

  return (
    <div className="relative flex min-h-0 flex-1 flex-col">
      <div ref={logRef} role="log" aria-label="Messages" aria-live="polite" onScroll={onScroll} className="flex-1 overflow-y-auto px-3 py-2">
        {hasMoreOlder ? (
          <div className="mb-2 flex justify-center">
            <Button variant="ghost" onClick={requestOlder} className="!px-3 !py-1 !text-sm">
              Load earlier messages
            </Button>
          </div>
        ) : null}
        {rows.map(({ message, day, newDay, newRun }) => {
          const mine = message.senderId === meId;
          return (
            <Fragment key={message.id}>
              {newDay ? <p className="my-3 text-center"><span className="rounded-full bg-ink px-3 py-1 text-xs font-bold text-paper">{day}</span></p> : null}
              <div className={newRun ? "mt-2" : "mt-0.5"}>
                <MessageBubble
                  message={message}
                  mine={mine}
                  senderName={showSenders && !mine && newRun && message.type !== "system" && message.senderId ? nameOf(message.senderId) : undefined}
                  nameOf={nameOf}
                  now={now}
                  onRetry={onRetry}
                />
              </div>
            </Fragment>
          );
        })}
      </div>
      {unseen ? (
        <div className="pointer-events-none absolute inset-x-0 bottom-3 flex justify-center">
          <Button onClick={scrollToEnd} className="pointer-events-auto !py-1 !text-sm">
            New messages ↓
          </Button>
        </div>
      ) : null}
    </div>
  );
}
