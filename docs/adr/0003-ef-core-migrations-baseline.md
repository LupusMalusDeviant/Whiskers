# 0003 — EF-Core-Migrations mit Baseline für Bestands-Datenbanken

- **Status:** Akzeptiert (2026-07-07)
- **Betrifft:** `src/ServerWatch/Program.cs`, `src/ServerWatch/Services/Persistence/DatabaseInitializer.cs`, `src/ServerWatch/Services/Persistence/MetricsDbContextFactory.cs`, `src/ServerWatch/Migrations/`
- **Bezug:** Full-Repo-Review 2026-07-06, Findings MIT-27 (fehlende `AlertHistory`-DDL) und MIT-29 (keine Migrations-Story)

## Kontext

Das Schema der SQLite-Metrik-DB (`MetricsDbContext`) wurde bisher auf zwei divergierenden Wegen erzeugt:

1. `EnsureCreatedAsync()` — legt bei einer **frischen** DB alle Tabellen aus dem EF-Modell an, tut aber auf einer **bestehenden** DB nichts (bekannte EnsureCreated-Grenze: es fügt keine neuen Tabellen/Spalten nach).
2. Ein ~220-Zeilen-Block handgeschriebener `CREATE TABLE IF NOT EXISTS`-DDL in `Program.cs` — der De-facto-„Migrations"-Ersatz, um auf Bestands-DBs beim Upgrade fehlende Tabellen nachzuziehen.

Diese Duplizierung driftet: der DDL-Block hatte `AlertHistory` nie enthalten (MIT-27). Auf DBs, die älter als die `AlertHistory`-Entität sind, warf die 30-Sekunden-Prune-Schleife deshalb `no such table: AlertHistory` — und da Audit-/MCP-Pruning im selben try-Block läuft, lief es ebenfalls nie (unbegrenztes Audit-Wachstum). MIT-29 benennt die Wurzelursache: es gibt keine echte Migrations-Story.

## Entscheidung

**EF Core Migrations vollständig übernehmen** (`MigrateAsync` statt `EnsureCreated` + Hand-DDL). Neue Schemaänderungen entstehen künftig ausschließlich per `dotnet ef migrations add`. Ein `MetricsDbContextFactory` (`IDesignTimeDbContextFactory`) hält die Tooling-Zeit vom App-Host fern.

Die Herausforderung ist der Übergang von Bestands-DBs (Badwolf), die per `EnsureCreated` entstanden sind und **kein `__EFMigrationsHistory`** besitzen. Ein naives `MigrateAsync` würde dort `InitialCreate` gegen bereits existierende Tabellen laufen lassen → `table already exists` → Crash-Loop bei jedem Start.

Deshalb baselined `DatabaseInitializer.InitializeAsync` solche DBs, statt sie neu zu migrieren:

1. **Erkennen:** keine Migrations-History vorhanden, aber eine Sentinel-Tabelle (`ContainerMetrics`) existiert → Legacy-`EnsureCreated`-DB.
2. **Heilen:** den (jetzt vollständigen, inkl. `AlertHistory`) `CREATE TABLE IF NOT EXISTS`-Block einmalig ausführen, damit das On-Disk-Schema `InitialCreate` entspricht.
3. **Stempeln:** `InitialCreate` in `__EFMigrationsHistory` als angewandt eintragen — **ohne** die `CREATE TABLE`s auszuführen.
4. **Migrieren:** `MigrateAsync` wendet nur noch Migrationen *nach* `InitialCreate` an (heute keine). Frische DBs bekommen alles, aktuelle DBs sind ein No-Op.

Die Migration-Id wird über `db.Database.GetMigrations().First()` gelesen, nicht hartkodiert.

## Datensicherheit

Die Routine ist **per Konstruktion nicht-destruktiv**: sie führt ausschließlich `CREATE TABLE IF NOT EXISTS`, genau ein History-`INSERT` und `MigrateAsync` (für eine korrekt gebaselinete DB nichts Ausstehendes) aus. Kein `DROP`, kein datenverändernder `ALTER`. Belegt durch `DbMigrationBaselineTests`: eine mit Zeilen befüllte Legacy-DB behält nach dem Baseline alle Zeilen, `AlertHistory` existiert und ist prunebar, und ein zweiter Lauf ist idempotent.

**Deploy-Schritt (manuell, Gürtel-und-Hosenträger):** vor dem ersten migrations-fähigen Deploy eine Kopie von `data/metrics.db` ziehen. Nicht als Korrektheitsvoraussetzung, sondern als Rückfallebene.

## Konsequenzen

- Der Schema-Drift ist beseitigt: eine neue Tabelle/Spalte wird nicht mehr zum Runtime-Fehler auf Bestandsinstallationen, weil sie über eine Migration fließt, die `MigrateAsync` beim Start anwendet.
- Der Heal-Pfad (Hand-DDL) bleibt als **einmaliger** Legacy-Reconciler erhalten; migrations-verwaltete DBs berühren ihn nie. Sobald alle Instanzen gebaselinet sind, ist er toter Code und kann in einer späteren Aufräum-Migration entfernt werden.
- Das Baseline setzt voraus, dass Legacy-DBs auf `InitialCreate`-Niveau sind (heute zutreffend — die Hand-DDL entspricht `InitialCreate`). Künftige Migrationen kommen additiv obendrauf.
- `Microsoft.EntityFrameworkCore.Design` ist als Design-Time-Abhängigkeit hinzugekommen (treibt `dotnet ef` und die Factory).
