# Services/ImageSearch/Providers

The registries the image search fans out to. Each implements `IImageSearchProvider` (see the parent folder);
the search service merges their results.

| File | What |
|---|---|
| `DockerHubSearchProvider.cs` | Docker Hub repository search and tags. |
| `GhcrSearchProvider.cs` | GitHub Container Registry: normalizes a reference to `owner/repo` (strips `ghcr.io/` and the tag) and lists tags. |
| `HarborSearchProvider.cs` | Self-hosted Harbor, opt-in via `ImageSearch:Harbor:BaseUrl`: the `/search` API for discovery, the artifacts API for tags, optional basic auth for private projects. |
