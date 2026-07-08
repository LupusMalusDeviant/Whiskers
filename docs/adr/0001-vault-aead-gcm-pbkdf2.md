# 0001 — Vault: authentifizierte Verschlüsselung (AES-256-GCM) mit PBKDF2-Schlüsselableitung

- **Status:** Akzeptiert (2026-07-07)
- **Betrifft:** `src/Whiskers/Services/Vault/VaultService.cs`, `src/Whiskers/Models/VaultEntry.cs`
- **Bezug:** Full-Repo-Review 2026-07-06, Finding HOCH-4; Sicherheits-Leitplanke „keine selbstgebaute/unauth. Krypto" (CLAUDE.md)

## Kontext

Der `VaultService` speichert Secrets (Container-Secrets, API-Tokens) verschlüsselt in `/app/data/vault.json`. Die bisherige Implementierung nutzte:

- **AES-256-CBC ohne MAC** — unauthentifiziert. Ciphertext ist manipulierbar (Bit-Flipping), Padding-Fehler wurden vom Catch-all in `GetSecret` verschluckt. Wer die Datei schreiben kann (anderer Prozess/Container mit Zugriff auf das Data-Volume, Backup-Restore-Pfad, `execute_command`), konnte Secrets unbemerkt verändern.
- **Master-Key = einzelner unsalted `SHA256(VAULT_KEY)`** — keine echte Schlüsselableitung. Ein geleaktes `vault.json` (Backup, Volume-Snapshot) ist offline mit GPU-Tempo gegen menschlich gewählte Passphrasen brute-forcebar.

Das verletzt die Projekt-Leitplanke aus dem Audit 2026-07-04 („kein AES-CBC ohne MAC, kein Key neben dem Ciphertext — AES-GCM oder Data Protection API").

## Anforderungen

- Authentifizierte Verschlüsselung (Manipulation erkennbar).
- Vernünftige Schlüsselableitung aus der Passphrase (Salt + Arbeitsfaktor).
- **Kein Datenverlust**: bestehende, mit dem alten Verfahren verschlüsselte Secrets müssen weiter lesbar bleiben und automatisch migriert werden.
- Keine neue externe Abhängigkeit; Bordmittel von .NET.

## Optionen

1. **AES-256-GCM + PBKDF2 (Bordmittel).** `AesGcm` liefert Auth-Tag; `Rfc2898DeriveBytes.Pbkdf2` leitet den Key ab. Salt in `VaultData` persistiert.
2. **ASP.NET Core Data Protection API.** Schlüssel-Ringverwaltung durch das Framework. Bindet die Vault-Daten aber an den DP-Keyring (zusätzlicher persistenter Zustand, komplexere Wiederherstellung bei Volume-Restore) und entkoppelt vom bewusst gewählten `VAULT_KEY`-Passphrase-Modell.
3. **libsodium/Drittanbieter-Krypto.** Zusätzliche native Abhängigkeit ohne echten Mehrwert gegenüber `AesGcm`.

## Entscheidung

**Option 1.** Secrets werden als `g1:` + Base64(`nonce(12) ‖ tag(16) ‖ ciphertext`) mit **AES-256-GCM** gespeichert. Der Master-Key wird per **PBKDF2-HMAC-SHA256, 600.000 Iterationen** aus `VAULT_KEY` über einen zufälligen, in `VaultData.KdfSalt` persistierten 16-Byte-Salt abgeleitet.

**Migration:** In `InitializeAsync` wird der Salt bei Erstnutzung erzeugt und gespeichert. Danach werden alle Einträge ohne `g1:`-Präfix mit dem alten CBC-Pfad (`SHA256(VAULT_KEY)`) entschlüsselt und mit GCM neu verschlüsselt; die Datei wird einmalig neu geschrieben. Der alte Ableitungs-/Entschlüsselungspfad bleibt ausschließlich als Migrations-Fallback im Code (`DecryptLegacyCbc`, `_legacyKey`).

## Konsequenzen

**Positiv**
- Manipulation an `vault.json` führt zu `CryptographicException` statt zu attacker-beeinflusstem Klartext; `GetSecret` loggt den Fehler jetzt statt ihn stumm zu schlucken.
- Offline-Brute-Force wird durch PBKDF2 (600k Iterationen) drastisch verteuert.
- Migration ist transparent, einmalig, ohne manuellen Schritt.

**Negativ / Risiken**
- `VAULT_KEY` bleibt der Single Point of Failure; PBKDF2 schützt nur die Ableitung, nicht ein geleaktes Klartext-`VAULT_KEY`.
- Der Legacy-CBC-Pfad muss im Code verbleiben, solange nicht-migrierte Vaults existieren könnten. Entfernen erst nach gesicherter Migration aller Instanzen (Folge-ADR).
- GCM-Format `g1:` ist versioniert; ein künftiger Wechsel braucht ein neues Präfix + Migration.
- Bei falschem `VAULT_KEY` nach Container-Neuerstellung schlägt die Migration/Entschlüsselung fehl und wird geloggt (vorher: stilles `null`).
