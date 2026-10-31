"use client";

import { useState, type KeyboardEvent } from "react";
import { Button } from "./Button";

const MAX = 4000;

export function Composer({ onSend, connected }: { onSend: (body: string) => void; connected: boolean }) {
  const [text, setText] = useState("");
  const length = text.trim().length; // the API trims before it counts
  const canSend = connected && length > 0 && length <= MAX;

  function send() {
    if (!canSend) return;
    onSend(text.trim());
    setText("");
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    // Enter while an input method is composing (Vietnamese Telex, CJK) confirms the word; it must not send.
    if (event.key !== "Enter" || event.shiftKey || event.nativeEvent.isComposing) return;
    event.preventDefault();
    send();
  }

  return (
    <div className="border-t-4 border-sand bg-water-deep p-3">
      {connected ? null : <p role="status" className="mb-2 text-center text-sm font-semibold text-gold">Reconnecting…</p>}
      <div className="flex items-end gap-2">
        <textarea
          aria-label="Message"
          rows={1}
          value={text}
          disabled={!connected}
          placeholder={connected ? "Write a message" : "Waiting for the connection"}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={onKeyDown}
          className="max-h-40 min-h-11 flex-1 resize-none rounded-2xl border-2 border-sand bg-cream px-3 py-2 text-ink [field-sizing:content] placeholder:text-ink/50 focus-visible:outline-2 focus-visible:outline-gold disabled:opacity-60"
        />
        <Button onClick={send} disabled={!canSend}>
          Send
        </Button>
      </div>
      {length >= 3500 ? (
        <p className={`mt-1 text-right text-xs ${length > MAX ? "font-bold text-gold" : "text-sand"}`}>
          {length} / {MAX}
        </p>
      ) : null}
    </div>
  );
}
