import type { ReactNode } from "react";
import { FishingScene } from "./FishingScene";
import { WaveBackground } from "./WaveBackground";

/** The frame of the sign-in and register pages. */
export function AuthCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="flex flex-1 items-center justify-center p-4">
      <WaveBackground />
      <div className="w-full max-w-md rounded-2xl border-3 border-ink bg-paper p-6 shadow-hard-lg">
        <FishingScene />
        <h1 className="mb-4 text-center font-display text-3xl font-extrabold">{title}</h1>
        {children}
      </div>
    </main>
  );
}

export function FormError({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-xl border-3 border-ink bg-butter px-3 py-2 text-sm font-semibold text-ink">
      {children}
    </p>
  );
}
