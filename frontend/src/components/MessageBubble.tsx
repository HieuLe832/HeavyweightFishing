import type { ChatMessage } from "@/lib/chat-state";
import { systemNoticeText, timeLabel } from "@/lib/format";

interface Props {
  message: ChatMessage;
  mine: boolean;
  /** The sender's name, shown above the first message of a run in a group. */
  senderName?: string;
  nameOf: (userId: string) => string;
  now: Date;
  onRetry: (clientMsgId: string) => void;
}

export function MessageBubble({ message, mine, senderName, nameOf, now, onRetry }: Props) {
  if (message.type === "system") {
    return <p className="my-2 text-center text-sm text-water-light">{systemNoticeText(message.body, nameOf)}</p>;
  }

  const deleted = message.deletedAt != null;
  const text = deleted ? null : message.body;
  return (
    <div data-mine={mine} className={`flex flex-col ${mine ? "items-end" : "items-start"}`}>
      {senderName ? <span className="mx-2 mt-2 text-xs font-bold text-gold">{senderName}</span> : null}
      <div
        className={`max-w-[85%] rounded-2xl border-2 px-3 py-2 shadow-sm md:max-w-[70%] ${
          mine ? "border-gold-dark bg-gold text-ink" : "border-sand bg-cream text-ink"
        } ${message.local === "failed" ? "border-coral" : ""}`}
      >
        {deleted ? <p className="italic opacity-70">Message deleted</p> : null}
        {!deleted && message.type === "media" ? <p className="font-semibold">Sent an attachment</p> : null}
        {text ? <p className="whitespace-pre-wrap break-words">{text}</p> : null}
        <p className="mt-1 flex items-center justify-end gap-2 text-xs opacity-70">
          {message.local === "sending" ? <span>Sending…</span> : null}
          {message.local === "failed" ? (
            <>
              <span className="font-bold text-coral">Not sent</span>
              <button type="button" onClick={() => onRetry(message.clientMsgId)} className="font-bold underline">
                Retry
              </button>
            </>
          ) : null}
          {message.editedAt && !deleted ? <span>edited</span> : null}
          <span>{timeLabel(message.createdAt, now)}</span>
        </p>
      </div>
    </div>
  );
}
