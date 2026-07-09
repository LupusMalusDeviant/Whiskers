# ADR-0004: PostgreSQL-Provider-Support und Multi-Provider-Migrations-Architektur

- **Status:** Vorgeschlagen (2026-07-09)
- **Datum:** 2026-07-09
- **Autor:** LupusMalusDeviant
- **Konsultiert:** —

## Kontext und Problemstellung

Whiskers soll wahlweise mit **PostgreSQL** laufen (Produktions-/KMU-Betrieb, Kubernetes, Vorbereitung auf Multi-Replica); **SQLite bleibt der Zero-Config-Default** für Single-Host-Installationen. Es gibt genau **einen** `MetricsDbContext` mit 15 Tabellen. Der konfigurierbare Provider-Switch (`Database:Provider`, `WHISKERS_DB_PROVIDER`/`_CONNECTION[_FILE]`) und die DateTime-UTC-Härtung sind bereits umgesetzt; offen sind die **Migrationen**.

Beide Provider brauchen je eigene Migrationen: Die bestehende `InitialCreate` ist SQLite-typgebunden — Spaltentypen sind hart als `TEXT` (auch für `DateTime`) bzw. `INTEGER` mit der Annotation `Sqlite:Autoincrement` für die `long`-Primärschlüssel kodiert. Auf PostgreSQL angewandt ergäbe das falsche Typen (Datum als `text` statt `timestamp with time zone`, PK ohne Identity). Eine einzige Migration kann also nicht beide Provider bedienen.

Beim Umsetzen des ursprünglich in `stableDB.md` skizzierten Wegs („zwei Migrations-Ordner im selben Projekt") sind zwei EF-Core-Randbedingungen aufgeschlagen, die diesen Weg unbrauchbar machen: (1) EF Core erlaubt nur **einen `ModelSnapshot` pro DbContext pro Assembly**; (2) ein separates Migrations-Projekt, das den `MetricsDbContext` referenziert, erzeugt einen **Zirkelbezug** mit dem App-Projekt, das die Migrationen zur Laufzeit laden muss. Dieses ADR hält die dadurch nötige, größere Architektur fest. Es **ergänzt** [ADR-0003](./0003-ef-core-migrations-baseline.md) (Migrations-Baseline) und ersetzt es nicht — die dort etablierte `InitialCreate`-Baseline bleibt unangetastet.

**Kernfrage:** Wie unterstützt Whiskers PostgreSQL als zweiten EF-Core-Provider neben SQLite, ohne einen zweiten DbContext einzuführen und ohne die bestehende `InitialCreate`-Migrations-Baseline (ADR-0003) zu brechen?

## Anforderungen

### Funktional

- Ein Boot mit `Database:Provider=postgres` migriert und betreibt alle 15 Tabellen (Metriken, Notifications, Audit, Scheduler, CVE-Age …).
- Der Default-Start ohne jede Konfiguration bleibt SQLite und verhält sich byte-identisch zu heute (WAL, Legacy-Baseline-Heal).
- Ein expliziter, einmaliger Datenumzug SQLite → PostgreSQL ist möglich (kein stiller Umzug beim Boot).

### Nicht-Funktional

- **Baseline-Sicherheit:** Die bestehende `InitialCreate` (Klassen-/Migrationsname) darf sich nicht ändern (ADR-0003) — sonst re-migrieren bestehende SQLite-Installationen.
- **EF-idiomatisch:** kein zweiter DbContext (Model-Doppelpflege), kein `EnsureCreated`, kein rohes Schema-SQL.
- **Geringer Konsumenten-Churn:** die ~20 Nutzer von `MetricsDbContext`/Entities sollen möglichst nicht angefasst werden müssen.
- **K8s-tauglich:** Grundlage für Multi-Replica (SQLite-Filelock verträgt keine zwei Pods).

## Betrachtete Optionen

### Option 0: Zwei Migrations-Ordner im selben Projekt

Der ursprüngliche `stableDB.md`-Vorschlag: `Migrations/Sqlite/` und `Migrations/Postgres/` im Hauptprojekt, je eigener Namespace, Auswahl über Design-Time-Factories.

**Positiv:**
- Kein neues Projekt, kleinster struktureller Eingriff.

**Negativ:**
- Funktioniert nicht: EF Core findet über das `[DbContext]`-Attribut mehrere `ModelSnapshot`-Klassen für denselben Context in einer Assembly und bricht ab („more than one snapshot found").

### Option 1: Provider-bedingte Migrationen in einer Datei

Eine Migrationsmenge mit `migrationBuilder.IsSqlite()`/`IsNpgsql()`-Weichen für die abweichende DDL.

**Positiv:**
- Ein Projekt, ein Snapshot, keine neue Projektstruktur.

**Negativ:**
- Ein einzelner Snapshot kann nicht gleichzeitig die SQLite-Form (`TEXT`/`Autoincrement`) und die PG-Form (`bigint identity`/`timestamptz`) abbilden → Folge-Migrationen driften provider-abhängig.
- Vermischtes Provider-DDL in einer Datei ist schlecht lesbar und fehleranfällig.
- Um beide Provider abzudecken, müsste die bestehende `InitialCreate` angefasst/neu erzeugt werden → Baseline-Risiko.

### Option 2: Zwei DbContexts

Ein zusätzlicher `PostgresMetricsDbContext` neben dem SQLite-Context.

**Positiv:**
- Jeder Context hat seinen eigenen, sauberen Snapshot.

**Negativ:**
- Doppelte Model-Pflege für 15 Tabellen inkl. aller Indizes; Snapshot-Drift zwischen beiden ist nur eine Frage der Zeit. In `stableDB.md` §4 bereits bewusst ausgeschlossen.

### Option 3: Separate Migrations-Assemblies mit `Whiskers.Data`-Extraktion

`MetricsDbContext` + die 15 Entity-Klassen wandern in ein eigenes Class-Lib-Projekt `Whiskers.Data` (**Namespace `Whiskers.Services.Persistence` bleibt erhalten** → Konsumenten-`using`s unverändert). Zwei Migrations-Projekte referenzieren `Whiskers.Data`: `Whiskers.Migrations.Sqlite` (die bestehende `InitialCreate` wird unverändert hierher verschoben) und `Whiskers.Migrations.Postgres` (neu gescaffoldete PG-`InitialCreate`). Je Provider setzt `UseSqlite`/`UseNpgsql` die passende `MigrationsAssembly`.

**Positiv:**
- Jedes Migrations-Projekt hat seinen eigenen, stabilen Snapshot → löst den Ein-Snapshot-Konflikt.
- Die Migrations-Projekte referenzieren `Whiskers.Data` (nicht die App) → kein Zirkelbezug.
- EF-Core-empfohlener Weg für Multi-Provider.
- `InitialCreate`-Name unverändert → ADR-0003-Baseline intakt; Namespace-Erhalt → kaum Konsumenten-Churn.

**Negativ:**
- Struktureller Umbau: ein neues `Whiskers.Data`-Projekt plus zwei Migrations-Projekte, mit den zugehörigen Projektreferenzen — deutlich größer als `stableDB.md` ursprünglich annahm.
- Die App muss zur Laufzeit beide Migrations-Assemblies mitliefern (Referenz mit `ReferenceOutputAssembly` sicherstellen).

## Vorschlag des Autors

Option 3 ist der einzige Weg, der beide harten EF-Randbedingungen (Ein-Snapshot-Regel und Zirkelbezug) auflöst, ohne einen zweiten DbContext einzuführen oder die Baseline zu gefährden. Der scheinbare Mehraufwand (drei Projekte statt eines) ist überschaubar, weil die Entities bereits in einer einzigen Datei liegen und der Namespace erhalten bleibt — die vielen Konsumenten müssen nicht angefasst werden. Die verworfenen Optionen scheitern jeweils an einem harten Faktum (O0 an EF, O1 am Snapshot, O2 an der Doppelpflege), nicht an Geschmack.

## Entscheidung

**Gewählte Option:** „Separate Migrations-Assemblies mit `Whiskers.Data`-Extraktion" (Option 3).

Ausschlaggebend waren die Baseline-Sicherheit (ADR-0003) und der Verzicht auf einen zweiten DbContext. Der bewusst in Kauf genommene Nachteil ist der einmalige strukturelle Umbau in drei Projekte.

## Konsequenzen

### Positiv

- Saubere, EF-idiomatische Provider-Trennung; jeder Provider hat seinen eigenen stabilen Migrations-Snapshot.
- Die bestehende SQLite-`InitialCreate` bleibt namens- und inhaltsgleich → keine Re-Migration bestehender Installationen.
- Namespace-Erhalt (`Whiskers.Services.Persistence`) hält den Konsumenten-Churn minimal.
- Legt die Grundlage für Multi-Replica/HA (`kubernetesImplement.md`).

### Negativ

- Neues `Whiskers.Data`-Projekt plus zwei Migrations-Projekte mit Projektreferenzen — die Lösung (`.slnx`) und die Build-/Publish-Pfade müssen angepasst werden.
- Die App muss beide Migrations-Assemblies zur Laufzeit laden; die Projektreferenzen müssen die Ausgabe-Assemblies tatsächlich mitliefern.
- Der Umfang übersteigt die ursprüngliche `stableDB.md`-Annahme — die dortigen Schritte 3/8 sind entsprechend zu korrigieren.

### Folge-Entscheidungen

- Design-Time-Factory: eine Factory, die über `WHISKERS_DB_PROVIDER` verzweigt und je Provider die `MigrationsAssembly` setzt.
- `DatabaseInitializer` verzweigt provider-spezifisch (SQLite: Legacy-Heal/WAL; Postgres: nur `MigrateAsync` + Connect-Retry).
- PostgreSQL-Runtime-Verifikation (SmokeTests/Boot) läuft über einen echten Docker-Host, da die Windows-Dev-Maschine kein Docker hat.
- Später: `changeme.md` C7 (JSON-Stores → DB) und der `outOfTheBox.md`-Wizard-Schritt „Datenbank" bauen hierauf auf.

### Review

**Reality-Check geplant für:** 2026-08-20 (ca. 6 Wochen nach Entscheidung, nach der ersten echten PostgreSQL-Inbetriebnahme).

## Weitere Informationen

### Scope

Gilt nur für die 15 EF-Core-Tabellen des `MetricsDbContext`. Nicht betroffen: die `JsonFileStore`-Konfigurationsspeicher (`servers.json`, `vault.json`, …) und die DataProtection-Keys — deren mögliche DB-Migration ist ein separates Thema (`changeme.md` C7).

### Referenzen

- [ADR-0003 — EF-Core-Migrations-Baseline](./0003-ef-core-migrations-baseline.md) (wird ergänzt, nicht ersetzt)
- `docs/roadmap/stableDB.md` (Schritte 3/8 durch dieses ADR korrigiert)
- EF Core Docs — „Managing Migrations" / „Using a Separate Migrations Project"
