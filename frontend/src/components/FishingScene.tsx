/** A little scene for the sign-in pages: a rod, a line and a bobber on the waves. Drawn here, no game assets. */
export function FishingScene() {
  return (
    <svg aria-hidden="true" viewBox="0 0 240 120" className="mx-auto mb-3 h-28 w-56 rounded-2xl">
      <path d="M0 78 Q 30 62 60 78 T 120 78 T 180 78 T 240 78 V 120 H 0 Z" fill="var(--water-light)" fillOpacity="0.55" />
      <path d="M0 94 Q 30 80 60 94 T 120 94 T 180 94 T 240 94 V 120 H 0 Z" fill="var(--water-deep)" fillOpacity="0.7" />
      <path d="M26 20 L 118 6" stroke="var(--sand)" strokeWidth="4" strokeLinecap="round" />
      <path d="M118 6 Q 150 20 150 60" stroke="var(--cream)" strokeWidth="1.5" fill="none" />
      <g className="animate-bob">
        <circle cx="150" cy="66" r="9" fill="var(--coral)" />
        <path d="M141 66 a9 9 0 0 1 18 0 Z" fill="var(--cream)" />
        <circle cx="150" cy="66" r="9" fill="none" stroke="var(--ink)" strokeWidth="1.5" />
      </g>
      <path d="M176 100 q 10 -9 22 0 q -10 9 -22 0 Z M198 100 l 7 -6 v 12 Z" fill="var(--gold)" />
    </svg>
  );
}
