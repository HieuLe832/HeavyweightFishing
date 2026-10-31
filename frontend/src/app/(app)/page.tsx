import { FishingScene } from "@/components/FishingScene";

/** Shown beside the list on wide screens when no chat is open. */
export default function Home() {
  return (
    <div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
      <FishingScene />
      <h2 className="font-display text-3xl font-bold text-gold">Pick a chat</h2>
      <p className="max-w-sm text-sand">Choose a conversation from the list. Chats started elsewhere appear here as soon as they have a message.</p>
    </div>
  );
}
