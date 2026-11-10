# Services/ImageSearch/Providers

The registry providers behind `IImageSearchProvider` (see the parent folder). The all-registries search fans out
only to providers with `SupportsSearch == true` and merges their results; a provider without search resolves an exact
reference.

| File | What |
|---|---|
| `DockerHubSearchProvider.cs` | Docker Hub repository search and tags. |
| `GhcrSearchProvider.cs` | GitHub Container Registry, exact-reference resolver only (`SupportsSearch` is false, so it is not part of the all-registries search): normalizes a reference to `owner/repo` (strips `ghcr.io/` and the tag) and lists the tags of a public image. |
| `HarborSearchProvider.cs` | Self-hosted Harbor, opt-in via `ImageSearch:Harbor:BaseUrl`: the `/search` API for discovery, the artifacts API for tags, optional basic auth for private projects. |
