# Low-barrier features — status & how to verify

Status date: 2026-06-16.

## 1. softwarePath tolerant resolution — IMPLEMENTED

`GetPlcSoftware` now falls back to a tolerant match when an exact path misses:

- wrong case and surrounding whitespace are accepted;
- a **single-PLC project** resolves any token to its sole PLC;
- a **unique case-insensitive substring** resolves (e.g. `"PLC"` → `"PLC_1"`, `"安全"` → `"安全PLC"`);
- when it still can't resolve, the listing tools (`GetPlcTagTables`, `GetPlcExternalSources`,
  `GetPlcWatchTables`) end the error with `Available PLC paths: <name1>, <name2>, …`.

**Verification status**

- Pure matcher `Guard.MatchPlcName` — 16 deterministic offline cases pass
  (sole-PLC / case / trim / unique-substring / ambiguous→none / not-found→none).
  Run `scripts/Test-MatchPlcName.ps1` (reflection over the built assembly).
- Live spot-check is best done in **your wired MCP** — a cold-spawned test process
  stalls on the Openness attach/trust handshake, so it is unreliable for this:
  ```
  AttachToOpenProject → GetProjectTree                  # note the real PLC name(s)
  GetPlcTagTables(softwarePath="<wrong case / spaces / unique substring>")  # resolves, no error
  GetPlcTagTables(softwarePath="NoSuchPlc")             # error ends with "Available PLC paths: …"
  ```
  All read-only; no writes.

## 2. Download to a V21 CPU — FIXED (verified on a real CPU 2026-06-17)

`DownloadToPlc` works on V21: the cast is resolved (a `ConfigurationTargetInterface` is the
`IConfiguration`) and the StopModules prompt is handled (`StopAll`). Verified end-to-end on a
real S7-1200 (安全PLC) → `state=Success, 0 errors` (stop → download → restart). See SKILL.md §13.
