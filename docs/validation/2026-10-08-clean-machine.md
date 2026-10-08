# Self-contained Windows Sandbox validation

**Result: passed**, with maintainer desktop monitoring.

| Item | Verified value |
| --- | --- |
| Test started UTC | 2026-10-08T20:46:14.5579035+00:00 |
| Package | `sBridge-0.0.0-dev-win-x64-self-contained.zip` |
| SHA-256 | `0be5028254ef6600d8327c6df5230f3244dfdd4c9bf32a9324c9ce367807542c` |
| Guest OS | Microsoft Windows 11 Enterprise, Windows Sandbox |
| Guest Windows build | 10.0.26100.0 (PowerShell build 26100.9549) |
| Shell | Windows PowerShell 5.1.26100.9549 |
| Installed .NET runtimes detected | None (`dotnetRuntimes: []`) |
| Guest result | `succeeded: true`, `error: null` |

The host verified that the guest's package hash matches the current package ZIP.
Evidence is retained locally under `artifacts/validation/sandbox-2026-10-08/`:
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

## Sandbox startup observations

Sandbox initially failed even in plain mode; the maintainer's Repair action
allowed plain Sandbox to start. Automated prepared-config launches subsequently
showed generic initialization/stack guard-page errors. Host memory diagnostics
showed approximately 19 GiB available RAM and 38% commit use, providing no evidence
of memory/page-file exhaustion. No host service, DNS or page-file settings changed.

The configuration was simplified to required folder mappings and the test command,
using Windows defaults for optional settings. A manual Win+R launch of that `.wsb`
file ran the guest test successfully and generated the result/transcript. The
earlier launcher errors' precise cause remains unresolved; they do not invalidate
the captured successful guest run. Prefer launching the prepared file directly
from the Windows desktop when validating through WSL.

## Scope

This proves the specific self-contained ZIP can start and perform the listed
operations on a fresh Sandbox without an installed .NET 10 runtime. It does not
establish live Steam readback, packaged COM/Epic authentication, controller input,
SISR/VIIPER integration or every Windows version/hardware combination. Hosted CI
subsequently passed; see [the hosted validation record](2026-10-08-hosted-ci.md).
