/** The unread badge: a float that bobs on the water. Nothing at zero. */
export function Bobber({ count }: { count: number }) {
  if (count <= 0) return null;
  return (
    <span
      aria-label={`${count} unread`}
      className="animate-bob inline-flex h-6 min-w-6 items-center justify-center rounded-full border-2 border-ink bg-float px-1.5 text-xs font-bold text-paper"
    >
      {count > 99 ? "99+" : count}
    </span>
  );
}
