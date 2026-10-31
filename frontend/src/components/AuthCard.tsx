import type { ReactNode } from "react";
import { FishingScene } from "./FishingScene";
import { WaveBackground } from "./WaveBackground";

/** The frame of the sign-in and register pages. */
export function AuthCard({ title, children }: { title: string; children: ReactNode }) {
  return (
    <main className="flex flex-1 items-center justify-center p-4">
      <WaveBackground />
      <div className="w-full max-w-md rounded-3xl border-4 border-sand bg-water-deep/90 p-6 shadow-xl">
        <FishingScene />
        <h1 className="mb-4 text-center font-display text-3xl font-bold text-gold">{title}</h1>
        {children}
      </div>
    </main>
  );
}

export function FormError({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-xl bg-cream px-3 py-2 text-sm font-semibold text-coral">
      {children}
    </p>
  );
}
