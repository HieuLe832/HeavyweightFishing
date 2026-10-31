/** Two slow rows of waves along the bottom edge. Decorative, so hidden from assistive tech. */
export function WaveBackground() {
  const wave = "M0 40 Q 50 0 100 40 T 200 40 T 300 40 T 400 40 T 500 40 T 600 40 T 700 40 T 800 40 V 80 H 0 Z";
  return (
    <div aria-hidden="true" className="pointer-events-none fixed inset-x-0 bottom-0 -z-10 h-24 overflow-hidden opacity-60">
      <svg className="animate-drift absolute bottom-0 h-20 w-[200%]" viewBox="0 0 800 80" preserveAspectRatio="none">
        <path d={wave} fill="var(--water-light)" fillOpacity="0.35" />
      </svg>
      <svg className="animate-drift absolute bottom-0 h-14 w-[200%] [animation-duration:11s]" viewBox="0 0 800 80" preserveAspectRatio="none">
        <path d={wave} fill="var(--water-deep)" fillOpacity="0.5" />
      </svg>
    </div>
  );
}
