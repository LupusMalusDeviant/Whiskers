# Services/ImageSearch

Searches container images across multiple registries ("marketplaces") so the App Store can discover
and deploy any public image — not just the built-in templates. Each registry is a pluggable provider;
results carry which marketplace they came from so the deploy uses the correct fully-qualified reference.

## Files

| File | Purpose |
|---|---|
| `IImageSearchProvider.cs` | One registry/marketplace. Exposes `Id`/`DisplayName`/`IsEnabled`/`SupportsSearch` plus `SearchAsync` + `GetTagsAsync`. |
| `IImageSearchService.cs` / `ImageSearchService.cs` | Aggregator: lists enabled registries and searches one (by id) or all search-capable providers in parallel, merged by popularity. |
| `ImageSearchModels.cs` | `ImageSearchResult` (a match + its source registry) and `ImageRegistryInfo` (a marketplace for the selector). |
| `ImageSearchSettings.cs` | Config (`ImageSearch` section): toggles for Docker Hub/GHCR and the opt-in Harbor instance. |
| `Providers/DockerHubSearchProvider.cs` | Docker Hub — full-text search + tag listing (anonymous, public images). |
| `Providers/GhcrSearchProvider.cs` | GHCR — no anonymous full-text search, so resolves an exact `owner/repo` reference and lists tags via the Registry v2 API. |
| `Providers/HarborSearchProvider.cs` | Self-hosted Harbor — opt-in via `ImageSearch:Harbor:BaseUrl`; uses Harbor's `/search` + artifacts APIs. |

## Related

- Deploying the chosen image (compose generated + deployed via the host executor): [`../Deployment/`](../Deployment/), [`../Server/`](../Server/)
- Built-in app templates: [`../Templates/`](../Templates/)
- Digest-based update detection (separate concern): [`../ImageUpdate/`](../ImageUpdate/)
- UI: [`../../Components/Pages/AppStore.razor`](../../Components/Pages/AppStore.razor) (the "Registry-Suche" mode)
