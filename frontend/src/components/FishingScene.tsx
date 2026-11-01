/** A little scene for the sign-in pages: a rod, a line and a bobber on the waves. Drawn here, no game assets. */
export function FishingScene() {
  return (
    <svg aria-hidden="true" viewBox="0 0 240 120" className="mx-auto mb-3 h-28 w-56 rounded-xl border-3 border-ink bg-sky">
      <path d="M-4 78 Q 30 62 60 78 T 120 78 T 180 78 T 244 78 V 124 H -4 Z" fill="var(--lake)" stroke="var(--ink)" strokeWidth="3" />
      <path d="M-4 96 Q 30 82 60 96 T 120 96 T 180 96 T 244 96 V 124 H -4 Z" fill="var(--sky-ink)" stroke="var(--ink)" strokeWidth="3" />
      <path d="M26 22 L 118 8" stroke="var(--ink)" strokeWidth="7" strokeLinecap="round" />
      <path d="M26 22 L 118 8" stroke="var(--sun)" strokeWidth="3" strokeLinecap="round" />
      <path d="M118 8 Q 150 22 150 58" stroke="var(--ink)" strokeWidth="1.5" fill="none" />
      <g className="animate-bob">
        <circle cx="150" cy="66" r="9" fill="var(--float)" />
        <path d="M141 66 a9 9 0 0 1 18 0 Z" fill="var(--paper)" />
        <circle cx="150" cy="66" r="9" fill="none" stroke="var(--ink)" strokeWidth="3" />
      </g>
      <path d="M176 104 q 10 -9 22 0 q -10 9 -22 0 Z M198 104 l 7 -6 v 12 Z" fill="var(--sun)" stroke="var(--ink)" strokeWidth="2" strokeLinejoin="round" />
    </svg>
  );
}
