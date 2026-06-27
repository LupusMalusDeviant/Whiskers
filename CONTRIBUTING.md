# Contributing to ServerWatch

Thanks for your interest in ServerWatch! It's an early-stage (`0.x`) project, so issues, ideas
and pull requests are very welcome.

## Ground rules

- **Open an issue first** for anything non-trivial (a feature, a refactor, a behavioural change),
  so we can agree on the approach before you invest time.
- **Security issues:** please do **not** open a public issue — follow [SECURITY.md](SECURITY.md).
- Be respectful. Assume good intent.

## Development setup

ServerWatch is an ASP.NET Core 10 / Blazor Server app.

```bash
cd src/ServerWatch
dotnet run            # or: docker compose up -d --build  (from the repo root)
```

For local development without SSO, set `Auth__Disabled=true` and
`ASPNETCORE_ENVIRONMENT=Development`. See [README.md](README.md#configuration) for configuration.

## Before you open a pull request

- **Build clean:** `dotnet build` with **0 warnings / 0 errors**.
- **Tests green:** `dotnet test` (add tests for new behaviour where it makes sense).
- **Boot it:** start the app in `Development` once — `ValidateOnBuild` aggregates the whole DI
  graph at startup and catches registration mistakes that the build alone misses.
- **Keep docs in sync:** new DI services go behind an `IFoo` interface and are registered
  interface-first; update the relevant per-folder `README.md` in the **same** commit.
- **No secrets** in the diff (`.env`, `vault*.json`, `data/`, keys, certificates are gitignored —
  keep it that way).
- Keep commits focused and write a clear commit message describing the *why*.

## Coding style

- C# with file-scoped namespaces; match the conventions of the surrounding code.
- Comments in English; user-facing UI strings are German.
- Prefer small, composable services behind interfaces over large god-objects.

## License

By contributing, you agree that your contributions are licensed under the project's
[Apache 2.0 license](LICENSE).
