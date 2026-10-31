import Link from "next/link";
import { conversationTitle, timeLabel } from "@/lib/format";
import type { ConversationSummary } from "@/lib/types";
import { Bobber } from "./Bobber";
import { Button } from "./Button";

interface Props {
  items: ConversationSummary[];
  openId: string | null;
  hasMore: boolean;
  onLoadMore: () => void;
  now?: Date;
}

export function ConversationList({ items, openId, hasMore, onLoadMore, now = new Date() }: Props) {
  if (items.length === 0) {
    return <p className="p-4 text-center text-sand">No conversations yet.</p>;
  }
  return (
    <nav aria-label="Conversations" className="flex-1 overflow-y-auto">
      <ul>
        {items.map((c) => (
          <li key={c.id}>
            <Link
              href={`/c/${c.id}`}
              aria-current={c.id === openId ? "page" : undefined}
              className={`flex items-center gap-3 border-b border-water-light/20 px-4 py-3 hover:bg-water-light/15 ${c.id === openId ? "bg-water-light/25" : ""}`}
            >
              <span className="flex h-11 w-11 shrink-0 items-center justify-center rounded-full border-2 border-sand bg-water-mid font-display text-xl text-cream" aria-hidden="true">
                {conversationTitle(c).charAt(0).toUpperCase()}
              </span>
              <span className="min-w-0 flex-1 truncate font-display text-lg font-semibold">{conversationTitle(c)}</span>
              <span className="flex shrink-0 flex-col items-end gap-1 text-xs text-sand">
                <span>{timeLabel(c.updatedAt, now)}</span>
                <Bobber count={c.unread} />
              </span>
            </Link>
          </li>
        ))}
      </ul>
      {hasMore ? (
        <div className="flex justify-center p-3">
          <Button variant="ghost" onClick={onLoadMore} className="!py-1 !text-base">
            Show more
          </Button>
        </div>
      ) : null}
    </nav>
  );
}
