# Focused Steam/game/controller validation

The automated Windows tests, native probes, installed-SISR no-Steam API checks
and earlier exact-ZIP Sandbox proof pass. They do not establish actual game/input
behavior. This checklist keeps the first user-assisted run small and records
results without copying credentials or raw logs.

## First run: one game, one account, one controller

1. Keep the current application's supporting files together in a permanent folder.
   Open sBridge with no arguments. Existing per-user JSON state is authoritative;
   do not change normal state through test environment overrides.
2. Choose a game you own that has a simple, known launch chain. Note its provider
   (custom Win32, Xbox/Store, Epic), expected real-game process and controller model.
3. Close Steam before importing. Select exactly the intended account on Steam
   Accounts. Keep a recovery copy of its original `shortcuts.vdf` if present; the
   app also stages its backup on normal existing-file edits. Avoid all-account
   experiments for this first check.
4. Import that game with SISR **Disabled** first. Restart Steam, verify the new
   shortcut and its `launch <UUID>` options, and launch it. Confirm the real game
   opens, arguments work, Steam tracks the session, and the bridge exits after the
   game. Existing AppIDs/artwork should not be recomputed. This is the first live
   import/restart/readback gate.
5. After that succeeds, configure the external SISR executable. Enable Managed
   SISR startup. Remove conflicting owned config/API/log flags from advanced args.
   Stop any manually running SISR yourself before the managed test: sBridge leaves
   existing redirectors untouched and reports a conflict.
6. Enable SISR for that registered UUID. Start with **inherit controller options**
   or an Xbox 360 emulation override for a basic compatibility check. Profile type
   is the virtual output, not necessarily the physical controller type. Controller
   Profiles supports Xbox 360, DualShock 4, DualSense, DualSense Edge and Switch 2
   Pro plus supported gyro/touchpad/back-button passthrough. Save the draft.
7. Launch from Steam. Confirm managed API/profile checks complete before the game;
   inspect SISR's first-run setup UI if required. Record whether the game sees the
   virtual pad and normal buttons/sticks/triggers work. API readiness/configuration
   matching alone does not prove input, Steam bindings or device support.
8. Close the game normally. Confirm sBridge/SISR finish and controller input is no
   longer redirected. In diagnostics, the last managed session is historical and
   should be ended; local logs should indicate normal owned API quit. A forced-stop
   warning means integration cleanup may be incomplete and is not a passed cleanup
   check. Do not use name-based kills or blanket Steam resets as a substitute.

SISR owns actual redirection, VIIPER and normal Steam Input cleanup. sBridge's
controller profiles configure SISR startup options; Steam action bindings/layouts
are still configured in Steam. Advanced controller flags conflict with an explicit
structured override and are rejected instead of silently overriding it. Inherit
keeps advanced/default controller behavior.

## Expand only after the first run passes

- Xbox/Store COM activation and argument/hint behavior with an actual title.
- Epic Launcher authentication and a real Epic game; configure extra game args
  in Epic Launcher. Test EAC/bootstrap chains with the real-game watch override.
- A multi-hop/protected game where process paths may be unavailable; record
  ambiguity/failure rather than accepting an unrelated pre-existing process.
- A second virtual controller type and supported gyro/touchpad/back-button options;
  unsupported hardware/game surfaces are not expected to work just because the
  flag is enabled.
- Selected-only add/remove/readback across a second account. Removal is scoped to
  checked shortcut rows; local library edits do not rename Steam's shortcut label.
- Failure cleanup: a controlled bad game target after SISR readiness should stop
  only the owned SISR before the error dialog. Keep normal controller context and
  do not test against an unrelated external SISR instance.

## Report template

Share the safe Copy Diagnostics summary and these observations:

```text
Application commit/package version:
Windows version:
Provider and game (title optional):
Expected real-game process:
Physical controller model:
Requested virtual controller type / passthrough flags:
SISR mode/version:
Steam import/restart/readback: pass/fail/not tested
Launch/arguments/session tracking: pass/fail/not tested
Buttons/sticks/triggers: pass/fail/not tested
Gyro/touchpad/back buttons: pass/fail/not supported/not tested
Normal exit / SISR and VIIPER cleanup: pass/fail/not tested
Warnings or exact error category:
```

Copy Diagnostics omits paths, account/game/device identities, args, credentials and
raw logs. Review/redact raw local/SISR logs before sharing them; do not include API
keys, passwords, device serials or account identifiers. Steam-generated VDF/AppID
fixtures need separate sanitized capture before any quoting/identity-policy change.

## Remaining release gates

- CI must pass for the intended source commit.
- Repeat the clean-machine test for the exact intended release ZIP; the earlier
  development ZIP's result does not cover rebuilt controller-profile packages.
- Select version/release notes/signing and publish only with maintainer approval.
- Artwork match selection/explicit repair and WebP/animated support remain optional
  enhancements, separate from the first reliable launch/input validation.
