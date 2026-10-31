import { useId, type InputHTMLAttributes } from "react";

type Props = InputHTMLAttributes<HTMLInputElement> & {
  label: string;
  error?: string;
};

export function Field({ label, error, className = "", ...rest }: Props) {
  const id = useId();
  const errorId = `${id}-error`;
  return (
    <div className="flex flex-col gap-1">
      <label htmlFor={id} className="font-display text-lg text-cream">
        {label}
      </label>
      <input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className={`rounded-xl border-2 border-sand bg-cream px-3 py-2 text-ink placeholder:text-ink/50 focus-visible:outline-2 focus-visible:outline-gold ${error ? "border-coral" : ""} ${className}`}
        {...rest}
      />
      {error ? (
        <p id={errorId} className="rounded-lg bg-cream px-2 py-1 text-sm font-semibold text-coral">
          {error}
        </p>
      ) : null}
    </div>
  );
}
