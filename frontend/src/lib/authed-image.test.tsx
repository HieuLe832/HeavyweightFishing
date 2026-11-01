import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Api } from "./api";
import { AuthContext, type AuthValue } from "./auth";
import { avatarPath, clearAuthedImages, useAuthedImage } from "./authed-image";

function wrapperWith(getBlob: Api["getBlob"]) {
  const auth = { user: null, ready: true, api: { getBlob } as unknown as Api } as unknown as AuthValue;
  return function Wrapper({ children }: { children: ReactNode }) {
    return <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>;
  };
}

let urls = 0;
beforeEach(() => {
  urls = 0;
  URL.createObjectURL = vi.fn(() => `blob:test/${++urls}`);
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => clearAuthedImages());

describe("avatarPath", () => {
  it("prefers an upload, then a preset, else nothing", () => {
    expect(avatarPath({ avatarMediaId: "m1", avatarPresetKey: "01-a" })).toBe("/media/m1");
    expect(avatarPath({ avatarPresetKey: "01-a" })).toBe("/avatars/01-a");
    expect(avatarPath({})).toBeNull();
  });
});

describe("useAuthedImage", () => {
  it("gives an object URL once the blob arrives", async () => {
    const getBlob = vi.fn(async () => new Blob(["x"]));
    const { result } = renderHook(() => useAuthedImage("/avatars/01-a"), { wrapper: wrapperWith(getBlob) });

    expect(result.current).toBeNull();
    await waitFor(() => expect(result.current).toBe("blob:test/1"));
    expect(getBlob).toHaveBeenCalledWith("/avatars/01-a");
  });

  it("gives null when the file cannot be fetched", async () => {
    const getBlob = vi.fn(async () => {
      throw new Error("503");
    });
    const { result } = renderHook(() => useAuthedImage("/avatars/01-a"), { wrapper: wrapperWith(getBlob) });

    await waitFor(() => expect(getBlob).toHaveBeenCalled());
    expect(result.current).toBeNull();
  });

  it("fetches a path once for every image that shows it", async () => {
    const getBlob = vi.fn(async () => new Blob(["x"]));
    const wrapper = wrapperWith(getBlob);
    const a = renderHook(() => useAuthedImage("/media/m1"), { wrapper });
    const b = renderHook(() => useAuthedImage("/media/m1"), { wrapper });

    await waitFor(() => expect(a.result.current).toBe("blob:test/1"));
    await waitFor(() => expect(b.result.current).toBe("blob:test/1"));
    expect(getBlob).toHaveBeenCalledTimes(1);
  });

  it("tries a failed path again on the next mount, so avatars come back with the pack", async () => {
    const getBlob = vi.fn().mockRejectedValueOnce(new Error("503")).mockResolvedValue(new Blob(["x"]));
    const wrapper = wrapperWith(getBlob);
    const first = renderHook(() => useAuthedImage("/avatars/01-a"), { wrapper });
    await waitFor(() => expect(getBlob).toHaveBeenCalledTimes(1));
    first.unmount();

    const second = renderHook(() => useAuthedImage("/avatars/01-a"), { wrapper });
    await waitFor(() => expect(second.result.current).toBe("blob:test/1"));
  });

  it("forgets every image on sign-out, so the next account fetches its own", async () => {
    const getBlob = vi.fn(async () => new Blob(["x"]));
    const wrapper = wrapperWith(getBlob);
    const before = renderHook(() => useAuthedImage("/media/m1"), { wrapper });
    await waitFor(() => expect(before.result.current).toBe("blob:test/1"));
    before.unmount();

    act(() => clearAuthedImages());
    const after = renderHook(() => useAuthedImage("/media/m1"), { wrapper });

    await waitFor(() => expect(after.result.current).toBe("blob:test/2"));
    expect(getBlob).toHaveBeenCalledTimes(2);
    expect(URL.revokeObjectURL).toHaveBeenCalledWith("blob:test/1");
  });
});
