# Sign-in identities (Google and OIDC)

Since R1-27 (contract [0003](../contracts/0003-federated-identity-binding.md)) Whiskers identifies a person who signs in
through Google or a generic OIDC provider by the provider's **issuer and subject** (`sub`), not by the email address.
This guide covers what the identity provider (IdP) must guarantee, how a sign-in gets bound, how pending requests are
confirmed, and what an upgrade looks like.

## How a federated sign-in is admitted

1. **No Admin, no federated sign-in.** Until an explicit Admin exists (the setup wizard creates it), every Google and
   OIDC sign-in is refused and every federated session is ended. Local accounts the operator created keep working.
2. **A bound subject keeps its account.** Once *(issuer, subject)* is bound to an account, that binding is used for
   every later sign-in; the address the IdP sends is not consulted again (an address changed at the IdP keeps the
   account). The whitelist still applies to the account.
3. **Binding a new subject.** The address must be an **entry** an Admin configured — an explicit role or a listing in
   an enabled whitelist. Unknown addresses are refused and leave no record (the audit row names `anonymous`).
   The subject is bound automatically only when all of these hold:
   - the account has no binding yet (for any provider);
   - the email comes from the standard `email` claim (`Oidc:EmailClaim` left at `email`);
   - the provider is trusted (`GoogleAuth:TrustEmailVerified`, default `true`; `Oidc:TrustEmailVerified`, default
     `false`);
   - the provider says `email_verified` is exactly `true`.

   Otherwise a **pending request** is recorded (valid 24 hours, newest per account and issuer, at most 64 in total)
   and the sign-in is refused with the usual message, which adds that an administrator can confirm it under
   *Settings → Sign-in identities*.
4. The binding is written only after its audit row (`auth.identity_bound`); without that row nothing is bound and the
   sign-in is refused. Every error on the way refuses.

The browser cookie of a federated session carries the account and the binding id. Each request re-checks that the
binding still exists and still belongs to that account (R1-26 session check); hubs, open pages and `/mcp` follow.

## Requirements on the identity provider

Automatic binding trusts the IdP's statement that the address belongs to the person. Set
`Oidc__TrustEmailVerified=true` **only** when all of these hold for your IdP:

- the `email` scope is requested and the IdP sends the `email` claim (Whiskers requests `openid profile email` unless `Oidc:Scopes` overrides it — keep `email` in it);
- users **cannot change their own email address** (in Authentik: the "users may change email" setting is off) and
  cannot self-register with an address they do not control;
- `email_verified` is emitted **honestly** — some IdPs (e.g. some Authentik scope mappings) send `true` for every
  user; if yours does, users must not be able to set their own address.

If you cannot assure this, leave OIDC untrusted: every new OIDC user is then confirmed once by an Admin or the host.
A non-standard `Oidc:EmailClaim` (`preferred_username`, `upn`, …) never binds automatically, because
`email_verified` vouches for `email` only.

## Confirming pending requests

- **In the app:** *Settings → Sign-in identities* (Admin). Pending requests show the account, provider, issuer, a
  subject fingerprint (the first 12 hex digits of SHA-256 over issuer and subject — the raw subject is never shown),
  why it is pending, whether the provider asserted `email_verified`, and when it expires. *Confirm* binds it (replacing
  an older binding of the same account and issuer); *Remove* deletes a binding or a pending request. Both are audited
  gateway operations (`identity.binding_confirm`, `identity.binding_remove`). The last way the last Admin can sign in
  (a local password or a binding) cannot be removed. With `AUTH_DISABLED` the panel's writes are refused.
- **On the host** (e.g. a federated-only Admin locked out after an upgrade), inside the running container:

  ```bash
  docker compose exec serverwatch dotnet Whiskers.dll --confirm-identity-binding <email> [--issuer <issuer>]
  ```

  It binds the newest pending request of that configured address. Exit codes: `0` bound, `2` usage, `3` no pending
  request for that address, `4` several issuers pending (lists issuer, time and fingerprint per line — no address, no
  subject; repeat with `--issuer`), `1` nothing changed (store or audit unavailable, refused). Its audit row
  (actor `cli (--confirm-identity-binding)`) is written first; without it nothing changes. The web host is not
  started. The command writes the same file the running instance uses, under a lock both share
  (`identity-bindings.json.lock`; do not set `DOTNET_SYSTEM_IO_DISABLEFILELOCKING`, which switches that lock off on Linux), and the running instance reads the change at once: the person signs in right
  afterwards, no restart. A binding the command replaced ends that subject's sessions at their next request.

## Removing a binding versus removing access

- **Removing a binding** (*Settings → Sign-in identities → Remove*) ends the sessions of that one IdP account. It does
  **not** remove the person's access: while the address is still configured, the next sign-in binds again —
  automatically through a trusted provider with a verified address, otherwise as a pending request to confirm.
- **Removing access** means the address stops being an entry: remove its role **and** its whitelist listing (deleting
  a local account removes its role). Only then are its bindings and pending requests removed — in the same write; if
  that cannot be saved, the role or whitelist change is refused. While the address is still an entry through the other
  grant (a role, or an enabled whitelist listing), its bindings stay. The start also removes bindings of addresses that
  are no longer entries. So an address given to someone else later never signs in as the old IdP account.

## Backups, damage and recovery

- `identity-bindings.json` lives in the data directory and is part of the self-backup. Restoring a backup from before
  R1-27 restores no bindings: every federated user binds again.
- Every save keeps the previous file as `identity-bindings.json.bak`. Whiskers **never** takes it over by itself: it may
  hold a binding that was revoked since.
- The store is **unavailable** — every federated sign-in and session is refused, local sign-in keeps working, the
  readiness check `identity-bindings` reports `Degraded` — when the file cannot be read or parsed, lacks its `version`,
  `bindings` or `pending`, is a directory, or is missing while `identity-bindings.json.bak` exists; and after a save that
  failed (a full disk), after a start-up prune that did not succeed, and after the file became readable again (repaired
  or restored) — each time until it has been read and pruned again (retried every 30 seconds, at every federated
  sign-in and with every role or whitelist change).
- **Every role or whitelist change that adds or removes an address prunes first** — the setup wizard's first
  administrator included. If that prune cannot be done (the file cannot be read, or the save fails), the change is
  refused; once the store can be read and saved again, the change goes through and takes the owed prune with it.
- **Recovery is the host operator's decision**, with Whiskers running or stopped:
  - restore a known-good file: copy `identity-bindings.json.bak` (check it first — it is the generation before the
    last change) or the file from a backup over `identity-bindings.json`; or
  - start over: delete **both** `identity-bindings.json` and `identity-bindings.json.bak` — every federated user binds
    again (Google automatically, OIDC after confirmation). Deleting only the current file leaves the store unavailable.

  The running instance picks the repaired file up at its next read and becomes available once it has pruned it (within
  30 seconds, or at the next federated sign-in or role/whitelist change): a restored `.bak` may hold bindings of
  addresses removed since, and those go before anyone is admitted.

## Upgrading to R1-27

- Nothing on disk changes; the file appears with the first binding. Roles, whitelist, owners, keys, local accounts and
  automations are untouched; MCP keys are unaffected.
- **Every Google/OIDC browser session ends at its next request** (open pages within 30 seconds). Local sessions continue.
- **Google users** sign in again and are bound (Google is trusted and sends `email_verified=true`).
- **OIDC users (Authentik included)** are pending once: run the host command above for a federated-only Admin, or let
  an Admin confirm in Settings; then sign in again. Or set `Oidc__TrustEmailVerified=true` if your IdP meets the
  requirements above.
- **Changing the OIDC Authority (issuer)** later is a new issuer: every OIDC user is pending again (a federated-only
  Admin needs the host command).
- An instance whose setup is not finished admits no federated sign-in until the wizard has run.
