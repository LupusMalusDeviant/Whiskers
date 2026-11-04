// R1-47: reads and writes identifiable records in a stopped Whiskers data directory, for the upgrade and recovery check
// of artifact-checks.sh. It runs in a pinned Node image (node:sqlite, node:crypto) against the data volume mounted at
// /data — independently of the application, so the check does not take the app's word for what is stored.
//
//   node artifact-data.mjs seed-vault-legacy   a vault entry in the legacy AES-CBC format; the release that starts next
//                                              must convert it to AES-GCM itself
//   node artifact-data.mjs seed                a second local account (a copy of the Administrator's password hash), its
//                                              role in roles.json, and a metrics row
//   node artifact-data.mjs state               the records as "name=value" lines: migration histories, both accounts
//                                              and the decrypted vault entry as SHA-256 fingerprints, the role, the
//                                              metrics row, the audit trail's last id
//   node artifact-data.mjs mutate              changes every record in a way the state shows (account deleted, role,
//                                              metrics value, vault value re-encrypted under the same key)
//   node artifact-data.mjs audit-since <id>    "id|timestamp|action" of every audit row after <id>
//   node artifact-data.mjs audit-present       how many of the lines in AUDIT_LINES are still in the audit trail
//
// Environment: VAULT_KEY (the vault passphrase of the run), MARKER_SEEDED and MARKER_CHANGED (random values the check
// generates: what the vault entry holds before and after the change), ADMIN_EMAIL, AUDIT_LINES (audit-present). The
// release's own files are written atomically (a temporary file, then a rename). Every failure exits non-zero with a
// message: a record that is not where it must be fails the check.
import { DatabaseSync } from "node:sqlite";
import { createCipheriv, createDecipheriv, createHash, pbkdf2Sync, randomBytes, randomUUID } from "node:crypto";
import { readFileSync, writeFileSync, renameSync } from "node:fs";

const DATA = process.env.DATA_DIR ?? "/data";
const ADMIN = process.env.ADMIN_EMAIL ?? "admin@artifact-checks.test";
const KEEPER = "keeper@artifact-checks.test";
const METRIC_SERVER = "artifact-checks-marker";
const VAULT_ENTRY = "artifact-checks-entry";
const SEEDED = { cpu: 42.5, role: 0 };   // Viewer
const CHANGED = { cpu: 7.25, role: 1 };  // Operator

function die(message) {
  console.error(`artifact-data: ${message}`);
  process.exit(1);
}

function env(name) {
  const value = process.env[name];
  if (!value) die(`${name} is not set`);
  return value;
}

const db = () => new DatabaseSync(`${DATA}/metrics.db`);
const readJson = (name) => JSON.parse(readFileSync(`${DATA}/${name}`, "utf8"));
function writeJson(name, value) {
  const tmp = `${DATA}/.${name}.artifact-checks`;
  writeFileSync(tmp, JSON.stringify(value, null, 2));
  renameSync(tmp, `${DATA}/${name}`);
}
// Property names as the release wrote them (PascalCase today; matched without regard to case).
const pick = (obj, name) => Object.keys(obj).find((k) => k.toLowerCase() === name.toLowerCase());

function vault() {
  const v = readJson("vault.json");
  const entries = v[pick(v, "Entries")] ?? die("vault.json has no Entries");
  return { v, entries };
}

// The vault's key: PBKDF2-SHA256, 600 000 iterations, the persisted salt (ADR-0001); the legacy key is SHA-256 of the
// passphrase.
function gcmKey(v) {
  const salt = v[pick(v, "KdfSalt")];
  if (!salt) die("vault.json has no KdfSalt");
  return pbkdf2Sync(Buffer.from(env("VAULT_KEY"), "utf8"), Buffer.from(salt, "base64"), 600000, 32, "sha256");
}

// "g1:" + base64(nonce 12 ‖ tag 16 ‖ ciphertext).
function openGcm(key, stored) {
  const blob = Buffer.from(stored.slice(3), "base64");
  const decipher = createDecipheriv("aes-256-gcm", key, blob.subarray(0, 12));
  decipher.setAuthTag(blob.subarray(12, 28));
  return Buffer.concat([decipher.update(blob.subarray(28)), decipher.final()]).toString("utf8");
}
function sealGcm(key, plain) {
  const nonce = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", key, nonce);
  const body = Buffer.concat([cipher.update(plain, "utf8"), cipher.final()]);
  return "g1:" + Buffer.concat([nonce, cipher.getAuthTag(), body]).toString("base64");
}

// Sensitive values (password hash, security stamp, the decrypted vault value) never leave this tool: the state carries
// their SHA-256 only, which is enough to tell equal from different and is what the check compares and could ever print.
const fingerprint = (value) => "sha256:" + createHash("sha256").update(value, "utf8").digest("hex");

const entryOf = (entries) => entries.find((e) => e[pick(e, "Key")] === VAULT_ENTRY);

function seedVaultLegacy() {
  const { v, entries } = vault();
  if (entryOf(entries)) die(`vault.json already has ${VAULT_ENTRY}`);
  const iv = randomBytes(16);
  const cipher = createCipheriv("aes-256-cbc", createHash("sha256").update(env("VAULT_KEY"), "utf8").digest(), iv);
  const stored = Buffer.concat([iv, cipher.update(env("MARKER_SEEDED"), "utf8"), cipher.final()]).toString("base64");
  entries.push({ Key: VAULT_ENTRY, EncryptedValue: stored, CreatedAt: new Date().toISOString() });
  writeJson("vault.json", v);
}

const accounts = (d) =>
  d.prepare("SELECT * FROM AspNetUsers WHERE NormalizedEmail IN (?, ?)").all(ADMIN.toUpperCase(), KEEPER.toUpperCase());

function seed() {
  const d = db();
  const admin = accounts(d).find((u) => u.NormalizedEmail === ADMIN.toUpperCase()) ?? die(`no account ${ADMIN} to copy`);
  const keeper = { ...admin, Id: randomUUID(), UserName: KEEPER, NormalizedUserName: KEEPER.toUpperCase(), Email: KEEPER,
    NormalizedEmail: KEEPER.toUpperCase(), SecurityStamp: randomUUID().replaceAll("-", "").toUpperCase(),
    ConcurrencyStamp: randomUUID(), AccessFailedCount: 0, LockoutEnd: null };
  const columns = Object.keys(keeper);
  d.prepare(`INSERT INTO AspNetUsers (${columns.map((c) => `"${c}"`).join(", ")}) VALUES (${columns.map(() => "?").join(", ")})`)
    .run(...columns.map((c) => keeper[c]));

  const now = new Date().toISOString().replace("T", " ").replace("Z", "");
  d.prepare(`INSERT INTO ServerMetrics (ServerId, ServerName, Timestamp, CpuPercent, MemoryUsedBytes, MemoryTotalBytes,
      DiskUsedBytes, DiskTotalBytes) VALUES (?, ?, ?, ?, 1, 2, 3, 4)`).run(METRIC_SERVER, METRIC_SERVER, now, SEEDED.cpu);
  d.close();

  const roles = readJson("roles.json");
  const list = roles[pick(roles, "Roles")] ?? die("roles.json has no Roles");
  list.push({ Email: KEEPER, Role: SEEDED.role });
  writeJson("roles.json", roles);
}

const keeperRole = (roles) =>
  (roles[pick(roles, "Roles")] ?? []).find((r) => String(r[pick(r, "Email")]).toLowerCase() === KEEPER);

function state() {
  const d = db();
  const lines = [];
  for (const table of ["__EFMigrationsHistory", "__IdentityMigrationsHistory"]) {
    const ids = d.prepare(`SELECT MigrationId FROM "${table}" ORDER BY MigrationId`).all().map((r) => r.MigrationId);
    if (ids.length === 0) die(`${table} is empty`);
    lines.push(`history.${table}=${ids.join(",")}`);
  }
  const found = accounts(d);
  for (const [name, email] of [["admin", ADMIN], ["keeper", KEEPER]]) {
    const u = found.find((x) => x.NormalizedEmail === email.toUpperCase());
    lines.push(`account.${name}=${u ? fingerprint(`${u.Id}|${u.PasswordHash}|${u.SecurityStamp}`) : "absent"}`);
  }
  const metric = d.prepare("SELECT CpuPercent FROM ServerMetrics WHERE ServerId = ?").all(METRIC_SERVER);
  lines.push(`metric=${metric.map((m) => m.CpuPercent).join(",") || "absent"}`);
  lines.push(`audit.last=${d.prepare("SELECT COALESCE(MAX(Id), 0) AS m FROM AuditLog").get().m}`);
  d.close();

  const role = keeperRole(readJson("roles.json"));
  lines.push(`role.keeper=${role ? role[pick(role, "Role")] : "absent"}`);

  const { v, entries } = vault();
  const entry = entryOf(entries);
  if (!entry) lines.push("vault.format=absent", "vault.value=absent");
  else {
    const stored = entry[pick(entry, "EncryptedValue")];
    if (!stored.startsWith("g1:")) lines.push("vault.format=legacy", "vault.value=(not converted)");
    else {
      let value;
      try { value = openGcm(gcmKey(v), stored); } catch (e) { die(`the vault entry does not open with VAULT_KEY: ${e.message}`); }
      lines.push("vault.format=gcm", `vault.value=${fingerprint(value)}`);
    }
  }
  console.log(lines.join("\n"));
}

function mutate() {
  const d = db();
  if (d.prepare("DELETE FROM AspNetUsers WHERE NormalizedEmail = ?").run(KEEPER.toUpperCase()).changes !== 1) die("no account to delete");
  if (d.prepare("UPDATE ServerMetrics SET CpuPercent = ? WHERE ServerId = ?").run(CHANGED.cpu, METRIC_SERVER).changes !== 1) die("no metrics row");
  d.close();
  const roles = readJson("roles.json");
  const role = keeperRole(roles) ?? die("no role to change");
  role[pick(role, "Role")] = CHANGED.role;
  writeJson("roles.json", roles);
  const { v, entries } = vault();
  const entry = entryOf(entries) ?? die("no vault entry to change");
  entry[pick(entry, "EncryptedValue")] = sealGcm(gcmKey(v), env("MARKER_CHANGED"));
  writeJson("vault.json", v);
}

function auditSince(id) {
  const d = db();
  const rows = d.prepare("SELECT Id, Timestamp, Action FROM AuditLog WHERE Id > ? ORDER BY Id").all(Number(id));
  console.log(rows.map((r) => `${r.Id}|${r.Timestamp}|${r.Action}`).join("\n"));
}

function auditPresent() {
  const wanted = (process.env.AUDIT_LINES ?? "").split("\n").filter(Boolean);
  const query = db().prepare("SELECT Timestamp, Action FROM AuditLog WHERE Id = ?");
  console.log(wanted.filter((line) => {
    const [id, timestamp, action] = line.split("|");
    const row = query.get(Number(id));
    return row !== undefined && row.Timestamp === timestamp && row.Action === action;
  }).length);
}

const [command, arg] = process.argv.slice(2);
switch (command) {
  case "seed-vault-legacy": seedVaultLegacy(); break;
  case "seed": seed(); break;
  case "state": state(); break;
  case "mutate": mutate(); break;
  case "audit-since": auditSince(arg ?? die("audit-since <id>")); break;
  case "audit-present": auditPresent(); break;
  default: die("usage: seed-vault-legacy | seed | state | mutate | audit-since <id> | audit-present");
}
