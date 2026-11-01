"use client";

import { useContext, useEffect, useState } from "react";
import { AuthContext } from "./auth";

/** Where the API serves someone's avatar: their upload first, else their character from the pack. */
export function avatarPath(who: { avatarMediaId?: string | null; avatarPresetKey?: string | null }): string | null {
  if (who.avatarMediaId) return `/media/${who.avatarMediaId}`;
  if (who.avatarPresetKey) return `/avatars/${encodeURIComponent(who.avatarPresetKey)}`;
  return null;
}

/**
 * Object URLs for files behind the token, one fetch per path for the session. A failed fetch is not kept, so an avatar
 * whose pack was down shows again the next time it is drawn.
 */
const cache = new Map<string, Promise<string | null>>();

/** Drops every image (on sign-out), so the next account never sees the previous one's files. */
export function clearAuthedImages(): void {
  for (const pending of cache.values()) void pending.then((url) => url && URL.revokeObjectURL(url));
  cache.clear();
}

/** An object URL for `path`, or null while it loads, when it fails, and outside a signed-in app. */
export function useAuthedImage(path: string | null): string | null {
  const api = useContext(AuthContext)?.api;
  const [loaded, setLoaded] = useState<{ path: string; url: string | null } | null>(null);

  useEffect(() => {
    if (!path || !api) return;
    let live = true;
    let pending = cache.get(path);
    if (!pending) {
      pending = api.getBlob(path).then(
        (blob) => URL.createObjectURL(blob),
        () => {
          cache.delete(path);
          return null;
        },
      );
      cache.set(path, pending);
    }
    void pending.then((url) => {
      if (live) setLoaded({ path, url });
    });
    return () => {
      live = false;
    };
  }, [api, path]);

  return path && loaded?.path === path ? loaded.url : null;
}
