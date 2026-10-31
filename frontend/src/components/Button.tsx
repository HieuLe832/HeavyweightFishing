import type { ButtonHTMLAttributes } from "react";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "ghost";
  busy?: boolean;
};

const styles = {
  primary:
    "bg-gold text-ink border-b-4 border-gold-dark hover:brightness-105 active:translate-y-[2px] active:border-b-2",
  ghost: "bg-transparent text-cream border-2 border-water-light/60 hover:bg-water-light/15",
};

export function Button({ variant = "primary", busy = false, disabled, className = "", children, type = "button", ...rest }: Props) {
  return (
    <button
      type={type}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={`rounded-2xl px-5 py-2 font-display text-lg font-semibold transition focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-cream disabled:cursor-not-allowed disabled:opacity-60 ${styles[variant]} ${className}`}
      {...rest}
    >
      {children}
    </button>
  );
}
