import { FishingScene } from "@/components/FishingScene";

/** Shown beside the list on wide screens when no chat is open. */
export default function Home() {
  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
      <FishingScene />
      <h2 className="font-display text-3xl font-extrabold">Pick a chat</h2>
      <p className="max-w-sm rounded-xl border-3 border-ink bg-paper px-4 py-3 font-medium shadow-hard">Choose a conversation from the list. Chats started elsewhere appear here as soon as they have a message.</p>
    </div>
  );
}
