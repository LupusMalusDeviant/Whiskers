<!-- Thanks for contributing! Please keep one PR = one thing. -->

## What & why

<!-- What does this change, and what problem does it solve? Link the issue if there is one. -->

## Checklist

- [ ] `dotnet build src/Whiskers/Whiskers.csproj` and `dotnet test src/Whiskers.Tests/Whiskers.Tests.csproj` are green
- [ ] New/changed services are interface-first (`IFoo` + DI registration); consumers inject the interface
- [ ] If DI registrations or constructors changed: the app boots in Development mode (`ValidateOnBuild` catches DI-graph errors that build+tests miss)
- [ ] Per-folder `README.md` of every materially changed folder is updated (new folders get one)
- [ ] Anything that builds shell commands from strings has command-building tests
- [ ] No secrets in code, logs, tests, or example configs
- [ ] Did NOT touch the auth middleware order, OAuth whitelist, mTLS layer, or socket-proxy verb whitelist (off-limits without maintainer approval — ask first in an issue)

## Notes for the reviewer

<!-- Anything non-obvious: trade-offs, deviations from an ADR/roadmap doc, follow-ups. -->
