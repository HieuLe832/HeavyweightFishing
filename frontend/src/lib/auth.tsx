"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { createApi, createLocalTokenStore, type Api, type User } from "./api";
import { config } from "./config";

export interface AuthValue {
  user: User | null;
  /** False until the stored session has been checked, so pages do not flash the login screen. */
  ready: boolean;
  api: Api;
  login(identifier: string, password: string): Promise<void>;
  logout(): Promise<void>;
}

export const AuthContext = createContext<AuthValue | null>(null);

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error("useAuth must be used inside <AuthProvider>");
  return value;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null);
  const [ready, setReady] = useState(false);
  const [tokens] = useState(createLocalTokenStore);
  const api = useMemo(() => createApi({ baseUrl: config.apiUrl, tokens, onSessionEnded: () => setUser(null) }), [tokens]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      if (tokens.getRefresh()) {
        try {
          await api.refresh();
          const me = await api.me();
          if (!cancelled) setUser(me);
        } catch {
          // An ended session is already handled by onSessionEnded; a network error leaves the person on the login page.
        }
      }
      if (!cancelled) setReady(true);
    })();
    return () => {
      cancelled = true;
    };
  }, [api, tokens]);

  const login = useCallback(async (identifier: string, password: string) => {
    const result = await api.login(identifier, password);
    setUser(result.user);
  }, [api]);

  const logout = useCallback(async () => {
    await api.logout();
    setUser(null);
  }, [api]);

  const value = useMemo(() => ({ user, ready, api, login, logout }), [user, ready, api, login, logout]);
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}
