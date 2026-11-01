"use client";

import Link from "next/link";
import { useEffect } from "react";
import { useChat } from "@/lib/chat";
import { conversationTitle } from "@/lib/format";
import { Composer } from "./Composer";
import { MessageList } from "./MessageList";

/** One open conversation: header, messages and composer. */
export function ConversationView({ id }: { id: string }) {
  const { state, meId, open, loadOlder, send, retry, nameOf } = useChat();

  // While this is on screen, the provider keeps it loaded and marks it read.
  useEffect(() => {
    open(id);
    return () => open(null);
  }, [id, open]);

  const conversation = state.conversations[id];
  if (!conversation) {
    return (
      <div className="flex flex-1 items-center justify-center p-6 text-center font-semibold">
        {state.listLoaded ? (
          <div>
            <p>That conversation is no longer available.</p>
            <Link href="/" className="font-bold underline decoration-float decoration-3 underline-offset-2">Back to your chats</Link>
          </div>
        ) : (
          <p>Loading…</p>
        )}
      </div>
    );
  }

  return (
    <section className="flex min-h-0 flex-1 flex-col" aria-label={conversationTitle(conversation)}>
      <header className="flex items-center gap-3 border-b-3 border-ink bg-paper px-4 py-3">
        <Link href="/" aria-label="Back to chats" className="rounded-lg border-3 border-ink bg-sun px-2 text-xl font-bold shadow-hard-sm md:hidden">
          ←
        </Link>
        <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full border-3 border-ink bg-lake font-display text-base font-extrabold" aria-hidden="true">
          {conversationTitle(conversation).charAt(0).toUpperCase()}
        </span>
        <h2 className="truncate font-display text-xl font-extrabold">{conversationTitle(conversation)}</h2>
      </header>
      <MessageList
        messages={state.messages[id] ?? []}
        meId={meId}
        nameOf={nameOf}
        showSenders={conversation.type === "group"}
        hasMoreOlder={state.hasMoreOlder[id] ?? false}
        onLoadOlder={() => void loadOlder(id)}
        onRetry={(clientMsgId) => retry(id, clientMsgId)}
      />
      <Composer onSend={(body) => send(id, body)} connected={state.status === "connected"} />
    </section>
  );
}
