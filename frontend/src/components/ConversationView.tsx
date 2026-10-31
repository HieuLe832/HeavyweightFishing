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
      <div className="flex flex-1 items-center justify-center p-6 text-center text-sand">
        {state.listLoaded ? (
          <div>
            <p>That conversation is no longer available.</p>
            <Link href="/" className="font-semibold text-gold underline">Back to your chats</Link>
          </div>
        ) : (
          <p>Loading…</p>
        )}
      </div>
    );
  }

  return (
    <section className="flex min-h-0 flex-1 flex-col" aria-label={conversationTitle(conversation)}>
      <header className="flex items-center gap-3 border-b-4 border-sand bg-water-deep px-4 py-3">
        <Link href="/" aria-label="Back to chats" className="rounded-full px-2 py-1 text-2xl text-gold md:hidden">
          ←
        </Link>
        <h2 className="truncate font-display text-2xl font-bold text-gold">{conversationTitle(conversation)}</h2>
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
