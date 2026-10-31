import { describe, expect, it } from "vitest";
import { ApiError } from "./api";
import { messageFor, messageForError } from "./errors";

describe("messageFor", () => {
  const known = [
    "invalid_credentials", "email_not_verified", "account_banned", "email_taken", "username_taken", "invalid_code", "validation_failed",
    "rate_limited", "blocked", "user_not_found", "session_ended", "conversation_not_found", "connection_lost",
  ];

  it.each(known)("has friendly copy for %s", (code) => {
    const text = messageFor(code);
    expect(text.length).toBeGreaterThan(10);
    expect(text).not.toContain("_");
  });

  it("falls back to a generic line for an unknown code", () => {
    expect(messageFor("something_new")).toBe(messageFor("unknown"));
    expect(messageFor("unknown")).toMatch(/something went wrong/i);
  });

  it("uses the copy for an ApiError, and the generic line for any other error", () => {
    expect(messageForError(new ApiError(401, "invalid_credentials", "raw server text"))).toBe(messageFor("invalid_credentials"));
    expect(messageForError(new TypeError("Failed to fetch"))).toMatch(/reach the server/i);
  });
});
