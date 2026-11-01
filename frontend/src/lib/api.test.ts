import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, createApi, createLocalTokenStore, type TokenStore } from "./api";

function memoryTokens(access: string | null = "access-1", refresh: string | null = "refresh-1"): TokenStore {
  let a = access;
  let r = refresh;
  return { getAccess: () => a, setAccess: (t) => (a = t), getRefresh: () => r, setRefresh: (t) => (r = t) };
}

const user = { id: "u1", username: "ann", email: "ann@example.com", displayName: "Ann", role: "user" };
const auth = (n: number) => ({ accessToken: `access-${n}`, expiresIn: 900, refreshToken: `refresh-${n}`, user });

function json(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { "Content-Type": status >= 400 ? "application/problem+json" : "application/json" },
  });
}

type Handler = (url: string, init: RequestInit) => Response | Promise<Response>;

function setup(handler: Handler, tokens = memoryTokens()) {
  const calls: { url: string; init: RequestInit }[] = [];
  const fetchFn = vi.fn(async (url: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(url), init: init ?? {} });
    return handler(String(url), init ?? {});
  });
  const onSessionEnded = vi.fn();
  const api = createApi({ baseUrl: "http://api.test", tokens, fetch: fetchFn as unknown as typeof fetch, onSessionEnded });
  return { api, calls, tokens, onSessionEnded, fetchFn };
}

const header = (init: RequestInit, name: string) => new Headers(init.headers).get(name);

/** The ApiError a call rejects with; fails the test if it resolves. */
async function failure(call: Promise<unknown>): Promise<ApiError> {
  try {
    await call;
  } catch (e) {
    return e as ApiError;
  }
  throw new Error("expected the call to fail");
}

describe("api.request", () => {
  it("sends the bearer token and a JSON body", async () => {
    const { api, calls } = setup(() => json(200, { ok: true }));

    const result = await api.request("POST", "/conversations/direct", { userId: "u2" });

    expect(result).toEqual({ ok: true });
    expect(calls[0].url).toBe("http://api.test/conversations/direct");
    expect(calls[0].init.method).toBe("POST");
    expect(header(calls[0].init, "Authorization")).toBe("Bearer access-1");
    expect(header(calls[0].init, "Content-Type")).toBe("application/json");
    expect(calls[0].init.body).toBe(JSON.stringify({ userId: "u2" }));
  });

  it("turns a problem+json response into an ApiError with status, code and fields", async () => {
    const { api } = setup(() => json(400, { title: "Bad", code: "validation_failed", errors: { email: ["Enter a valid email address."] } }));

    const error = await failure(api.request("POST", "/auth/register", {}));

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(400);
    expect(error.code).toBe("validation_failed");
    expect(error.fields).toEqual({ email: ["Enter a valid email address."] });
  });

  it("turns a non-JSON error body (a proxy's HTML page) into code unknown", async () => {
    const { api } = setup(() => new Response("<html>Bad gateway</html>", { status: 502, headers: { "Content-Type": "text/html" } }));

    const error = await failure(api.request("GET", "/conversations"));

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(502);
    expect(error.code).toBe("unknown");
  });

  it("resolves to undefined for 204", async () => {
    const { api } = setup(() => json(204));

    await expect(api.request("DELETE", "/x")).resolves.toBeUndefined();
  });

  it("sends no Authorization header without a token", async () => {
    const { api, calls } = setup(() => json(200, {}), memoryTokens(null, null));

    await api.request("GET", "/health");

    expect(header(calls[0].init, "Authorization")).toBeNull();
  });
});

describe("token refresh", () => {
  it("refreshes once on 401 and retries the request with the new token", async () => {
    const { api, calls, tokens } = setup((url, init) => {
      if (url.endsWith("/auth/refresh")) return json(200, auth(2));
      return header(init, "Authorization") === "Bearer access-2" ? json(200, { ok: 1 }) : json(401, { code: "unauthorized" });
    });

    await expect(api.request("GET", "/conversations")).resolves.toEqual({ ok: 1 });

    expect(calls.map((c) => c.url)).toEqual(["http://api.test/conversations", "http://api.test/auth/refresh", "http://api.test/conversations"]);
    expect(JSON.parse(calls[1].init.body as string)).toEqual({ refreshToken: "refresh-1" });
    expect(tokens.getAccess()).toBe("access-2");
    expect(tokens.getRefresh()).toBe("refresh-2");
  });

  it("shares one refresh call between concurrent 401s", async () => {
    const { api, calls } = setup(async (url, init) => {
      if (url.endsWith("/auth/refresh")) {
        await new Promise((r) => setTimeout(r, 10));
        return json(200, auth(2));
      }
      return header(init, "Authorization") === "Bearer access-2" ? json(200, {}) : json(401, {});
    });

    await Promise.all([api.request("GET", "/a"), api.request("GET", "/b"), api.request("GET", "/c")]);

    expect(calls.filter((c) => c.url.endsWith("/auth/refresh"))).toHaveLength(1);
  });

  it("clears both tokens and ends the session when the refresh token is rejected, without looping", async () => {
    const { api, calls, tokens, onSessionEnded } = setup((url) =>
      url.endsWith("/auth/refresh") ? json(401, { code: "invalid_refresh_token" }) : json(401, { code: "unauthorized" }));

    const error = await failure(api.request("GET", "/conversations"));

    expect(error).toBeInstanceOf(ApiError);
    expect(error.status).toBe(401);
    expect(calls).toHaveLength(2); // the request and one refresh: no retry, no second refresh
    expect(tokens.getAccess()).toBeNull();
    expect(tokens.getRefresh()).toBeNull();
    expect(onSessionEnded).toHaveBeenCalled();
  });

  it("does not log out when the refresh fails for a network reason", async () => {
    const { api, tokens, onSessionEnded } = setup((url) => {
      if (url.endsWith("/auth/refresh")) throw new TypeError("network down");
      return json(401, {});
    });

    await expect(api.request("GET", "/conversations")).rejects.toThrow();

    expect(tokens.getRefresh()).toBe("refresh-1");
    expect(onSessionEnded).not.toHaveBeenCalled();
  });

  it("retries only once: a second 401 after refreshing is an error", async () => {
    const { api, calls } = setup((url) => (url.endsWith("/auth/refresh") ? json(200, auth(2)) : json(401, {})));

    await expect(api.request("GET", "/conversations")).rejects.toBeInstanceOf(ApiError);

    expect(calls).toHaveLength(3);
  });

  it("serialises refreshes across tabs that share the refresh token, so the second uses the rotated token", async () => {
    // Two tabs: one refresh token (localStorage), separate access tokens, one cross-tab lock. The server rotates the token and
    // treats a used one as theft, so an overlap would end the session.
    let stored: string | null = "refresh-1";
    const tab = () => {
      let access: string | null = "access-1";
      return { getAccess: () => access, setAccess: (t: string | null) => (access = t), getRefresh: () => stored, setRefresh: (t: string | null) => (stored = t) };
    };
    let tail: Promise<unknown> = Promise.resolve();
    const lock = <T,>(_name: string, fn: () => Promise<T>): Promise<T> => {
      const run = tail.then(fn);
      tail = run.catch(() => {});
      return run;
    };
    let current = 1;
    const seen: string[] = [];
    const fetchFn = (async (_url: RequestInfo | URL, init?: RequestInit) => {
      const { refreshToken } = JSON.parse(init!.body as string);
      seen.push(refreshToken);
      await new Promise((r) => setTimeout(r, 5));
      if (refreshToken !== `refresh-${current}`) return json(401, { code: "invalid_refresh_token" });
      current++;
      return json(200, auth(current));
    }) as typeof fetch;
    const onSessionEnded = vi.fn();
    const a = createApi({ baseUrl: "http://api.test", tokens: tab(), fetch: fetchFn, onSessionEnded, lock });
    const b = createApi({ baseUrl: "http://api.test", tokens: tab(), fetch: fetchFn, onSessionEnded, lock });

    await Promise.all([a.refresh(), b.refresh()]);

    expect(seen).toEqual(["refresh-1", "refresh-2"]);
    expect(stored).toBe("refresh-3");
    expect(onSessionEnded).not.toHaveBeenCalled();
  });

  it("never refreshes for the auth endpoints themselves", async () => {
    const { api, calls } = setup(() => json(401, { code: "invalid_credentials" }));

    const error = await failure(api.login("ann", "wrong"));

    expect(error.code).toBe("invalid_credentials");
    expect(calls).toHaveLength(1);
  });
});

describe("auth calls", () => {
  it("login stores both tokens and returns the response", async () => {
    const { api, tokens, calls } = setup(() => json(200, auth(5)), memoryTokens(null, null));

    const result = await api.login("ann", "pw");

    expect(result.user.username).toBe("ann");
    expect(tokens.getAccess()).toBe("access-5");
    expect(tokens.getRefresh()).toBe("refresh-5");
    expect(JSON.parse(calls[0].init.body as string)).toEqual({ identifier: "ann", password: "pw" });
  });

  it("logout clears the tokens even if the server call fails", async () => {
    const { api, tokens } = setup(() => {
      throw new TypeError("offline");
    });

    await api.logout();

    expect(tokens.getAccess()).toBeNull();
    expect(tokens.getRefresh()).toBeNull();
  });

  it("register, verifyEmail and resendVerification post to their endpoints", async () => {
    const { api, calls } = setup(() => json(202, {}));

    await api.register({ username: "ann", email: "ann@example.com", password: "password1", displayName: "Ann" });
    await api.verifyEmail("ann@example.com", "123456");
    await api.resendVerification("ann@example.com");

    expect(calls.map((c) => c.url.replace("http://api.test", ""))).toEqual(["/auth/register", "/auth/verify-email", "/auth/resend-verification"]);
    expect(JSON.parse(calls[1].init.body as string)).toEqual({ email: "ann@example.com", code: "123456" });
  });
});

describe("accessToken", () => {
  it("is the token in the store now, including after a refresh", async () => {
    const { api } = setup((url) => (url.endsWith("/auth/refresh") ? json(200, auth(2)) : json(200, {})));
    expect(api.accessToken()).toBe("access-1");

    await api.refresh();

    expect(api.accessToken()).toBe("access-2");
  });
});

describe("createLocalTokenStore", () => {
  beforeEach(() => localStorage.clear());

  it("keeps the access token in memory and the refresh token in localStorage", () => {
    const store = createLocalTokenStore();

    store.setAccess("a");
    store.setRefresh("r");

    expect(localStorage.getItem("refreshToken")).toBe("r");
    expect(localStorage.getItem("accessToken")).toBeNull();
    expect(createLocalTokenStore().getRefresh()).toBe("r");
    expect(createLocalTokenStore().getAccess()).toBeNull();

    store.setRefresh(null);
    expect(localStorage.getItem("refreshToken")).toBeNull();
  });
});

describe("avatars and files", () => {
  it("uploads a file as multipart form data, leaving the content type to the browser", async () => {
    const { api, calls } = setup(() => json(201, { id: "m1", mimeType: "image/png", sizeBytes: 3 }));
    const file = new File([new Uint8Array([1, 2, 3])], "me.png", { type: "image/png" });

    const result = await api.uploadMedia(file);

    expect(result).toEqual({ id: "m1", mimeType: "image/png", sizeBytes: 3 });
    expect(calls[0].url).toBe("http://api.test/media");
    expect(calls[0].init.method).toBe("POST");
    expect(header(calls[0].init, "Authorization")).toBe("Bearer access-1");
    expect(header(calls[0].init, "Content-Type")).toBeNull();
    expect((calls[0].init.body as FormData).get("file")).toBeInstanceOf(File);
  });

  it("fetches a blob with the token, refreshing once on 401", async () => {
    let first = true;
    const { api, calls } = setup((url) => {
      if (url.endsWith("/auth/refresh")) return json(200, auth(2));
      if (first) {
        first = false;
        return json(401, { code: "unauthorized" });
      }
      return new Response(new Uint8Array([7, 8]), { status: 200, headers: { "Content-Type": "image/webp" } });
    });

    const blob = await api.getBlob("/avatars/01-a");

    expect(blob.size).toBe(2);
    expect(calls.map((c) => c.url)).toEqual(["http://api.test/avatars/01-a", "http://api.test/auth/refresh", "http://api.test/avatars/01-a"]);
    expect(header(calls[2].init, "Authorization")).toBe("Bearer access-2");
  });

  it("sets, clears and lists avatars", async () => {
    const { api, calls } = setup((url, init) =>
      url.endsWith("/avatars") && init.method === "GET" ? json(200, { avatars: [{ key: "01-a" }, { key: "02-b" }] }) : json(204));

    await api.setAvatar({ presetKey: "01-a" });
    await api.clearAvatar();
    const keys = await api.avatarPresets();

    expect(calls[0].init.method).toBe("PUT");
    expect(calls[0].url).toBe("http://api.test/users/me/avatar");
    expect(calls[0].init.body).toBe(JSON.stringify({ presetKey: "01-a" }));
    expect(calls[1].init.method).toBe("DELETE");
    expect(calls[1].url).toBe("http://api.test/users/me/avatar");
    expect(keys).toEqual(["01-a", "02-b"]);
  });
});
