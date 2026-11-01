import type { ButtonHTMLAttributes } from "react";

type Props = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "ghost";
  busy?: boolean;
};

const styles = {
  primary: "bg-float text-paper shadow-hard hover:brightness-105 active:translate-x-[2px] active:translate-y-[2px] active:shadow-none",
  ghost: "bg-paper text-ink shadow-hard-sm hover:bg-butter-light active:translate-x-[1px] active:translate-y-[1px] active:shadow-none",
};

export function Button({ variant = "primary", busy = false, disabled, className = "", children, type = "button", ...rest }: Props) {
  return (
    <button
      type={type}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={`rounded-xl border-3 border-ink px-5 py-2 text-lg font-bold transition focus-visible:outline-3 focus-visible:outline-offset-2 focus-visible:outline-lake disabled:cursor-not-allowed disabled:opacity-60 disabled:shadow-none ${styles[variant]} ${className}`}
      {...rest}
    >
      {children}
    </button>
  );
}
