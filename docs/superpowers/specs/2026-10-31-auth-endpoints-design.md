# Auth endpoints — design

Date: 2026-10-31

## Goal

Implement process 1.0 Authenticate & Authorize (sub-processes 1.1–1.5 and 1.7) as HTTP endpoints in
`Chat.Api`, on top of the existing schema (D1 `users`, D2 `sessions` / `refresh_tokens`,
D3 `verification_codes`). No schema change, so the ERD, `ErdSchemaTests` and the diagrams stay as they are.

**Out of scope:** 1.6 Sign In with Google (needs real OAuth credentials; follow-up), per-IP rate
limiting, RabbitMQ-based email delivery, the ban endpoint (6.4), frontend screens, push-token
registration, a revoke-one-device endpoint. A pending account blocks its email and username until
verified; the owner can use resend-verification.

## Approach

Built-in `JwtBearer` authentication plus plain services over `ChatDbContext`. No ASP.NET Identity
tables (they would diverge from the ERD).

- **Password hashing:** `PasswordHasher<User>` from `Microsoft.Extensions.Identity.Core` (PBKDF2).
- **Access token:** JWT (HS256) with `sub` (user id), `role`, `sid` (session id). 15 minutes.
- **Refresh token:** 32 random bytes, base64url. Stored as SHA-256 in `refresh_tokens.token_hash`.
  30 days, sliding: each refresh extends `sessions.expires_at`.
- **Verification / reset code:** 6 digits from a CSPRNG. Stored as hex HMAC-SHA256 keyed with
  `Auth:CodeKey`, over `userId:purpose:code`. A plain hash of 6 digits would be brute-forceable
  from a leaked table; the key and the 5-attempt limit prevent that.
- **Time:** all services take `TimeProvider`, and set `created_at` / `updated_at` explicitly in UTC
  (the DB defaults are only a fallback). Tests use `FakeTimeProvider`.
- **Email:** `IEmailSender` with an SMTP implementation (MailKit → Mailpit in development) and a
  capturing fake in tests.

## Endpoints

All under `/auth`. JSON in and out, camelCase. Errors are `application/problem+json` with an extra
`code` field; validation errors are 400 with `code = validation_failed` and an `errors` map.

| Proc | Endpoint | Request | Success | Failures |
|---|---|---|---|---|
| 1.1 | `POST /auth/register` | `username`, `email`, `password`, `displayName?` | 201 `{ userId }`; account `pending`; code emailed | 400 invalid input; 409 `email_taken` / `username_taken` |
| 1.2 | `POST /auth/verify-email` | `email`, `code` | 204; account `active`, `email_verified_at` set | 400 `invalid_code` (wrong, expired, consumed, or attempts exhausted) |
| 1.2 | `POST /auth/resend-verification` | `email` | 202 always | — |
| 1.3 | `POST /auth/password-reset/request` | `email` | 202 always; code emailed only if an active account exists | — |
| 1.3 | `POST /auth/password-reset/confirm` | `email`, `code`, `newPassword` | 204; hash replaced, lock cleared, all sessions revoked (`password_reset`) | 400 `invalid_code`; 400 validation |
| 1.4 / 1.5 | `POST /auth/login` | `identifier` (email or username), `password`, `deviceName?` | 200 `{ accessToken, expiresIn, refreshToken, user }`; new session | 401 `invalid_credentials`; 403 `email_not_verified` / `account_banned` |
| 1.5 | `POST /auth/refresh` | `refreshToken` | 200 as login (rotated token, same session) | 401 `invalid_refresh_token` |
| 1.5 | `POST /auth/logout` | — (bearer) | 204; current session revoked (`logout`) | 401 |
| 1.7 | `GET /auth/me` | — (bearer) | 200 `{ id, username, email, displayName, role }` | 401 |
| 1.7 | `GET /auth/sessions` | — (bearer) | 200 list of the caller's live sessions, `current` flagged | 401 |

`GET /auth/me` is the sample protected route. The `Admin` authorization policy (`role = admin`) is
registered now for 6.x to use; nothing uses it yet, so 403 is covered by a test-only route.

## Behaviour rules

**Register.** Username 3–32 chars `[A-Za-z0-9_]`; email syntactically valid, ≤ 254; password 8–128 chars;
`displayName` defaults to the username. Uniqueness is case-insensitive (`citext`); a race on the unique
index is caught and reported as 409. Creating the user and its code is one transaction; the email is sent
after commit. A failed send is logged and does not fail registration (the user can resend).

**Codes.** Issuing a code marks the user's earlier unconsumed codes of that purpose consumed. Valid for
15 minutes. Each wrong guess increments `attempts`; at 5 the code is dead. A correct code is consumed
once. Resend and reset-request are throttled to one code per 60 s per user (a throttled call still
returns 202, sends nothing).

**Login.**
1. Look up by email or username. Unknown user: still run a password hash verification against a dummy
   hash (equal timing) and return `invalid_credentials`.
2. If `locked_until` is in the future, return `invalid_credentials` regardless of the password, and
   do not count the attempt. Revealing the lock would let a guesser tell when a guess is right.
3. Wrong password: `failed_login_count++`; at 5, set `locked_until = now + 15 min` and reset the
   counter. Return `invalid_credentials`.
4. Correct password: reset the counter. `pending` → 403 `email_not_verified`; `banned` → 403
   `account_banned`. Otherwise create the session (`device_name` from `deviceName`, default
   "Unknown device"; `user_agent` and `ip_address` from the request) and issue tokens.

A user with no password hash (future Google-only account) fails step 3 as a wrong password.

**Refresh.** Look up by hash. Unknown, expired token, or revoked / expired session → 401. A token with
`used_at` set → revoke the whole session (`refresh_reuse`) and 401 (pitfalls.md). Re-read the user: not
`active` → revoke the session (`user_inactive`) and 401. Otherwise mark the token used with a single
conditional `UPDATE … WHERE used_at IS NULL` (zero rows affected means a concurrent use, treated as reuse),
insert the new token, bump `last_seen_at` and `expires_at`, issue a fresh access token with the
current role. There is no grace window for concurrent refreshes from the same client; clients must
serialise refresh calls.

**Authorize (1.7).** `JwtBearer` validates signature, issuer, audience and lifetime (30 s skew). An
`OnTokenValidated` handler then loads the session by `sid` and fails the request unless it exists,
belongs to `sub`, is not revoked and has not expired. This is one indexed lookup per request, and it is
what makes revocation (logout, reset, ban) immediate despite the 15-minute token. The principal is
the JWT's claims.

**Password reset confirm** revokes every non-revoked session of the user, in the same transaction as
the hash update.

## Code layout

```
backend/src/Chat.Api/
  Program.cs                      wiring only; adds `public partial class Program`
  Auth/
    AuthOptions.cs                bound from "Auth"; validated on start (JwtKey ≥ 32 bytes, etc.)
    AuthEndpoints.cs              MapAuth(): the table above; thin, calls services
    AuthRequests.cs               request/response records and validation
    AccountService.cs             register, verify email, resend, reset request/confirm
    SignInService.cs              login, refresh, logout, list sessions
    TokenService.cs               JWT creation, refresh-token generation and hashing
    CodeService.cs                code generation, HMAC, issue / check with attempts
    SessionValidator.cs           the OnTokenValidated check
    ProblemResults.cs             ProblemDetails helper with `code`
  Email/
    IEmailSender.cs, SmtpEmailSender.cs, EmailTemplates.cs
backend/tests/Chat.Api.Tests/
  ApiFactory.cs                   WebApplicationFactory<Program>, own PostgreSQL, fake email, FakeTimeProvider
  RegisterAndVerifyTests.cs, LoginTests.cs, RefreshTests.cs, PasswordResetTests.cs, AuthorizeTests.cs
```

Each service takes `ChatDbContext`, `TimeProvider`, `IOptions<AuthOptions>`; endpoints hold no logic.

## Configuration

`appsettings.json` has the non-secret defaults (`AccessTokenMinutes` 15, `RefreshTokenDays` 30,
`MaxFailedLogins` 5, `LockoutMinutes` 15, `CodeMinutes` 15, `CodeMaxAttempts` 5,
`ResendCooldownSeconds` 60, `Issuer`, `Audience`, `Smtp:Host/Port/From`). `JwtKey` and `CodeKey` have no
default: startup fails if they are missing. `appsettings.Development.json` carries dev-only keys
(Mailpit on `localhost:1025`).

## Testing

TDD, integration tests through `WebApplicationFactory` against a real PostgreSQL started the same way
as `Chat.Data.Tests` (its `PostgresFixture` is reused or copied). Each test class gets a fresh database.
Cases:

- Register: success, duplicate email / username (case-insensitive), invalid input, code emailed.
- Verify: success, wrong code (attempt counted), 5 wrong then right code rejected, expired, reused, newer
  code supersedes older.
- Login: success, wrong password, unknown user (same response), lockout after 5 and recovery after 15 min,
  pending → 403, banned → 403, email-or-username identifier.
- Refresh: rotation, old token reuse revokes the session (new token then also fails), expired, revoked
  session, banned user between refreshes, role re-read.
- Reset: unknown email still 202 and sends nothing, success revokes all sessions and clears the lock, bad
  code.
- Authorize: no token, bad signature, expired token, revoked session (after logout), `Admin` policy 403
  for a user, `/auth/sessions` flags the current session.

## Docs to update with the code

- `docs/README.md`: add an "API: auth" section (endpoint table, token model).
- `docs/pitfalls.md`: drop "not built yet" from refresh-token reuse; add the notes on code HMAC, no
  concurrent refresh, and the session check per request.
- `CLAUDE.md`: remove step 1 from "Next steps"; add `Chat.Api.Tests` to the Stack and Commands lines.
