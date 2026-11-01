"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { AuthCard, FormError } from "@/components/AuthCard";
import { Button } from "@/components/Button";
import { Field } from "@/components/Field";
import { ApiError } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { messageForError } from "@/lib/errors";

type Step = "form" | "code";

export default function RegisterPage() {
  const { api, login } = useAuth();
  const router = useRouter();
  const [step, setStep] = useState<Step>("form");
  const [form, setForm] = useState({ username: "", email: "", displayName: "", password: "" });
  const [code, setCode] = useState("");
  const [haveCode, setHaveCode] = useState(false); // verifying without having just registered: no password to sign in with
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const [notice, setNotice] = useState("");

  const set = (field: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [field]: e.target.value }));
  const fields = error instanceof ApiError ? error.fields : undefined;
  const fieldError = (name: string) => fields?.[name]?.[0] ?? fields?.[name.charAt(0).toUpperCase() + name.slice(1)]?.[0];
  const generalError = error && !(error instanceof ApiError && error.code === "validation_failed" && fields) ? messageForError(error) : null;

  async function register(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api.register({
        username: form.username.trim(), email: form.email.trim(), password: form.password,
        ...(form.displayName.trim() ? { displayName: form.displayName.trim() } : {}),
      });
      setStep("code");
    } catch (e) {
      setError(e);
    }
    setBusy(false);
  }

  async function verify(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setError(null);
    setNotice("");
    try {
      const email = form.email.trim();
      await api.verifyEmail(email, code.trim());
      if (haveCode) {
        router.push("/login");
        return;
      }
      await login(email, form.password);
      router.replace("/");
    } catch (e) {
      setError(e);
      setBusy(false);
    }
  }

  async function resend() {
    setError(null);
    setNotice("");
    try {
      await api.resendVerification(form.email.trim());
      setNotice("A new code has been sent.");
    } catch (e) {
      setError(e);
    }
  }

  if (step === "code" || haveCode) {
    return (
      <AuthCard title="Check your email">
        <form onSubmit={verify} className="flex flex-col gap-4" noValidate>
          {haveCode ? <Field label="Email" type="email" autoComplete="email" value={form.email} onChange={set("email")} error={fieldError("email")} /> : (
            <p className="text-center text-sm">We sent a 6-digit code to {form.email}.</p>
          )}
          <Field label="6-digit code" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} onChange={(e) => setCode(e.target.value)} error={fieldError("code")} />
          {generalError ? <FormError>{generalError}</FormError> : null}
          {notice ? <p role="status" className="text-center text-sm font-semibold text-sky-ink">{notice}</p> : null}
          <Button type="submit" busy={busy}>
            Verify
          </Button>
          {!haveCode || form.email.trim() ? (
            <Button variant="ghost" onClick={resend}>
              Resend code
            </Button>
          ) : null}
        </form>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Join the crew">
      <form onSubmit={register} className="flex flex-col gap-4" noValidate>
        <Field label="Username" autoComplete="username" value={form.username} onChange={set("username")} error={fieldError("username")} />
        <Field label="Email" type="email" autoComplete="email" value={form.email} onChange={set("email")} error={fieldError("email")} />
        <Field label="Display name" autoComplete="name" value={form.displayName} onChange={set("displayName")} error={fieldError("displayName")} />
        <Field label="Password" type="password" autoComplete="new-password" value={form.password} onChange={set("password")} error={fieldError("password")} />
        {generalError ? <FormError>{generalError}</FormError> : null}
        <Button type="submit" busy={busy}>
          Create account
        </Button>
        <Button variant="ghost" onClick={() => { setError(null); setHaveCode(true); }}>
          I already have a code
        </Button>
      </form>
      <p className="mt-4 text-center text-sm">
        Already registered?{" "}
        <Link href="/login" className="font-bold text-ink underline decoration-float decoration-3 underline-offset-2">
          Sign in
        </Link>
      </p>
    </AuthCard>
  );
}
