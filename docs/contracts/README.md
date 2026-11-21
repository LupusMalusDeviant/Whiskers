# docs/contracts — interface contracts

Whiskers works contracts first: before a work package is built, its interfaces, types, API and module boundaries
are written down here and accepted by the maintainer. If the implementation has to deviate, the contract is
amended and accepted again before the code changes.

Each contract (0001–0022, numbered in the order they were written) starts with a **Status** line: who accepted
it, when, and with which decisions. Amendments are added to the contract itself, each with its own status and
date, so the file always shows what was agreed and when.

Tests and code comments refer to these files by path; keep the names stable.
