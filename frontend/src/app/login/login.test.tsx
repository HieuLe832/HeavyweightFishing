import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api";
import { AuthContext, type AuthValue } from "@/lib/auth";
import { messageFor } from "@/lib/errors";
import LoginPage from "./page";

const replace = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace, push: vi.fn() }) }));

function renderPage(login: AuthValue["login"]) {
  const value = { user: null, ready: true, login, logout: vi.fn(), api: {} } as unknown as AuthValue;
  return render(<AuthContext.Provider value={value}><LoginPage /></AuthContext.Provider>);
}

async function fillAndSubmit() {
  await userEvent.type(screen.getByLabelText("Username or email"), "ann");
  await userEvent.type(screen.getByLabelText("Password"), "secret-pw");
  await userEvent.click(screen.getByRole("button", { name: /sign in/i }));
}

describe("login page", () => {
  beforeEach(() => replace.mockReset());

  it("shows the friendly message for invalid_credentials, not the server text", async () => {
    renderPage(vi.fn().mockRejectedValue(new ApiError(401, "invalid_credentials", "raw server text")));

    await fillAndSubmit();

    expect(await screen.findByRole("alert")).toHaveTextContent(messageFor("invalid_credentials"));
    expect(screen.queryByText("raw server text")).not.toBeInTheDocument();
    expect(replace).not.toHaveBeenCalled();
  });

  it("links to verification when the email is not verified", async () => {
    renderPage(vi.fn().mockRejectedValue(new ApiError(403, "email_not_verified", "x")));

    await fillAndSubmit();

    expect(await screen.findByRole("link", { name: /verify/i })).toHaveAttribute("href", "/register");
  });

  it("goes to / on success", async () => {
    const login = vi.fn().mockResolvedValue(undefined);
    renderPage(login);

    await fillAndSubmit();

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/"));
    expect(login).toHaveBeenCalledWith("ann", "secret-pw");
  });

  it("disables the button while signing in", async () => {
    let finish!: () => void;
    renderPage(vi.fn(() => new Promise<void>((resolve) => { finish = resolve; })));

    await fillAndSubmit();

    expect(screen.getByRole("button", { name: /sign in/i })).toBeDisabled();
    finish();
  });

  it("does not submit an empty form", async () => {
    const login = vi.fn();
    renderPage(login);

    await userEvent.click(screen.getByRole("button", { name: /sign in/i }));

    expect(login).not.toHaveBeenCalled();
  });

  it("links to the register page", () => {
    renderPage(vi.fn());

    expect(screen.getByRole("link", { name: /create an account/i })).toHaveAttribute("href", "/register");
  });
});
