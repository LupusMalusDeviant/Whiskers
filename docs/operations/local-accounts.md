# Local accounts

Whiskers signs people in three ways: **local accounts** (email + password, ASP.NET Identity, stored in the Whiskers
database), **Google** and **generic OIDC**. This guide covers the local accounts: who may change what, how a forgotten
or compromised password is recovered, and what Whiskers does **not** offer for them.

## The MFA posture (read this first)

**Local accounts have no second factor in Whiskers 1.0.** A correct password alone signs in. What protects them is the
password policy, the account lockout and the rate limits of the sign-in and recovery pages (below), and that every credential change ends the
account's sessions at once.

For production, and for any instance reachable from the internet:

- **Sign in through your identity provider (OIDC) and require MFA there** (Authentik, Keycloak, Authelia, Zitadel,
  Entra ID, …). Whiskers takes the identity from the IdP; MFA, passkeys and password rules are the IdP's job.
- Keep local accounts for the **first setup** and as a **break-glass** fallback: few of them, long unique passwords,
  stored in a password manager. Delete local accounts nobody needs. `Auth__LocalLogin__Enabled=false` only hides the
  password form on the sign-in page; it does not switch local sign-in off — an existing local account can still sign
  in, so deleting the account is what removes it.

The same statement is shown in *Settings → Local accounts* and on the *My account* page.

## Password policy and lockout

Set by ASP.NET Identity's options in Whiskers (not configurable in 1.0):

| Rule | Value |
|---|---|
| Minimum length | 12 characters |
| Character classes | at least one upper-case letter, one lower-case letter, one digit and one symbol |
| Lockout | 5 wrong passwords (or wrong current passwords, or wrong recovery codes for an account that has a code) lock the account for 15 minutes |
| Sign-in rate limit | 10 sign-in attempts (`/login-local`) per minute per client address (HTTP rate limiter) |
| Recovery-page limit | 10 recovery-code attempts (`/account/recover`) per minute per client (an IPv4 address, or an IPv6 /64), and 10 per account per 15 minutes from all clients together (enforced by Whiskers itself: the page runs in a live connection the HTTP limiter does not see); every answer of the page takes at least one second |

Passwords and recovery codes are hashed with ASP.NET Identity's password hasher (PBKDF2); Whiskers stores neither in
readable form and never writes them to a log or the audit trail — not even a prefix.

## Who can do what

| Action | Where | Who | Capability (audited) |
|---|---|---|---|
| Change your own password | *My account* (person icon in the top bar) | every signed-in user with a local account; needs the current password | `account.password_change` (read level — also allowed while the kill switch is on) |
| Add a local account | *Settings → Local accounts* | Admin | `account.create` |
| Reset another account's password | *Settings → Local accounts* | Admin (not for the own account) | `account.password_reset` |
| Delete a local account | *Settings → Local accounts* | Admin (not the own account, never the last Admin who can sign in) | `account.delete` |
| Set a new password with a recovery code | `/account/recover` (linked from the sign-in page) | whoever holds the code | audited as `auth.recovery_redeemed` / `auth.recovery_refused` |
| Issue a recovery code on the host | `--recover-local-account` (below) | whoever can run a command in the Whiskers container | audited as `auth.recovery_issued` |

Every Admin action runs through the Mutation Gateway and is recorded with the account's email (and the role, when an
account is added) — never with a password or code. Roles stay in *Settings → User roles*; an account added here gets
its role there as well.

### Sessions end on every credential change

Changing a password, resetting it, redeeming a recovery code, issuing a code on the host and deleting the account all
give the account a new security stamp or remove it. Since R1-26 every cookie is checked against the account on each
request, so **every session of the account ends at once** — browser cookies on their next request, open pages within
30 seconds, terminals within 15 seconds. After changing your own password you are signed out and sign in again.

## Recovery codes instead of temporary passwords

Whiskers never hands out a temporary password. An Admin's reset, an invitation for a new account and the host recovery
all produce a **one-time recovery code**:

- 24 random characters, shown as `XXXX-XXXX-XXXX-XXXX-XXXX-XXXX`, **shown once** to whoever issued it;
- valid for **24 hours** and for **one** use; a new code replaces an older one;
- it **cannot sign in**. The account's owner opens `/account/recover`, enters the email, the code and a new password.
  So the change of password on first use is enforced by construction;
- the reset removes the old password at once, so a compromised password stops working before the owner has picked a
  new one.

A wrong code counts towards the lockout like a wrong password — but only for an account that has a code: trying codes
for an account with an ordinary password does not lock it. Unknown email, wrong, used or expired code, a locked account
and a throttled attempt all get the same answer. Attempts beyond the recovery-page limit check nothing; the first one of
a client in each minute is audited (`auth.recovery_refused`, `reason=rate_limited`), the rest only counted in the log.
A successful redemption is audited before the password is set; if the audit row cannot be written, nothing is set.

**Which address the limit sees.** The client address is the one of the page's live connection (WebSockets or long
polling — both work), as the forwarded-headers handling resolved it. The per-client limit becomes **shared** — many
people counted as one client — when:

- your reverse proxy is **not** in `ForwardedHeaders:TrustedNetworks`: every visitor then has the proxy's address
  (the sign-in limit of `/login-local` has the same problem);
- the address cannot be determined at all: those clients share one bucket (`unknown`);
- Whiskers remembers 4096 clients and 4096 accounts at most: beyond that, while the older entries have not expired,
  every new address (or account) shares one overflow bucket. Under a flood of new addresses recovery may then be
  refused for a few minutes (clients) and up to about 15 minutes after the flood stops (accounts), and new entries may
  share one budget for up to about two windows (2 minutes for clients, 30 for accounts); the limit is never lifted. IPv6 clients are counted per /64, so a site with a delegated /56 or /48 prefix can present many clients.

**List your real proxy in `ForwardedHeaders:TrustedNetworks`, and only it.** Since R1-50 the default trusts loopback
only, so no client can pose as another with its own `X-Forwarded-For`; a proxy in another container or on another host
must be listed, or every visitor counts as that proxy (Settings → production readiness then names the proxy). Do not
list whole private ranges: anything in a listed range can claim any address, and the per-client limit and the bounded
audit trail of refusals no longer hold for it (the per-account limit and the lockout still do). Example:
`ForwardedHeaders__TrustedNetworks__0=172.18.0.2/32` for a single proxy container. An entry that is not a canonical CIDR
or address stops the start, naming the reason (for example host bits set in `172.18.0.2/16`). Do not set
`ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`: it makes ASP.NET Core trust every peer, so Whiskers refuses to start with it.

The recovery page and the host CLI are not stopped by the kill switch (a person recovering their own access is not
a write to managed infrastructure). Adding, resetting and deleting accounts — and redeeming a code — are refused while
`AUTH_DISABLED` is set: every client is the synthetic Admin then, and there is no local sign-in to recover. A code
issued on the host meanwhile can be redeemed once authentication is on again (within its 24 hours).

An Admin who resets another account (another Admin included) or adds one receives its code and could use it
themselves — by design, as with any Admin-issued credential; the reset is audited and the owner notices that their
password no longer works.

Hand the code over a trusted channel (in person, a password manager share, a phone call) — not in the same email
thread as the address of your Whiskers instance.

## Lost the last Admin password (host recovery)

If no Admin can sign in any more, whoever operates the host issues a code from inside the running container:

```bash
docker compose exec serverwatch dotnet Whiskers.dll --recover-local-account admin@example.com
# or: docker exec -it <whiskers-container> dotnet Whiskers.dll --recover-local-account admin@example.com
```

The command prints the code **to your terminal only**, then exits; it does not start a second Whiskers and applies no
staged restore. The account's password is removed and its sessions end; set a new one at `/account/recover`.

- Use `exec` into the running container. Do **not** use `docker compose run`: the output of a `run` container is part
  of that container's log and may be shipped by a logging driver.
- It works only for an **existing** local account and changes nothing else: it never creates an account and never
  grants a role. To re-create a lost Admin entirely, see the admin bootstrap (`WHISKERS_ADMIN_EMAIL`, first run only)
  in the README.
- The audit row (`auth.recovery_issued`, actor `cli (--recover-local-account)`) is written **before** anything changes;
  if it cannot be written, nothing changes (exit code 1).
- Exit codes: `0` code issued, `2` usage, `3` no local account with that email, `1` nothing changed (audit or store
  unavailable, or the account changed at the same moment — run it again).

Why this is safe: anyone who can run a command in the Whiskers container already holds its database, data directory and
keys (PRODUCT_CONTRACT §5.2). The command gives that person nothing new; it only spares them editing the database.

## The last Admin

Whiskers guards the last Admin as follows:

- *User roles* refuses to demote or remove the last **explicit** Admin entry (since TA-02). It counts entries, not
  whether their owner can sign in: with another explicit Admin that cannot sign in (no account yet, or waiting for a
  recovery code), the only Admin who can sign in may still be demoted there. The host CLI then restores access.
- *Local accounts* refuses to delete an Admin when no other Admin can sign in: another Admin counts only with a local
  account that has a password, or — without a local account — as a Google/OIDC user Whiskers has seen signing in. An
  Admin still waiting for a recovery code does not count.
- Admin changes are serialised: two Admins deleting each other at the same moment leave one of them. A deletion removes
  the role first, so a demotion on *User roles* at the same moment makes the role store refuse it before anything
  changed.
- You cannot delete or reset your own account here; another Admin does that.

Deleting an account also removes its explicit role entry, so a Google/OIDC sign-in with the same address does not
inherit it. Unattended items the deleted user owned lose their authority (R1-04).

## Upgrading

No database change and no configuration change. Existing local accounts keep their passwords. Pending recovery codes
live in the Identity token table, which the SQLite → PostgreSQL migrator copies.
