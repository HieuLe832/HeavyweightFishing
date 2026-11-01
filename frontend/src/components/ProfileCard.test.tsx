import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, type Api, type User } from "@/lib/api";
import { AuthContext, type AuthValue } from "@/lib/auth";
import { clearAuthedImages } from "@/lib/authed-image";
import { ProfileCard } from "./ProfileCard";

const lan: User = { id: "u1", username: "lan", email: "lan@example.com", displayName: "Lan Nguyen", role: "user" };

function setup(user: User = lan, over: Partial<Api> = {}) {
  const api = {
    getBlob: vi.fn(async () => new Blob(["x"])),
    avatarPresets: vi.fn(async () => ["01-a", "02-b"]),
    setAvatar: vi.fn(async () => undefined),
    clearAvatar: vi.fn(async () => undefined),
    uploadMedia: vi.fn(async () => ({ id: "m1", mimeType: "image/png", sizeBytes: 3 })),
    ...over,
  } as unknown as Api;
  const auth = { user, ready: true, api, login: vi.fn(), logout: vi.fn(async () => undefined), reloadUser: vi.fn(async () => undefined) } as unknown as AuthValue;
  render(
    <AuthContext.Provider value={auth}>
      <ProfileCard />
    </AuthContext.Provider>,
  );
  return { api, auth, ui: userEvent.setup() };
}

const card = () => screen.getByRole("button", { name: /Lan Nguyen/ });

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => "blob:test/1");
  URL.revokeObjectURL = vi.fn();
});
afterEach(() => clearAuthedImages());

describe("ProfileCard", () => {
  it("shows who is signed in and opens their profile, closing on Escape", async () => {
    const { ui } = setup();
    expect(card()).toHaveTextContent("@lan");

    await ui.click(card());
    const dialog = screen.getByRole("dialog", { name: "Profile" });
    expect(within(dialog).getByText("Lan Nguyen")).toBeInTheDocument();
    expect(within(dialog).getByText("@lan")).toBeInTheDocument();
    expect(within(dialog).getByText("lan@example.com")).toBeInTheDocument();

    await ui.keyboard("{Escape}");
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(card()).toHaveFocus();
  });

  it("closes on a click outside", async () => {
    const { ui } = setup();
    await ui.click(card());
    expect(screen.getByRole("dialog")).toBeInTheDocument();

    await ui.click(document.body);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("lists the characters, marks the current one and picks another", async () => {
    const { ui, api, auth } = setup({ ...lan, avatarPresetKey: "01-a" });
    await ui.click(card());

    const current = await screen.findByRole("button", { name: "Use 01-a" });
    expect(current).toHaveAttribute("aria-pressed", "true");
    const other = screen.getByRole("button", { name: "Use 02-b" });
    expect(other).toHaveAttribute("aria-pressed", "false");

    await ui.click(other);
    expect(api.setAvatar).toHaveBeenCalledWith({ presetKey: "02-b" });
    await waitFor(() => expect(auth.reloadUser).toHaveBeenCalled());
  });

  it("falls back to the character's name, not its number, when a picture cannot be fetched", async () => {
    const { ui } = setup(lan, {
      avatarPresets: vi.fn(async () => ["07-curly-blond"]),
      getBlob: vi.fn(async () => Promise.reject(new ApiError(503, "avatar_provider_unavailable", "x"))),
    });
    await ui.click(card());

    const button = await screen.findByRole("button", { name: "Use 07-curly-blond" });
    await waitFor(() => expect(button).toHaveTextContent("C"));
  });

  it("says when the characters are offline", async () => {
    const { ui } = setup(lan, { avatarPresets: vi.fn(async () => Promise.reject(new ApiError(503, "avatar_provider_unavailable", "x"))) });
    await ui.click(card());

    expect(await screen.findByText("Character avatars are offline right now.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Use / })).not.toBeInTheDocument();
  });

  it("uploads a picture and makes it the avatar", async () => {
    const { ui, api, auth } = setup();
    await ui.click(card());

    const file = new File([new Uint8Array([1])], "me.png", { type: "image/png" });
    await ui.upload(screen.getByLabelText("Upload a picture"), file);

    await waitFor(() => expect(api.setAvatar).toHaveBeenCalledWith({ mediaId: "m1" }));
    expect(api.uploadMedia).toHaveBeenCalledWith(file);
    await waitFor(() => expect(auth.reloadUser).toHaveBeenCalled());
  });

  it("shows an upload error inside the profile", async () => {
    const { ui, api } = setup(lan, { uploadMedia: vi.fn(async () => Promise.reject(new ApiError(413, "file_too_large", "x"))) });
    await ui.click(card());

    await ui.upload(screen.getByLabelText("Upload a picture"), new File([new Uint8Array([1])], "big.png", { type: "image/png" }));

    expect(await within(screen.getByRole("dialog")).findByRole("alert")).toHaveTextContent("That picture is too large");
    expect(api.setAvatar).not.toHaveBeenCalled();
  });

  it("offers to remove the avatar only when there is one", async () => {
    const without = setup();
    await without.ui.click(card());
    expect(screen.queryByRole("button", { name: "Remove avatar" })).not.toBeInTheDocument();
  });

  it("removes the avatar", async () => {
    const { ui, api, auth } = setup({ ...lan, avatarMediaId: "m1" });
    await ui.click(card());

    await ui.click(screen.getByRole("button", { name: "Remove avatar" }));
    expect(api.clearAvatar).toHaveBeenCalled();
    await waitFor(() => expect(auth.reloadUser).toHaveBeenCalled());
  });

  it("signs out", async () => {
    const { ui, auth } = setup();
    await ui.click(card());

    await ui.click(screen.getByRole("button", { name: "Sign out" }));
    expect(auth.logout).toHaveBeenCalled();
  });
});
