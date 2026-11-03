# Local accounts

Whiskers signs people in three ways: **local accounts** (email + password, ASP.NET Identity, stored in the Whiskers
database), **Google** and **generic OIDC**. This guide covers the local accounts: who may change what, how a forgotten
or compromised password is recovered, and what Whiskers does **not** offer for them.

## The MFA posture (read this first)

**Local accounts have no second factor in Whiskers 1.0.** A correct password alone signs in. What protects them is the
password policy, the account lockout and the per-client rate limit (below), and that every credential change ends the
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
| Lockout | 5 wrong passwords (or wrong current passwords, or wrong recovery codes) lock the account for 15 minutes |
| Rate limit | 10 sign-in attempts per minute per client address |

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

A wrong code counts towards the lockout like a wrong password. Unknown email, wrong, used or expired code and a locked
account all get the same answer.

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

Whiskers keeps at least one Admin who can sign in:

- *User roles* refuses to demote or remove the last explicit Admin (since TA-02).
- *Local accounts* refuses to delete an Admin when no other Admin can sign in: another Admin counts only with a local
  account that has a password, or — without a local account — as a Google/OIDC user Whiskers has seen signing in. An
  Admin still waiting for a recovery code does not count.
- Admin changes are serialised: two Admins deleting each other at the same moment leave one of them.
- You cannot delete or reset your own account here; another Admin does that.

Deleting an account also removes its explicit role entry, so a Google/OIDC sign-in with the same address does not
inherit it. Unattended items the deleted user owned lose their authority (R1-04).

## Upgrading

No database change and no configuration change. Existing local accounts keep their passwords. Pending recovery codes
live in the Identity token table, which the SQLite → PostgreSQL migrator copies.
