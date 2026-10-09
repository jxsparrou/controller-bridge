# Self-contained 0.9.0 Windows Sandbox validation

**Result: passed**, with maintainer desktop monitoring.

| Item | Verified value |
| --- | --- |
| Test started UTC | 2026-10-09T10:51:54.1651620+00:00 |
| Package | `sBridge-0.9.0-win-x64-self-contained.zip` |
| SHA-256 | `f08c8b9809bd68c70553ca79bd95fba2503a81eb646406e9b42c35eb247c1b9e` |
| Guest OS | Microsoft Windows 11 Enterprise, Windows Sandbox |
| Installed .NET runtimes detected | None (`dotnetRuntimes: []`) |
| Guest result | `succeeded: true`, `error: null` |

The host verified that the guest's package hash matches the released 0.9.0 ZIP
built from source commit `a6f7aace4b1b77262d64d9c837ad11e028a551a0`.
Evidence is retained locally under `artifacts/validation/sandbox-2026-10-09-090/`:
`result.json` and `transcript.txt`. Build/test artifacts are ignored by Git; this
summary records the reviewed outcome without guest account/machine identifiers.

## Checks passed

- ZIP checksum, per-file hashes/lengths and bundled runtime payload.
- Extracted, isolated SISR-disabled custom launch from a path containing spaces.
- Settings GUI startup, JSON migration and save with unchanged legacy input.
- Unsupported JSON schema rejection without fallback/overwrite.
- Locked-save error reporting without modal reentry, preserving original bytes.

The guest used its own temporary app/data resources. No host LocalAppData, Steam
userdata or checkout was mapped. The input bundle was read-only and only a
dedicated host results folder was writable.

## Scope

This proves the specific 0.9.0 self-contained ZIP can start and perform the listed
operations on a fresh Sandbox without an installed .NET 10 runtime. It does not
establish live Steam readback, packaged COM/Epic authentication, controller input,
SISR/VIIPER integration or every Windows version/hardware combination.
