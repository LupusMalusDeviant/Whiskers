# Models/Cve

Models for vulnerability scanning ([`../../Services/Cve/`](../../Services/Cve/)).

## Files

| File | Purpose |
|---|---|
| `CveFinding.cs` | A single CVE finding (id, package, severity, fixed version). |
| `CveScanResult.cs` | The result of scanning one target (server OS or container image). |
| `CveSummary.cs` | Aggregated counts/severity rollup for the dashboard. |
| `CveSeverity.cs` | Severity enum (Critical/High/Medium/Low/…). |
| `CveSource.cs` | Source enum (OS packages vs container image / Trivy). |

## Related

- Scanners & store: [`../../Services/Cve/`](../../Services/Cve/)
