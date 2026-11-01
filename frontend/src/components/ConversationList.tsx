import Link from "next/link";
import { conversationTitle, timeLabel } from "@/lib/format";
import type { ConversationSummary } from "@/lib/types";
import { Avatar } from "./Avatar";
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
    return <p className="p-4 text-center font-semibold text-ink/70">No conversations yet.</p>;
  }
  return (
    <nav aria-label="Conversations" className="flex-1 overflow-y-auto p-2">
      <ul className="flex flex-col gap-1.5">
        {items.map((c) => (
          <li key={c.id}>
            <Link
              href={`/c/${c.id}`}
              aria-current={c.id === openId ? "page" : undefined}
              className={`flex items-center gap-3 rounded-xl border-3 px-3 py-2.5 transition ${
                c.id === openId ? "border-ink bg-paper shadow-hard-md" : "border-transparent hover:bg-butter-light"
              }`}
            >
              <Avatar
                name={conversationTitle(c)}
                mediaId={c.type === "direct" ? c.peer?.avatarMediaId : c.avatarMediaId}
                presetKey={c.type === "direct" ? c.peer?.avatarPresetKey : null}
              />
              <span className="min-w-0 flex-1 truncate font-semibold">{conversationTitle(c)}</span>
              <span className="flex shrink-0 flex-col items-end gap-1 text-xs font-semibold text-ink/70">
                <span>{timeLabel(c.updatedAt, now)}</span>
                <Bobber count={c.unread} />
              </span>
            </Link>
          </li>
        ))}
      </ul>
      {hasMore ? (
        <div className="flex justify-center p-3">
          <Button variant="ghost" onClick={onLoadMore} className="!py-1 !text-sm">
            Show more
          </Button>
        </div>
      ) : null}
    </nav>
  );
}
