# Windows packages and release checks

## Build checked packages

Use a Windows .NET 10 SDK, from the repository root:

```powershell
dotnet.exe restore .\sBridge.slnx --locked-mode
dotnet.exe build .\sBridge.slnx -c Release --no-restore -warnaserror
dotnet.exe test .\sBridge.slnx -c Release --no-build
powershell.exe -ExecutionPolicy Bypass -File .\package.ps1
```

The default version is **`0.0.0-dev`**, a local development label. For an intended
release, choose its version explicitly and supply the reviewed source commit:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\package.ps1 -Version "0.5.0-preview.1" -Revision "<40-character-reviewed-commit>"
```

That version is an example, not a selected release version. Package only reviewed
source; a manifest revision argument does not prove a dirty checkout matches that
commit. Keep release notes and test results with the reviewed source history.

Outputs in `artifacts\packages`:

- `sBridge-<version>-win-x64-framework-dependent.zip`: requires the x64 .NET 10
  Desktop Runtime. Copying only the executable is insufficient.
- `sBridge-<version>-win-x64-self-contained.zip`: includes the Windows x64 .NET
  Desktop runtime. Keep every extracted file together.
- A `.sha256` sidecar for each archive; both archives contain a per-file SHA-256
  manifest, package README and GPL license. Self-contained output includes the
  exact bundled runtime-pack licenses/notices.

`package.ps1 -Mode framework-dependent` or `-Mode self-contained` builds just one.
It publishes into a fresh owned staging directory, excludes state/tests/PDBs,
records package/runtime/SDK/source metadata, uses sorted ZIP entries with fixed
timestamps, and replaces the generated output ZIP after successful creation.
This is repeatable scripted packaging, not a claim of bit-identical output across
different SDK/runtime/compiler/compression versions. No signing or release upload
is performed by this script.

## Automated local/package checks

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tests\WindowsPackageSmoke.ps1 -Package .\artifacts\packages\sBridge-0.0.0-dev-win-x64-framework-dependent.zip
powershell.exe -ExecutionPolicy Bypass -File .\tests\WindowsPackageSmoke.ps1 -Package .\artifacts\packages\sBridge-0.0.0-dev-win-x64-self-contained.zip -DesktopChecks
```

The non-desktop check verifies checksum (when provided), manifest hashes/lengths,
required payload/runtime files, and a SISR-disabled custom launch from an extracted
space-containing path with temporary per-user data. `-DesktopChecks` additionally
runs the isolated settings migration/save/error smoke checks against that package.
Tests restore environment overrides and remove their owned temporary state.

Self-contained checks set invalid `DOTNET_ROOT`/`DOTNET_ROOT_X64` values and inspect
included-runtime metadata, exercising app-local hosting. A machine with registered
.NET installations still has fallback mechanisms; this does **not** replace the
clean-machine check below. Framework-dependent packages intentionally need the
Desktop Runtime, not just the base .NET runtime.

## GitHub CI

`.github/workflows/windows.yml` runs on push, PR and manual dispatch using a Windows
runner and stable .NET 10 SDK. It performs locked restore, warning-as-error Release
build, default portable/native Windows tests with TRX output and a hang watchdog,
development publish, both package modes, and non-desktop package smoke checks.
Action versions are pinned to commit SHAs; token permissions are read-only.

Checked packages and test results become workflow artifacts. Package versions use
`0.0.0-ci.<run-number>` and the workflow source SHA. This is development artifact
automation, not automatic public releases. Desktop UI Automation, installed SISR,
Appx inventory, controllers and live Steam edits are not CI gates. Default tests
skip their explicit opt-in cases. Native controlled process/socket/registry tests
use only owned test resources.

**Maintainer intervention:** the workflow cannot run remotely until the changes
are committed and pushed. After that, review its first Windows run and download
the checked artifacts. Local equivalent commands and workflow linting have passed;
the hosted runner has not yet executed this new workflow.

## Clean-machine gate — maintainer/user assisted

The self-contained `0.0.0-dev` package passed this gate on 2026-10-08. See
[the recorded package hash and guest results](validation/2026-10-08-clean-machine.md).
Repeat the gate for a newly built release package; this result covers that exact ZIP.

Use a disposable **Windows 10/11 x64 VM or Windows Sandbox with no .NET 10 SDK or
runtime installed**. Enabling Sandbox/creating the VM may need administrator
access and a reboot, so this step needs your intervention.

To prepare an automated Sandbox check from the Windows checkout:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\tests\PrepareWindowsSandbox.ps1
```

It creates a uniquely named local Windows temp bundle and prints a `.wsb` path.
Enable **Windows Sandbox** in **Turn Windows features on or off**, reboot if
prompted, then open that `.wsb` file. Preparation itself does not enable features,
reboot, or launch Sandbox. The input mapping is read-only and contains only the
self-contained ZIP/checksum and three test scripts. Only the bundle's dedicated
results folder is writable; normal user folders and the checkout are not mapped.
Optional networking/redirection settings use Windows defaults. The test itself
does not contact an API or launch controller integration.

`WindowsSandboxVerify.ps1` checks for installed .NET 10 before running package and
desktop smoke. It writes `result.json` and `transcript.txt` back to the printed
host results folder and leaves its console open for monitoring. If a WSL-started
launch reports initialization errors, try opening the prepared `.wsb` directly
through Win+R on the Windows desktop. A host-side preparation or successful XML
parse is not a clean-machine test result; wait for the guest result. Preserve the
result with the package hash, then remove the specific temp bundle when finished.

1. Transfer the self-contained ZIP, checksum and `WindowsPackageSmoke.ps1` plus
   `WindowsSmoke.ps1` scripts into the disposable machine. If mapping folders into
   Sandbox, expose only those release/test files read-only, rather than normal
   LocalAppData, Steam userdata or a credential-bearing checkout.
2. Confirm `.NET 10` is not installed (`dotnet --list-runtimes` should be unavailable
   or contain no .NET 10 runtime). Do not install it to make this check pass.
3. Run the self-contained package smoke with `-DesktopChecks`, or manually extract
   all files and open `sBridge.exe`. Verify settings opens, isolated save works,
   and a SISR-disabled custom launch completes. Record OS/package/hash/results.
4. For the framework-dependent ZIP, verify the documented .NET Desktop Runtime
   prerequisite. Install the **x64 .NET 10 Desktop Runtime** in a separate fresh VM
   or after finishing the self-contained proof, then repeat startup/launch checks.
5. Unsigned packages can trigger SmartScreen. Code-signing credentials/certificate
   and a chosen release version require maintainer decisions; neither is inferred
   from a successful build.

## Remaining product validation — user assisted

Use your configured desktop for actual Steam import/restart/readback and sanitized
Steam-created AppID/quoting fixtures; close Steam before writes. Validate packaged
COM and Epic authentication/EAC handoffs using games you own. Physical controller
input, VIIPER/device cleanup, Steam profiles/first-run setup and managed SISR context
need real gameplay observation. Controlled probes and CI package checks do not
establish those outcomes. Registered-library editing remains planned.
