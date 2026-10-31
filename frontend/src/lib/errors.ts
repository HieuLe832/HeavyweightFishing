import { ApiError } from "./api";

const copy: Record<string, string> = {
  invalid_credentials: "Wrong username, email or password.",
  email_not_verified: "Verify your email before signing in.",
  account_banned: "This account has been banned.",
  email_taken: "That email is already registered.",
  username_taken: "That username is taken.",
  invalid_code: "That code is wrong or has expired.",
  validation_failed: "Please check the highlighted fields.",
  rate_limited: "You are going too fast. Wait a moment and try again.",
  blocked: "You cannot message this person.",
  user_not_found: "That person could not be found.",
  session_ended: "Your session has ended. Sign in again.",
  conversation_not_found: "That conversation is no longer available.",
  connection_lost: "The connection was lost. Check your network and try again.",
  unknown: "Something went wrong. Please try again.",
};

/** Friendly text for an API error code. The server's own wording is never shown. */
export const messageFor = (code: string): string => copy[code] ?? copy.unknown;

export function messageForError(error: unknown): string {
  if (error instanceof ApiError) return messageFor(error.code);
  if (error instanceof TypeError) return "Could not reach the server. Check your network and try again.";
  return messageFor("unknown");
}
