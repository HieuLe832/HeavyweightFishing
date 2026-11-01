export interface User {
  id: string;
  username: string;
  email: string;
  displayName: string;
  role: string;
  /** An uploaded picture; never set together with `avatarPresetKey`. */
  avatarMediaId?: string | null;
  /** A character from the avatar pack. */
  avatarPresetKey?: string | null;
}

/** What `POST /media` answers with. */
export interface MediaInfo {
  id: string;
  mimeType: string;
  sizeBytes: number;
  width?: number | null;
  height?: number | null;
}

export type AvatarChoice = { mediaId: string } | { presetKey: string };

export interface AuthResponse {
  accessToken: string;
  expiresIn: number;
  refreshToken: string;
  user: User;
}

export interface RegisterInput {
  username: string;
  email: string;
  password: string;
  displayName?: string;
}

/** An error answer from the API: `code` is the machine-readable code of the problem+json body ("unknown" when there is none). */
export class ApiError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: string,
    message: string,
    public readonly fields?: Record<string, string[]>,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

export interface TokenStore {
  getAccess(): string | null;
  setAccess(token: string | null): void;
  getRefresh(): string | null;
  setRefresh(token: string | null): void;
}

const REFRESH_KEY = "refreshToken";

/** The access token lives in memory only; the refresh token survives a reload in localStorage. */
export function createLocalTokenStore(): TokenStore {
  let access: string | null = null;
  return {
    getAccess: () => access,
    setAccess: (token) => {
      access = token;
    },
    getRefresh: () => localStorage.getItem(REFRESH_KEY),
    setRefresh: (token) => {
      if (token === null) localStorage.removeItem(REFRESH_KEY);
      else localStorage.setItem(REFRESH_KEY, token);
    },
  };
}

export interface ApiOptions {
  baseUrl: string;
  tokens: TokenStore;
  fetch?: typeof fetch;
  /** Called when the session cannot be continued (the refresh token was refused). */
  onSessionEnded: () => void;
  /** Runs `fn` while holding a lock shared by every tab. Defaults to the Web Locks API, when the browser has it. */
  lock?: <T>(name: string, fn: () => Promise<T>) => Promise<T>;
}

export interface Api {
  request<T>(method: string, path: string, body?: unknown): Promise<T>;
  login(identifier: string, password: string): Promise<AuthResponse>;
  register(input: RegisterInput): Promise<void>;
  verifyEmail(email: string, code: string): Promise<void>;
  resendVerification(email: string): Promise<void>;
  logout(): Promise<void>;
  me(): Promise<User>;
  refresh(): Promise<void>;
  /** The access token now in use, for the SignalR connection (which sends it in the query string). */
  accessToken(): string | null;
  uploadMedia(file: Blob): Promise<MediaInfo>;
  setAvatar(choice: AvatarChoice): Promise<void>;
  clearAvatar(): Promise<void>;
  /** The keys of the character avatar pack. Rejects with `avatar_provider_unavailable` while the pack is down. */
  avatarPresets(): Promise<string[]>;
  /** A file the API serves behind the token (uploads, avatars), for an object URL. */
  getBlob(path: string): Promise<Blob>;
}

/**
 * Tabs share the refresh token (localStorage) and the server revokes the session when a used one comes back, so two tabs must
 * never refresh at the same time. Without the Web Locks API (old browsers, tests) refreshes are serialised per tab only.
 */
function browserLock<T>(name: string, fn: () => Promise<T>): Promise<T> {
  if (typeof navigator === "undefined" || !navigator.locks) return fn();
  return navigator.locks.request(name, fn) as Promise<T>;
}

const REFRESH_LOCK = "chat-auth-refresh";

/** Calls that must never trigger a token refresh: they run without a session, or are the refresh itself. */
const NO_REFRESH = ["/auth/refresh", "/auth/login", "/auth/register", "/auth/verify-email", "/auth/resend-verification"];

export function createApi({ baseUrl, tokens, fetch: fetchFn = (...args) => fetch(...args), onSessionEnded, lock = browserLock }: ApiOptions): Api {
  let refreshing: Promise<void> | null = null;

  /** One call with the current token. A FormData body goes as multipart (the browser sets its boundary), anything else as JSON. */
  async function fetchOnce(method: string, path: string, body: unknown): Promise<Response> {
    const headers = new Headers();
    const form = body instanceof FormData;
    if (body !== undefined && !form) headers.set("Content-Type", "application/json");
    const access = tokens.getAccess();
    if (access) headers.set("Authorization", `Bearer ${access}`);

    const payload = body === undefined ? undefined : form ? body : JSON.stringify(body);
    const response = await fetchFn(`${baseUrl}${path}`, { method, headers, body: payload });
    if (response.ok) return response;
    throw await toError(response);
  }

  async function send<T>(method: string, path: string, body: unknown): Promise<{ status: number; value: T | undefined }> {
    const response = await fetchOnce(method, path, body);
    if (response.status === 204) return { status: 204, value: undefined };
    const text = await response.text();
    return { status: response.status, value: text ? (JSON.parse(text) as T) : undefined };
  }

  async function toError(response: Response): Promise<ApiError> {
    try {
      const problem = await response.json();
      return new ApiError(response.status, typeof problem.code === "string" ? problem.code : "unknown", problem.title ?? response.statusText, problem.errors);
    } catch {
      return new ApiError(response.status, "unknown", response.statusText || "Request failed");
    }
  }

  function endSession() {
    tokens.setAccess(null);
    tokens.setRefresh(null);
    onSessionEnded();
  }

  /**
   * One refresh at a time: refresh tokens are single-use, so concurrent calls in this tab share the answer, and other tabs wait
   * for the lock. The token is read inside the lock, so a tab that waited uses the one the other tab just received.
   */
  function refresh(): Promise<void> {
    refreshing ??= lock(REFRESH_LOCK, async () => {
      const refreshToken = tokens.getRefresh();
      if (!refreshToken) {
        endSession();
        throw new ApiError(401, "invalid_refresh_token", "Sign in again.");
      }
      try {
        const result = (await send<AuthResponse>("POST", "/auth/refresh", { refreshToken })).value!;
        tokens.setAccess(result.accessToken);
        tokens.setRefresh(result.refreshToken);
      } catch (e) {
        // Only a refusal ends the session; a network blip must not sign the person out.
        if (e instanceof ApiError && e.status === 401) endSession();
        throw e;
      }
    }).finally(() => {
      refreshing = null;
    });
    return refreshing;
  }

  /** Runs `call`; on a 401 refreshes the session once and runs it again. */
  async function withRefresh<T>(path: string, call: () => Promise<T>): Promise<T> {
    try {
      return await call();
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401 || NO_REFRESH.includes(path)) throw e;
      try {
        await refresh();
      } catch {
        throw e;
      }
      return await call();
    }
  }

  const request = <T,>(method: string, path: string, body?: unknown): Promise<T> =>
    withRefresh(path, async () => (await send<T>(method, path, body)).value as T);

  async function login(identifier: string, password: string) {
    const result = await request<AuthResponse>("POST", "/auth/login", { identifier, password });
    tokens.setAccess(result.accessToken);
    tokens.setRefresh(result.refreshToken);
    return result;
  }

  return {
    request,
    login,
    register: (input) => request("POST", "/auth/register", input),
    verifyEmail: (email, code) => request("POST", "/auth/verify-email", { email, code }),
    resendVerification: (email) => request("POST", "/auth/resend-verification", { email }),
    logout: async () => {
      try {
        await request("POST", "/auth/logout");
      } catch {
        // Signing out must work offline and with an expired session.
      }
      tokens.setAccess(null);
      tokens.setRefresh(null);
    },
    me: () => request<User>("GET", "/auth/me"),
    refresh,
    accessToken: () => tokens.getAccess(),
    uploadMedia: (file) => {
      const form = new FormData();
      form.append("file", file);
      return request<MediaInfo>("POST", "/media", form);
    },
    setAvatar: (choice) => request("PUT", "/users/me/avatar", choice),
    clearAvatar: () => request("DELETE", "/users/me/avatar"),
    avatarPresets: async () => (await request<{ avatars: { key: string }[] }>("GET", "/avatars")).avatars.map((a) => a.key),
    getBlob: (path) => withRefresh(path, async () => (await fetchOnce("GET", path, undefined)).blob()),
  };
}
