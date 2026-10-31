"use client";

import { use } from "react";
import { ConversationView } from "@/components/ConversationView";

export default function ChatPage({ params }: PageProps<"/c/[id]">) {
  const { id } = use(params);
  return <ConversationView id={id} />;
}
