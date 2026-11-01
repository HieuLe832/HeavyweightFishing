import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { Api } from "@/lib/api";
import { AuthContext, type AuthValue } from "@/lib/auth";
import { clearAuthedImages } from "@/lib/authed-image";
import { Avatar } from "./Avatar";

function withAuth(getBlob: Api["getBlob"], node: ReactNode) {
  const auth = { user: null, ready: true, api: { getBlob } as unknown as Api } as unknown as AuthValue;
  return render(<AuthContext.Provider value={auth}>{node}</AuthContext.Provider>);
}

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => "blob:test/1");
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => clearAuthedImages());

describe("Avatar", () => {
  it("shows the initial when there is no avatar", () => {
    const getBlob = vi.fn();
    const { container } = withAuth(getBlob, <Avatar name="lan" />);

    expect(container.textContent).toBe("L");
    expect(container.querySelector("img")).toBeNull();
    expect(getBlob).not.toHaveBeenCalled();
  });

  it("shows the picture once it loads", async () => {
    const { container } = withAuth(vi.fn(async () => new Blob(["x"])), <Avatar name="Lan" presetKey="01-a" />);

    await waitFor(() => expect(container.querySelector("img")).toHaveAttribute("src", "blob:test/1"));
    expect(container.textContent).toBe("");
  });

  it("falls back to the initial when the picture cannot be fetched or shown", async () => {
    const down = withAuth(vi.fn(async () => Promise.reject(new Error("503"))), <Avatar name="Lan" presetKey="01-a" />);
    await waitFor(() => expect(down.container.textContent).toBe("L"));
    down.unmount();
    clearAuthedImages();

    const broken = withAuth(vi.fn(async () => new Blob(["x"])), <Avatar name="Minh" mediaId="m1" />);
    const img = await waitFor(() => {
      const found = broken.container.querySelector("img");
      expect(found).not.toBeNull();
      return found!;
    });
    fireEvent.error(img);
    expect(screen.getByText("M")).toBeInTheDocument();
  });
});
