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
      <label htmlFor={id} className="text-sm font-semibold text-ink">
        {label}
      </label>
      <input
        id={id}
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? errorId : undefined}
        className={`rounded-xl border-3 bg-paper px-3 py-2 text-ink placeholder:text-ink/50 focus-visible:outline-3 focus-visible:outline-offset-1 focus-visible:outline-lake ${error ? "border-float" : "border-ink"} ${className}`}
        {...rest}
      />
      {error ? (
        <p id={errorId} className="text-sm font-semibold text-float">
          {error}
        </p>
      ) : null}
    </div>
  );
}
