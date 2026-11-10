# Contract 0011 — Self-report claims (INV-56)

Status: **Accepted** · 10 November 2026 by the maintainer, with the decisions D1–D12 as written and Q1–Q5 decided as
recommended (see *Decisions on the open questions*).

## Purpose

INV-56: "Every self-report (production-readiness check, README, SECURITY.md, UI hints) matches the code's actual
behaviour." Two gaps keep it ⚠️ (`RELEASE_GATE.md`, *Open invariants*):

1. the production-readiness lines are German only, hard-coded in `ProductionReadinessService` (no resx);
2. no test compares what README.md and SECURITY.md say with what the code does.

Maintainer decision, 8 November 2026: **"resx + Aussagen-Register"**. The readiness lines move to resx (English default,
German), like the rest of the UI. A register lists every checkable claim of the governed documents with a test against
code or configuration, and a new claim without a register entry fails a test.

## Current state (verified at `79ed43b2`)

- `ProductionReadinessService.GetChecksAsync` (`Services/Setup/ProductionReadinessService.cs`) builds about 20 line
  kinds. Titles, details and link texts are German literals, except the contract-0006 line (`SelfExposureTitle`,
  English literal). The record `ReadinessCheck(Title, Ok, Detail, LinkHref, LinkText)` has no stable id; tests find
  lines by their German title.
- The only consumer is `ProductionReadinessPanel.razor` (Settings), which already uses `IStringLocalizer<SharedResource>`
  for its own summary. The service is a singleton; it is not exposed over MCP.
- Localization (F2/i18n L1): `Resources/SharedResource.resx` (en, neutral) and `SharedResource.de.resx`, 1992 keys each.
  `UseRequestLocalization` supports `en` and `de`, default `en` (cookie, then `Accept-Language`). Services inject
  `IStringLocalizer<SharedResource>` (`WebhookService`, `UiMutationRunner`). There is no key-parity test across the two
  files; `AuditHashChainTests.Every_finding_has_both_texts` checks one key family.
- Documents quote readiness lines by their German title: SECURITY.md ("mTLS-Client-Schlüssel geschützt"),
  `docs/operations/production-checklist.md` (five titles). After the move these quotes would be wrong in English.
- No test reads README.md or SECURITY.md for claims. `ReleaseGate.cs` reads them only for exception limitations.
- Contract 0008 machinery to reuse: markdown tables parsed by tests, `TestCatalog.Resolve`/`CaseCount`/`RunOf`, and the
  generated `# BEGIN release-gate (generated)` block in `.github/required-tests/*.txt`, checked by
  `check-test-results.sh --require`.

## Decisions (D1–D12, recommendation first)

### Part A — Readiness lines in resx

- **D1 — Stable line id.** `ReadinessCheck` gets a first field `string Id` (kebab-case, e.g. `auth`, `vault`,
  `cloud-dns`, `notify-ai`, `stored-encrypted`, `self-backup`, `pg-tools`, `update-policy`, `stores-readable`,
  `non-root`, `https`, `oidc-https`, `proxy-trusted`, `self-exposure`, `mtls-proxy`, `mtls-shared-live`,
  `mtls-allow-cn`, `mtls-identity`, `mtls-shared-key`, `mtls-keys-protected`). Ids are not translated; tests and the
  register refer to lines by id, never by title. `SelfExposureTitle` is removed. The panel shows `Title` as before.
- **D2 — Keys.** One key per text variant, prefix `Readiness_<Id>_`:
  `…_Title`, `…_Ok`, `…_Fail`, and named variants where a line has more than two outcomes (e.g.
  `Readiness_vault_Wrong`, `Readiness_vault_Missing`, `Readiness_pgTools_TooOld`, `Readiness_pgTools_Unusable`,
  `Readiness_mtlsIdentity_Shared`, `…_Unreadable`, `…_Unfinished`, `…_Operator`, `…_KeysWindows`, `…_KeysUnix`), and
  `…_Link` for link texts. Every key holds whole sentences; the service never concatenates translated sentence
  fragments. It may join whole translated sentences with a space and data lists with `", "`.
- **D3 — Data stays data.** Values inserted through `{0}`, `{1}` … are not translated: counts, file and server names,
  setting names (`Section:Property`), label keys, common names, versions, peer addresses, the `pg_dump` error text.
  Dates keep the invariant `yyyy-MM-dd` / `yyyy-MM-dd HH:mm` UTC format in both languages.
- **D4 — Culture.** The service takes `IStringLocalizer<SharedResource>` (optional constructor parameter; without it,
  a localizer over the real resources, so existing call sites get English, not key names). Texts are resolved at call
  time against `CultureInfo.CurrentUICulture`, i.e. the UI culture of the Blazor circuit that calls it (request
  localization: cookie, `Accept-Language`, default `en`). Nothing localized is cached in the singleton. An unsupported
  culture falls back to English (neutral resx).
- **D5 — Both languages say the same facts.** Mechanical proxies, checked by `ReadinessLocalizationTests`:
  - every `Readiness_*` key exists in both files with a non-empty value, and the German value differs from the English
    one (an untranslated copy fails);
  - the set of placeholders (`{n}`) is equal in both;
  - the set of *fact tokens* is equal in both: digit sequences, file and path names (`*.json`, `ssh-keys/`,
    `docs/…`), setting and variable names (`VAULT_KEY`, `Oidc:RequireHttpsMetadata`, `ForwardedHeaders__…`), CLI
    flags and backticked spans. A number or a name that appears in one language only is a finding.
- **D6 — No hard-coded text left.** Every scenario of the readiness tests also runs with a key-echo localizer: each
  emitted `Title`, `Detail` and `LinkText` must consist only of echoed `Readiness_*` keys, the scenario's data values
  and the separators of D2. Every `Readiness_*` key in the resx must be emitted by at least one scenario, so an unused
  key, or an untested branch, fails. Each scenario also runs under `en` and `de`: same ids, same `Ok` values, the
  same data values present.
- **D7 — Global key parity.** One test checks that `SharedResource.resx` and `SharedResource.de.resx` hold the same
  key set with equal placeholder sets (all 1992 keys, not only `Readiness_*`). This is the "UI hints" part of INV-56
  at the level the code can prove; wording beyond that is out of scope (see *Not in scope*). See Q5.

### Part B — Claim register

- **D8 — Governed documents.** `README.md`, `SECURITY.md`, `docs/security-architecture.md`,
  `docs/operations/production-checklist.md`. Why these: README and SECURITY.md are named by INV-56;
  `security-architecture.md` is where both send the reader for the security and supply-chain claims, and it makes
  nothing but such claims; the production checklist is the written counterpart of the readiness card and quotes its
  lines. Not governed for now: `docs/ARCHITECTURE.md`, `features.md`, `installation.md`, `configuration.md`,
  `container-hardening.md`, `mcp.md`, `governance.md`, ADRs, folder READMEs (long descriptive documents; their
  security statements repeat the governed ones), `docs/v1/*` (already checked by the release gate), `docs/reviews/*`
  (dated history). Adding a file later is one line in `ClaimRules.GovernedFiles` plus its markers. See Q1.
- **D9 — Register: `docs/v1/CLAIMS.md`**, markdown tables parsed by the tests, as in contract 0008.
  - *Claims* table: `| Claim | File | Block | Quote | Check | Tests |`
    - `Claim`: id `RM-nn` (README), `SEC-nn`, `SA-nn`, `PC-nn`; never reused, retired ids stay listed as retired.
    - `Block`: fingerprint of the marked block (D11).
    - `Quote`: the exact words the test proves, a substring of the block after whitespace normalization (`\|` for a
      pipe). One block may hold several claims; each has its own row and quote.
    - `Check`: one of `constant`, `default`, `config`, `artifact` (a repository file: compose files, Helm chart,
      `install.sh`, workflows), `behaviour` (runs the code), `readiness` (runs `ProductionReadinessService`).
    - `Tests`: one or more `` `Class.Method` `` in contract 0008's R4 form.
  - *Claim-free blocks* table: `| File | Block | Reason | Starts with |`. `Reason` is one of the fixed set
    `policy` (a process commitment: response times, supported versions, disclosure), `advice` (an instruction to the
    operator, imperative), `limit` (states what Whiskers does **not** do; pinned by PRODUCT_CONTRACT §5 and its
    invariants), `history` (behaviour of older releases), `pointer` (navigation only).
- **D10 — Markers in the documents.** An HTML comment at the end of the block, invisible when rendered (also inside a
  table cell): `<!-- claim:SEC-07 -->`, several ids comma-separated (`<!-- claim:SEC-07,SEC-08 -->`), or
  `<!-- claim-free:advice -->`. A marker belongs to the block it ends. A block may carry claim ids or a claim-free
  reason, not both.
- **D11 — What counts as a claim (mechanical rule).** The governed files are split into *blocks*: a paragraph, a list
  item (with its continuation lines; nested items are their own blocks), a table body row, a fenced code block.
  Headings, table header rows, HTML comments and lines that hold only links or images are not blocks. A block is
  **claim-shaped** when, outside its markers, it contains a backticked span, a digit, or a word starting with one of
  the stems in `ClaimRules.Triggers`: *always, never, only, every, all, none, no, default, cannot, must, encrypt,
  plaintext, hash, sign, verif, refus, reject, block, delet, remov, pin, protect, owner-only, constant time, secure*.
  Then:
  - every claim-shaped block carries a marker; one without fails, naming the file, line and first words;
  - every marked block's fingerprint (first 12 hex digits of SHA-256 over the block text with markers removed and
    whitespace collapsed) equals the register's `Block` cell. Any edit to a marked block — a new sentence in an old
    paragraph included — fails until the register row is re-confirmed. On a difference the test writes
    `docs/v1/CLAIMS.md.actual` with the recomputed fingerprints, like the gate's manifest snapshot;
  - every id in a marker has exactly one register row and every register row has exactly one marker (no orphans);
  - every `Quote` is found in its block.
  The rule forces a decision on every new or changed statement; whether "advice" or "limit" is the honest label stays
  a review question (the register diff shows it).
- **D12 — What a claim test may check, and the honesty rule.**
  - Allowed: code constants and defaults (by reflection or the symbol itself), configuration defaults
    (`IConfiguration` over the shipped `appsettings*.json`), repository artifacts (compose files, Helm values,
    `install.sh`, workflow YAML, `.github/dependabot.yml`), running a service in process, running the readiness
    service. Not allowed: grepping the document's own words in the code, or a test that only checks that a test exists.
  - **The test must not prove less than the sentence says.** A quantifier (*every, only, never, all, none*) needs an
    exhaustive check — an enumeration by reflection, of a registry such as `VaultBackedSettings.Fields`, or of all
    files of a kind — never a sample. A behaviour sentence may be backed by a constant only when that constant is the
    single source the behaviour uses, and the test names the use site. A sentence about an external system (GitHub,
    Sigstore, an IdP) is proven only as far as the repository configures it, so the sentence must say what Whiskers
    or its pipeline does. If no honest test is possible, the sentence is narrowed or marked claim-free in the same
    pull request — the test is never weakened to fit.
  - Readiness quotes: in a governed document, a quoted string that follows the words "readiness line" (or
    "readiness card … line") must equal the English `Readiness_*_Title` value of a line. This replaces the German
    quotes in SECURITY.md and the production checklist.

## Initial register (30 claims)

| Id | Claim (short) | Check | Proved by (existing class, or new in `SelfReportClaimTests`) |
|---|---|---|---|
| RM-01 | MCP over Streamable HTTP at `/mcp` | behaviour | new: endpoint mapped at `/mcp` |
| RM-02 | `AUTH_DISABLED=true` (`Auth__Disabled`) makes every request an Admin | artifact + behaviour | compose mapping (new) + `AuthDisabledAccessPolicyTests` |
| RM-03 | MCP keys stored as SHA-256 hashes, shown once | behaviour | `McpPermissionServiceTests` |
| RM-04 | `Confirm` runs only the exact approved call | behaviour | approval tests (`ApprovalStore`) |
| RM-05 | Audit checkpoints signed only with a vault key | behaviour | `AuditHashChainTests` |
| RM-06 | Data in `/app/data` (`WHISKERS_DATA_DIR`), SQLite by default | default | new: `DataPathOptions`, `DatabaseRegistration` |
| RM-07 | Vault AES-256-GCM; SSH and mTLS keys file permissions only | constant + readiness | new + `ProductionReadinessServiceTests.The_vault_line_names_every_plaintext_key_kind_and_claims_no_more_than_file_protection` |
| RM-08 | Images amd64 + arm64, Trivy-gated, SBOM + provenance, keyless cosign | artifact | `ReleaseWorkflowTests` |
| RM-09 | Installer pins the release, generates `VAULT_KEY_FILE`, verifies with cosign if present, UI at `127.0.0.1:5100` | artifact | `InstallerContractTests` |
| RM-10 | Unattended admin via `WHISKERS_ADMIN_EMAIL` + `WHISKERS_ADMIN_PASSWORD_FILE` | config | new |
| RM-11 | Config table: each `.env` name maps to the named container variable; `HOST_BIND:HOST_PORT` default `127.0.0.1:5100` | artifact | new (all rows, `docker-compose.yml`) |
| RM-12 | `TrustedNetworks`: loopback only by default | default | `ForwardedHeadersConfigTests` |
| RM-13 | Compose run budget 5 min per command; Git deploy 300 s / 600 s / 300 s | constant | new |
| SEC-01 | Supported line is `0.15.x` | artifact | new: equals `<Version>` major.minor in `Whiskers.csproj` |
| SEC-02 | Helm `replicas: 1`, `Recreate` | artifact | `HelmDeploymentTests` |
| SEC-03 | Role → scope: Viewer → Read, Operator → Write, Admin → Admin | behaviour | new (every role) |
| SEC-04 | An approved call runs once within 10 minutes | constant + behaviour | new (`ApprovalStore.GrantLifetime`) + approval tests |
| SEC-05 | Kill-switch exceptions are exactly those of contract 0009 | behaviour | `KillSwitchTests` + 0009 tests |
| SEC-06 | MCP key: 256 bits CSPRNG, SHA-256, constant-time compare; `api-keys.json` deleted on start | behaviour | `McpPermissionServiceTests` |
| SEC-07 | Vault-held credential list (cloud keys … Git credentials) | readiness + reflection | new: every `VaultBackedSettings.Fields` entry and every named kind |
| SEC-08 | SSH keys and per-host mTLS keys written owner-only; readiness line "mTLS client keys protected" | behaviour + readiness | `PerHostCredentialTests` + quote rule |
| SEC-09 | Default `docker-compose.yml` privileged (host PID); hardened one is not | artifact | new |
| SEC-10 | Wrong/lost `VAULT_KEY`: vault left unchanged, secrets locked | behaviour | `VaultKeyCheckTests` |
| SEC-11 | Restore limits: 100,000 entries, 32 GiB | constant | `BackupArchiverTests` (+ new constant check) |
| SEC-12 | PostgreSQL restore only with `\restrict`, client tools 17.6+ | constant | new (`PostgresDumpTool.CanRestore`) |
| SEC-13 | `AUTH_DISABLED=true` refuses public addresses, non-IP hosts, cross-site origins | behaviour | `AuthDisabledAccessPolicyTests` |
| SEC-14 | Setup token from `/app/data/setup-token.txt` or `WHISKERS_SETUP_TOKEN` | behaviour | `SetupStateServiceTests` |
| SA-01 | Vault AES-256-GCM + PBKDF2 | constant | new (`VaultService`, 600,000 iterations) |
| PC-01 | Installer binds `127.0.0.1` by default (`--bind`, `WHISKERS_BIND`) | artifact | `InstallerContractTests` |
| PC-02 | `…_FILE` variants exist **only** for the six named settings | config (exhaustive) | new: every `*_FILE`/`*File` read in the code equals the list |

The implementation pull request writes the exact `Class.Method` names into `CLAIMS.md`. Where an existing test proves
less than the quote (D12), it names a new test instead, or narrows the sentence.

**Claim-free, deliberately (examples, with reason):** response times, coordinated disclosure, "only the latest release
receives fixes" (`policy`); the hardening checklist's imperatives (`advice`); *Accepted limits*, the threat model's
"root-equivalent", the compose exposure *remaining risks*, "commands in a terminal are not recorded" (`limit`, pinned by
PRODUCT_CONTRACT §5 and INV-62); "images before v0.12.1 are not cosign-signed", the 2026-07 review summary, the
pre-0.15 README pointer (`history`). Not claim-free and not registered yet, so they must be reworded or registered in the
implementation pull request: "management ports are mesh-bound … nothing management-related is exposed to the public
internet by design" (SECURITY.md, `security-architecture.md`) — see Q4.

## Tests

- `ReadinessLocalizationTests` (new): D5 key/placeholder/fact-token parity for `Readiness_*`; D6 key-echo and
  both-culture runs over every scenario; unused key fails; unsupported culture (`fr`) gives English.
- `ResxParityTests` (new): D7, all keys.
- `ProductionReadinessServiceTests` (changed): lines found by `Id`; assertions on wording use the English resx values.
  Method names stay, so the names in SECURITY_INVARIANTS.md keep resolving.
- `ClaimRegisterTests` (new): D11's four checks over the governed files; claim-free reasons from the fixed set; every
  `Tests` cell resolves (R4), is not statically skipped (R7) and names at least one test; the readiness-quote rule;
  `CLAIMS.md.actual` written on a fingerprint difference.
- `ClaimBlockParserTests` (new): paragraphs, nested list items, table rows with a comment in a cell, fenced blocks,
  link-only lines, markers with several ids.
- `SelfReportClaimTests` (new): the claims above marked "new".

## Gate impact

- **Required tests:** the gate (contract 0008, R8) reads `docs/v1/CLAIMS.md` as a further source of named tests: every
  test in a `Tests` cell is rendered into the generated block of its run's manifest with its xUnit case count, as for a
  ✅ invariant. A claim test therefore cannot vanish behind the floor. See Q3.
- **INV-56 → ✅** in `SECURITY_INVARIANTS.md`; evidence cell names this contract, the register and the tests:
  `ProductionReadinessServiceTests`, `JsonFileStoreRecoveryTests`, `ReadinessLocalizationTests`, `ResxParityTests`,
  `ClaimRegisterTests`. New wording: "Every self-report — the production-readiness lines in both UI languages, the
  claims of the governed documents in contract 0011, and quoted readiness lines — matches the code's actual behaviour;
  each registered claim is pinned by a test and an unregistered claim-shaped statement fails CI." §11 counts updated.
- **`RELEASE_GATE.md`:** the INV-56 row leaves *Open invariants*; *What the gate checks* gets one bullet for the
  register.
- **`.github/required-tests/in-process.txt`:** static entries for `ClaimRegisterTests`, `ClaimBlockParserTests`,
  `ReadinessLocalizationTests` and `ResxParityTests` (like the gate's own tests); the generated block is re-rendered.

## Document changes (same pull request)

- New `docs/v1/CLAIMS.md`; markers in the four governed files.
- SECURITY.md and `production-checklist.md`: readiness quotes in English (e.g. "mTLS client keys protected",
  "Hardened profile (non-root)", "HTTPS at the reverse proxy", "Secret vault active (VAULT_KEY)", "Self-backup
  scheduled", "PostgreSQL client tools for the self-backup" — final wording from the resx).
- `docs/v1/WORK_PACKAGES.md` WP7 and `docs/v1/RELEASE_GATE.md` as above; `Services/Setup` folder README names the
  resx keys and the id list; `CONTRIBUTING.md` gets three lines on markers and `CLAIMS.md.actual`.

## Plan triage

- [Claude-prüfbar] D1–D12, all tests, gate and document changes.
- [braucht Nutzer] acceptance of this contract and Q1–Q5 (done, 10 November 2026); a read of the German readiness
  wording in the UI.

## Decisions on the open questions (maintainer, 10 November 2026)

All five as recommended:

- **Q1 — Governed scope: (a).** Only the four files of D8. `container-hardening.md`, `configuration.md` and
  `installation.md` stay ungoverned for now; adding one later is one line in `ClaimRules.GovernedFiles` plus its markers.
- **Q2 — Fingerprints: (a).** Claim-free blocks are fingerprinted too, so a claim added to an "advice" paragraph fails
  until the register row is re-confirmed.
- **Q3 — Required claim tests: (a).** The gate reads `docs/v1/CLAIMS.md` as one more source in R8; INV-56's test column
  does not repeat the claim tests.
- **Q4 — "Mesh-bound by design": (a).** The sentence in SECURITY.md and `security-architecture.md` is reworded to what
  onboarding does (the Docker proxy and `node_exporter` bind to the host's mesh address) and registered against the
  onboarding artifact.
- **Q5 — Global resx parity: (a).** D7 (all keys of both resx files, keys and placeholders) is part of this pull
  request.

## Not in scope

- The meaning of UI hints outside the readiness card (only key and placeholder parity, D7).
- Languages other than English and German.
- Any change to what a readiness line checks; only its text, id and language change.
- Claims in commit messages, release notes and the website.
