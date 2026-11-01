"use client";

import { useRouter, usePathname } from "next/navigation";
import { Suspense, useEffect, type ReactNode } from "react";
import { Bobber } from "@/components/Bobber";
import { Button } from "@/components/Button";
import { ConversationList } from "@/components/ConversationList";
import { WaveBackground } from "@/components/WaveBackground";
import { useAuth } from "@/lib/auth";
import { ChatProvider, useChat } from "@/lib/chat";

function Splash({ text }: { text: string }) {
  return (
    <main className="flex flex-1 flex-col items-center justify-center gap-3">
      <WaveBackground />
      <span className="font-display text-2xl font-extrabold">{text}</span>
      <Bobber count={1} />
    </main>
  );
}

/** Two panes from md up. Below that, the list is the home route and a chat is a page of its own. */
function Shell({ children }: { children: ReactNode }) {
  const { state, hasMoreConversations, loadMoreConversations } = useChat();
  const { user, logout } = useAuth();
  const inChat = usePathname().startsWith("/c/");

  return (
    <div className="flex h-dvh min-h-0">
      <aside className={`${inChat ? "hidden md:flex" : "flex"} w-full flex-col bg-butter md:w-80 md:border-r-3 md:border-ink lg:w-96`}>
        <header className="flex items-center justify-between gap-2 border-b-3 border-ink bg-sun px-4 py-3">
          <div className="min-w-0">
            <h1 className="font-display text-2xl font-extrabold">Chats</h1>
            <p className="truncate text-xs font-semibold text-ink/70">{user?.displayName}</p>
          </div>
          <Button variant="ghost" onClick={() => void logout()} className="!px-3 !py-1 !text-sm">
            Sign out
          </Button>
        </header>
        <ConversationList
          items={state.order.map((id) => state.conversations[id])}
          openId={state.openId}
          hasMore={hasMoreConversations}
          onLoadMore={() => void loadMoreConversations()}
        />
      </aside>
      <main className={`${inChat ? "flex" : "hidden md:flex"} min-w-0 flex-1 flex-col`}>
        <Suspense fallback={null}>{children}</Suspense>
      </main>
    </div>
  );
}

export default function AppLayout({ children }: { children: ReactNode }) {
  const { user, ready } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (ready && !user) router.replace("/login");
  }, [ready, user, router]);

  if (!ready) return <Splash text="Casting off…" />;
  if (!user) return <Splash text="Heading to the dock…" />;
  return (
    <ChatProvider>
      <Shell>{children}</Shell>
    </ChatProvider>
  );
}
