"use client";

import { useRouter, usePathname } from "next/navigation";
import { Suspense, useEffect, type ReactNode } from "react";
import { Bobber } from "@/components/Bobber";
import { ConversationList } from "@/components/ConversationList";
import { ProfileCard } from "@/components/ProfileCard";
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
  const inChat = usePathname().startsWith("/c/");

  return (
    <div className="flex h-dvh min-h-0">
      <aside className={`${inChat ? "hidden md:flex" : "flex"} w-full flex-col bg-butter md:w-80 md:border-r-3 md:border-ink lg:w-96`}>
        <header className="border-b-3 border-ink bg-sun px-4 py-3">
          <h1 className="font-display text-2xl font-extrabold">Chats</h1>
        </header>
        <ConversationList
          items={state.order.map((id) => state.conversations[id])}
          openId={state.openId}
          hasMore={hasMoreConversations}
          onLoadMore={() => void loadMoreConversations()}
        />
        <ProfileCard />
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
