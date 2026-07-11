# Services/Registries

**UI-managed container registries** (missingFeatures F8, v1): credentials for private registries
are stored in the **vault** (`registry-cred:{id}`) and used automatically for **authenticated image
pulls** — `ImageOperations.PullImageAsync` matches the image reference's registry host
(Docker-convention parsing, see `RegistryHostOf` + `RegistryConfigTests`) against the configured
registries and passes an `AuthConfig`; no match = anonymous pull, unchanged.

| File | Purpose |
|---|---|
| `IRegistryConfigService.cs` / `RegistryConfigService.cs` | CRUD (`registries.json`) + synchronous, cache-backed credential resolution for the pull hot path. |

Managed in *Settings → Registries* ([`RegistriesPanel`](../../Components/Shared/RegistriesPanel.razor)).

**v1 scope note:** the image SEARCH providers (Docker Hub/GHCR/Harbor) still read their config from
the `ImageSearch` settings section — feeding them from this store is the documented follow-up.
