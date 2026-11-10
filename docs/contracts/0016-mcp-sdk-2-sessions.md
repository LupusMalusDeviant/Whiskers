# Contract 0016 — MCP sessions on SDK 2.x (API-key subject claim)

Status: **Accepted** · 10 November 2026 by the maintainer (approval of the `sub` claim for the API-key principal).
Scope of the approval: **only** the subject claim on the API-key principal described below. Middleware order, the
OAuth whitelist, roles, RBAC and every other part of authentication stay as they are. No synthetic admin principal.

## Purpose

Dependabot PR #165 raises `ModelContextProtocol.AspNetCore` from 1.4.1 to 2.4.0. Two SDK changes meet Whiskers:

1. **Stateless is the new default** (`HttpServerTransportOptions.SessionMode = Stateless`, SEP-2567). No
   `Mcp-Session-Id` is minted, and GET/DELETE `/mcp` disappear. Whiskers' MCP surface and its wire tests rely on the
   session handshake (`initialize` → `Mcp-Session-Id` → later calls), so 17 wire tests fail.
2. **Stateful sessions are bound to a user.** An authenticated principal must carry `ClaimTypes.NameIdentifier`,
   `sub` or `ClaimTypes.Upn`, or the SDK answers `403 Forbidden` ("Authenticated users need a … claim to use stateful
   sessions"). Every later request on the session must come from the same user id, or the SDK answers `403`. The
   API-key principal (`McpBearerAuthMiddleware`, auth type `McpBearer`) carries only `mcp-api-key=true`.

## Current state (verified at the merge of `origin/main` `9a12f7a3` into `deps/mcp-2.4`)

- `WhiskersHostingExtensions.AddWhiskersModules`: `.WithHttpTransport()` without options → Stateless on 2.4.
- `McpBearerAuthMiddleware`: for `/mcp` with a valid `Bearer` key, sets `context.User` to a `ClaimsIdentity` with the
  single claim `mcp-api-key=true`. An invalid key leaves the user untouched.
- `MapMcp("/mcp").RequireAuthorization(...)`: passes a valid bearer key or an authenticated web user.
- Web principals: local password sign-in carries `NameIdentifier = user.Id`; `Auth:Disabled` carries
  `NameIdentifier = local-user`; the federated (Google/OIDC) cookie principal carries Email, Name and the binding id,
  **no** `NameIdentifier`.

## Changes

### C1 — Session mode

`.WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateful)` — the behaviour of SDK 1.x: every client gets
an `Mcp-Session-Id` from `initialize`; GET and DELETE `/mcp` stay. Clients on the `2026-07-28` revision get
`-32022 UnsupportedProtocolVersion` and downgrade to the `initialize` handshake (SDK behaviour).
`StatefulForInitializeClients` (stateless service for new-revision clients on the same endpoint) is **not** chosen: it
opens a second, session-less request path that has no wire tests yet. That is a later, separate decision.

### C2 — Subject claim on the API-key principal

| Item | Value |
|---|---|
| Claim type | `ClaimTypes.NameIdentifier` |
| Value | `mcp-key:<McpApiKeyConfig.Id>` — the key's stable record id (8 hex characters from a random GUID, created with the key, shown in the UI). The prefix keeps it apart from local user ids. |
| Issuer | `Whiskers.McpBearer` |
| Never | the plaintext key, any part of it, its hash or its hint (`KeyHint`). The id is not derived from the secret. |
| Set by | `McpBearerAuthMiddleware`, in the same place and order as today, next to `mcp-api-key=true`. |
| Read by | the MCP SDK only (session binding). Nothing in Whiskers reads `NameIdentifier` of an `McpBearer` principal: `SessionValidator` reads it only for local-password principals, audit identifies keys through `mcp-api-key` + `ValidateKey`. |

### C3 — Session binding

- A session belongs to the key that sent `initialize`. A request on that session with **another** valid key gets
  `403 Forbidden` from the SDK; the session stays with its owner.
- A request on the session with a web principal (other user id) is refused the same way.
- Revoking or disabling a key: its next request fails `ValidateKey` → no principal, the authorization policy refuses it
  (unchanged). The session expires through the SDK idle timeout (2 h).

### C4 — Web principals (cookie / OIDC)

Unchanged. Local-password and `Auth:Disabled` principals already carry `NameIdentifier` and get sessions bound to it.
The **federated cookie principal has no `NameIdentifier`**: on `/mcp` the SDK refuses it with `403` (fail-closed). It
is not given a claim here, because the approval covers only the API-key principal. Federated users reach MCP through an
API key, as documented. If cookie access to `/mcp` for federated users is wanted, that is a separate change to the
federated principal and needs its own approval.

### C5 — Fail-closed

A valid key whose record id is empty or whitespace, or whose id is not unique among the enabled keys (only possible by
editing the permission file by hand), cannot be given a subject that is its own. The middleware then answers
`401 Unauthorized` and does not call the rest of the pipeline. It does not fall back to an unbound or shared subject,
and it logs the record id state without any key material.

## Tests

- All existing MCP wire tests green on 2.4 (they use the session handshake).
- New: a session opened by key A is refused (`403`) when key B calls on it, and key A can still use it afterwards.
- New: the subject claim of the API-key principal is `mcp-key:<id>` and contains neither the plaintext key, nor its
  hash, nor its hint.
- New: a key with an empty or duplicated record id is refused with `401`.
- `EndpointAuthMatrixTests` keeps GET/DELETE `/mcp/` classified (they exist again with C1).

## Documentation

`src/Whiskers/Services/Auth/README.md`, `src/Whiskers/Mcp/README.md`, `CHANGELOG.md` (Unreleased).
