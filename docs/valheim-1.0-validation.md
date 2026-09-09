# Valheim 1.0 validation

Tested on Valnet Client 02 on 2026-09-09 with Valheim `l-1.0.7`, Steam build
`25185596`, network version 39, and BepInEx 5.4.23.3. The isolated profile
contained DiscordTools and valheimCLI. Tests used the development character
`Prae107Test` and local world `TestingWorld`.

## Reproduced failure

Published DiscordTools 1.4.1 throws `MissingMethodException` in `Awake` because
its `Terminal.ConsoleCommand` constructor reference does not match Valheim 1.0.
The exception prevents network-handler patches from being installed.

## Fix and evidence

Rebuilding against Client 02's matching game assemblies removed the exception.
The source now names `hideBehindDevCommands: false`, the new constructor option,
to prevent accidental builds against the older API. This preserves the default
command visibility used in the successful rebuild.

The rebuilt DLL passed these live checks before adding the explicit argument:

- Loaded without a DiscordTools exception.
- Registered DiscordTools network handlers when the world started.
- Entered the local world.
- Returned the expected usage and unknown-player responses from `client-logs`.
- Returned to the main menu through `cli_logout_save`.

The explicit argument has the same value as the omitted optional argument in
that tested build. The final source builds against the same assemblies with
zero warnings and zero errors. An `ilspycmd -il -t DiscordTools.ClientLogCommand`
comparison of the tested DLL and final DLL produced identical output.

## Limits

Dedicated-server requests, log transfer, archive contents, automatic logout
and quit uploads, and Discord bot posting were not tested. Local logout does
not exercise the upload path. These tests do not establish release readiness.

The game logged missing script and attachment messages in the isolated profile.
The character had previously used modded equipment; those messages were not
investigated as DiscordTools failures. valheimCLI's status summary reported
`Unknown` while its game log recorded `InWorld` and `MainMenu`; the world-state
checks above use the game log.

No package version was changed and no release was published by this fix.
