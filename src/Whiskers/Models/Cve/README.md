# Models/Cve

Models for vulnerability scanning ([`../../Services/Cve/`](../../Services/Cve/)).

## Files

| File | Purpose |
|---|---|
| `CveFinding.cs` | A single CVE finding (id, package, severity, fixed version, OS context, published date, `IsVerified`/`HasFix`). |
| `CveScanResult.cs` | The result of scanning one target (server OS or container image). |
| `CveGroup.cs` | `CveGroup` + `CveAffected`, a CVE **de-duplicated** across the fleet: one CVE-ID with every real affected (server, container/OS, package) instance behind it, worst severity, fix availability, and the age it has been open. |
| `CveFirstSeenEntity.cs` | EF entity persisting when a vulnerability instance was first detected (powers the age indicator). Lives in the `CveFirstSeen` table of `MetricsDbContext`. |
| `CveSummary.cs` | Aggregated counts/severity rollup for the dashboard. |
| `CveSeverity.cs` | Severity enum (Critical/High/Medium/Low/...). |
| `CveSource.cs` | Source enum (OS packages vs container image / Trivy). |

## Related

- Scanners & store: [`../../Services/Cve/`](../../Services/Cve/)
