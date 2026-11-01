"use client";

import { useState } from "react";
import { avatarPath, useAuthedImage } from "@/lib/authed-image";

const sizes = {
  sm: "h-9 w-9 text-base",
  md: "h-11 w-11 text-xl",
  lg: "h-24 w-24 text-4xl",
};

interface Props {
  name: string;
  mediaId?: string | null;
  presetKey?: string | null;
  size?: keyof typeof sizes;
}

/** A round avatar: the picture when it can be fetched and shown, else the name's initial. Decorative either way. */
export function Avatar({ name, mediaId, presetKey, size = "md" }: Props) {
  const url = useAuthedImage(avatarPath({ avatarMediaId: mediaId, avatarPresetKey: presetKey }));
  const [broken, setBroken] = useState<string | null>(null);
  const showImage = url !== null && url !== broken;

  return (
    <span
      aria-hidden="true"
      className={`flex shrink-0 items-center justify-center overflow-hidden rounded-full border-3 border-ink bg-lake font-display font-extrabold ${sizes[size]}`}
    >
      {showImage ? (
        // eslint-disable-next-line @next/next/no-img-element -- an object URL, which next/image cannot optimise
        <img src={url} alt="" className="h-full w-full object-cover" onError={() => setBroken(url)} />
      ) : (
        name.trim().charAt(0).toUpperCase()
      )}
    </span>
  );
}
