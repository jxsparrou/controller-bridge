# sBridge modernization plan

## Status and scope

Audit date: 2026-10-07. Baseline: `4137c7486be2a64e3b2afb11952862354296ec42`.
Reviewed PR #1 head: `ce213d5dcac8aaa11bbaeb202387070c91ce09db`.
SISR research baseline: release `v0.6.1` (2026-09-30).

Phase 0's audit, Phases 1A-C (SDK/tests, Steam safety, owned SISR), and Phases 2A-C
(launch requests, multi-hop monitoring, typed legacy settings) are implemented. Existing shortcut
legacy launch formats, settings migration and stored AppIDs remain. Unsafe Steam writes,
blanket process termination, and argument-boundary loss have been addressed.
Session monitoring is separated with scored matching/async waits. The larger
Phase 3A implements versioned LocalAppData settings and DPAPI credential migration;
Phase 3B adds stable Game UUIDs and ID-based launching/imports. The Xbox/Win32
provider-boundary slice adds responsive, cancellable packaged discovery; the
bounded Epic provider adds typed manifest discovery and launcher activation.
Selected Steam accounts, safe first-file creation, bounded artwork HTTP/image
validation/UI-owned jobs, and opt-in managed SISR config/API readiness/status are
implemented, along with bounded diagnostic logging/retention and a read-only
diagnostics UI. Full SISR profiles/Steam/controller validation remains planned.
PR #1 remains unmerged.
Later architecture and phases below remain proposals.

### Phase 1A implementation and verification (2026-10-07)

- `sBridge.csproj`: .NET 10 Windows WinForms, explicit sources, same STA entrypoint,
  executable name, and embedded icon. The icon is also copied for the existing
  form's file-based lookup. New code is nullable-aware; five legacy files have
  temporary local opt-outs to avoid a broad null-contract refactor.
- `global.json`: stable .NET 10, minimum 10.0.100, latest installed feature band.
- `sBridge.slnx` and xUnit test project with a dependency lockfile. Tests link
  production logic rather than start the app or reference WinForms.
- Extracted AppID arithmetic to `Steam/SteamShortcutIdentity.cs`, retaining the
  legacy wrapper/caller contract. Extracted the root serialization block to
  `VdfParser.cs` so saves and tests use the same root/trailer implementation.
- `build.ps1` now publishes the complete framework-dependent win-x64 app to
  `artifacts\app`. Distribution requires all files together and the x64 .NET 10
  Desktop Runtime; self-contained release validation remains Phase 10 work.
- Windows SDK 10.0.401 successfully restored, built, tested, and published from
  the WSL UNC checkout. 13 tests passed: independent AppID vectors/UTF-8/quoting,
  VDF fixture decode/exact round trip/edit preservation/empty root/UTF-8 bytes,
  unterminated strings, and all truncations before the root close.
- `tests/WindowsSmoke.ps1` passed on Windows: isolated GUI startup/save-on-close
  preserved global and per-game settings; a disabled-SISR custom launch with a
  space-containing executable path exited successfully. This is an automated
  desktop smoke check, not manual packaged-game or controller validation.
- One compiler warning remains: legacy `WebClient` (`SYSLIB0014`), tracked for
  Phase 7. Tests use synthetic independently constructed bytes; Steam-created
  AppID/quoting fixtures, strict malformed-input validation, and real Steam/SISR/
  packaged-game runtime checks are still outstanding.

### Phase 1B implementation and verification (2026-10-07)

- `VdfParser.cs` is nullable-aware and now has a shared strict root reader.
  It rejects wrong root/type/trailer, unsupported tags, invalid UTF-8, truncation,
  invalid output strings/cycles, and excessive size/depth/element counts. Limits:
  32 MiB per file, 1 MiB per string, 64 depth levels, 100,000 elements. Existing
  supported-type field order, unknown names, Int32 bits, Unicode, AppIDs, and
  unrelated shortcuts are retained. Root-close alone or one extra end marker is
  accepted; output retains the two-marker format. Steam-captured fixtures are
  still needed to establish actual Steam conventions before identity changes.
- `Steam/SteamShortcutRepository.cs` owns a loaded path/root/content fingerprint.
  Existing files must parse successfully; missing/unreadable/malformed input is
  not converted to a new empty library. In-process writes are serialized per path.
- Saves recheck Steam and the original fingerprint, serialize to a unique
  same-directory temporary file, flush, parse and byte-compare it, stage the backup,
  recheck before commit, and use `File.Replace`. No destructive fallback is used
  on unsupported filesystems. `.bak` replacement is itself staged; cleanup failures
  are reported. A successful save advances its snapshot for later edits.
- Imports, removal, and UWPHook migration carry loaded documents and consume
  per-file results. Failure/partial success is displayed without unconditional
  success; failed custom imports keep the form fields for retry. The first existing
  discovered account remains the import target, and removal still changes only
  checked entries' original files. First-file creation/account selection are not
  added by this slice.
- Windows SDK 10.0.401: locked restore, Release build, all **52 tests**, publish
  wrapper, and isolated desktop smoke checks passed from WSL. Tests cover parser
  rejection/bounds, byte preservation, backup correctness, stale/deleted/malformed
  originals, denied reads, partial temp writes, corrupt/different valid temp bytes,
  backup/replace failures, unsupported replacement, cleanup reporting, Steam
  restarting mid-save, and independent account results. A real Windows open-handle
  sharing violation verified that replacement failure leaves the original intact;
  this test skips on other OSes. The earlier invalid-surrogate theory case was
  corrected to construct its surrogate at runtime rather than through attribute
  metadata. One legacy `WebClient` warning remains.
- Scope limits: actual Steam-file editing/readback was not performed, and API/
  controller/packaged-game runtime checks remain pending. Fingerprints and Steam
  rechecks are optimistic concurrency protection, not an OS compare-and-swap:
  another process may still change the path after the last check. Close Steam;
  cross-process sBridge coordination belongs in a future concurrency slice.
  Atomic replacement protects file completeness, not concurrent-edit merging.

### Phase 1C implementation and verification (2026-10-07)

- `Sisr/SisrProcessManager.cs` owns the actual started process and a per-user,
  Windows-session semaphore lease. Startup does not kill/reset existing integration
  resources. An existing named/configured SISR instance or managed lease blocks
  competing startup, without adoption or termination. SISR-disabled launches do
  not acquire the integration lease. Advanced arguments, cwd, and inherited Steam
  context remain unchanged; configure the actual SISR executable, not a wrapper.
- `Program.Main` starts/monitors inside an inner `try/finally`. Owned cleanup runs
  before the outer error dialog, including on game launch failures. Main and COM
  activation remain STA/synchronous; cleanup has bounded async internals.
- `WindowsTcpListeners` uses Windows IP Helper owner-PID tables for IPv4/IPv6
  loopback listeners. `WindowsSisrShutdown` uses direct local HTTP with redirects/
  proxies disabled and a process-bound connection pool. Listener ownership is
  checked after connecting, before transmitting HTTP, so a port check followed
  by connection to a different owner cannot trigger a quit on that server.
- The small shutdown adapter checks `/api/v1/version/info`, accepts audited
  v0.6.1/compatible v0.6.x patches, and posts `/api/v1/quit`. Unknown version families
  or unavailable/unproven endpoints fall back to a window-close request, then only
  the retained root handle. No name/PID re-adoption, process-tree kill, VIIPER kill,
  or global Steam force-input reset is performed by sBridge. Cleanup is idempotent;
  timeouts/failures are logged and leases released. Default per-step budget is five
  seconds, with at most four request/exit-wait budgets on the longest fallback path.
- Installed Windows SISR was discovered via the existing LocalAppData default.
  `--help` identified **v0.6.1 (c81195e)**; Windows file-version resources reported
  0.0.0.0 and are not a reliable capability signal. No SISR/VIIPER was running before
  the checks. The installed SISR passed actual startup, owned loopback/version
  discovery, API quit, and process exit with no forced termination.
- Locked restore, Release build, and **80 tests passed** on Windows SDK 10.0.401
  with the installed test enabled. Normal runs skip that one opt-in test. Coverage
  includes external/busy/start-failure decisions, early exit, exception cleanup,
  repeated/concurrent stop, graceful/window/forced fallback, unavailable API/window,
  supported versions, real Windows owner-PID tables, real API HTTP connections,
  stale discovery connecting to a different socket owner without sending HTTP, and
  retained-handle termination leaving an unrelated controlled process alive.
- Publish passed. Both desktop scripts passed: `WindowsSmoke.ps1` preserved the
  existing settings/custom-disabled-launch behavior; `WindowsOwnedSisrSmoke.ps1`
  left an external stand-in untouched and verified cleanup before the missing-game
  error dialog. The stand-in is a renamed temporary apphost opening settings, not
  an input-emulation stack. All temporary processes/configs are scoped to the tests.
- The installed opt-in test uses `--no-steam`, a random IPv4 loopback API port,
  hidden/non-fullscreen window, updates-notify disabled, temporary logging, and a
  non-resolving VIIPER address so SISR cannot auto-spawn VIIPER. SISR/VIIPER were not
  running after verification. Steam forcing/reset, VIIPER/device readiness, profiles,
  first-run Steam setup, and physical controller input were **not** validated.

Remaining lifecycle constraints: there is no startup-readiness gate yet; immediate
process exit is checked but later startup failure can still occur while the game
launches. External-instance detection cannot identify every arbitrarily renamed
SISR executable or prevent a user starting one mid-session. Named leases coordinate
sBridge in the same Windows session, not unrelated applications; separate API
ports do not isolate Steam Input. Normal SISR shutdown owns its integration cleanup;
forced root termination may leave a server/device/forced layout requiring manual
recovery. Do not broaden fallback killing to hide this limitation. Crash recovery,
structured profiles, status/readiness, and full API integration remain Phase 6 work.

### Phase 2A implementation and verification (2026-10-07)

- `Launching/LegacyLaunchRequest.cs` is a nullable-aware compatibility adapter with
  an immutable target/kind/hint and defensively copied argument tokens. No arguments
  selects settings. It preserves legacy File.Exists/.exe/backslash classification,
  forward-slash target normalization, raw watch overrides, packaged hint position,
  and global/per-game SISR inheritance. Invalid empty/NUL launch input fails before
  owned-resource startup. `launch <game-id>` is not reserved or implemented yet.
- `Program.Main` now resolves preferences against this request instead of embedding
  parsing/argument concatenation. Existing case-insensitive dictionaries and
  normalized unquoted keys remain; ownership/finally and STA activation remain.
- Win32 launch info uses BCL `ArgumentList`, with the same executable working
  directory and `UseShellExecute=true` for UAC-capable launching. COM receives
  `WindowsCommandLine.Join` output, with proper whitespace/empty/quote/backslash
  escaping. This is argument encoding, not a cmd.exe script escaping facility.
- `LegacyLaunchOptions` constructs new packaged/custom launch options. Space-
  containing packaged executable hints are now one quoted token. UI custom game
  argument text stays raw; only its executable target is encoded/quoted. Ordinary
  generated options retain their bytes, and bridge Exe/StartDir/AppID/artwork
  logic and stored IDs are unchanged. Existing files are not automatically rewritten.
- `ParseFirstArgument` delegates to the same Windows-token helper for UI keys;
  scan duplicate detection uses it too. An empty packaged hint must be explicitly
  reserved with `""` before extra game arguments, even when a watch override exists.
- The test project has an explicit test-only executable entrypoint that serializes
  received argv to JSON. VSTest still discovers/executes tests without calling that
  entrypoint; it is not included in application sources or publish output.
- Windows SDK 10.0.401 locked restore/Release build passed. **118 tests passed,
  one installed-SISR opt-in test skipped (119 total)**. New coverage includes settings
  mode, legacy classifications, slash/key rules, packaged hints/overrides, defensive
  copies, SISR inheritance, raw option generation, first-token parsing, and quoting
  vectors. Real child-process tests cover BCL ArgumentList, the COM argument string's
  Windows decoding, and packaged shortcut/hint/game-argument boundaries.
  First-token splitting uses only Windows space/tab delimiters so legal Unicode
  nonbreaking spaces in filenames remain part of settings keys.
- Publish and all three desktop scripts passed: settings/custom launch, owned-SISR
  failure cleanup, and `WindowsLaunchArgumentsSmoke.ps1`. The latter launches the
  actual bridge with a probe under a space-containing path and compares received
  empty values, whitespace, embedded quotes/backslashes, tabs, and Unicode. Its
  PowerShell 5 JSON reader was corrected to avoid nesting ConvertFrom-Json's array
  result in another array; this harness issue did not require a product change.
- The existing `WebClient` warning remains. Installed SISR was not restarted for
  this slice; no controller, packaged COM activation, live Steam write/readback,
  or proprietary game argument-parser verification is claimed. Windows/CLR quoting
  preserves tokens but cannot dictate an individual game's nonstandard parsing.

### Phase 2B implementation and verification (2026-10-07)

- Added `Sessions/GameLaunchEvidence`, immutable observations/identities/lifetimes,
  `ProcessMatcher`, `GameSessionMonitor`, and `WindowsProcessObserver`. Providers
  can later supply the same evidence without embedding monitoring. Main captures
  a baseline and UTC just before launch, then supplies target/watch hints and
  known Win32 install/path information. Packaged install directories remain unknown
  unless future discovery supplies them; legacy hints and actual returned PID remain.
- Win32/COM launchers retain an initial observation handle immediately when
  accessible, before COM's existing foreground delay. The monitor consumes and
  disposes it; no game-process termination operation exists on the observation
  interface. Matching/waiting may continue asynchronously after STA activation.
- Matching rejects baseline/prelaunch identities, unreadable creation times,
  retired processes, excluded crash/updater/controller/runtime helpers, known
  wrong paths for unrelated same-name processes, and near-tied candidates. Scores
  combine path/name, install boundaries, validated parent relationships, and visible
  windows. PID ordering only stabilizes diagnostics; it never breaks a near-tie.
  Authoritative initial handles can track a pre-existing app actually returned by
  activation, distinct from name-only adoption of an unrelated existing process.
- Parent chains are bounded and cycle/time checked. Live retired ancestors retain
  observation handles until exit so their exact exit times can reject stale-PID
  parentage; a newer current process at the same parent PID is not that ancestor.
  Multiple handoffs work even when later bootstrap stages take longer or a launcher
  stays alive. Old bootstrap survivors are not allowed to prolong normal game exit.
- Internal defaults: 1s scans, 90s discovery/provisional exit gaps, 3s matched-game
  exit grace, 32 handoffs, and 10m for a live provisional process. Budgets use a
  monotonic clock; timestamps describe creation/lineage, not wait duration. Real
  matched games have no overall duration cap. One cancellable retained exit wait
  per tracked process and cancellable delays replace monitor `Thread.Sleep` loops.
- Session IDs, target/hint, scores/reasons, handoffs/exits, ambiguity, rejected hint
  candidates, and final outcomes are logged. PID-zero launches search by evidence.
  Unresolved/ambiguous/limit outcomes are errors in Main, with owned SISR cleanup
  still before the error dialog; a launcher disappearing without a confirmed game
  is not reported as success.
- Windows observation uses Toolhelp parent/name enumeration, current-session
  filtering, creation times, limited-information image queries, and one visible-
  window enumeration per scan. It does not recurse game directories, use ETW/WMI,
  or require broad process-module access. Unknown metadata remains unknown.
- Windows SDK 10.0.401 locked restore/build/publish passed. The full suite passed
  **154 tests, one installed-SISR opt-in test skipped (155 total)**, including
  deterministic multi-hop/slow-bootstrap/persistent-launcher/quick-exit/PID-zero/
  ambiguity/budget/cancellation/failure cases and native Windows parent/path/start
  identity/cancellation/two-hop checks. Existing codec/AppID/argument/SISR tests pass.
- Published desktop smoke checks passed for settings/custom launch, argument
  forwarding, ownership cleanup, and the new `tests/WindowsSessionSmoke.ps1`:
  launcher -> bootstrap -> game with a real-game watch override and SISR disabled.
  The app-level console probe revealed `conhost` being considered provisional;
  console/runtime hosts are now excluded and have regression cases. The full
  suite also stalled in an existing UNC argument-probe launch; local probe staging
  plus one serialized Windows integration collection resolved the test setup, and
  the final suite passed with a 45s hang watchdog. No hang dump was collected.
- Synthetic/native controlled chains are not validation of a protected or service-
  launched commercial game, packaged COM activation, or controllers. An initial
  executable that is a poorly identified launcher may require an explicit watch
  override naming the real game. Candidates without observable creation time are
  intentionally not adopted. Late handoffs after an apparently confirmed game's
  exit must arrive within exit grace; timing/role heuristics still need real-game
  evidence before new tuning. Keep Luke1505's PR #1 credit for the motivating
  multi-hop/timing/install-path scenarios; this is a rewrite, not a PR merge.

### Phase 2C implementation and verification (2026-10-07)

- `Core/AppSettings.cs` is the single typed transitional settings model. Immutable
  `GameProfile` values unify per-target integration/watch choices with
  `SteamInputMode.Automatic/Enabled/Disabled`; Automatic inherits the global
  choice. Reads do not add profiles. Clearing one override retains the other;
  fully automatic/empty profiles are removed. Keys remain case-insensitive legacy
  launch targets, not UUIDs. Cloning produces independent dictionaries.
- Startup, GUI, logging, SISR, and artwork consume `Program.Settings` instead of
  independent static fields/parallel dictionaries. Preference resolution moved
  out of launch-request parsing. UI labels, auto-save, global default, toggling,
  watch hints, and configuration location remain. SavePaths also guards
  programmatic population; malformed-load failures occur before launch resources.
- `Configuration/LegacyConfigurationCodec.cs` owns key/value parsing with the
  first `=` separator, whitespace/comments, case-insensitive known keys, per-game
  flags, and duplicate valid keys' last occurrence. It retains comments,
  blank lines, and unknown settings across writes, updates known entries in place,
  and appends new keys. No comment/header multiplication occurs on repeated saves.
- A 4 MiB bound and strict UTF-8/UTF-16/UTF-32 BOM decoding prevent lossy text reads.
  Output is UTF-8. Invalid booleans/keys/lines/encodings are rejected rather than
  silently mutating defaults or truncating user files. Error messages omit invalid
  values. This intentionally tightens malformed-input behavior; valid existing
  configuration and advanced SISR argument text remain compatible.
- `LegacyConfigurationStore` retains path/settings/original lines/content hash.
  New own settings files may be created, while missing/removed/stale/unreadable
  loaded originals are not recreated or overwritten. Writes are flushed to a
  same-directory temp, decoded/parsed and byte-validated, backed up to a staged
  `.bak`, rechecked, and atomically replaced. First creation is a non-overwriting
  move. Save outcomes and temp cleanup errors are explicit. Cross-process stale
  checks are optimistic, not a compare-and-swap; unsupported replacement fails
  closed. No writable-executable-directory fix is claimed until JSON migration.
- Core target validation does not prohibit `=` in paths; the legacy codec rejects
  keys that its format cannot represent instead of emitting a changed target.
  Settings save errors are logged/shown without repeated modal focus-change
  notifications. In-memory edits remain for retry, but a failed save is not claimed
  as persisted. Unknown fields are retained for forward compatibility, not applied.
- Windows SDK 10.0.401 locked restore/build/publish and the full suite passed:
  **185 tests passed, one installed-SISR opt-in skipped (186 total)**. New cases
  cover inheritance/watch-only profiles, clone isolation, casing/equals/Unicode,
  comments/unknown keys/repeated saves, BOM decoding, malformed input, injection,
  limits, first creation/update/backup, denied reads, partial/temp validation/backup/
  replacement failures, unsupported replacement, stale/deleted/new originals, and
  a first-file creation race. Existing launch preference cases now use AppSettings.
- All four Windows desktop checks passed: settings/argument capture, owned-process
  failure cleanup, and native handoffs. WindowsSmoke additionally verifies comment/
  unknown-key retention and malformed existing config rejection without overwrite.
  A retained Windows read handle also forces replacement failure: the smoke check
  finds/closes the PID-bound owned error dialog, verifies no modal reentry, and
  confirms original bytes remain intact. Main-window title alone cannot identify
  an owned modal dialog. Backup/temporary config output is ignored by Git.
  Real controllers/Steam writes were not tested; SISR was not restarted for this
  extraction. One legacy WebClient warning remains; new config/test warnings were
  resolved rather than suppressed.

### Phase 3A implementation and verification (2026-10-07)

- `Configuration/AppPaths.cs` selects `%LOCALAPPDATA%\sBridge\config.json` and
  `logs\sBridge.log`; startup/UI saves/logging no longer require a writable
  executable directory. State is per Windows user and shared across executable
  copies. Only the isolated desktop harness uses `SBRIDGE_TEST_DATA_DIRECTORY`,
  an absolute Windows test-path override. No Linux paths enter product settings.
- `JsonSettingsCodec` defines schemaVersion 1 with required scalar settings,
  typed integration/watch profiles keyed by existing legacy targets, optional
  migration provenance, and JSON extension data. Strict size/type/duplicate/
  Unicode validation rejects unsupported/corrupt state without overwriting it.
  Unknown JSON root/profile/migration fields survive saves. JSON can represent
  `=` paths; UUIDs/providers/new CLI are deliberately not introduced yet.
- `WindowsSecretProtector` uses current-user DPAPI (UI forbidden, no machine-wide
  flag) and a versioned application entropy context. No dependency/package was
  added. The protected credential is embedded in the same atomic JSON document,
  avoiding config/secret split-commit races. Only the in-memory model/UI input
  contains the clear key. The textbox is masked. Unchanged keys reuse ciphertext;
  edited/cleared keys are explicit. DPAPI native/input buffers are freed/cleared.
- `JsonSettingsStore.LoadOrMigrate` treats existing JSON as authoritative. If it
  is absent, valid beside-executable `.cfg` settings are imported once (or defaults
  created when no legacy file exists); path/arguments/global/per-game/watch/key
  choices remain. Source path/hash/time are recorded. The original `.cfg` and its
  backup are not renamed, rewritten, or deleted. Comments/opaque legacy keys remain
  in that original, not copied into JSON or applied as new settings. Existing JSON
  never falls back to legacy/defaults on schema, I/O, or decryption failure.
- Migration rechecks source bytes and aborts if they change before commit. Secret
  protection, partial write, or validation failure leaves the source intact and
  retryable. First creation cannot overwrite a newly appeared JSON file. Save
  validates/decrypts temp bytes, checks exact output/key, stages an encrypted JSON
  backup, rechecks snapshot hashes, and uses atomic replacement. Stale/deleted/
  denied/unsupported writes fail closed. `ConfigurationFileOperations` is the
  shared bounded file-I/O seam for legacy/JSON stores. Optimistic checks still do
  not merge arbitrary external concurrent edits or provide an OS compare-and-swap.
- Legacy files/backups may still contain plaintext credentials for recovery;
  migration does not silently redact them. Users may secure/remove those originals
  after verifying import if old-version recovery is no longer needed. DPAPI blobs
  cannot be transferred to another Windows user/profile merely by copying JSON.
  A second executable copy's differing legacy file is not auto-merged once shared
  JSON exists. Schema/property and credential errors are surfaced before launch.
- Windows SDK 10.0.401 locked restore/build/publish passed. New decision tests cover
  JSON shape/version/duplicates/required fields/typed modes, extension preservation,
  path equals/Unicode, key changes/clearing/reuse, encryption/decryption failure,
  non-destructive/repeat migration, unsupported existing JSON precedence, denied
  reads, source changes, partial/temp write failures, backup/replace/unsupported
  replacement, stale/deleted originals, and first-creation races. Native Windows
  checks verify DPAPI roundtrip/tamper rejection, protected config/backup content,
  real default paths, absolute override validation, and read-only legacy migration.
  The final full suite passed **222 tests, one installed-SISR opt-in skipped
  (223 total)** under the 45s hang watchdog. Pure migration tests use a labeled
  fake protector; real DPAPI checks run only on Windows in the native collection.
- The published app passed all four isolated desktop scripts: JSON migration/
  profile preservation and unchanged legacy bytes, unsupported schema rejection
  without legacy fallback, locked-save modal reporting without reentry, custom
  argument forwarding, external-process protection/finally cleanup, and two-hop
  session monitoring. Harness logs/config reads now use isolated Data/config.json
  and Data/logs paths rather than modifying normal LocalAppData. No real user
  settings, live Steam data, controller drivers, or installed SISR were changed
  by these harnesses; commercial-game/controller validation remains outstanding.
- Logging remains the small existing text logger relocated to LocalAppData; full
  retention/structured diagnostic UI/cache/managed SISR config directories remain
  later work. One legacy WebClient warning is tracked. No release/CI automation
  or settings-reset/import-selection UX was added in this slice.

### Phase 3B implementation and verification (2026-10-07)

- `Core/Game.cs` introduces immutable stable UUID/name/provider/provider-ID/
  explicit launch kind/target/argument-token/hint/install metadata. Provider strings
  are independent from launch kind (packaged applications can be packaged Win32,
  not only UWP). Current imports register `win32` and `xbox`; this is not full
  IGameProvider discovery/launch service implementation.
- The registry lives in the optional schema-1 `games` map of config.json, instead
  of splitting registrations/profiles across two independently committed files.
  Pre-3B schema-1 files without that map load unchanged; subsequent saves include
  it. Required game fields, UUIDs, kinds, Unicode/text, and duplicate registration
  identities are validated. Unknown game/root/profile fields survive updates.
- GameCatalog preserves UUIDs for matching provider identity/kind/exact args,
  including Windows case/slash conventions; rename/hint/install metadata can be
  refreshed without new UUIDs. Argument variants (including absent vs empty) get
  distinct IDs. Win32's default provider ID is the absolute path; relocation cannot
  be inferred from a display name, but editing an existing UUID's target keeps its
  UUID. No Steam AppID is derived from a sBridge UUID.
- New registrations copy legacy target preferences once when no choice is supplied;
  rediscovery retains an existing UUID profile. Explicit custom import choices
  apply to that ID. Automatic inherits global, not an unrelated old target override.
  Legacy target profiles remain for old shortcuts. Catalog/profile registration is
  one JSON transaction; failure rolls back those in-memory changes and no VDF ID
  entry is constructed. A committed registration survives a failed Steam write,
  enabling a stable-ID retry. This is not an atomic JSON+Steam-file transaction.
- `GameLaunchCommand` dispatches strict `launch <D-format nonempty UUID>` before
  legacy parsing. Malformed/unknown IDs never fall through to COM or SISR startup.
  The stored explicit kind/arguments/hints/install information feeds the same STA
  launcher, session evidence, monitor, and owned SISR cleanup. Only two command
  tokens are accepted; override argument syntax is not added. Legacy CLI remains.
- New Win32/packaged UI imports register/save first, then construct short ID
  LaunchOptions. UI per-game controls/status/duplicate scans resolve ID commands
  to UUID profile/actual target; they do not create a bogus override for 'launch'.
  Missing registrations show an error status and disable per-game editors.
  Existing shortcuts/UWPHook migrations stay in legacy format, and reading UI or
  launching a legacy shortcut does not auto-register/rewrite it. Removing a Steam
  shortcut retains the local game/profile registration for manual launch/retry.
- `SteamShortcutBuilder` extracts unchanged entry construction into testable logic:
  bridge Exe/StartDir quoting, CRC32/AppID, field order/flags/tags/icon behavior
  remain. Only new LaunchOptions use ID commands. Stored IDs/artwork and current
  first-existing-account import/removal scope are not broadened.
- Windows build/publish, tests, and all five isolated desktop checks passed. ID
  smoke captures stored args (spaces/empty/quotes/backslashes/tabs), verifies an
  ID-disabled choice wins over a conflicting enabled legacy target, and confirms
  unknown-ID failure before activation/integration without registry modification.
  Catalog tests cover rediscovery/rename/variants/profile isolation/clone/collision/
  rollback; persistence tests cover optional old schema, restart, corruption,
  unknown game fields, and unchanged Steam identity fields. Native argument capture
  checks the decoder used to tokenize custom UI arguments.
  The final full suite passed **241 tests, one installed-SISR opt-in skipped
  (242 total)** under the 45s hang watchdog on Windows SDK 10.0.401.
- The ownership smoke exposed concurrent initial JSON creation by two executable
  copies. Startup now validates/loads the winning JSON after a non-overwriting
  create race; corrupt/unsupported winning data still fails safely. A regression
  verifies no overwrite or legacy merge. Nested saves during settings-error modal
  handling are suppressed so a failed registration cannot secretly commit while
  its error dialog is open.
- Full Steam import/readback, packaged COM activation, protected/service-launched
  commercial games, and controller/profile checks remain outstanding. ID launching
  depends on this Windows user's registry; moving executable files does not move
  the library to another account. New fields are retained by schema-1 older readers,
  but binaries predating ID dispatch cannot launch new ID shortcuts. One legacy
  WebClient warning remains; no extra packages/CI/release/UI library rewrite added.

### Xbox/Win32 provider boundary and discovery (2026-10-08)

- `Providers/IGameProvider.cs` defines discovery records and synchronous launch
  evidence. Xbox and Win32 adapters route by explicit request kind; discovery does
  not allocate persistent UUIDs. Registration/persistence, Steam edits, SISR
  lifetime, and session decisions remain owned by existing orchestration services.
  Custom Win32 games remain user registrations rather than drive-wide scans.
- `AppManager.cs` is replaced by nullable-aware provider sources, explicitly
  included in the application/test projects. Win32 retains shell/UAC launching,
  BCL ArgumentList, cwd, and initial observation handles. Packaged activation stays
  on the STA entrypoint with an explicit apartment guard; COM uses the native
  HRESULT signature, checks failures, and releases its activation object. The
  existing two-second foreground delay remains after initial handle retention.
- `WindowsXboxDiscovery` uses a unique temporary BOM UTF-8 PowerShell script and
  structured JSON for Unicode/pipe-containing names, AUMIDs, executable hints,
  install directories, and warnings. Start-menu names, Appx manifests, and a
  single MicrosoftGame.Config executable hint are used; inaccessible metadata is
  reported without recursive filesystem searches or guessed executable selection.
- `DiscoveryProcessRunner` drains stdout/stderr concurrently with byte limits,
  a 60s discovery budget, cancellation, and bounded retained-root cleanup. It
  never terminates unrelated processes or a process tree. The settings scan
  awaits asynchronously, offers Cancel, disables import while scanning, and
  retains results on cancellation/failure. Close-during-scan defers closing while
  pumping the UI until command/pipes/temp-script cleanup completes.
- Windows SDK 10.0.401 locked restore, Release build, and publish passed. Full suite:
  **255 passed, two opt-in tests skipped (257 total)** under the 45s hang watchdog.
  New tests cover routing/caller thread/evidence, structured/malformed JSON,
  concurrent pipe draining, size limits, timeout, cancellation with an unrelated
  process left alive, UTF-8/exit codes, and the MTA activation guard. The separate
  opt-in `InstalledPackagedInventoryReadOnlyDiscovery` passed against the actual
  installed Windows Appx inventory with `SBRIDGE_TEST_PACKAGED_DISCOVERY=1`.
- All six isolated published-app desktop scripts passed: the five existing
  migration/argument/ownership/session/UUID gates plus `WindowsDiscoverySmoke.ps1`.
  The new UI Automation smoke verifies a responsive cancel button and bounded
  close-during-scan cleanup with no owned PowerShell remaining or game registration.
  It reads inventory but does not launch packages, edit Steam, or start SISR.
- Actual packaged COM activation, commercial-game handoffs, Steam import/readback,
  and controller/VIIPER readiness remain outstanding. Installed SISR was not
  restarted for this slice. One legacy WebClient warning remains; three legacy
  files retain temporary nullable opt-outs. No application packages were added.

### Bounded Epic provider (2026-10-08)

- Epic `.item` manifest and launcher-protocol concepts are adapted from
  [Luke1505's PR #1](https://github.com/jxsparrou/controller-bridge/pull/1), reviewed
  head `ce213d5dcac8aaa11bbaeb202387070c91ce09db`. This is a new typed provider
  implementation; the PR remains unmerged and its Steam/account/artwork changes
  are not imported with it. Contributor credit is retained in source and README.
- `EpicManifestCodec` uses System.Text.Json DTOs, rejects duplicate properties,
  retains Unicode display names, and captures full namespace/catalog-item/app-name
  identity plus absolute install/contained executable evidence. Incomplete,
  explicitly non-executable/non-application, and main-game-linked DLC entries are
  omitted. Unknown fields are not interpreted as launcher command arguments.
  Conflicting install/executable metadata for one identity is omitted, not picked
  by enumeration order. No recursive executable search or name stripping occurs.
- `WindowsEpicDiscovery` reads the common and per-user manifest directories on an
  async worker, with cancellation, 1 MiB/file, 4096-manifest, and 30s cooperative
  budget limits. Missing installation is a normal empty result; malformed/denied
  files and directories emit warnings. Discovery is read-only. The new Add Epic
  Games tab shares responsive scan/cancel/close cleanup and transactional UUID
  registration/Steam imports with packaged discovery. Existing account scope and
  loaded-document Steam save safety remain in force.
- `GameLaunchKind.EpicLauncher` and the request's explicit Epic kind preserve URI
  targets without legacy slash normalization or accidental COM/Win32 routing.
  Stored identity/target mismatches and extra argument tokens fail during game
  validation/JSON load before SISR or activation. Registry/profile persistence
  remains schema-1's single atomic document. Earlier binaries that recognize the
  games section but not the new enum reject Epic-containing settings safely;
  copying an older binary back does not provide Epic launch compatibility.
- URI shape is `com.epicgames.launcher://apps/<namespace>%3A<item>%3A<app>?action=launch&silent=true`.
  Full catalog identity is escaped as one component with strict identity validation.
  Format cross-check: [Playnite EpicLauncher.cs at 2280c953](https://github.com/JosefNemec/PlayniteExtensions/blob/2280c95383c302c4f1276f7acb6ec234f3454c31/source/Libraries/EpicLibrary/EpicLauncher.cs).
  This is corroboration of protocol shape, not installed Epic runtime validation.
  The PR's `args=` behavior is unverified; extra sBridge arguments are rejected.
  Epic's own launch configuration remains responsible for its default/custom args.
- Activation uses Windows shell protocol dispatch, disposes any returned launcher
  handle, and supplies PID-zero evidence. It never adopts/kills Epic Launcher.
  The existing monitor discovers the real game using executable/install/watch
  evidence, rejecting prelaunch/ambiguous candidates. A manifest that names a
  bootstrap may require a watch override naming the actual game.
- Windows locked restore/Release build/publish and full suite passed:
  **270 passed, two opt-in tests skipped (272 total)** with the 45s hang watchdog.
  Fifteen new tests cover canonical identity/URI escaping, unsupported arguments,
  stable UUID/profile rediscovery, typed/duplicate/invalid manifest JSON, BOM and
  Unicode, path boundaries, omitted DLC/incomplete installs, read-only discovery,
  conflicting installations, cancellation, provider evidence, and JSON restart.
  A native unique-HKCU-protocol test verifies URI argv plus a controlled
  launcher -> bootstrap -> game session starting from PID-zero evidence; the game
  is found after its launcher exits. The association and owned probes are removed.
- All six isolated published-app smoke scripts passed. Discovery smoke now checks
  the Epic UI against read-only local manifest inventory as well as packaged
  responsiveness/cancel/close. These checks do not activate installed Epic games,
  edit real Steam data, or start installed SISR. Epic authentication/actual URI
  handling, real Epic/EAC handoffs, Steam import/readback, and controllers remain
  user-assisted validation. The existing WebClient warning is still tracked.

### Selected Steam accounts and safe first-file creation (2026-10-08)

- Account-name and first-file concepts are adapted with credit to Luke1505's
  [PR #1](https://github.com/jxsparrou/controller-bridge/pull/1). `SteamAccounts.cs`
  discovers nonzero numeric userdata directories from the configured Steam root,
  including accounts without a shortcut file. It uses a bounded, strict UTF-8
  text-VDF reader for login-user display names (4 MiB, depth 32, 100,000 elements),
  supporting quotes/backslash escapes, nested unknown maps, and comments. Invalid
  name metadata falls back to account-ID labels without changing source files.
- The Steam Accounts tab persists explicit checkboxes in optional schema-1
  `selectedSteamAccountIds`. Old JSON starts with no selection; there is no
  first-account or empty-means-all fallback. Unknown/unavailable/ambiguous selected
  IDs block import/migration rather than silently reducing or broadening targets.
  UI population retains its save guard and displays unavailable saved choices.
  Imports resolve fresh account inventory. Older binaries may preserve the JSON
  field as extension data but do not enforce its scope; use the current app.
- Xbox/Epic/custom imports register UUID games once, then independently load,
  deduplicate, and save each selected account. Global scan status is informational
  and no longer prevents adding a game to another account. Deduplication is scoped
  to each account's modern ID or equivalent legacy bridge launch arguments.
  A failed account does not overwrite malformed data or prevent other selected
  accounts from succeeding; retry skips successfully saved copies. Committed local
  registrations remain after a Steam failure, as before. This is not one atomic
  transaction across JSON and all Steam files. Legacy artwork jobs are queued
  only after the corresponding account save succeeds, preventing rejected first-
  file imports from creating account folders through artwork side effects. Full
  artwork cancellation/HTTP/failure modernization remains the next slice.
- Existing-file edits retain loaded snapshots, validated temp output, staged
  backups, Steam-running checks, and atomic replacement. Only explicit account
  import can prepare a missing-file snapshot, and only on missing-file/directory
  exceptions—not unreadable or invalid data. First save creates only the config
  folder of an existing numeric account, validates same-directory temp bytes,
  rechecks Steam/absence/account paths, and commits with a non-overwriting move.
  No new backup is created without an original; an existing backup remains intact.
  A concurrent creator wins without overwrite. After success, the snapshot becomes
  an existing-file edit, so deleting it later cannot trigger recreation.
- Linked userdata/account/config paths are rejected/omitted. Account-path rechecks
  remain optimistic: this is not an OS compare-and-swap, cross-process merge, or
  proof against hostile junction swaps after checking. Steam must remain closed.
  Shortcut rows retain per-account associations and now show an Account column;
  removal affects only checked original rows, while UWPHook migration uses the
  explicit selected-account scope. Stored AppIDs/artwork identities are unchanged.
- Windows locked restore, Release build, publish, and full suite passed:
  **285 passed, two opt-in tests skipped (287 total)** with the 45s hang watchdog.
  Fifteen new tests cover text-VDF Unicode/escapes/comments/malformed input,
  account discovery/ID fallback, empty/unknown/ambiguous selection, JSON/clone
  compatibility, first creation/backup/snapshot transitions, denied/malformed
  files, partial/corrupt temp output, Steam restarting, concurrent winning files,
  per-account deduplication/partial failures, and native junction rejection.
- All seven isolated desktop smoke scripts passed. New
  `WindowsSteamAccountsSmoke.ps1` checks actual UI checkbox persistence,
  empty-selection blocking, one existing and one first-file selected account,
  untouched unselected malformed data, backup correctness, and unchanged VDF bytes
  on retry. It uses a synthetic Steam root and temporary app/data copy; it does not
  edit live Steam userdata. The initial run encountered the correct Steam-running
  guard; after the maintainer closed Steam the check passed. The harness now checks
  Steam before opening and posts button clicks so modal dialogs do not block the
  test driver. The six previous desktop gates also passed.
- Real Steam restart/readback, Steam-captured identity fixtures, commercial games,
  controller/VIIPER readiness, and distribution validation remain outstanding.
  No identity/quoting policy changes are made in this slice. The legacy WebClient
  warning remains for the next networking slice.

### Bounded artwork HTTP and UI-owned jobs (2026-10-08)

- `Artwork/` replaces the WebClient/regex/fire-and-forget downloader with typed
  SteamGridDB API envelopes, a shared redirect-disabled HttpClient and owned job
  results. API authorization is per request and restricted to the HTTPS
  `www.steamgriddb.com/api/v2/` origin/path; CDN requests never carry the key.
  URLs require HTTPS/default port, no userinfo/fragment. Redirects fail explicitly.
  Response bodies, keys and signed image URLs are not logged. Luke1505's PR #1
  supplied the motivating rate-limit/optional-artwork concepts; its background
  shared-VDF mutation mechanism is not used.
- HTTP bounds: three attempts for 429/selected transient status/transport failures,
  Retry-After delta/date up to 10s (longer values fail without an early truncated
  retry), 20s/request including response-body reads, 1 MiB API JSON and 16 MiB
  image responses. A two-minute active-job CTS includes all asset lookups/downloads.
  Requests and streams are disposed between attempts. Search keeps the existing
  first-result fallback but prefers an exact title match. Invalid JSON, no match,
  invalid images and per-asset failures yield warnings, not shortcut rollback.
- New imports start with an empty icon. Post-save jobs capture stored AppID,
  Exe, LaunchOptions, name and account—not recalculated IDs or shared VDF objects.
  The settings form owns up to 32 pending jobs and two active slots, reports
  separate artwork status/warnings, and cancels/awaits them when closing alongside
  scan cleanup. HTTP/image work runs off the UI thread. Cancelled queued jobs do
  not start new requests; close remains responsive until resources are released.
- PNG/JPEG signatures/dimensions are inspected before native decoding, with
  16-million-pixel/16384-edge limits. `WindowsArtworkImages` validates/forces GDI+
  decoding; icon conversion writes through a bounded stream and produces actual
  PNG bytes. No failed conversion writes original bytes under a fake extension.
  Grid names retain the captured AppID and legacy suffixes; extensions come from
  content, never URL guesses. Writes stage same-directory files, flush, recheck
  account/config/grid links and VDF availability, then move without overwriting.
  Existing art is retained; explicit repair remains separate work.
- Only a validated new icon result reaches `SteamArtworkIconUpdater`, on the UI
  orchestration side. It reloads the scoped account document, requires an exact
  unique stored identity and unchanged original icon, then uses normal snapshot/
  backup/Steam-running/atomic repository saves. Changed selection, icon, identity,
  deleted/malformed files or ambiguity prevent the update. Unrelated fresh edits
  survive. The UI reloads documents after successful icon saves so old snapshots
  are not silently reused. Grid/metadata checks remain optimistic, not a proof
  against cross-process filesystem races.
- Windows locked restore, Release build/publish passed with **zero warnings**;
  the legacy WebClient warning is eliminated. Full suite: **311 passed, three
  opt-in tests skipped (314 total)** under the 45s watchdog. Twenty-six new
  default tests cover typed/Unicode search, header separation, retry/status/date/
  backoff cancellation, request/job budgets, declared/chunked byte limits, URI
  restrictions, invalid/no-match/image responses, partial disk failures, image
  headers/pixel bounds, stored-ID staged artwork, untouched existing art, changed
  icons, Steam running, deleted files, ambiguity and preservation of fresh edits.
- Native image coverage loads the actual Release application's processor and
  installed .NET 10 Desktop assemblies, keeping the main test project portable.
  It verifies real PNG decoding, JPEG-to-PNG icon conversion and corrupt-payload
  rejection. The separate opt-in `SBRIDGE_TEST_ARTWORK_UI=1` check passed: isolated
  STA settings with injected hanging HTTP confirms 32 pending/two concurrent
  jobs, overflow warning, close-time cancellation and no fabricated Steam config
  folders. No real key/network or normal LocalAppData/userdata is used.
- All seven published-app desktop smoke scripts passed. No live Steam readback,
  actual API/CDN downloads or commercial-game/controller validation is claimed.
  Existing-art repair, user-selected search matches, WebP/animated assets and
  long-term caching remain separate enhancements. No new application packages
  or GUI framework migration were introduced.

### Opt-in managed SISR startup, readiness and status (2026-10-08)

- Optional schema-1 `managedSisrStartup` and a Global Settings checkbox enable a
  bounded managed startup policy. Existing settings default false: raw legacy
  SISR args/cwd/environment behavior remains available. SISR-disabled launches
  still acquire no integration resources. Full per-game controller profiles and
  Steam-context enforcement are not added by this slice.
- `SisrManagedStartup` creates a unique owned LocalAppData session directory with
  `startup.json` and `SISR.log`. Controlled cwd removes accidental neighboring
  JSON/YAML/TOML config discovery. Config uses Kong flag-name JSON keys (including
  `api.listen_address`, `log.file`, `window.fullscreen`, `window.show`), not the
  read-only API config object's PascalCase subfields. Resolver source is pinned to
  [Kong v1.16.1](https://github.com/alecthomas/kong/blob/v1.16.1/resolver.go), the
  dependency in SISR v0.6.1. Advanced tokens are decoded/preserved; conflicting
  config/API/log flags (including log alias lf) are rejected before startup.
- Managed startup owns random IPv4 loopback binding and per-session log/config
  environment values, preserving all Steam identity/controller/other context.
  Generated JSON contains hidden/non-fullscreen window defaults, not passwords
  or raw advanced args. Relative advanced paths resolve in the managed cwd.
  Config/logs remain for diagnostics; retention is the next bounded slice.
  No first-run marker creation, profile mutation, CEF enabling/restart, VIIPER
  spawning, or global input reset is implemented by sBridge here.
- `OwnedSisrHttp` is shared by readiness and shutdown. Every connection pool is
  bound to one retained process/loopback endpoint; Windows ownership is verified
  after socket connection before sending any HTTP. Redirects/proxies are disabled
  and responses capped at 64 KiB. `WindowsSisrStatus` probes up to eight owned
  listeners, version checks v0.6.1+ compatible v0.6.x, and reads Steam, cached
  VIIPER, devices (null is zero; <=256), and effective configuration diagnostics.
  Only counts/flags/whitelisted controller type/canonical version are logged;
  passwords, paths, device identities and raw bodies are not retained in summaries.
- Managed launch waits at most 15s with 250ms polling and 2s per request before
  activation, on the existing synchronous STA orchestration thread. A supported
  owned API plus readable Steam status gates activation; early exit, timeout or
  unsupported version is an error. Cleanup remains in Main's inner finally and
  runs before the exception dialog. Missing Steam/CEF/marker, cached VIIPER
  disconnection, setup mode or no devices emit status warnings; absence of a
  controller is not a launch failure. API reachability does not prove actual
  input enforcement/readiness and does not guarantee SISR remains live later.
- Locked Windows restore/Release build/publish passed with zero warnings. Full
  suite: **324 passed, four opt-in tests skipped (328 total)** under the 45s
  watchdog. Thirteen default new tests cover delayed readiness, no-controller
  success, unsupported/exit/cancel/timeout failure, owned cleanup before activation,
  safe summaries, optional typed JSON/clone policy, conflicting flags, token/cwd/
  environment/config semantics, native owned status reads, unsupported versions,
  stale socket ownership before HTTP, malformed/oversized response rejection.
- Both installed-SISR tests passed separately with `SBRIDGE_TEST_SISR_PATH` set:
  existing no-Steam API lifecycle and new managed no-Steam readiness/configuration.
  The latter proved generated JSON overrides fullscreen=true defaults, hidden UI,
  effective controller type, owned logging and graceful quit on actual v0.6.1.
  A non-resolving VIIPER address prevents VIIPER auto-start; Steam profiles,
  device readiness/physical input and initial Steam setup are not validated.
- All eight published-app desktop smoke scripts passed. New
  `WindowsManagedSisrSmoke.ps1` runs a controlled API-capable stand-in from local
  staging: config/cwd and readiness precede activation, forwarded game args remain
  intact, normal cleanup uses API quit, unsupported API blocks the game and stops
  the owned root before the exception dialog. Its registry/game/Steam state is
  isolated; it does not launch installed SISR. The seven prior desktop gates pass.

### Bounded logs and read-only diagnostics (2026-10-08)

- `Diagnostics/BoundedLog` replaces unbounded appends with a 2 MiB active bridge
  log plus three capped archives. Oversized prior logs retain bounded complete-line
  UTF-8 tails; individual oversized messages are omitted rather than partially
  leaking credentials. UTC timestamps, bridge correlation IDs and PIDs structure
  the existing compatible text messages. Named-mutex coordination protects
  concurrent writers/rotation; failed/blocked writes remain non-fatal and increment
  a visible count. Linked diagnostic paths are rejected; checks remain optimistic.
- Current API-key values and recognized password/secret/token/API-key options and
  Bearer values are redacted; CR/LF are escaped into single-line records. This is
  targeted local-log redaction, not a claim every arbitrary positional argument is
  public. Local logs can retain paths/titles/ordinary args. Copy Diagnostics never
  exports free-form logs, raw SISR logs or arbitrary exception/server text.
- Managed sessions now own `.sbridge-session` marker handles and atomic bounded
  `summary.json` snapshots for starting/ready/ended/incomplete phases. Summaries
  contain only canonical version, typed Steam/no-Steam/VIIPER/device-count state
  and whitelisted controller type, not paths, IDs, passwords or advanced args.
  Read-only latest-summary lookup is bounded and skips malformed/unrecognized data.
- Retention keeps eight completed sessions and caps completed third-party SISR
  logs at 4 MiB. It operates only on marked, unlocked, ended, known-content,
  non-linked session directories. Active locks, starting/ready/incomplete states
  (possible orphan roots), older unmarked folders and unknown content are retained.
  Active SISR log files are not trimmed while the third-party process writes them.
  Cleanup runs before new managed sessions and after owned shutdown; it never
  recursively removes unknown data or stops processes. Third-party raw log content
  has different privacy properties from the generated safe summary.
- The new Diagnostics tab collects off-thread with a close-time completion guard,
  displaying config/logging health, Steam/account/library counts, SISR mode/path
  existence/external instance count, API-key-presence flag and last managed status.
  Status is explicitly historical, not a live API/controller-health claim. Refresh
  does not start SISR, contact the network, mutate Steam/config, or create resource
  folders. Copy is user initiated and contains only typed safe fields, omitting
  account/game/device identities, usernames, paths, argument text and credentials.
- Locked Windows restore/Release build/publish passed with zero warnings. Full
  suite: **333 passed, four opt-in tests skipped (337 total)** under the 45s watchdog.
  Nine new tests cover capped rotation/UTF-8 tails/entry limits, concurrent writers,
  redaction/newline injection, real denied-write reporting, active/unfinished/
  unknown/unmarked retention protection, completed log limits, safe poisoned-string
  summaries and read-only missing/malformed/oversized lookup behavior.
- All nine published-app desktop smoke scripts passed. New
  `WindowsDiagnosticsSmoke.ps1` verifies responsive read-only refresh, safe payload,
  enabled copy action and unchanged settings/Steam/SISR resources in temporary
  roots. It does not replace the user's clipboard. The eight previous launch/
  ownership/config/account/discovery gates pass with the bounded logger and marker
  lifetime changes. Both installed-SISR no-Steam tests also passed separately,
  verifying normal owned status/shutdown and session-handle release.
- Normal LocalAppData/live Steam/controller state is not used by the desktop
  harnesses. Full Steam/profile/controller validation remains outstanding; a safe
  status snapshot does not establish input enforcement or real-game compatibility.

### Windows CI and distribution package automation (2026-10-08)

- `.github/workflows/windows.yml` adds push/PR/manual Windows CI: stable .NET 10,
  locked restore, warning-as-error Release build, default portable/native tests
  with TRX and the 45s hang watchdog, development publish, both ZIP modes and
  non-desktop package smoke. Actions are pinned to verified commit SHAs, token
  contents permission is read-only, and development packages/test results are
  uploaded as artifacts. No release publishing or installed/live integrations
  are run automatically. Hosted execution awaits committing/pushing these changes.
- `package.ps1` stages fresh framework-dependent and self-contained win-x64
  payloads, keeping WinExe/WinForms and every required file together; no trimming,
  single-file bundling or external SISR inclusion. It stamps explicit version/
  optional revision, records SDK/runtime metadata and SHA-256 payload lengths/
  hashes, includes GPL/package instructions/exact runtime-pack license notices,
  and creates sorted, fixed-timestamp ZIPs plus checksum sidecars. Default
  `0.0.0-dev` and CI `0.0.0-ci.<run-number>` are development labels. This is
  repeatable scripted packaging, not cross-SDK bit-identical output or release
  provenance for an unreviewed dirty checkout. Build.ps1's existing app output
  contract remains framework dependent.
- Windows SDK 10.0.401 successfully published both variants from the UNC
  checkout. Self-contained ZIP is approximately 48 MiB and includes .NET 10.0.12
  Core/Desktop runtime files and notices. Both packages passed manifest/checksum/
  runtime-layout checks and extracted, isolated SISR-disabled launches. The
  self-contained ZIP additionally passed interactive settings migration/save,
  unsupported-schema rejection and sharing-violation reporting. Explicit invalid
  DOTNET_ROOT/X64 values exercise app-local hosting but do not remove registered
  SDK-machine fallback, so clean-machine proof is still pending.
- Local workflow-equivalent locked restore, warning-as-error Release build and
  test/TRX generation passed with **333 passed, four opt-in tests skipped (337
  total)** and zero warnings. Actionlint v1.7.7 validated the workflow. Package
  scripts were checked on Windows PowerShell 5, including WSL UNC ProviderPath
  handling; native runtime and GUI dependencies came from the packaged payload.
- `docs/RELEASE_CHECKLIST.md` separates local/CI automation from user-assisted
  gates. Intervention is required for the first hosted run (commit/push), a fresh
  Windows VM/Sandbox without .NET, chosen release version/signing credentials,
  and live Steam/game/controller validation. Nothing was committed, pushed,
  signed or released in this slice. No driver/runtime installation or normal
  Steam/LocalAppData writes are performed by package smoke checks.

**Next work:** hosted CI and clean-machine gates need maintainer intervention;
registered-library editing can be a separate bounded coding slice. Full Steam/
controller profiles and real-world setup/input validation remain planned. Keep
release automation separate from claims of validated controller gameplay.

### Clean-machine preparation and completed guest verification (2026-10-08)

- The maintainer is monitoring the Windows desktop remotely. Read-only host
  inventory confirmed Windows 11 Pro and Windows Sandbox feature install state 2
  (disabled), with no Sandbox executable available. Enabling the optional feature
  and any required reboot are user/admin intervention; neither was performed by
  the agent.
- `tests/PrepareWindowsSandbox.ps1` prepares a unique local Windows temp bundle
  containing only the self-contained ZIP/checksum and package/desktop/guest smoke
  scripts. It generates a `.wsb` mapping read-only inputs and a separate writable
  results folder, avoiding the UNC checkout and normal LocalAppData/Steam data.
  Networking/clipboard/audio/video/printer redirection are disabled. Preparation
  does not launch Sandbox or change system features.
- `WindowsSandboxVerify.ps1` runs in the mapped guest, checks for installed .NET 10
  before testing, invokes package smoke plus desktop checks, and writes
  `result.json`/`transcript.txt` for host collection. Windows PowerShell parsing and
  generated XML/input-scope checks passed locally; five input files were staged.
  **No clean-machine success is claimed until the guest runs and reports success.**
- After the maintainer enabled Sandbox and reconnected, Windows reported the
  optional feature enabled, hypervisor present, and vmcompute/hns running. Both
  the prepared `.wsb` launch and a plain Sandbox launch failed to initialize with
  `Exception of type 'System.Exception' was thrown.` No guest transcript/result
  was produced; Windows event logs did not identify a specific startup cause.
  This establishes a host Sandbox blocker, not a package failure. Sandbox app
  repair or another clean Windows VM needs user intervention before this gate
  can proceed. No host service/DNS/Hyper-V configuration was changed.
- The maintainer repaired Sandbox, restoring plain launch. Prepared-config
  automated launches still showed initialization/stack guard-page errors; the
  configuration was simplified to required mappings/test command using Windows
  defaults for optional settings. A manual Windows Win+R launch succeeded. Guest
  result: **passed**, no installed runtimes detected, Windows 11 Enterprise Sandbox,
  UTC 2026-10-08T20:46:14.5579035+00:00. Package SHA-256
  `0be5028254ef6600d8327c6df5230f3244dfdd4c9bf32a9324c9ce367807542c`
  matches the host's self-contained ZIP. Payload/launch, GUI migration/save,
  unsupported-schema and sharing-violation checks all passed. Evidence is copied
  to ignored `artifacts/validation/sandbox-2026-10-08`; a safe committed-document
  summary is at `docs/validation/2026-10-08-clean-machine.md` (changes uncommitted).
  The exact package's clean-machine gate is complete; earlier launcher errors'
  cause remains unresolved. No page-file/service/DNS settings were modified.

Product promise: add a game once, launch it from Steam, prepare controller
integration when needed, follow the real game session, clean up owned resources,
and exit. SISR remains responsible for redirection and virtual controllers.

Keep WinForms, minimal dependencies, explicit ownership, and small changes with
buildable checkpoints. Do not introduce drivers, a controller-emulation stack,
telemetry, cloud/accounts, a launcher platform, a Windows service, or a new UI
framework. Work on one bounded slice per implementation session.

## Audited baseline

All five C# files form a single global `partial class Program`:

| Source | Responsibilities |
| --- | --- |
| `Program.cs` | STA entrypoint, CLI interpretation, configuration, logging, SISR lifecycle, launch orchestration |
| `AppManager.cs` | COM activation, Win32 launch, process monitoring, embedded PowerShell packaged-app discovery |
| `SettingsForm.cs` | Nested, hand-built WinForms UI; auto-save; shortcut imports/removal/migration |
| `SteamShortcuts.cs` | Registry/filesystem Steam discovery, shortcut mutation/persistence, AppID calculation, artwork networking |
| `VdfParser.cs` | Binary map/string/Int32 codec |

`build.ps1` invokes the Windows .NET Framework compiler directly, explicitly
listing all five files and assembly references. There are no automated tests,
SDK manifests, dependency lockfiles, CI workflows, or lint/formatter tasks.
The repository is GPL-3.0; preserve existing notices and contributor attribution.

### Compatibility contract

- No arguments opens settings.
- Packaged-game launches: `<AUMID> [executable_hint] [game_args...]`. Argument two
  is reserved for the process hint, even when a watch override exists.
- Custom launches: `"<exe_path>" [game_args...]`, with the executable directory
  as working directory. Existing forward-slash path normalization must be
  considered when migrating legacy keys.
- Per-game SISR/watch keys are case-insensitive, unquoted launch targets.
  `ParseFirstArgument` handles quoted paths; absence of a SISR override means
  inherit, distinct from storing `false`.
- SISR-disabled launching works without SISR installed. Initially retain the
  global enabled default and existing per-game choices during migration.
- Settings auto-save. Preserve `isUpdatingUi` when loading controls. History
  commit `59b2236` fixed initialization events overwriting loaded configuration;
  `8355ef7` added save-on-close.
- Continue recognizing existing `sBridge.exe`, `uwphook-bridge.exe`, and UWPHook
  shortcuts. Keep the legacy CLI alongside `launch <game-id>` during migration.
- Legacy config/log files live beside the executable. Migration must read that
  location rather than assume the working directory contains user settings.
- Preserve stored Steam shortcut IDs and artwork associations. Stable sBridge
  UUIDs are separate from Steam identity. Do not rewrite existing shortcuts merely
  because the application can now launch by UUID.
- Binary shortcut files use UTF-8 NUL-terminated strings, type tags, and two
  trailing `0x08` bytes on root save. Preserve unrelated entries and fields.

Compatibility does not require retaining blanket process killing, silent data
loss, or arbitrary account targeting. Replace those deliberately with tested
behavior and explain any user-visible change.

## Findings and priority

The following are verified source-level defects or limitations, not claims of
Windows gameplay reproduction. Line references describe the audited baseline.

### P0: user-data integrity

1. **Parse failure becomes an empty shortcut library.** Both imports catch read/
   parse failures and construct an empty root (`SettingsForm.cs:1056-1072`,
   `1315-1330`). A subsequent save can discard unrelated shortcuts. Distinguish
   a genuinely absent file from an unreadable or malformed existing file.
2. **Non-transactional writes.** `SteamShortcuts.cs:159-178` overwrites `.bak`
   then writes directly to the original. No temporary-file validation, stale-read
   check, or atomic replacement protects an interrupted/concurrent write.
3. **Misleading success.** `SaveSteamShortcuts` catches failures and returns
   `void`; UI callers can report success after an error. Return explicit results
   per account and refresh from successfully committed data.
4. **Codec can desynchronize on unsupported tags.** `VdfParser.cs:29-59` reads
   the key but neither consumes nor rejects an unknown value type. Truncation,
   root/trailer validation, nesting and size bounds need explicit handling.

### P1: session and redirector lifecycle

1. **Unowned termination.** Startup/normal cleanup reset forced Steam Input and
   kill all named SISR/VIIPER processes (`Program.cs:104-145`, `157-197`). The
   launched SISR process handle is discarded. Concurrent bridge launches can
   terminate each other's integration.
2. **Exceptions skip cleanup.** Cleanup is after monitoring rather than in
   `finally`; launch/monitor errors can leave newly started SISR running.
3. **No startup readiness.** The game launches without checking SISR startup,
   Steam integration, or VIIPER/device readiness.
4. **Single handoff.** `AppManager.cs:139-247` permits one replacement only,
   and only after an initial exit within two minutes. PID zero ends monitoring.
5. **Arbitrary candidate selection.** Replacement uses `candidates[0]`; process
   age, path, relationships, helpers, ambiguity, and PID reuse are not modeled.
6. **Argument boundaries lost.** `Program.cs:74-86` joins parsed arguments with
   spaces. A single quoted argument containing spaces can become multiple child
   arguments. Correct forwarding needs Windows command-line regression coverage.

### P2: persistence, discovery, UI, and networking

- Steam imports target the first discovered existing VDF, not a selected account.
  Accounts without a shortcut file cannot receive their first import; backup
  copying also assumes an original file exists.
- `CloseSteam` kills processes. Removal/migration can proceed after an attempted
  close without rechecking that Steam actually stopped.
- Configuration rewrites require a writable executable directory and swallow
  errors. Loading malformed boolean values can overwrite defaults with `false`.
  SteamGridDB credentials are plaintext. Logs lack session identity and bounds.
- Scanning is synchronous on the UI thread. PowerShell reads stdout and stderr
  sequentially before `WaitForExit(60000)`, so blocked reads can defeat that
  timeout. All scans share a fixed temporary script name.
- Process objects are not consistently disposed; polling and waits block.
- Artwork uses fire-and-forget ThreadPool jobs, `WebClient`, regex JSON parsing,
  and no rate-limit policy. Process exit can interrupt downloads. Icon fields
  can reference missing files; failed conversion writes original bytes under a
  `.png` name.
- UWPHook migration changes executable/start directory while retaining stored
  AppIDs. Preserve that baseline intentionally until Steam fixture/runtime checks
  establish whether an identity/artwork migration is needed. UI prose still
  mentions the old bridge name; README's Save & Close setup instruction is stale.

**AppID quoting is unresolved, not a confirmed baseline bug.** Current creation
hashes a quoted bridge executable and writes that quoted value. PR #1 changes
both. Do not assume either interpretation without fixtures captured from Steam.

## PR #1 review and contributor credit

[PR #1](https://github.com/jxsparrou/controller-bridge/pull/1), by **Luke1505**,
adds Epic support, account targeting, handoff fixes, and artwork changes. Review
covered its full diff, commit history, description, comments, and reviews. At the
audit date it is the only open PR, with no submitted or inline reviews.
The contributor reports daily use across two accounts and Epic/EAC titles.
That is valuable evidence, not a substitute for repository regression tests;
the described deterministic real-process harness is not included in the diff.

| Idea/change | Disposition | Destination/condition |
| --- | --- | --- |
| Epic `.item` discovery and launcher protocol | Rewrite during modernization | `EpicProvider`; typed JSON; verify identity/URI/argument semantics on Windows |
| Account names from `loginusers.vdf` | Rewrite during modernization | Steam account service; small text-VDF reader, not regex |
| Selected-account paths even without a VDF | Adapt early as a bounded bug fix | Safe file creation/backup logic after test foundation |
| Multi-hop handoffs and PID-zero startup search | Rewrite during modernization | Session state machine and regression scenarios |
| Install-path and hidden-path executable hints | Adapt | Scored, bounded signals; never proof by themselves |
| Shorter later-exit search | Adapt intent | Distinguish startup/handoff from confirmed-game exit instead of counting exits |
| AppID and Exe/StartDir quoting changes | Postpone | Steam-created fixtures, paths with spaces, stored-ID/artwork migration policy |
| Force controller AppID at startup | Postpone/rework | Use verified session/Steam identity and SISR-supported enforcement; do not guess from AUMID |
| Rate limits and missing-artwork repair | Rewrite during modernization | Typed HTTP status, bounded retries, explicit repair jobs |
| Background artwork VDF saves on refresh | Reject mechanism | All writes go through one coordinated persistence boundary |
| Deduplicated library display | Adapt | Retain per-account associations and account-specific duplicate checks |
| Removal from every account | Reject default | Explicit selected-account scope; all-account removal only as a clear user choice |
| No checked accounts means all accounts | Reject default | Empty selection must not silently broaden writes |
| Non-ASCII name stripping | Reject | Preserve Unicode names |
| Regex Epic JSON parsing | Reject mechanism | `System.Text.Json` DTOs |
| Recursive install-directory executable scan | Postpone unless justified | Cache/bound scanning; exclude helpers; handle denied access |

Do not merge the whole PR. Credit Luke1505 and link the relevant PR/commits when
adapting contributions; retain notices when copying source. Particularly useful
history includes `7aade7f` (multi-hop), `d2c65fe` (exit budgets/start times),
`b2caecd` (fresh accounts), and `ab3aca7` (EAC diagnostics/identity/icons).

Residual issues in the reviewed PR:

- Name matching still returns the first candidate and bypasses start-time checks.
- Later handoffs receive only a short search budget. A process already running
  before the previous process exits can fail the later start-time filter.
- Install-directory prefix matching lacks a directory boundary; unreadable
  start times remain eligible, and executable-name fallback is broad.
- Background artwork jobs mutate shared roots and write while Steam may be
  running; the HTTP lock does not make those persistence operations safe.
- Artwork repair recomputes identity using the current bridge path rather than
  consistently using the stored shortcut ID. Extensions can misrepresent bytes.
- Non-Epic forced AppIDs use a launch target, not necessarily the shortcut name.
- Display deduplication loses account scope; duplicate detection is not per
  selected account. Removal count division assumes uniform copies across accounts.

## SISR API/config research

Pinned executable sources of truth:

- [Configuration types](https://github.com/Alia5/SISR/blob/v0.6.1/config/config.go)
- [Config loader](https://github.com/Alia5/SISR/blob/v0.6.1/cmd/sisr/main.go)
- [API specification](https://github.com/Alia5/SISR/blob/v0.6.1/openapi.yaml)
- [API server](https://github.com/Alia5/SISR/blob/v0.6.1/cli/cmd/sisr/api_server.go)
- [Run/cleanup](https://github.com/Alia5/SISR/blob/v0.6.1/cli/cmd/sisr/sisr.go)
- [Quit handler](https://github.com/Alia5/SISR/blob/v0.6.1/api/handler/quit/quit.go)
- [Input enforcement](https://github.com/Alia5/SISR/blob/v0.6.1/input/steaminputbindings/bindingenforcer.go)
- [VIIPER ownership](https://github.com/Alia5/SISR/blob/v0.6.1/input/viiperbridge.go)

Phase 1C implemented owned-process shutdown/version discovery; the managed SISR
slice now adds opt-in generated startup configuration, readiness and read-only
status diagnostics. The constraints below still apply to full profile/controller
integration; not every API is wired into sBridge.

Verified capabilities: JSON/TOML/YAML `--config`; explicit
`--api.listen-address`; version, Steam, device, and VIIPER status; graceful quit.

| Endpoint | Planned use |
| --- | --- |
| `GET /api/v1/version/info` | Version/capability discovery and API reachability |
| `GET /api/v1/steam/status` | Steam identity/integration diagnostics |
| `GET /api/v1/devices` | Connected device diagnostics |
| `GET /api/v1/viiper/status` | Cached VIIPER status |
| `POST /api/v1/viiper/ping` | Explicit connection/status refresh |
| `POST /api/v1/quit` | Graceful shutdown of a proven-owned instance |

Constraints to resolve before implementation:

- Default API binding is `localhost:0`, not the example port 6400. An explicit
  loopback endpoint is possible; handle binding failure without adopting another
  server. Port availability checks alone do not establish ownership.
- API reachability does not guarantee Steam/VIIPER/controller readiness; do not
  require a connected controller merely to launch a game unless the user requests
  that policy.
- The config endpoint is GET-only in this release. Generate startup config; do
  not assume live per-game config mutation is supported.
- `/steam/force-config` accepts an enforcement boolean and uses SISR's own
  identity, not an arbitrary AppID parameter. SISR derives identity from Steam
  environment variables/marker state. Preserve and validate Steam launch context.
- SISR resets forced Steam Input on normal shutdown. It tracks its own spawned
  VIIPER process. sBridge should not independently kill all VIIPER processes.
- The API has no instance-ownership credential in the reviewed specification.
  Use retained process identity plus endpoint-owner verification before mutation.
- Config docs describe merging, but the loader groups candidate files by format
  and discovers files in the working directory. Verify effective precedence with
  the actual supported binary; use a controlled managed working directory.
- A unique API port does not isolate global Steam Input or controller devices.
  Start with a per-user integration lease and explicit external-instance policy,
  not multiple competing redirectors or a background service.
- For external SISR, default to leaving it untouched. Report conflicts and allow
  an explicit reuse policy only after validating compatibility. A fallback kill
  may target only a retained, proven-owned SISR process after bounded graceful
  shutdown failure. Recheck version/capabilities when supporting other releases.

## Target architecture and decisions

Start with folders and explicit composition; introduce only two production
assemblies when extraction makes that useful:

- `sBridge` (`net10.0-windows`, WinForms): Windows integration, provider adapters,
  UI, and the composition root.
- `sBridge.Core` (`net10.0`): domain types, pure CLI/configuration decisions,
  Steam identity/VDF logic, process scoring, and lifecycle state machines.
- Tests target pure logic first; a Windows process harness/integration suite is
  added when lifecycle work needs it. Do not create an assembly per subsystem.

Conceptual folders: `Core`, `Providers`, `Steam`, `Sisr`, `Sessions`, `UI`, and
`Configuration`/`Diagnostics` as needed. The folder layout need not move all
existing files in the build-foundation phase.

- `Game` owns a stable UUID, name, provider identity, typed launch target,
  installation/hints, and references to profile/artwork/Steam associations.
  Keep provider ID, sBridge UUID, and Steam AppID separate. Rediscovery updates
  known games rather than regenerating UUIDs.
- `GameProfile` expresses inherited/explicit Steam Input integration choices and
  structured SISR/process settings. `AppSettings` owns global defaults.
- `IGameProvider` discovers and launches; it returns launch evidence with optional
  initial PID, launch time, hints, and install path. It does not monitor sessions,
  write Steam files, or own redirector cleanup. Custom Win32 discovery can simply
  be user registration. Prefer Xbox/packaged-game terminology over a UWP domain.
- `GameLaunchCoordinator` resolves requests and owns the lifetime:
  resolve -> acquire integration lease -> prepare SISR -> launch -> monitor ->
  cleanup in `finally`. STA-sensitive COM activation needs explicit placement;
  do not assume an async console continuation remains on an STA thread.
- `GameSessionMonitor` consumes Windows process observations; `ProcessMatcher`
  scores immutable snapshots. Track PID plus creation time, evidence confidence,
  ambiguity, helpers, known install boundaries, descendants, and windows where
  available. Inaccessible anti-cheat metadata is unknown, not proof of a match.
- Monitor states distinguish awaiting launch, provisional launcher, handoff,
  active game, exit grace, and completion. Support several handoffs and bounded
  cancellation. Use async waiting plus cancellable observation/retry where needed;
  no ETW dependency initially.
- Steam services separate installation/account discovery, binary shortcut
  persistence, text login-user parsing, and artwork. Choose write targets explicitly;
  never couple UI display deduplication to removal scope.
- `SisrProcessManager` owns a lease/process identity; `SisrClient` speaks a small
  typed, version-aware API; profile service generates supported configs. Avoid a
  wrapper abstraction for every BCL call; inject only useful test seams.

### Persistence and migration design

- `%LOCALAPPDATA%\sBridge\`: versioned `config.json` including the optional atomic
  game registry/profile section, `logs`, and future `cache`/managed `sisr` files.
  Phase 3B keeps game/settings state in one document rather than a separate
  `games.json` transaction. Use `System.Text.Json`; explicit schemaVersion
  and migration decisions; reject unsupported newer schemas without overwriting.
- Migrate beside-executable `.cfg` once after successful validated persistence.
  Leave the original intact, preserve overrides and advanced SISR arguments, and
  make interrupted/repeated migration safe. Do not silently replace unreadable
  settings with defaults. Coordinate game/settings writes and concurrent sessions.
- New shortcuts use `sBridge.exe launch <game-id>`; old shortcuts resolve through
  a compatibility adapter. Existing `.cfg` alone does not contain full game
  metadata: recover registered games from legacy shortcuts when available and
  support legacy launch resolution when no shortcut can be inspected.
- Evaluate current-user DPAPI/ProtectedData versus Windows Credential Manager
  for the SteamGridDB key. A small ProtectedData dependency or native adapter is
  acceptable if justified. Mask secrets, redact diagnostics, and avoid logging
  sensitive arguments. LocalAppData state is per-user even for portable releases;
  executable portability does not imply portable protected credentials.
- Shortcut transaction: read/validate -> detect stale original/Steam running ->
  serialize same-directory temp -> parse and validate expected structure/content
  -> create backup -> replace atomically where supported -> report outcome.
  Recheck before commit, serialize writes per file, and distinguish first creation
  from replacement. Unsupported/malformed files must remain untouched. This is
  not an atomic transaction across multiple Steam accounts; report partial success.
- AppID generation operates on the exact serialized executable identity. Preserve
  existing stored IDs; any quoting/rename/relocation repair requires an explicit
  identity/artwork migration. Artwork failures must not roll back game registration.
- Shared `HttpClient`, typed DTOs, bounded concurrency, cancellation, HTTP status
  handling, `Retry-After`, and image-format validation. Artwork produces results;
  coordinated shortcut persistence commits optional icon changes after reloading
  current data and verifying account/shortcut identity.
- Lightweight structured session logs with retention and UUIDs. Diagnostics cover
  Steam/accounts, SISR version/API, VIIPER, config paths, optional Gaming Services
  and SteamGridDB checks. Copy diagnostics is user initiated and redacted; no telemetry.

## Phases and acceptance gates

Each phase can have several small changes. Keep the repository buildable at each
practical checkpoint and document any temporary tooling transition.

| Phase | Scope | Completion gate |
| --- | --- | --- |
| 0: Baseline | Audit, contracts, PR dispositions, agent guidance; define regression fixtures | Documentation reviewed; characterization coverage starts in 1A before behavior refactors |
| 1: Build foundation | SDK-style .NET 10 WinForms, solution/SDK policy, tests, same executable/icon | Windows restore/build/test passes; legacy settings and SISR-disabled launch smoke checks |
| 2: Domain separation | Game/profile/session types, coordinator, provider launch evidence, Steam/SISR/session boundaries | Existing CLI/GUI behavior preserved; no new providers required |
| 3: Configuration | LocalAppData JSON, protected secret, repeatable `.cfg` migration, stable IDs, new CLI alongside old | Migration/failure/schema/quoting tests; old shortcuts still launch |
| 4: Steam hardening | Account discovery/selection, first-file creation, transactional persistence, AppID policy | Fixture/round-trip/malformed/stale-write/failure tests; selected-account Windows verification |
| 5: Sessions | Async multi-hop states, scoring, timeouts/cancellation, Windows process adapter | Deterministic decision tests and real-process multi-hop Windows harness pass |
| 6: SISR modernization | Supported config/API, ownership, readiness, graceful shutdown, coexistence | Owned/external/error/cancel decisions covered; real SISR/Steam/VIIPER checks |
| 7: Artwork | Typed HTTP/JSON, rate-limit policy, coordinated optional repairs | HTTP failure/429/cancel/image tests; shortcut creation survives all artwork failures |
| 8: Providers | Adapt packaged discovery and Epic PR concepts | Unicode/manifest/identity/URI tests and actual Windows launch/handoff verification |
| 9: UI/diagnostics | Responsive WinForms, Library/Steam/Controllers/Settings/Diagnostics, copy diagnostics | UI startup/inheritance/async/error smoke checks; redaction verified |
| 10: Distribution | Windows CI restore/build/test/publish, self-contained win-x64 portable output | Clean-machine smoke checks; stable manual release process before automated releases |

**Priority adjustment:** after 1A, pull P0 Steam write protection and minimal
owned-process/finally cleanup forward as separate tested slices. Do not wait for
all domain/configuration work to fix them. Conservative parser rejection needs
malformed-input tests before it is used as the write-validation boundary.

### Phase 1A scope (implemented)

1. Add an SDK-style Windows WinForms project and solution with explicit source
   inclusion initially. Avoid root default globs pulling test/generated sources
   into the application. Keep `sBridge.exe`, STA startup, icon, and current UI.
2. Enable nullable analysis and useful implicit usings; address or narrowly
   contain legacy warnings rather than mix a broad nullable refactor into tooling.
3. Add a test project exercising the existing VDF codec through linked source or
   a small extracted unit. Extract CRC/AppID logic without changing its output;
   update the legacy explicit compiler list if retaining that build route.
4. Add VDF supported-format round trips, truncated-input characterization, UTF-8,
   unrelated fields, and known AppID vectors. Capture sanitized Steam-generated
   fixtures before choosing a quoting change. Label known defects explicitly;
   do not encode them as desired behavior or claim unsafe-input handling is fixed.
5. Document real Windows build/test commands and SDK selection. Once verified,
   migrate `build.ps1` to the SDK workflow or document its short transition; do
   not leave two indefinitely divergent build definitions.
6. Run Windows build/tests and focused manual checks. No JSON migration, Epic,
   UI restructure, or monitor rewrite belongs in this slice.

Independent UTF-8 CRC32 characterization vectors (name is `Example Game`, with
`0x80000000` OR'd into the result):

| Exact executable string hashed before the name | AppID |
| --- | --- |
| `"C:\Games\sBridge\sBridge.exe"` | `0x8B0F1B27` |
| `C:\Games\sBridge\sBridge.exe` | `0xCD2CEBFC` |
| `"C:\Program Files\sBridge\sBridge.exe"` | `0x94CFC93E` |

These are independent arithmetic checks, not Steam runtime validation. Fixtures
should additionally cover Unicode names, case, quoted/unquoted paths, relocation,
existing stored IDs, and artwork naming.

## Verification strategy and WSL2

Current supported publish wrapper (framework-dependent), on Windows:

```powershell
powershell.exe -ExecutionPolicy Bypass -File .\build.ps1
```

Current solution verification commands, from the repository root on Windows:

```powershell
dotnet.exe restore .\sBridge.slnx --locked-mode
dotnet.exe build .\sBridge.slnx -c Release --no-restore
dotnet.exe test .\sBridge.slnx -c Release --no-build
```

Focused codec test and isolated Windows desktop smoke check:

```powershell
dotnet.exe test .\tests\sBridge.Tests\sBridge.Tests.csproj -c Release --no-build --filter FullyQualifiedName~VdfParserTests
powershell.exe -ExecutionPolicy Bypass -File .\tests\WindowsSmoke.ps1
```

Future self-contained publish (not validated in 1A):

```powershell
dotnet.exe publish .\sBridge.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\self-contained
```

Audit environment: Windows SDK `10.0.401`, Desktop runtime `10.0.12`, and
`powershell.exe` are reachable through WSL interoperability. `dotnet.exe --info`
was executed successfully during the audit. Phase 1A subsequently verified the
Windows solution build, tests, framework-dependent publish wrapper, and isolated
desktop smoke script; 1B repeated those gates with the safe persistence implementation.
Phase 1C verified the installed SISR's no-Steam API lifecycle and repeated Windows
build/test/publish/desktop checks. Gameplay, COM/Appx activation, live Steam writes,
actual VIIPER/devices/Steam controller integration, and self-contained distribution
were not validated.

- Resolve the checkout using `wslpath -w "$PWD"` and explicitly set a Windows
  working directory. This checkout resolves to a WSL UNC path; 1A verified Windows
  restore/build/test/publish there, but other integration tools may need validation.
  If needed, use a controlled Windows staging checkout, not product path changes.
- Windows is authoritative for COM, registry, Appx, WinForms, process observations,
  DPAPI, atomic replacement, Steam, and SISR. Linux pure-logic tests can supplement
  but cannot substitute for those checks.
- Keep Windows quoting/paths in product configuration. Do not introduce WSL paths,
  Linux path semantics, or line-ending-only churn.
- Unit tests use temporary fixtures, never live userdata. Prioritize CLI quoting,
  config migration/inheritance, stable identities, VDF safety, scoring, handoffs,
  and SISR ownership decisions. Test adapters separate logic from OS observation.
- Windows process harness cases: launcher -> bootstrap -> game, PID-zero launch,
  slow later handoff, overlapping same-name processes, helper surviving game exit,
  replacement already running, denied metadata, cancellation, and normal exit.
- Manual gates: settings initialization/inheritance; legacy packaged/custom
  launches (including spaces/arguments); owned/external SISR; failure cleanup;
  selected-account add/remove; Steam restart/readback; artwork independent of launch.
- Phase 1C removes that baseline blanket termination. The optional installed-SISR
  check requires no active SISR and validates API lifecycle only. Set
  `SBRIDGE_TEST_SISR_PATH` in Windows PowerShell, filter
  `FullyQualifiedName~InstalledSISRNoSteamApiLifecycle`, and clear the variable
  afterward. `tests\WindowsOwnedSisrSmoke.ps1` verifies Main's failure cleanup using
  an isolated stand-in. Real Steam/game/controller checks still need user assistance.
- Phase 2A's `tests\WindowsLaunchArgumentsSmoke.ps1` requires the Release test probe
  and published application. It verifies actual Main/Win32 argument forwarding in
  a temporary copy; it does not validate a packaged game's COM behavior. Windows
  argument tests skip on non-Windows hosts; parser/encoder decision tests are portable.
- Phase 2B's `tests\WindowsSessionSmoke.ps1` uses the same probe with controlled
  process chains. Native argument/session/SISR tests now use local staging and
  a shared nonparallel integration collection. A diagnostic full-suite command
  can add `--blame-hang-timeout 45s --blame-hang-dump-type none`; this aborts a
  stalled test host rather than substituting Linux behavior. Default smoke exit
  checks now use matched-game grace, not the old 90-second name-search penalty.
- Each implementation summary records exact commands/results, Windows checks
  actually performed, blocked checks, compatibility effects, and the next slice.

### Registered-library edit slice (2026-10-08)

- Added the Registered Games tab with explicit Save Game drafts for local name,
  target, stored Windows argument tokens, process/install hints and UUID-scoped
  SISR/watch choices. Selecting another game/Reload replaces the draft. Existing
  registrations are shown independently of Steam presence; no deletion/rewrite of
  registrations or Steam shortcuts is introduced in this bounded slice.
- `GameLibraryEditor` keeps UUID/provider/explicit kind, refreshes conventional
  path/AUMID identity after a target edit, and preserves opaque provider identities.
  Epic target/args remain read-only and constructor validated. Win32 edits require
  absolute Windows targets, packaged identities retain explicit kind, and nonempty
  install folders require absolute paths. Files need not currently exist, allowing
  stored launch fixes before install/relocation; hints should be reviewed together.
- Current immutable game/profile snapshots are checked before commit. Duplicate
  provider/kind/argument variants, stale game/profile edits and kind/UUID changes
  fail before persistence. Game and profile are committed in one config document;
  false/throwing saves restore exact prior state and keep drafts for correction.
  Explicit automatic/empty profiles retain representation so codec extension fields
  survive edits back to defaults. Existing Steam AppName/Exe/LaunchOptions/AppIDs/
  artwork are untouched; ID shortcuts resolve new metadata on their next launch.
- Windows locked restore, warning-as-error Release build and full suite passed
  with **339 passed, four opt-in skipped (343 total)** and zero warnings. Six new
  tests cover UUID/identity/argument/profile updates, rollback, duplicate/stale
  rejection, explicit-kind/Epic restrictions, unknown JSON field preservation and
  actual store concurrency failure without overwriting external edits.
- `WindowsLibrarySmoke.ps1` passed against the published app and synthetic data:
  actual UI edits preserve UUID/other games/extensions/VDF bytes, a real locked
  config save reports failure and rolls back memory, and launch UUID forwards edited
  empty/space/Unicode arguments with its disabled integration profile. The original
  nine desktop gates were checked: eight passed, while the synthetic Steam import
  smoke was skipped because Steam was running. No request to stop Steam or alter
  normal user data was needed. PowerShell 5 property enumeration in the new harness
  was corrected to count the collection rather than per-property Count values.
- This is local editing coverage, not Steam rename/readback or gameplay proof.
  Earlier hosted CI and Sandbox records still cover their exact recorded commits/
  ZIPs, not these new uncommitted edits. Additional per-game SISR controller-profile
  configuration can be a separate coding slice; live Steam/controller validation
  remains user assisted.

### Structured per-game SISR controller profiles (2026-10-08)

- `GameProfile.Controller` is an optional immutable `SisrControllerProfile`,
  configuring the pinned five virtual types (xbox360/dualshock4/dualsense/
  dualsenseedge/ns2pro) plus gyro/touchpad/back-button passthrough. Null inherits
  SISR defaults/advanced options. Existing integration/watch edits, cloning,
  catalog rediscovery/retry and local library edits retain the override.
- Optional schema-1 `controller` JSON inside each profile requires typed type and
  all three boolean fields when present. Unsupported/missing/wrong types fail
  loading instead of defaulting. Nested controller extension fields survive edits
  while the override remains; explicitly clearing means inheritance. No migration
  overwrites old JSON or applies options to disabled integration.
- Controller Profiles UI provides explicit Save Profile drafts per registered
  UUID. It uses the library transaction's immutable game/profile snapshot checks,
  duplicate protection and false/throwing-save rollback. Steam per-game toggles
  and Save Game retain controller options; the editor can explicitly clear them.
  No VDF, AppID, artwork or Steam binding/layout writes occur on profile edits.
- When SISR is enabled, a structured override requires managed startup. Conflicting
  global long/alias/negated controller flags fail before starting; inheritance
  permits existing advanced options. Managed JSON/environment owns the four pinned
  keys/values while preserving Steam context. After API readiness, effective type
  and all three booleans must match before game activation. Missing/mismatched
  fields cause a launch error with owned cleanup before the dialog. Disabled SISR
  bypasses the profile startup policy. This is emulation configuration, not physical
  input or Steam action-layout selection.
- Windows locked restore, warning-as-error build/publish and full suite passed:
  **358 passed, five opt-in skipped (363 total)** with zero warnings. Nineteen
  new default tests cover independent profiles/setters/clone, old-schema inheritance,
  strict typed JSON/nested extensions, registry/library preservation and rollback,
  conflicting flags, pinned enum mapping, config/environment output and effective
  mismatch rejection. Earlier library-editor tests remain passing.
- All three installed-SISR no-Steam checks passed separately, including actual
  DualSense Edge/gyro=false/touchpad=false/back-buttons=true effective API config
  and graceful owned quit with non-resolving VIIPER. An initial legacy lifecycle
  log assertion exposed quit racing listener creation before status initialization;
  that test now waits for owned API readiness, and the rerun passed. No physical
  controller, normal Steam setup or VIIPER creation is used by these checks.
- Published library/controller UI smoke passed UUID/profile/nested-extension
  preservation, locked-save rollback, untouched synthetic VDF and disabled ID launch.
  Managed SISR smoke passed profile application, readiness/argument/graceful-quit
  ordering, unsupported API and ignored-profile rejection/owned cleanup, plus
  pre-start legacy-policy rejection. Seven other desktop smokes passed; the account
  import smoke was skipped because Steam was running. No request to stop live Steam
  or change user integration was needed.
- `docs/REAL_WORLD_VALIDATION.md` supplies a focused one-game/one-account/one-pad
  checklist for the remaining live import/readback, real launch/input/cleanup and
  Steam profile-context gates. Automated matching configuration is not controller
  input proof. Artwork match selection/repair/WebP remain optional enhancements;
  exact new release ZIPs still need their own clean-machine check.
- Committed/pushed library and controller-profile work as `c6d564b` on
  modernization. [Hosted run 37872788183](https://github.com/jxsparrou/controller-bridge/actions/runs/37872788183)
  passed build/tests/publish/both package checks and uploads. Downloaded TRX
  confirms 358 passed/zero failed/363 total, with five explicit opt-in skips.
  Evidence and updated artifact links are recorded in the hosted CI validation
  document. Worktree was clean after the feature push; the earlier exact-ZIP
  Sandbox result remains distinct from newly built CI artifacts.

## Open validation items

### First hosted CI execution (2026-10-08; completed)

- Maintainer authorized commit/push. Commit `da3d3f6` published the accumulated
  modernization on the `modernization` branch, preserving main. The Git identity
  was supplied per commit using the maintainer-confirmed existing noreply address;
  no Git configuration was changed. NuGet's Windows-generated dependency lock is
  normalized in Git through a targeted `.gitattributes` rule.
- [Initial hosted run](https://github.com/jxsparrou/controller-bridge/actions/runs/37845096934)
  passed SDK setup, locked restore and warning-as-error build. Tests reported 331
  passed, four opt-in skipped and two failures in native fixture teardown: staged
  Game.exe deletion was denied immediately after forced process termination.
  Session behavior assertions were not the reported failure. Packaging was skipped
  because the test step failed.
- The fixture now retries only its owned directory deletion for at most five
  monotonic seconds, allowing executable-image/scanner handles to be released.
  Process cleanup remains path/creation-time scoped; no session assertions are
  skipped or changed. Local focused native checks passed all three cases with a
  warning-free build. Commit `d8304e3` applied the correction.
- [Hosted rerun](https://github.com/jxsparrou/controller-bridge/actions/runs/37845958021)
  passed all stages on Windows Server 2025 with .NET SDK 10.0.401: zero build
  warnings/errors, **333 passed/four opt-in skipped (337 total)**, development
  publish, framework-dependent/self-contained `0.0.0-ci.2` ZIP creation, both
  checksum/payload/runtime-layout/disabled-launch smoke checks, and package/TRX
  artifact uploads. `docs/validation/2026-10-08-hosted-ci.md` records exact commit,
  run and artifact links. The hosted CI gate is complete; main remains unchanged.

Xbox/Win32, bounded Epic providers, and selected Steam accounts/first-file creation
are implemented, along with artwork and opt-in managed SISR readiness/status/config.
Bounded diagnostic logging/UI and local Windows CI/distribution automation are
implemented. The exact development self-contained ZIP passed the clean Sandbox
gate; hosted Windows CI also passed build/test/package verification.
Do not assume these items are resolved by the audit:

- Steam-generated AppID/quoting fixtures and behavior on paths with spaces.
- Both runtime-mode packages publish and pass local startup/payload checks from
  the WSL UNC checkout. The recorded self-contained ZIP passed runtime-free
  Sandbox verification; repeat that gate for release ZIPs. Hosted CI verified the
  modernization branch and uploaded checked development package/TRX artifacts.
- SISR effective config precedence, inherited Steam identity/profile correctness,
  external-instance coexistence beyond startup refusal, readiness, VIIPER/device
  cleanup, crash recovery, and supported versions beyond the audited API family.
  Owned loopback endpoint verification and graceful no-Steam quit are now tested.
- Real packaged Win32/MSIX and Epic/EAC handoffs, including denied process metadata.
- DPAPI versus Credential Manager dependency/portability tradeoff.
- Existing-shortcut identity handling when the bridge moves or its display name
  changes; do not silently regenerate UUIDs or Steam AppIDs.
