/** The unread badge: a float that bobs on the water. Nothing at zero. */
export function Bobber({ count }: { count: number }) {
  if (count <= 0) return null;
  return (
    <span
      aria-label={`${count} unread`}
      className="animate-bob inline-flex h-7 min-w-7 items-center justify-center rounded-full border-2 border-cream bg-coral px-1.5 text-sm font-bold text-cream shadow"
    >
      {count > 99 ? "99+" : count}
    </span>
  );
}
