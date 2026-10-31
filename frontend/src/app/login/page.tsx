"use client";

import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState, type FormEvent } from "react";
import { AuthCard, FormError } from "@/components/AuthCard";
import { Button } from "@/components/Button";
import { Field } from "@/components/Field";
import { ApiError } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { messageForError } from "@/lib/errors";

export default function LoginPage() {
  const { user, ready, login } = useAuth();
  const router = useRouter();
  const [identifier, setIdentifier] = useState("");
  const [password, setPassword] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);

  useEffect(() => {
    if (ready && user) router.replace("/");
  }, [ready, user, router]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (!identifier.trim() || !password) return;
    setBusy(true);
    setError(null);
    try {
      await login(identifier.trim(), password);
      router.replace("/");
    } catch (e) {
      setError(e);
      setBusy(false);
    }
  }

  return (
    <AuthCard title="Welcome back">
      <form onSubmit={submit} className="flex flex-col gap-4" noValidate>
        <Field label="Username or email" autoComplete="username" value={identifier} onChange={(e) => setIdentifier(e.target.value)} />
        <Field label="Password" type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        {error ? <FormError>{messageForError(error)}</FormError> : null}
        {error instanceof ApiError && error.code === "email_not_verified" ? (
          <Link href="/register" className="text-center font-semibold text-gold underline">
            Verify your email
          </Link>
        ) : null}
        <Button type="submit" busy={busy}>
          Sign in
        </Button>
      </form>
      <p className="mt-4 text-center text-sm">
        New here?{" "}
        <Link href="/register" className="font-semibold text-gold underline">
          Create an account
        </Link>
      </p>
    </AuthCard>
  );
}
