"use client";

import { useCallback, useEffect, useId, useRef, useState, type ChangeEvent } from "react";
import { ApiError, type AvatarChoice } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { messageForError } from "@/lib/errors";
import { Avatar } from "./Avatar";
import { Button } from "./Button";

type Presets = { state: "loading" } | { state: "offline" } | { state: "failed"; message: string } | { state: "ready"; keys: string[] };

const ACCEPT = "image/png,image/jpeg,image/gif,image/webp";

/** "07-curly-blond" → "curly blond": the pack numbers its files, which makes a poor initial. */
const presetName = (key: string) => key.replace(/^\d+-/, "").replaceAll("-", " ");

/** Who is signed in, in the corner of the sidebar. Clicking it opens their profile, where they change their avatar or sign out. */
export function ProfileCard() {
  const { user, api, logout, reloadUser } = useAuth();
  const [open, setOpen] = useState(false);
  const [presets, setPresets] = useState<Presets>({ state: "loading" });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const wrapper = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const dialog = useRef<HTMLDivElement>(null);
  const uploadId = useId();

  const close = useCallback(() => {
    setOpen(false);
    trigger.current?.focus();
  }, []);

  const toggle = () => {
    if (open) return close();
    setError(null);
    setPresets({ state: "loading" });
    setOpen(true);
  };

  // The pack is read each time the profile opens, so it reflects whether the provider is up right now.
  useEffect(() => {
    if (!open) return;
    let live = true;
    api.avatarPresets().then(
      (keys) => live && setPresets({ state: "ready", keys }),
      (e: unknown) => {
        if (!live) return;
        setPresets(e instanceof ApiError && e.code === "avatar_provider_unavailable" ? { state: "offline" } : { state: "failed", message: messageForError(e) });
      },
    );
    return () => {
      live = false;
    };
  }, [open, api]);

  useEffect(() => {
    if (!open) return;
    dialog.current?.focus();
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    const onPointer = (e: MouseEvent) => {
      if (!wrapper.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("mousedown", onPointer);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("mousedown", onPointer);
    };
  }, [open, close]);

  if (!user) return null;

  async function change(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    try {
      await action();
      await reloadUser();
    } catch (e) {
      setError(messageForError(e));
    } finally {
      setBusy(false);
    }
  }

  const choose = (choice: AvatarChoice) => change(() => api.setAvatar(choice));

  const upload = (e: ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    e.target.value = ""; // the same file can be chosen again after an error
    if (file) void change(async () => api.setAvatar({ mediaId: (await api.uploadMedia(file)).id }));
  };

  const hasAvatar = Boolean(user.avatarMediaId || user.avatarPresetKey);

  return (
    <div ref={wrapper} className="relative border-t-3 border-ink bg-sun p-2">
      {open ? (
        <div
          ref={dialog}
          role="dialog"
          aria-label="Profile"
          tabIndex={-1}
          className="absolute bottom-full left-2 right-2 mb-2 flex max-h-[70dvh] flex-col gap-4 overflow-y-auto rounded-2xl border-3 border-ink bg-paper p-4 shadow-hard focus:outline-none"
        >
          <div className="flex items-center gap-4">
            <Avatar name={user.displayName} mediaId={user.avatarMediaId} presetKey={user.avatarPresetKey} size="lg" />
            <div className="min-w-0">
              <h2 className="truncate font-display text-2xl font-extrabold">{user.displayName}</h2>
              <p className="truncate font-semibold text-ink/70">@{user.username}</p>
              <p className="truncate text-sm text-ink/70">{user.email}</p>
            </div>
          </div>

          <section aria-labelledby={`${uploadId}-characters`} className="flex flex-col gap-2">
            <h3 id={`${uploadId}-characters`} className="font-display text-lg font-extrabold">Characters</h3>
            {presets.state === "loading" ? <p className="text-sm font-semibold text-ink/70">Loading characters…</p> : null}
            {presets.state === "offline" ? <p className="text-sm font-semibold text-ink/70">Character avatars are offline right now.</p> : null}
            {presets.state === "failed" ? <p className="text-sm font-semibold text-ink/70">{presets.message}</p> : null}
            {presets.state === "ready" ? (
              <ul className="grid grid-cols-6 gap-2">
                {presets.keys.map((key) => (
                  <li key={key}>
                    <button
                      type="button"
                      aria-label={`Use ${key}`}
                      aria-pressed={user.avatarPresetKey === key}
                      disabled={busy}
                      onClick={() => void choose({ presetKey: key })}
                      className={`rounded-full transition focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-lake disabled:opacity-60 ${
                        user.avatarPresetKey === key ? "ring-4 ring-float" : "hover:scale-105"
                      }`}
                    >
                      <Avatar name={presetName(key)} presetKey={key} size="sm" />
                    </button>
                  </li>
                ))}
              </ul>
            ) : null}
          </section>

          <div className="flex flex-wrap gap-2">
            <label
              htmlFor={uploadId}
              className="cursor-pointer rounded-xl border-3 border-ink bg-paper px-3 py-1 text-sm font-bold shadow-hard-sm hover:bg-butter-light has-[:focus-visible]:outline-3 has-[:focus-visible]:outline-lake"
            >
              Upload a picture
              <input id={uploadId} type="file" accept={ACCEPT} className="sr-only" disabled={busy} onChange={upload} />
            </label>
            {hasAvatar ? (
              <Button variant="ghost" busy={busy} onClick={() => void change(() => api.clearAvatar())} className="!px-3 !py-1 !text-sm">
                Remove avatar
              </Button>
            ) : null}
          </div>

          {error ? (
            <p role="alert" className="rounded-xl border-3 border-ink bg-butter-light px-3 py-2 text-sm font-semibold">
              {error}
            </p>
          ) : null}

          <Button variant="ghost" onClick={() => void logout()} className="self-start !px-3 !py-1 !text-sm">
            Sign out
          </Button>
        </div>
      ) : null}

      <button
        ref={trigger}
        type="button"
        aria-haspopup="dialog"
        aria-expanded={open}
        onClick={toggle}
        className="flex w-full items-center gap-3 rounded-xl border-3 border-transparent px-2 py-1.5 text-left transition hover:border-ink hover:bg-butter-light focus-visible:outline-3 focus-visible:outline-lake"
      >
        <Avatar name={user.displayName} mediaId={user.avatarMediaId} presetKey={user.avatarPresetKey} />
        <span className="min-w-0 flex-1">
          <span className="block truncate font-bold">{user.displayName}</span>
          <span className="block truncate text-xs font-semibold text-ink/70">@{user.username}</span>
        </span>
      </button>
    </div>
  );
}
