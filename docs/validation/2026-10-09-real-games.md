# Initial real-game/controller observations

**User-reported results**, collected 2026-10-09. These observations are distinct
from automated probes and do not establish unreported provider/input details.

Physical controller: **Steam Controller 2**, as identified by the maintainer.

| Game (user-provided title) | Integration choice | Reported observation |
| --- | --- | --- |
| The Witcher 3 remastered | SISR disabled | Created Steam shortcut worked; user reports Steam Input worked. |
| Hades II | SISR enabled, legacy startup | Game/controller test worked; SISR exited after the game closed. |
| Hades II | Managed startup, inherited controller options | User reports all input worked after connecting the controller after game startup. |
| Hades II | Managed startup, explicit Xbox 360 override | User confirmed the requested input/normal-exit/Steam return-to-Play checks worked without issues. |

The user reports all buttons worked. Axis/trigger behavior, gyro/touchpad/back
buttons, the exact provider/process chain, and Steam returning to Play were not
separately reported. No VDF/AppID fixture was captured, and a successful created
shortcut launch is not a byte-level Steam readback/identity verification.

## Diagnostic context

Safe summary captured UTC `2026-10-09T10:09:45.4553170+00:00`:

- App assembly version 1.0.0.0; .NET 10.0.12. Exact copied binary/source SHA was
  not established from that summary.
- One discovered/selected account; none unavailable; two games and one profile.
- Logging enabled; zero failed writes in the diagnostics process.
- SISR enabled/configured/executable present; managed startup **false**.
- No external SISR instances at capture; latest managed-session status unavailable
  (expected because the reported integration run used legacy startup).
- SteamGridDB key present; no key or raw credential/log content included.

The Documents-copy executable initially produced a Store/app-association prompt.
The user reports unblocking the executable resolved it. Zone.Identifier contents
were not captured, so the precise shell/security cause was not independently
established. No application code change was needed for these successful runs.

## Managed session follow-up

Safe summary captured UTC `2026-10-09T10:19:02.9558467+00:00` reports:

- Managed startup enabled; latest session timestamp
  `2026-10-09T10:18:53.8066496+00:00`, phase **ended**, API ready **true**.
- SISR v0.6.1; Steam **true**, no-Steam **false**; Xbox 360 output type.
- Cached startup snapshot: VIIPER **false**, devices **0**. These flags were sampled
  during readiness and carried into the ended summary, not live gameplay or final
  device health. They neither prove nor disprove later redirected input.
- Zero stored profiles: games inherit global enabled integration/default controller
  options unless an explicit per-game override is restored.
- No external SISR instance at diagnostic capture; zero failed bridge-log writes.

This demonstrates managed readiness and an ended owned session. In the follow-up,
the user confirmed all buttons/everything worked and stated the Steam Controller 2
was connected **after the game started**. That explains the readiness-time zero
device count and provides user-observed late-connect input coverage. The summary
still does not identify the live virtual device or prove every passthrough feature.
Steam return-to-Play was not separately described beyond the general success report.

## Structured-profile follow-up

After instructions to select an explicit Xbox 360 override for Hades II, the user
provided a safe summary captured UTC `2026-10-09T10:27:56.2777840+00:00`:

- One stored profile; managed startup enabled.
- Last session timestamp `2026-10-09T10:27:54.4184309+00:00`, phase **ended**,
  API ready **true**; SISR v0.6.1, Steam **true**, no-Steam **false**.
- Readiness snapshot: VIIPER **true**, devices **1**, controller **xbox360**.
- No external SISR instances at capture; zero failed bridge-log writes.

This is consistent with the requested structured profile and a controller/VIIPER
connection present at readiness, followed by owned process termination. The copy
does not export the requested passthrough flags or identify the game/device; those
details come from the test context, not independently from the snapshot. In
response to the input and Steam return-to-Play confirmation request, the user
reported **everything worked without issues**. This completes the basic
user-observed explicit Xbox 360 profile run. An ended phase does not by itself
distinguish API quit from fallback termination or independently verify underlying
VIIPER/Steam Input cleanup.

## Remaining validation scope

The first basic game/controller matrix is successful for these reported runs.
Other output types, gyro/touchpad/back buttons, byte-level Steam/AppID fixtures,
additional providers/accounts and protected launch chains remain separate checks.
Steam bindings remain configured in Steam, and SISR's virtual output type is
distinct from the physical Steam Controller 2.

Use [the real-world checklist](../REAL_WORLD_VALIDATION.md) for remaining live
Steam readback, provider-specific activation, profile context and cleanup gates.
