import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api";
import { AuthContext, type AuthValue } from "@/lib/auth";
import { messageFor } from "@/lib/errors";
import RegisterPage from "./page";

const replace = vi.fn();
const push = vi.fn();
vi.mock("next/navigation", () => ({ useRouter: () => ({ replace, push }) }));

function setup(api: Partial<Record<"register" | "verifyEmail" | "resendVerification", ReturnType<typeof vi.fn>>> = {}) {
  const fakeApi = { register: vi.fn().mockResolvedValue(undefined), verifyEmail: vi.fn().mockResolvedValue(undefined), resendVerification: vi.fn().mockResolvedValue(undefined), ...api };
  const login = vi.fn().mockResolvedValue(undefined);
  const value = { user: null, ready: true, login, logout: vi.fn(), api: fakeApi } as unknown as AuthValue;
  render(<AuthContext.Provider value={value}><RegisterPage /></AuthContext.Provider>);
  return { fakeApi, login };
}

async function fillForm() {
  await userEvent.type(screen.getByLabelText("Username"), "ann_fisher");
  await userEvent.type(screen.getByLabelText("Email"), "ann@example.com");
  await userEvent.type(screen.getByLabelText("Display name"), "Ann");
  await userEvent.type(screen.getByLabelText("Password"), "password-1");
  await userEvent.click(screen.getByRole("button", { name: /create account/i }));
}

describe("register page", () => {
  beforeEach(() => { replace.mockReset(); push.mockReset(); });

  it("shows the field errors from validation_failed under their fields", async () => {
    setup({ register: vi.fn().mockRejectedValue(new ApiError(400, "validation_failed", "x", { username: ["Use 3-32 letters, digits or underscores."], password: ["Use 8-128 characters."] })) });

    await fillForm();

    expect(await screen.findByText("Use 3-32 letters, digits or underscores.")).toBeInTheDocument();
    expect(screen.getByLabelText("Password")).toHaveAccessibleDescription("Use 8-128 characters.");
  });

  it("shows the friendly message when the email is taken", async () => {
    setup({ register: vi.fn().mockRejectedValue(new ApiError(409, "email_taken", "x")) });

    await fillForm();

    expect(await screen.findByRole("alert")).toHaveTextContent(messageFor("email_taken"));
  });

  it("moves to the code step after registering, sending the right body", async () => {
    const { fakeApi } = setup();

    await fillForm();

    expect(await screen.findByLabelText("6-digit code")).toBeInTheDocument();
    expect(fakeApi.register).toHaveBeenCalledWith({ username: "ann_fisher", email: "ann@example.com", displayName: "Ann", password: "password-1" });
  });

  it("verifies the code, then signs in and goes to /", async () => {
    const { fakeApi, login } = setup();
    await fillForm();

    await userEvent.type(await screen.findByLabelText("6-digit code"), "123456");
    await userEvent.click(screen.getByRole("button", { name: /verify/i }));

    await waitFor(() => expect(replace).toHaveBeenCalledWith("/"));
    expect(fakeApi.verifyEmail).toHaveBeenCalledWith("ann@example.com", "123456");
    expect(login).toHaveBeenCalledWith("ann@example.com", "password-1");
  });

  it("shows invalid_code and stays on the code step", async () => {
    setup({ verifyEmail: vi.fn().mockRejectedValue(new ApiError(400, "invalid_code", "x")) });
    await fillForm();

    await userEvent.type(await screen.findByLabelText("6-digit code"), "000000");
    await userEvent.click(screen.getByRole("button", { name: /verify/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(messageFor("invalid_code"));
    expect(replace).not.toHaveBeenCalled();
  });

  it("resend calls resend-verification and says so", async () => {
    const { fakeApi } = setup();
    await fillForm();

    await userEvent.click(await screen.findByRole("button", { name: /resend/i }));

    expect(fakeApi.resendVerification).toHaveBeenCalledWith("ann@example.com");
    expect(await screen.findByRole("status")).toHaveTextContent(/sent/i);
  });

  it("a person who already has a code can verify without registering, then goes to sign in", async () => {
    const { fakeApi } = setup();

    await userEvent.click(screen.getByRole("button", { name: /already have a code/i }));
    await userEvent.type(screen.getByLabelText("Email"), "ann@example.com");
    await userEvent.type(screen.getByLabelText("6-digit code"), "123456");
    await userEvent.click(screen.getByRole("button", { name: /verify/i }));

    await waitFor(() => expect(push).toHaveBeenCalledWith("/login"));
    expect(fakeApi.verifyEmail).toHaveBeenCalledWith("ann@example.com", "123456");
  });
});
