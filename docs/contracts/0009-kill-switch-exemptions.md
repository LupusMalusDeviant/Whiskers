# Contract 0009 — Kill-switch exemptions (INV-21)

Status: **Accepted** · 7 November 2026 by the maintainer, with the decisions D1–D6 as written. The acceptance is also the
maintainer's explicit approval for the change to the sign-in flow named in D5. **Amendment 1** (implementation review)
was accepted on 7 November 2026.

## Purpose

The read-only kill switch (`GuardrailPolicy.ReadOnlyMode`) is the emergency brake: while it is on, nothing changes.
INV-21 says so, with one exception, switching it off. The code has more exceptions than that. Some are in accepted
contracts (0002 D4, D8), some are in none (contract 0003's identity bindings). This contract makes the list complete and
fixed, shows it in the UI, and lets an Admin **block** some exceptions in advance. Nobody can **add** one.

Maintainer decision, 7 November 2026: "fixed in code, shown in the UI, configurable only in the stricter direction".

## Current state (verified at `49e167f4`)

- `ReadOnlyModeRule` (`Services/Guardrails/BuiltInGuardrailRules.cs:75`) denies every gateway request whose required
  level is not `read`. It covers MCP, UI, agent, automation, webhooks and AI triggers, because all of them go through
  the mutation gateway.
- These paths still change state while the switch is on:

| # | Path | Where | Through the gateway? | Source |
|---|---|---|---|---|
| E1 | Switching the kill switch off | `guardrails.kill_switch_off` | yes, Admin, UI only | INV-21 |
| E2 | First-run setup writes the first admin | setup wizard | no | R1-26 / F1 |
| E3 | Host CLIs: local-credential CLI, `--confirm-identity-binding` (`IdentityBindingStore.ConfirmFromHostAsync`, `:405`) | container shell | no | 0002 D8, 0003 |
| E4 | Own password change | `account.password_change` (level `read`, `CapabilityCatalog.cs:242`) | yes | 0002 D4 |
| E5 | Recovery-code redemption | `LocalAccountService.RedeemRecoveryCodeAsync` (`:319`) | no | 0002 D8 |
| E6 | Federated identity binding at sign-in | `IdentityBindingStore.BindAtSignInAsync` (`:244`), called from `FederatedAdmission.cs:117` | no | 0003 (kill switch not mentioned) |

## Decisions (D1–D6, recommendation first)

- **D1 — Fixed exemptions, never blockable: E1, E2, E3.**
  - E1 is the way out of the switch.
  - E2 runs only while no admin exists, so nothing is there yet to protect.
  - E3 needs a shell in the container, which is root-equivalent and outside the product contract (PRODUCT_CONTRACT §5.2).
    It is also the last way back in when the blockable exemptions are blocked.
- **D2 — Blockable exemptions, allowed by default: E4, E5, E6.** Each one restores or changes a person's own access.
  Allowing them by default keeps an admin from being locked out at the moment they need to sign in and turn the switch
  off.
- **D3 — The setting.**
  - `GuardrailPolicy.KillSwitchBlockedExemptions`: a list of ids out of `password_change`, `recovery_code` and
    `federated_binding_at_sign_in`.
  - Optional in `guardrails.json`, and left out of the stored and hashed policy while empty, the same way as
    `RequireSecondApprover`. Existing policies keep their version.
  - An unknown id makes the configuration invalid, so the store fails closed (INV-25).
  - It is saved through `guardrails.save` (Admin, audited). That capability is itself refused while the switch is on, so
    the blocks are set **before** an emergency, never during one.
- **D4 — Fail closed.** When the guardrail store is in its fail-closed state, every blockable exemption counts as
  blocked. That state already has the switch on (`GuardrailPolicy.FailClosed()`). The host CLIs (E3) remain the way back
  in.
- **D5 — Where the check is made.**
  - E4 is checked in `ReadOnlyModeRule`. While the switch is on and `password_change` is blocked, `account.password_change`
    is refused with rule `read-only-mode`.
  - E5 and E6 are checked at their entry points, `RedeemRecoveryCodeAsync` and `BindAtSignInAsync`, against
    `IGuardrailStore`. A refusal changes nothing, writes one audit event naming the rule `read-only-mode`, and is shown
    to the user as "blocked while the kill switch is on".
  - This touches the sign-in flow (`FederatedAdmission`) but not the authentication middleware or its order. Accepting
    this contract is the maintainer's explicit approval for that change.
- **D6 — UI.**
  - The Guardrails page gets a section "While the kill switch is on, still allowed", listing E1–E6 with a one-line reason.
  - E4–E6 each get a toggle ("also block while the kill switch is on"). E1–E3 are shown as fixed.
  - Both the English and German resx entries are added.

## INV-21, new wording (replaces the current row)

> While the read-only kill switch is on, every mutation on every path is denied and no new terminal session opens.
> The only exceptions are the ones in contract 0009. These are always allowed: switching the switch off (Admin, audited),
> the first-run setup while no admin exists, and the host CLIs. These are allowed unless an Admin blocked them
> beforehand: own password change, recovery-code redemption, and federated identity binding at sign-in. Each exception,
> and each block, is pinned by a test.

## Tests (pinning)

- One test per exemption showing it works while the switch is on (E1–E6).
- One test per blockable exemption showing that, when blocked, it is refused with no side effect and one audit event
  naming `read-only-mode` (E4–E6).
- With the store fail-closed, E4–E6 are refused and E3 still works.
- An unknown exemption id fails the store closed.
- Saving the setting is refused while the switch is on.
- The policy hash is unchanged while the list is empty.

## Amendment 1 (implementation review, 7 November 2026) — Accepted by the maintainer on 7 November 2026

**A1.1 Records are not mutations.** INV-21's "every mutation" does not cover writes that only record or remove access,
and these keep running while the switch is on:
- the gateway's own records: audit events, decision records, the operation journal;
- sign-in bookkeeping: a pending identity-binding request (`IdentityBindingStore.RecordPendingAsync`), the last sign-in
  day (`RecordSignInAsync`), lockout counters and the authentication audit. A pending request grants nothing. It takes
  effect only through E3 (`--confirm-identity-binding`) or a gateway confirmation, and that confirmation is refused
  while the switch is on;
- start-up housekeeping that grants nothing or only removes access: the orphan prune (`PruneOrphansAsync`), the start-up
  backup and the credential migrations listed in `MutationGatewayCoverageTests.WriteNamedButNotGated`.

Tests pin the first two groups: a pending request recorded under the switch grants no access until it is confirmed.
INV-21's evidence cell gets one sentence naming A1.1.

**A1.2 The way back when E5 is blocked.** While the guardrail store is fail-closed, or `recovery_code` is blocked under
the switch, a recovery code cannot be redeemed (D2, D4). `--recover-local-account` would clear the password and leave a
code that cannot be used. So:
- the recovery CLI refuses to run in that state unless `--force` is given, and says why;
- the documentation and the UI name the real way back: repair or remove `guardrails.json` on the host, or turn off
  `ReadOnlyMode` or the block in it, then restart. The binding CLI only helps for an existing pending request.

E3 stays unblockable (D1). The CLI asks for an explicit `--force`; it does not refuse outright.

## Not in scope

- Exemptions an Admin could add. That is excluded by the maintainer's decision.
- Any change to the authentication middleware, the OAuth whitelist, the mTLS layer or the socket-proxy verbs.
