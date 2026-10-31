export interface User {
  id: string;
  username: string;
  email: string;
  displayName: string;
  role: string;
}

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
}

/** Calls that must never trigger a token refresh: they run without a session, or are the refresh itself. */
const NO_REFRESH = ["/auth/refresh", "/auth/login", "/auth/register", "/auth/verify-email", "/auth/resend-verification"];

export function createApi({ baseUrl, tokens, fetch: fetchFn = (...args) => fetch(...args), onSessionEnded }: ApiOptions): Api {
  let refreshing: Promise<void> | null = null;

  async function send<T>(method: string, path: string, body: unknown): Promise<{ status: number; value: T | undefined }> {
    const headers = new Headers();
    if (body !== undefined) headers.set("Content-Type", "application/json");
    const access = tokens.getAccess();
    if (access) headers.set("Authorization", `Bearer ${access}`);

    const response = await fetchFn(`${baseUrl}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (response.ok) {
      if (response.status === 204) return { status: 204, value: undefined };
      const text = await response.text();
      return { status: response.status, value: text ? (JSON.parse(text) as T) : undefined };
    }
    throw await toError(response);
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

  /** One refresh at a time: refresh tokens are single-use, so concurrent calls must share the answer. */
  function refresh(): Promise<void> {
    refreshing ??= (async () => {
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
    })().finally(() => {
      refreshing = null;
    });
    return refreshing;
  }

  async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    try {
      return (await send<T>(method, path, body)).value as T;
    } catch (e) {
      if (!(e instanceof ApiError) || e.status !== 401 || NO_REFRESH.includes(path)) throw e;
      try {
        await refresh();
      } catch {
        throw e;
      }
      return (await send<T>(method, path, body)).value as T;
    }
  }

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
  };
}
