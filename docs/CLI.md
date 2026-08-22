# CLI Guide

AudioPilot supports command-line control through `AudioPilot.Cli.exe`.

This document is the canonical CLI reference.

Keep detailed command, JSON output, and exit-code behavior here. Keep [../README.md](../README.md) and [USER_GUIDE.md](USER_GUIDE.md) at summary level with links to this page.

## Execution Model

- `AudioPilot.Cli.exe` is the shipped CLI executable.
- The host project is `AudioPilot.CliHost`, but the published command users run is `AudioPilot.Cli.exe`.
- If a UI instance is running, commands are forwarded to that instance first.
- If no UI instance is running, automation-safe commands run locally in headless mode.
- Routine inspection, mutation, export, and import commands are automation-safe and work in either forwarded or local headless execution.
- UI-only commands such as `show`, `hide`, and `startup open` require a running UI host and return exit code `3` otherwise.
- `show` and `hide` are intentionally explicit and deterministic for automation; only the global window hotkey toggles based on current visibility.
- If forwarding fails because an existing instance is unreachable, the CLI returns exit code `4` so scripts can treat it as a transport problem instead of a normal command failure.
- If a running instance uses an incompatible forwarding protocol, the CLI returns exit code `6`.
- Local or forwarded runtime execution failures return exit code `7`.
- Grouped-command parse failures point to the matching topic help in text mode, for example `AudioPilot.Cli.exe help devices`.
- Text-mode errors emitted by the host use the `[diag-code:<code>]` prefix so they align with command/runtime error output.
- `completion powershell|bash` prints shell completion scripts generated from the centralized CLI metadata, so completions track the documented command surface.
- `refresh` supports `--json` and returns a minimal success envelope for automation.
- `refresh` failure paths use the normal error contract with exit code `7` and error code `refresh-failed` in both headless and forwarded execution.
- `media play-pause|next|previous` remains fire-and-forget, but forwarded UI execution records a completed media history entry once overlay resolution finishes. Media history details include privacy-safe overlay diagnostics such
  as `diagCode`, `finalPhase`, `outcome`, `finalChangeKind`, `finalFallbackClassification`, `finalPath`, session-drop flags, and same-source conflict counters when available.
- `media status [--json] [--redact]` returns the current media snapshot and is safe to use in automation.

Practical rule: if you are writing automation, prefer commands that already expose `--json`, and treat exit code `4` separately from precondition or execution failures.

## Quick Reference

Common commands by task:

```powershell
# Inspect state
AudioPilot.Cli.exe status --json
AudioPilot.Cli.exe diagnostics status --json --redact
AudioPilot.Cli.exe diagnostics history --limit 20 --json --redact
AudioPilot.Cli.exe diagnostics history-detail <opId> --json --redact
AudioPilot.Cli.exe diagnostics export-logs .\artifacts\logs.zip --json --redact --detail manifest
AudioPilot.Cli.exe diagnostics export-bundle .\artifacts\support-bundle.zip --json --detail manifest
AudioPilot.Cli.exe media status --json --redact

# Switch devices and listen state
AudioPilot.Cli.exe switch output
AudioPilot.Cli.exe switch input --reverse
AudioPilot.Cli.exe listen toggle --json

# Work with volume
AudioPilot.Cli.exe volume get master --json
AudioPilot.Cli.exe volume set mic 25
AudioPilot.Cli.exe volume get master --device "Speakers"
AudioPilot.Cli.exe volume get master --device-id <playbackDeviceId>
AudioPilot.Cli.exe volume set mic 15 --device-id <recordingDeviceId>

# Resolve devices and cycle entries
AudioPilot.Cli.exe devices get output "Speakers" --json
AudioPilot.Cli.exe devices find input usb --json
AudioPilot.Cli.exe cycle test output --json
AudioPilot.Cli.exe cycle add output <deviceId>

# Work with routines
AudioPilot.Cli.exe routine list --json
AudioPilot.Cli.exe network list --json --redact
AudioPilot.Cli.exe routine create routine.json --allow-any-path
AudioPilot.Cli.exe routine update routine-desk routine.json --allow-any-path
AudioPilot.Cli.exe routine delete routine-desk
AudioPilot.Cli.exe routine import routines.json --replace --allow-any-path
AudioPilot.Cli.exe routine export routines.json --allow-any-path

# Persisted config and runtime tuning
AudioPilot.Cli.exe config list --json
AudioPilot.Cli.exe runtime list --json
AudioPilot.Cli.exe config get output-switch-hotkey --json
AudioPilot.Cli.exe config get redact-log-content --json
AudioPilot.Cli.exe config set overlay-position BottomCenter
AudioPilot.Cli.exe config set play-dialog-sounds false
AudioPilot.Cli.exe config set redact-log-content false

# Startup control
AudioPilot.Cli.exe startup status --json
```

Use cases:

- Use `status` to inspect current device state, mute state, and routine state.
- Use `switch`, `mute`, `listen`, and `media` for direct control.
- Use `volume get` and `volume set` for automation-safe master or microphone endpoint volume changes, including exact-name `--device` lookup or explicit non-default devices via `--device-id`.
- Use `devices get` and `devices find` to validate selectors before scripting follow-up volume or cycle changes.
- Use `routine` commands to inspect, mutate, or trigger saved automations.
- Use `routine export` to capture the current routines file shape, then edit that JSON for `routine create`, `routine update`, or `routine import` automation.
- Use `config` for persisted settings and `runtime` for in-memory tuning.
- Use `config list` and `runtime list` when you need to discover supported keys before scripting `get` or `set` operations.
- Use `listen-monitor-output-device-id` together with `listen-monitor-output-device-name` when you want listen monitoring to survive endpoint-ID churn more reliably.
- Use `cycle add`, `cycle remove`, and `cycle reorder` to script cycle maintenance without editing the full config file.
- Use `diagnostics status` when you need backup, log, or reconnect state for troubleshooting.
- Use `diagnostics history` to inspect recent routine, switch, media, and mute outcomes for the current app session.
- Use `diagnostics history-detail` when you need the full per-operation record for a specific `opId` returned by the history list.
- Use `diagnostics export-logs` when you need one zip archive containing the current log plus rotated log backups. Add `--redact` to sanitize paths and quoted identifiers inside the archived logs as well as the command output.
- Use `diagnostics export-bundle` when you need a share-safe support zip with status, recent history, media status, config validation, and sanitized logs. Add `--include-sensitive` only for private/local troubleshooting where raw
  paths and names are acceptable.

## Common Automation Recipes

These examples are intentionally short end-to-end flows you can paste into a shell and adapt.

### Switch Then Validate

```powershell
AudioPilot.Cli.exe switch output --json | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "Output switch failed"
}

$status = AudioPilot.Cli.exe status --json | ConvertFrom-Json
$status.data.currentOutputDeviceName
```

### Export Config Safely

```powershell
# Export under the current working directory so no path override is needed
AudioPilot.Cli.exe config export .\backup\settings-export.json --json --redact

if ($LASTEXITCODE -ne 0) {
  throw "Config export failed"
}
```

### Run A Routine From PowerShell

```powershell
$result = AudioPilot.Cli.exe routine run routine-desk --json --redact | ConvertFrom-Json

if ($LASTEXITCODE -ne 0) {
  throw "Routine run failed"
}

$result.data.routine.id
$result.data.targetSummary
```

## Shell Completion

Generate completion scripts directly from the CLI:

```powershell
AudioPilot.Cli.exe completion powershell
AudioPilot.Cli.exe completion bash
```

Typical setup:

```powershell
# PowerShell: register for the current session
Invoke-Expression (& AudioPilot.Cli.exe completion powershell)
```

```bash
# Bash: source in the current shell
source <(AudioPilot.Cli.exe completion bash)
```

The generated scripts only complete fixed command words and fixed flag values that are part of the documented CLI surface. They intentionally do not invent dynamic values such as routine ids, device ids, or file paths.

## Help Topics

Use either `AudioPilot.Cli.exe help <topic>` or `AudioPilot.Cli.exe <topic> help` for grouped command help.

### core

```powershell
AudioPilot.Cli.exe help core
AudioPilot.Cli.exe core help
AudioPilot.Cli.exe show
AudioPilot.Cli.exe hide
AudioPilot.Cli.exe refresh [--json]
AudioPilot.Cli.exe status [--json] [--redact]
AudioPilot.Cli.exe version
```

Use a command's --help option or audio-pilot help core for these one-shot commands.

### completion

```powershell
AudioPilot.Cli.exe help completion
AudioPilot.Cli.exe completion help
AudioPilot.Cli.exe completion powershell|bash
```

Use completion powershell or completion bash to print a shell script generated from the centralized CLI metadata.

### diagnostics

```powershell
AudioPilot.Cli.exe help diagnostics
AudioPilot.Cli.exe diagnostics help
AudioPilot.Cli.exe diagnostics refresh [--json]
AudioPilot.Cli.exe diagnostics status [--json] [--redact] [--show-paths]
AudioPilot.Cli.exe diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact]
AudioPilot.Cli.exe diagnostics history-detail <opId> [--json] [--redact]
AudioPilot.Cli.exe diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path]
AudioPilot.Cli.exe diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive]
AudioPilot.Cli.exe diagnostics reset-per-app-audio [--json]
```

Use diagnostics history to inspect recent routine, switch, media, and mute outcomes for the current app session.

Use diagnostics export-bundle to create a redacted support zip; pass --include-sensitive only for local private troubleshooting.

Use --detail manifest when you want per-entry archive results in addition to the summary.

### media

```powershell
AudioPilot.Cli.exe help media
AudioPilot.Cli.exe media help
AudioPilot.Cli.exe media play-pause|next|previous
AudioPilot.Cli.exe media status [--json] [--redact]
```

Use media status when you need the current media snapshot in text or JSON form. The transport commands remain fire-and-forget.

### mute

```powershell
AudioPilot.Cli.exe help mute
AudioPilot.Cli.exe mute help
AudioPilot.Cli.exe mute mic|sound|deafen [toggle|on|off] [--json]
```

If no mode is provided, mute commands default to toggle. Pass --json to return the resulting mute state.

### listen

```powershell
AudioPilot.Cli.exe help listen
AudioPilot.Cli.exe listen help
AudioPilot.Cli.exe listen toggle|on|off [--json] [--redact]
```

If no mode is provided, listen defaults to toggle.

### volume

```powershell
AudioPilot.Cli.exe help volume
AudioPilot.Cli.exe volume help
AudioPilot.Cli.exe volume get master|mic [--device <name>|--device-id <deviceId>] [--json] [--redact]
AudioPilot.Cli.exe volume set master|mic <percent> [--device <name>|--device-id <deviceId>] [--json] [--redact]
```

Use either --device or --device-id to target a non-default endpoint.

### routine

```powershell
AudioPilot.Cli.exe help routine
AudioPilot.Cli.exe routine help
AudioPilot.Cli.exe routine list [--json] [--redact]
AudioPilot.Cli.exe routine run|enable|disable|delete <id|name> [--json] [--redact]
AudioPilot.Cli.exe routine create <path.json> [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe routine import <path.json> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe routine export <path.json> [--json] [--redact] [--allow-any-path]
```

routine import merges by default. Pass --replace to replace the full saved routine list.

### config

```powershell
AudioPilot.Cli.exe help config
AudioPilot.Cli.exe config help
AudioPilot.Cli.exe config list [--json]
AudioPilot.Cli.exe config get <key> [--json] [--redact]
AudioPilot.Cli.exe config set <key> <value>
AudioPilot.Cli.exe config export <path.json|path.zip> [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe config validate [--json] [--redact]
```

config import merges by default. Pass --replace to replace the imported settings snapshot.

### runtime

```powershell
AudioPilot.Cli.exe help runtime
AudioPilot.Cli.exe runtime help
AudioPilot.Cli.exe runtime list [--json]
AudioPilot.Cli.exe runtime get <key> [--json] [--redact]
AudioPilot.Cli.exe runtime set <key> <value>
```

Runtime changes affect only the current AudioPilot process and are reset when that process exits. Use config set for the subset of tuning keys that can be persisted.

### network

```powershell
AudioPilot.Cli.exe help network
AudioPilot.Cli.exe network help
AudioPilot.Cli.exe network list [--json] [--redact]
```

Lists currently visible network names for network-trigger routine setup. Use --redact before sharing output because network names can identify locations.

### devices

```powershell
AudioPilot.Cli.exe help devices
AudioPilot.Cli.exe devices help
AudioPilot.Cli.exe devices list output|input [--json] [--redact]
AudioPilot.Cli.exe devices get output|input <id|name> [--json] [--redact]
AudioPilot.Cli.exe devices find output|input <text> [--json] [--redact]
```

devices find accepts multi-word text and performs a case-insensitive substring search across ids and names.

### cycle

```powershell
AudioPilot.Cli.exe help cycle
AudioPilot.Cli.exe cycle help
AudioPilot.Cli.exe cycle show|validate|test output|input [--json] [--redact]
AudioPilot.Cli.exe cycle add|remove output|input <deviceId> [--json] [--redact]
AudioPilot.Cli.exe cycle reorder output|input <deviceId...> [--json] [--redact]
```

cycle reorder expects the full current cycle device list in the new order; the parser rejects blank or duplicate ids, and execution verifies the configured cycle membership.

### switch

```powershell
AudioPilot.Cli.exe help switch
AudioPilot.Cli.exe switch help
AudioPilot.Cli.exe switch output [--reverse] [--mute-mic] [--mute-sound] [--deafen] [--dry-run] [--require-current <deviceId>] [--json] [--redact]
AudioPilot.Cli.exe switch input [--reverse] [--dry-run] [--require-current <deviceId>] [--json] [--redact]
```

Input switching supports --reverse, --dry-run, and --require-current, but not --mute-mic, --mute-sound, or --deafen.

### wait

```powershell
AudioPilot.Cli.exe help wait
AudioPilot.Cli.exe wait help
AudioPilot.Cli.exe wait --wait-for-device <deviceId> [--timeout <ms>] [--output|--input] [--json] [--redact]
```

Use --output or --input to scope the wait to one device class; the parser rejects passing both flags together, and omitting both lets either class satisfy the wait.

### startup

```powershell
AudioPilot.Cli.exe help startup
AudioPilot.Cli.exe startup help
AudioPilot.Cli.exe startup enable|disable|status [--json] [--redact]
AudioPilot.Cli.exe startup open
```

startup open is UI-only and requires a running UI host instance.

## Commands

### UI And Lifecycle Commands

```powershell
AudioPilot.Cli.exe completion powershell|bash
AudioPilot.Cli.exe show
AudioPilot.Cli.exe hide
AudioPilot.Cli.exe startup enable|disable|status [--json] [--redact]
AudioPilot.Cli.exe startup open
AudioPilot.Cli.exe help [core|completion|diagnostics|media|mute|listen|volume|routine|config|runtime|network|devices|cycle|switch|wait|startup]
AudioPilot.Cli.exe version
```

Grouped help is also available as `<topic> help`, for example `AudioPilot.Cli.exe diagnostics help` or `AudioPilot.Cli.exe volume help`.
When a help topic or grouped subcommand is close to a known value, text-mode parse errors also include a `Did you mean ...?` suggestion.

### Switching, Refresh, And Direct Control

```powershell
AudioPilot.Cli.exe switch output [--reverse] [--mute-mic] [--mute-sound] [--deafen] [--dry-run] [--require-current <deviceId>] [--json] [--redact]
AudioPilot.Cli.exe switch input [--reverse] [--dry-run] [--require-current <deviceId>] [--json] [--redact]
AudioPilot.Cli.exe wait --wait-for-device <deviceId> [--timeout <ms>] [--output|--input] [--json] [--redact]
AudioPilot.Cli.exe refresh [--json]
AudioPilot.Cli.exe diagnostics refresh [--json]
AudioPilot.Cli.exe diagnostics status [--json] [--redact] [--show-paths]
AudioPilot.Cli.exe diagnostics history [--limit <n>] [--type routine|switch|media|mute] [--json] [--redact]
AudioPilot.Cli.exe diagnostics history-detail <opId> [--json] [--redact]
AudioPilot.Cli.exe diagnostics export-logs <path.zip> [--json] [--redact] [--detail summary|manifest] [--allow-any-path]
AudioPilot.Cli.exe diagnostics export-bundle <path.zip> [--json] [--detail summary|manifest] [--allow-any-path] [--include-sensitive]
AudioPilot.Cli.exe diagnostics reset-per-app-audio [--json]
AudioPilot.Cli.exe media play-pause|next|previous
AudioPilot.Cli.exe media status [--json] [--redact]
AudioPilot.Cli.exe mute mic|sound|deafen [toggle|on|off] [--json]
AudioPilot.Cli.exe listen toggle|on|off [--json] [--redact]
AudioPilot.Cli.exe volume get master|mic [--device <name>|--device-id <deviceId>] [--json] [--redact]
AudioPilot.Cli.exe volume set master|mic <percent> [--device <name>|--device-id <deviceId>] [--json] [--redact]
```

### Routine Commands

```powershell
AudioPilot.Cli.exe routine list [--json] [--redact]
AudioPilot.Cli.exe routine run|enable|disable|delete <id|name> [--json] [--redact]
AudioPilot.Cli.exe routine create <path.json> [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe routine update <id|name> <path.json> [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe routine import <path.json> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe routine export <path.json> [--json] [--redact] [--allow-any-path]
```

### Status, Config, And Runtime Commands

```powershell
AudioPilot.Cli.exe status [--json] [--redact]
AudioPilot.Cli.exe config list [--json]
AudioPilot.Cli.exe config get <key> [--json] [--redact]
AudioPilot.Cli.exe config set <key> <value>
AudioPilot.Cli.exe config export <path.json|path.zip> [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe config import <path.json|path.zip> [--merge(default)|--replace] [--json] [--redact] [--allow-any-path]
AudioPilot.Cli.exe config validate [--json] [--redact]
AudioPilot.Cli.exe runtime list [--json]
AudioPilot.Cli.exe runtime get <key> [--json] [--redact]
AudioPilot.Cli.exe runtime set <key> <value>
AudioPilot.Cli.exe network list [--json] [--redact]
```

### Device And Cycle Commands

```powershell
AudioPilot.Cli.exe devices list output|input [--json] [--redact]
AudioPilot.Cli.exe devices get output|input <id|name> [--json] [--redact]
AudioPilot.Cli.exe devices find output|input <text> [--json] [--redact]
AudioPilot.Cli.exe cycle show|validate|test output|input [--json] [--redact]
AudioPilot.Cli.exe cycle add|remove output|input <deviceId> [--json] [--redact]
AudioPilot.Cli.exe cycle reorder output|input <deviceId...> [--json] [--redact]
```

Selector notes:

- `--device-id` always performs an exact ID match.
- `--device` performs an exact active-device name match.
- If multiple active devices share the same exact name, the CLI returns a precondition failure with the matching IDs so scripts can disambiguate.
- `devices get` applies the same exact ID-first, exact-name-second resolution used by volume targeting.
- `devices find` performs a case-insensitive substring search across IDs and names and returns ranked matches for scripting and validation.
- `cycle reorder` expects the full configured cycle list in the new order. The parser rejects blank or duplicate IDs, and execution still verifies that the submitted list matches the configured cycle exactly.

## Exit codes

- `0`: success
- `2`: invalid command-line usage
- `3`: UI host unavailable for a UI-only command
- `4`: command forwarding failed (existing instance unreachable)
- `5`: resolver or precondition failure after parsing succeeded, for example a missing config/runtime key, an ambiguous selector, or no configured cycle devices
- `6`: forwarded protocol mismatch
- `7`: runtime execution failure

## JSON mode (`--json`)

Commands that support `--json` return machine-readable output.

For `refresh`, success returns a small `{ success, diagCode }` payload and failures return the standard `error` envelope with `code = "refresh-failed"` and `exitCode = 7`.

`diagnostics history` and `diagnostics history-detail` include optional `diagCode`, `elapsedMs`, and `details` fields when the current-session recorder has richer routine or media diagnostics for that operation. For media hotkey
and CLI media-command issues, `diagnostics history-detail <opId>` is the best place to inspect the final overlay phase and outcome that would otherwise require reading trace logs. Older fields remain stable.

`diagnostics export-bundle --json` returns `diagCode = "diagnostics-export-bundle-success"`, `redactionMode`, `partialExport`, `fileCount`, and `exportedBytes`. Use `--detail manifest` when automation needs the per-entry export result list.

Config and runtime commands use the same split:

- Missing command words or values are invalid usage and return exit code `2`.
- Unknown keys and rejected values are post-parse contract failures and return exit code `5`.

## Redaction mode (`--redact`)

`--redact` is a global opt-in privacy flag for output-producing commands.

- Default CLI behavior stays rich. Names, labels, and trigger targets are shown unless you request redaction.
- `config set redact-log-content true|false` controls persisted application log redaction in `settings.json`; it does not change CLI command output by itself.
- Redaction preserves JSON property names, object shape, ordering, and stable IDs so existing automation can keep parsing the same payload structure.
- Sensitive display values are anonymized instead of removed. This includes routine names, device names, trigger app paths, process names, and config import/export paths.
- `diagnostics status` already redacts absolute paths by default. `--redact` is stricter and keeps paths anonymized even if `--show-paths` is also passed.
- `diagnostics export-bundle` is redacted by default and intentionally does not accept `--redact`; pass `--include-sensitive` only when you want raw local paths, names, and log content in a private bundle.
- `config get` and `runtime get` accept `--redact` for consistency, although many keys do not currently expose sensitive display values.

Use `--redact` when you need one CLI invocation to avoid printing raw values.
Use `config set redact-log-content false` only when you intentionally want
background app logs to write raw identifiers until you turn that persisted
setting back on.

## Routines

Routine selectors accept an exact routine id or an exact routine name. If multiple routines share the same name, the CLI returns a precondition failure and requires the id.

### Routine file formats

- `routine create` and `routine update` accept a JSON file containing exactly one `AudioRoutine` object.
- `routine import` accepts either the `routine export` envelope shape (`SchemaVersion` plus `Routines`) or a raw JSON array of `AudioRoutine` objects.
- Routine targets can be output devices, input devices, master output volume, microphone volume, or any supported combination.
- If neither `--merge` nor `--replace` is passed, `routine import` merges by default.
- `routine import --merge` replaces matching routine ids and appends new ids.
- `routine import --replace` replaces the entire saved routine list.

### Routine output shape

`routine list` reports the full saved trigger configuration.

- Text output includes target summary, hotkey, primary trigger summary,
  optional application target, optional restore-on-exit hint, optional app-only
  output scope, optional conflict summary, tray visibility, and routine id.
- JSON output includes `triggerMode`, `usesApplicationTrigger`, `triggerAppPath`,
  `restorePreviousAudioOnDeactivate`, `switchOutputPerApp`, `showInTrayMenu`,
  `triggerSummary`, and `conflictSummary` for each routine.
- `routine run --json` returns the same trigger metadata alongside the applied device results so automation can correlate the run with the saved routine definition.

### Trigger behavior

- `AudioPilot startup` runs once after the app finishes booting.
- `Steam Big Picture` activates a routine while Steam Big Picture is visible.
- `Device change` runs a routine after AudioPilot finishes processing a hotplug or default-device refresh.
- `showInTrayMenu` is only persisted for `Hotkey` routines.
- `restorePreviousAudioOnDeactivate` records the previous default output and input before activation and restores those defaults when the stateful routine ends.
- `switchOutputPerApp` remains specific to routines that can resolve a live process target.
- Network trigger `Disconnect` mode is any-network by design: it runs when the machine goes from connected to no connected networks. Network trigger `Both` mode uses the saved network name for both connect and disconnect transitions.

Application-trigger routines support both desktop `.exe` paths and packaged-app AUMIDs. Packaged app matching uses the saved AUMID first and can fall back to the WindowsApps package family when a running process does not expose
its AUMID. The CLI preserves whichever trigger target the routine already has saved.

When you manually run a routine from CLI and that routine uses the
`Application` trigger, the target app must already be running. This
applies to both desktop `.exe` targets and packaged-app AUMIDs. If the app is
not running, `routine run` returns exit code `5` with error code
`routine-trigger-app-not-running` instead of silently degrading to a different
switch path.

### Routine examples

Listen examples:

```powershell
# Toggle Windows "Listen to this device" on the current default input
AudioPilot.Cli.exe listen toggle

# Force listen on and inspect the resulting state
AudioPilot.Cli.exe listen on --json

# Use a dedicated output device for monitoring
AudioPilot.Cli.exe config set listen-monitor-output-device-id "device-id-here"
```

Mutation workflow:

```powershell
# Export the current routines as a starting point
AudioPilot.Cli.exe routine export routines.json --allow-any-path

# Create a new routine from a single JSON object
AudioPilot.Cli.exe routine create desk.json --allow-any-path

# Update an existing routine by id using a single JSON object payload
AudioPilot.Cli.exe routine update routine-desk desk.json --allow-any-path

# Replace the full routine list from an exported envelope
AudioPilot.Cli.exe routine import routines.json --replace --allow-any-path
```

Example `routine list` output:

```powershell
AudioPilot.Cli.exe routine list
1. Desk [enabled] | Output: Speakers | Hotkey: Ctrl+Alt+D | Trigger: Hotkey: Ctrl+Alt+D | Tray: shown | Id: routine-desk
2. Discord [enabled] | Output: Headset | Hotkey: none | Trigger: Application launch: Discord
  | Application audio only | App: C:\Apps\Discord\Discord.exe | Scope: app-only audio
  | Conflict: Application trigger for Discord conflicts with 1 other enabled routine:
  different output targets. | Tray: hidden | Id: routine-discord
3. Spotify [enabled] | Output: Speakers | Hotkey: none | Trigger: Application launch: SpotifyAB SpotifyMusic | App: SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify | Tray: hidden | Id: routine-spotify
4. Big Picture [enabled] | Output: Headset | Hotkey: none | Trigger: Steam Big Picture | Restore on exit | Tray: hidden | Id: routine-big-picture
5. Docked [enabled] | Output: Speakers | Hotkey: none | Trigger: Device change
  | Conflict: Device change conflicts with 1 other enabled routine: different output targets.
  | Tray: hidden | Id: routine-device-change
6. Startup Ready [enabled] | Output: Speakers | Hotkey: none | Trigger: AudioPilot startup | Tray: hidden | Id: routine-audiopilot-startup
```

```json
{
  "schemaVersion": "1.0.0",
  "data": {
    "routines": [
      {
        "order": 1,
        "id": "routine-desk",
        "name": "Desk",
        "enabled": true,
        "hotkey": "Ctrl+Alt+D",
        "triggerMode": "Hotkey",
        "usesApplicationTrigger": false,
        "triggerAppPath": "",
        "restorePreviousAudioOnDeactivate": false,
        "switchOutputPerApp": false,
        "showInTrayMenu": true,
        "triggerSummary": "Hotkey: Ctrl+Alt+D | Tray menu",
        "outputDeviceId": "out-1",
        "outputDeviceName": "Speakers",
        "inputDeviceId": "",
        "inputDeviceName": "",
        "targetSummary": "Output: Speakers",
        "conflictSummary": ""
      },
      {
        "order": 2,
        "id": "routine-discord",
        "name": "Discord",
        "enabled": true,
        "hotkey": "",
        "triggerMode": "Application launch",
        "usesApplicationTrigger": true,
        "triggerAppPath": "C:\\Apps\\Discord\\Discord.exe",
        "restorePreviousAudioOnDeactivate": false,
        "switchOutputPerApp": true,
        "showInTrayMenu": false,
        "triggerSummary": "Application launch: Discord | Application audio only",
        "outputDeviceId": "out-2",
        "outputDeviceName": "Headset",
        "inputDeviceId": "",
        "inputDeviceName": "",
        "targetSummary": "Output: Headset",
        "conflictSummary": "Application trigger for Discord conflicts with 1 other enabled routine: different output targets."
      },
      {
        "order": 3,
        "id": "routine-spotify",
        "name": "Spotify",
        "enabled": true,
        "hotkey": "",
        "triggerMode": "Application launch",
        "usesApplicationTrigger": true,
        "triggerAppPath": "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify",
        "restorePreviousAudioOnDeactivate": false,
        "switchOutputPerApp": false,
        "showInTrayMenu": false,
        "triggerSummary": "Application launch: SpotifyAB SpotifyMusic",
        "outputDeviceId": "out-1",
        "outputDeviceName": "Speakers",
        "inputDeviceId": "",
        "inputDeviceName": "",
        "targetSummary": "Output: Speakers",
        "conflictSummary": ""
      },
      {
        "order": 4,
        "id": "routine-big-picture",
        "name": "Big Picture",
        "enabled": true,
        "hotkey": "",
        "triggerMode": "Steam Big Picture",
        "usesApplicationTrigger": false,
        "triggerAppPath": "",
        "restorePreviousAudioOnDeactivate": true,
        "switchOutputPerApp": false,
        "showInTrayMenu": false,
        "triggerSummary": "Steam Big Picture | Restore on exit",
        "outputDeviceId": "out-2",
        "outputDeviceName": "Headset",
        "inputDeviceId": "",
        "inputDeviceName": "",
        "targetSummary": "Output: Headset",
        "conflictSummary": ""
      },
      {
        "order": 5,
        "id": "routine-device-change",
        "name": "Docked",
        "enabled": true,
        "hotkey": "",
        "triggerMode": "Device change",
        "usesApplicationTrigger": false,
        "triggerAppPath": "",
        "restorePreviousAudioOnDeactivate": false,
        "switchOutputPerApp": false,
        "showInTrayMenu": false,
        "triggerSummary": "Device change",
        "outputDeviceId": "out-1",
        "outputDeviceName": "Speakers",
        "inputDeviceId": "",
        "inputDeviceName": "",
        "targetSummary": "Output: Speakers",
        "conflictSummary": "Device change conflicts with 1 other enabled routine: different output targets."
      },
      {
        "order": 6,
        "id": "routine-audiopilot-startup",
        "name": "Startup Ready",
        "enabled": true,
        "hotkey": "",
        "triggerMode": "AudioPilot startup",
        "usesApplicationTrigger": false,
        "triggerAppPath": "",
        "restorePreviousAudioOnDeactivate": false,
        "switchOutputPerApp": false,
        "showInTrayMenu": false,
        "triggerSummary": "AudioPilot startup",
        "outputDeviceId": "out-1",
        "outputDeviceName": "Speakers",
        "inputDeviceId": "",
        "inputDeviceName": "",
        "targetSummary": "Output: Speakers",
        "conflictSummary": ""
      }
    ]
  }
}
```

## Config Import, Export, And Recovery

For privacy, `diagnostics status` redacts absolute paths by default. Use `--show-paths` to include full paths unless `--redact` is also set.

Default background log files also avoid raw routine and device identifiers where practical. CLI output is user-requested output and may still include saved routine names, trigger targets, or device names unless you opt into `--redact`.

### Path safety

- `config export` and `config import` only allow paths under the current working directory, app base directory, or active settings directory by default.
- Use `--allow-any-path` to bypass this guard.
- `config export` and `config import` support plain `.json` files and `.zip` archives containing a root `settings.json` entry.
- Imported config files are size-limited to `256 KB`, and ZIP imports reject oversized `settings.json` entries before reading them.
- If neither `--merge` nor `--replace` is passed, `config import` merges by default.

### Validation and recovery

- If `settings.json` is corrupted or unreadable, recovery is attempted from `backups/settings.json.bak*`.
- Log archives and settings backups are retained in `backups/` with a max count of `5` in newest-first order.
- Log rollover is time-gated at `3` days and also size-triggered, with archive age pruning.
- `config import --merge` and `config import --replace` are mutually exclusive. Passing both returns invalid usage with exit code `2`.
- `config import --replace` requires a valid `SchemaVersion` and rejects unsupported top-level properties or newer schema versions.

### Settings File Structure

Settings are stored in `settings.json` with a nested structure organized into logical sections:

```json
{
  "SchemaVersion": "1.0.0",
  "Theme": "System",
  "RunAtStartup": false,
  "DeviceSwitching": {
    "Output": {
      "CycleDevices": [],
      "SwitchRoles": ["Multimedia", "Communications", "Console"],
      "SwitchHotkey": "",
      "ReverseSwitchHotkey": "",
      "HotkeysEnabled": true
    },
    "Input": {
      "CycleDevices": [],
      "SwitchRoles": ["Multimedia", "Communications", "Console"],
      "SwitchHotkey": "",
      "ReverseSwitchHotkey": "",
      "HotkeysEnabled": true
    }
  },
  "Hotkeys": {
    "App": { "ToggleAppVisibility": "Ctrl+Alt+H" },
    "Media": {
      "PlayPause": "Ctrl+Alt+P",
      "NextTrack": "Ctrl+Alt+.",
      "PreviousTrack": "Ctrl+Alt+,"
    },
    "Mute": {
      "MuteMic": "",
      "MuteSound": "",
      "Deafen": ""
    },
    "Volume": {
      "MasterVolumeUp": "",
      "MasterVolumeDown": "",
      "MasterVolumeStepPercent": 5,
      "MicVolumeUp": "",
      "MicVolumeDown": "",
      "MicVolumeStepPercent": 5
    },
    "Listen": {
      "ListenToInput": "",
      "MonitorOutputDeviceId": "",
      "MonitorOutputDeviceName": ""
    },
    "Global": { "AdditionalStandaloneKeys": [] }
  },
  "Routines": { "Items": [] },
  "Overlay": {
    "Enabled": true,
    "Position": "TopRight",
    "DurationSeconds": 2.0
  },
  "Miscellaneous": {
    "BluetoothReconnectEnabled": true,
    "PreserveAudioLevels": true,
    "DeviceReferenceFileMode": "Off",
    "LogLevel": "Info",
    "RedactLogContent": true,
    "AutoSaveEnabled": false,
    "PlayDialogSounds": true,
    "ScheduleTimeZoneId": "Pacific Standard Time",
    "AutoScrollToMixerOnRestore": true,
    "SuppressDeviceStartupWarnings": false
  },
  "AdvancedTuning": {
    "BluetoothReconnect": {
      "MaxAttempts": 2,
      "AttemptTimeoutMs": 1200,
      "CooldownMs": 5000,
      "OnlyLikelyBluetoothEndpoints": true,
      "CachedEndpointVisibilityProbeAttempts": 4,
      "CachedEndpointVisibilityProbeDelayMs": 120
    },
    "SteamBigPicture": {
      "MonitorDebounceMs": 150,
      "ConfirmationDelayMs": 650
    }
  }
}
```

Sections:

- **DeviceSwitching**: Output/input device cycles, hotkeys, roles
- **Hotkeys**: Organized by function (App, Media, Mute, Volume, Listen, Global)
- **Routines**: Contains `Items` array with all saved routines
- **Overlay**: Overlay display settings
- **Miscellaneous**: BluetoothReconnectEnabled, **PreserveAudioLevels**, DeviceReferenceFileMode, LogLevel, RedactLogContent, AutoSaveEnabled, PlayDialogSounds, ScheduleTimeZoneId, AutoScrollToMixerOnRestore, SuppressDeviceStartupWarnings
- **AdvancedTuning**: Bluetooth reconnect and Steam Big Picture timing parameters

CLI `config` commands use flat keys that map to these nested paths (e.g., `output-switch-hotkey` maps to `DeviceSwitching.Output.SwitchHotkey`).

## Supported Config Keys

### General app

`theme`, `auto-save-enabled`, `play-dialog-sounds`, `run-at-startup`, `preserve-audio-levels`, `auto-scroll-to-mixer-on-restore`, `suppress-device-startup-warnings`, `generate-device-reference-file`, `schedule-timezone` (Windows
time zone ID, e.g., `Pacific Standard Time` or `UTC`)

### Logging and overlay

`log-level` (`Trace|Debug|Info|Warning|Error|Fatal|None`; use `None` to disable logging), `overlay-enabled`, `overlay-position`, `overlay-duration-seconds`

### Switching and hotkeys

`output-switch-hotkey`, `output-reverse-switch-hotkey`,
`output-switch-roles`, `input-switch-hotkey`,
`input-reverse-switch-hotkey`, `input-switch-roles`,
`output-hotkeys-enabled`, `input-hotkeys-enabled`, `toggle-app-visibility-hotkey`,
`show-current-track-hotkey`,
`play-pause-hotkey`, `next-track-hotkey`, `previous-track-hotkey`,
`mute-mic-hotkey`, `mute-sound-hotkey`, `deafen-hotkey`,
`listen-to-input-hotkey`, `master-volume-up-hotkey`,
`master-volume-down-hotkey`, `master-volume-step-percent`,
`mic-volume-up-hotkey`, `mic-volume-down-hotkey`, `mic-volume-step-percent`,
`additional-standalone-hotkey-keys`

### Listen and reconnect

`listen-monitor-output-device-id` (empty means use the current default output), `bluetooth-reconnect-enabled`

`bluetooth-reconnect-max-attempts` (`1` to `3`),
`bluetooth-reconnect-attempt-timeout-ms` (`250` to `10000`),
`bluetooth-reconnect-cooldown-ms` (`500` to `30000`),
`bluetooth-reconnect-only-likely` (`true|false`),
`bluetooth-reconnect-cached-endpoint-probe-attempts` (`1` to `10`),
`bluetooth-reconnect-cached-endpoint-probe-delay-ms` (`25` to `1000`),
`steam-big-picture-monitor-debounce-ms` (`25` to `2000`),
`steam-big-picture-confirmation-delay-ms` (`50` to `5000`)

`output-switch-roles` and `input-switch-roles` accept a comma-separated subset of `Multimedia,Communications,Console` or `all`.

`master-volume-step-percent` and `mic-volume-step-percent` accept whole numbers from `1` to `100`.

`suppress-device-startup-warnings` controls only the startup message box for disconnected output/input cycle devices. `config validate` still reports those diagnostics for automation and troubleshooting.

`generate-device-reference-file` values:

- `false`: disable reference file generation
- `true`: emit plaintext device IDs
- `hashed`: emit anonymized IDs

### Hotkey examples

Volume hotkey examples:

```powershell
# Raise master output by 5% per press
AudioPilot.Cli.exe config set master-volume-up-hotkey "Ctrl+Alt+PageUp"
AudioPilot.Cli.exe config set master-volume-down-hotkey "Ctrl+Alt+PageDown"
AudioPilot.Cli.exe config set master-volume-step-percent 5

# Raise or lower microphone input by 3% per press
AudioPilot.Cli.exe config set mic-volume-up-hotkey "Ctrl+Alt+Home"
AudioPilot.Cli.exe config set mic-volume-down-hotkey "Ctrl+Alt+End"
AudioPilot.Cli.exe config set mic-volume-step-percent 3
```

Mouse hotkey examples:

```powershell
# Use side buttons for media navigation
AudioPilot.Cli.exe config set next-track-hotkey "Ctrl+MouseX1"
AudioPilot.Cli.exe config set previous-track-hotkey "Ctrl+MouseX2"

# Use modified wheel directions for volume or mute actions
AudioPilot.Cli.exe config set master-volume-up-hotkey "Ctrl+WheelUp"
AudioPilot.Cli.exe config set mute-mic-hotkey "Ctrl+WheelDown"

# Tilt-wheel directions are supported on compatible mice
AudioPilot.Cli.exe config set next-track-hotkey "Ctrl+WheelRight"
AudioPilot.Cli.exe config set previous-track-hotkey "Ctrl+WheelLeft"
```

Supported mouse and wheel tokens are `MouseLeft`, `MouseRight`,
`MouseMiddle`, `MouseX1`, `MouseX2`, `WheelUp`, `WheelDown`, `WheelLeft`,
and `WheelRight`.
Mouse-button and wheel hotkeys must include at least one modifier. Bare
text-producing keys such as `A`, `1`, or `/` also require a modifier,
while standalone function keys and dedicated media keys are allowed.

CLI and JSON hotkeys always use canonical, layout-independent tokens. The AudioPilot UI resolves number-row, punctuation, international, and numpad-decimal key labels through the active Windows keyboard layout without rewriting
those saved tokens.

Advanced users can allow a small set of extra standalone keys with:

```powershell
AudioPilot.Cli.exe config set additional-standalone-hotkey-keys "PrintScreen,Home"
```

The allowlist accepts up to `8` entries from `PrintScreen`, `Pause`, `Scroll`, `Insert`, `Home`, `End`, `PageUp`, `PageDown`, `Delete`, and `NumLock`.

Persisted advanced-tuning examples:

```powershell
AudioPilot.Cli.exe config get bluetooth-reconnect-cached-endpoint-probe-attempts
AudioPilot.Cli.exe config set bluetooth-reconnect-cached-endpoint-probe-attempts 5
AudioPilot.Cli.exe config set bluetooth-reconnect-cached-endpoint-probe-delay-ms 150
AudioPilot.Cli.exe config set steam-big-picture-monitor-debounce-ms 200
AudioPilot.Cli.exe config set steam-big-picture-confirmation-delay-ms 800
```

## Advanced Runtime Tuning

Some advanced defaults are persisted under the nested `AdvancedTuning` section in `settings.json` and are applied during startup, config import, and external settings reload. You can manage the persisted ones through flat `config
set/get` keys, and they are stored back into `settings.json -> AdvancedTuning`. Some runtime knobs can also be seeded from persisted `AdvancedTuning` defaults.

Example:

```json
{
  "SchemaVersion": "1.0.0",
  "AdvancedTuning": {
    "BluetoothReconnect": {
      "MaxAttempts": 2,
      "AttemptTimeoutMs": 1200,
      "CooldownMs": 5000,
      "OnlyLikelyBluetoothEndpoints": true,
      "CachedEndpointVisibilityProbeAttempts": 4,
      "CachedEndpointVisibilityProbeDelayMs": 120
    },
    "SteamBigPicture": {
      "MonitorDebounceMs": 150,
      "ConfirmationDelayMs": 650
    }
  }
}
```

## Runtime Tuning Keys

`runtime get` and `runtime set` always affect the current process only.

Some runtime knobs also have persisted startup defaults under `settings.json -> AdvancedTuning`. In those cases, `config set` changes the saved startup default, while `runtime set` still acts only as an in-memory override for the
current app process.

### Switch behavior

`auto-save-debounce-ms` (`100` to `10000`), `output-switch-debounce-ms` (`25` to `2000`),
`input-switch-debounce-ms` (`25` to `2000`), `switch-retry-delay-ms` (`10` to `1000`),
`switch-retry-max-delay-ms` (`10` to `2000`), `switch-max-retries` (`1` to `10`)

### Bluetooth reconnect core

`bluetooth-reconnect-max-attempts` (`1` to `3`), `bluetooth-reconnect-attempt-timeout-ms` (`250` to `10000`), `bluetooth-reconnect-cooldown-ms` (`500` to `30000`), `bluetooth-reconnect-only-likely` (`true|false`)

These reconnect-core values can also be persisted as startup defaults under `settings.json -> AdvancedTuning -> BluetoothReconnect`.

### Bluetooth reconnect timing and adaptive windows

`bluetooth-reconnect-post-attempt-quick-recheck-delay-ms` (`50` to `5000`),
`bluetooth-reconnect-post-attempt-recheck-delay-ms` (`100` to `10000`),
`bluetooth-reconnect-success-recheck-initial-interval-ms` (`50` to `5000`),
`bluetooth-reconnect-success-recheck-mid-interval-ms` (`50` to `5000`),
`bluetooth-reconnect-success-recheck-interval-ms` (`100` to `5000`),
`bluetooth-reconnect-success-observed-recheck-interval-ms` (`50` to `5000`),
`bluetooth-reconnect-success-stabilize-window-ms` (`1000` to `120000`),
`bluetooth-reconnect-success-active-stable-ms` (`100` to `20000`),
`bluetooth-reconnect-success-timeout-grace-ms` (`0` to `10000`),
`bluetooth-reconnect-deferred-auto-switch-window-ms` (`1000` to `300000`)

### Bluetooth reconnect timeout circuit

`bluetooth-reconnect-timeout-circuit-threshold` (`1` to `10`), `bluetooth-reconnect-timeout-circuit-open-ms` (`1000` to `900000`)

### Bluetooth cached-endpoint probe

`bluetooth-reconnect-cached-endpoint-probe-attempts` (`1` to `10`), `bluetooth-reconnect-cached-endpoint-probe-delay-ms` (`25` to `1000`)

These cached-endpoint probe values can also be persisted as startup defaults under `settings.json -> AdvancedTuning -> BluetoothReconnect`.

### Hotplug and hotkey timing

`hotplug-refresh-debounce-ms` (`50` to `2000`), `hotplug-refresh-fast-path-debounce-ms` (`25` to `1000`), `hotplug-connected-overlay-suppress-ms` (`0` to `15000`), `resume-hotkey-retry-delay-ms` (`50` to `5000`)

### Mixer refresh, diagnostics, and cache

`mixer-session-refresh-debounce-ms` (`50` to `2000`),
`show-window-mixer-refresh-debounce-ms` (`25` to `2000`),
`visible-mixer-activation-refresh-debounce-ms` (`25` to `2000`),
`mixer-snapshot-cache-interactive-ms` (`0` to `2000`),
`mixer-snapshot-cache-background-ms` (`0` to `4000`),
`mixer-diagnostics-summary-window-seconds` (`5` to `300`),
`mixer-cache-window-diagnostics-log-every-n-refreshes` (`1` to `1000`)

### Media overlay telemetry and state maintenance

`media-overlay-telemetry-flush-every-events` (`1` to `1000`),
`media-overlay-telemetry-flush-interval-seconds` (`1` to `600`),
`media-overlay-state-trim-command-cadence` (`1` to `1000`),
`media-overlay-state-trim-interval-seconds` (`1` to `600`)

### Steam Big Picture detection timing

`steam-big-picture-monitor-debounce-ms` (`25` to `2000`), `steam-big-picture-confirmation-delay-ms` (`50` to `5000`)

These Steam Big Picture detection timing values can also be persisted as startup defaults under `settings.json -> AdvancedTuning -> SteamBigPicture`.

### Advanced runtime tuning quick commands

```powershell
# Inspect current advanced runtime knobs
AudioPilot.Cli.exe runtime get auto-save-debounce-ms
AudioPilot.Cli.exe runtime get output-switch-debounce-ms
AudioPilot.Cli.exe runtime get switch-max-retries
AudioPilot.Cli.exe runtime get show-window-mixer-refresh-debounce-ms
AudioPilot.Cli.exe runtime get visible-mixer-activation-refresh-debounce-ms
AudioPilot.Cli.exe runtime get mixer-diagnostics-summary-window-seconds
AudioPilot.Cli.exe runtime get mixer-cache-window-diagnostics-log-every-n-refreshes
AudioPilot.Cli.exe runtime get hotplug-refresh-fast-path-debounce-ms
AudioPilot.Cli.exe runtime get bluetooth-reconnect-timeout-circuit-threshold
AudioPilot.Cli.exe runtime get bluetooth-reconnect-timeout-circuit-open-ms
AudioPilot.Cli.exe runtime get bluetooth-reconnect-cached-endpoint-probe-attempts
AudioPilot.Cli.exe runtime get bluetooth-reconnect-cached-endpoint-probe-delay-ms
AudioPilot.Cli.exe runtime get bluetooth-reconnect-success-recheck-initial-interval-ms
AudioPilot.Cli.exe runtime get bluetooth-reconnect-deferred-auto-switch-window-ms
AudioPilot.Cli.exe runtime get bluetooth-reconnect-success-timeout-grace-ms
AudioPilot.Cli.exe runtime get steam-big-picture-monitor-debounce-ms
AudioPilot.Cli.exe runtime get steam-big-picture-confirmation-delay-ms
AudioPilot.Cli.exe runtime get media-overlay-telemetry-flush-every-events
AudioPilot.Cli.exe runtime get media-overlay-telemetry-flush-interval-seconds
AudioPilot.Cli.exe runtime get media-overlay-state-trim-command-cadence
AudioPilot.Cli.exe runtime get media-overlay-state-trim-interval-seconds

# Tune diagnostics cadence for a noisy debugging session
AudioPilot.Cli.exe runtime set mixer-diagnostics-summary-window-seconds 15
AudioPilot.Cli.exe runtime set mixer-cache-window-diagnostics-log-every-n-refreshes 5

# Tune switch debounce and retry behavior
AudioPilot.Cli.exe runtime set auto-save-debounce-ms 900
AudioPilot.Cli.exe runtime set output-switch-debounce-ms 140
AudioPilot.Cli.exe runtime set input-switch-debounce-ms 140
AudioPilot.Cli.exe runtime set switch-retry-delay-ms 60
AudioPilot.Cli.exe runtime set switch-retry-max-delay-ms 120
AudioPilot.Cli.exe runtime set switch-max-retries 4

# Tune reconnect timeout-circuit behavior
AudioPilot.Cli.exe runtime set bluetooth-reconnect-timeout-circuit-threshold 3
AudioPilot.Cli.exe runtime set bluetooth-reconnect-timeout-circuit-open-ms 120000

# Tune reconnect recheck cadence and window behavior
AudioPilot.Cli.exe runtime set bluetooth-reconnect-post-attempt-quick-recheck-delay-ms 180
AudioPilot.Cli.exe runtime set bluetooth-reconnect-post-attempt-recheck-delay-ms 700
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-recheck-initial-interval-ms 180
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-recheck-mid-interval-ms 300
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-recheck-interval-ms 450
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-observed-recheck-interval-ms 220
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-stabilize-window-ms 10000
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-active-stable-ms 800
AudioPilot.Cli.exe runtime set bluetooth-reconnect-success-timeout-grace-ms 750
AudioPilot.Cli.exe runtime set bluetooth-reconnect-deferred-auto-switch-window-ms 20000

# Tune cached-endpoint probing and Steam Big Picture detection behavior
AudioPilot.Cli.exe runtime set bluetooth-reconnect-cached-endpoint-probe-attempts 5
AudioPilot.Cli.exe runtime set bluetooth-reconnect-cached-endpoint-probe-delay-ms 150
AudioPilot.Cli.exe runtime set steam-big-picture-monitor-debounce-ms 200
AudioPilot.Cli.exe runtime set steam-big-picture-confirmation-delay-ms 800

# Tune media overlay maintenance cadence for debugging sessions
AudioPilot.Cli.exe runtime set media-overlay-telemetry-flush-every-events 16
AudioPilot.Cli.exe runtime set media-overlay-telemetry-flush-interval-seconds 30
AudioPilot.Cli.exe runtime set media-overlay-state-trim-command-cadence 16
AudioPilot.Cli.exe runtime set media-overlay-state-trim-interval-seconds 15
```

Note: `bluetooth-reconnect-enabled`, `auto-save-enabled`, `play-dialog-sounds`, `log-level`, `redact-log-content`, `suppress-device-startup-warnings`, and `device-reference-file-mode` are persisted under `Miscellaneous`.
`bluetooth-reconnect-max-attempts`, `bluetooth-reconnect-attempt-timeout-ms`, `bluetooth-reconnect-cooldown-ms`, `bluetooth-reconnect-cached-endpoint-probe-attempts`, `bluetooth-reconnect-cached-endpoint-probe-delay-ms`,
`bluetooth-reconnect-only-likely`, `steam-big-picture-monitor-debounce-ms`, and `steam-big-picture-confirmation-delay-ms` are persisted via `config set` and stored under `settings.json -> AdvancedTuning`. All `runtime get` and
`runtime set` commands still modify only the current process.

## Validation And Diagnostic Codes

`config validate` returns only configuration diagnostics suitable for automation.

- Exit `0`: no warnings.
- Exit `5`: warnings found.

`refresh --json` returns:

```json
{
  "schemaVersion": "1.0.0",
  "data": {
    "success": true,
    "diagCode": "refresh-success"
  }
}
```

CLI surfaces stable `diag-code` values in text and JSON payloads for machine parsing.

- `switch-dry-run`: dry-run preview generated without mutation.
- `switch-success`: switch completed successfully.
- `require-current-mismatch`: `--require-current` guard failed.
- `routine-run-success`: routine execution completed successfully.
- `routine-state-updated`, `routine-state-unchanged`: routine enablement change result.
- `routine-not-found`, `routine-selector-ambiguous`, `routine-disabled`: routine targeting or state precondition failed.
- `wait-device-found`: `wait` detected the requested device.
- `wait-device-timeout`: `wait` timed out before the device became available.
- `config-export-success`, `config-export-failed`: config export result.
- `config-import-success`, `config-import-file-missing`, `config-import-failed`: config import result.
- `invalid-hotkey-*`: invalid configured hotkey warning from validation.
- `output-cycle-disconnected-devices`, `input-cycle-disconnected-devices`: disconnected cycle entries detected.

## Automation Examples

```powershell
# Parse machine-readable status
$status = AudioPilot.Cli.exe status --json | ConvertFrom-Json
$status.data.availableOutputDevices
$status.data.bluetoothReconnect

# Use redaction when command output may be captured in shared logs
$safeStatus = AudioPilot.Cli.exe status --json --redact | ConvertFrom-Json
$safeStatus.data.currentOutputDeviceId
$safeStatus.data.currentOutputDeviceName

# Redacted routine output preserves ids while anonymizing display values
$routines = AudioPilot.Cli.exe routine list --json --redact | ConvertFrom-Json
$routines.data.routines[0].id
$routines.data.routines[0].name

# Fail script early if output preflight fails
AudioPilot.Cli.exe cycle test output --json | Out-Null
if ($LASTEXITCODE -ne 0) {
  throw "Output cycle preflight failed"
}

# Validate configured output cycle and inspect issues
$validation = AudioPilot.Cli.exe cycle validate output --json | ConvertFrom-Json
if (-not $validation.data.isValid) {
  $validation.data.disconnectedDeviceNames
}

# Read listen state and monitor target from status
$status = AudioPilot.Cli.exe status --json | ConvertFrom-Json
$status.data.currentInputListenEnabled
$status.data.listenMonitorTargetOutputDeviceName

# Listen commands return the same listen status payload
$listen = AudioPilot.Cli.exe listen toggle --json | ConvertFrom-Json
$listen.data.currentInputListenEnabled
$listen.data.listenMonitorTargetOutputDeviceName

# Diagnostics status provides log and backup health details
$diag = AudioPilot.Cli.exe diagnostics status --json | ConvertFrom-Json
$diag.data.logBackupFiles
$diag.data.settingsBackupFiles
$diag.data.bluetoothReconnect

# Redaction overrides explicit path display for share-safe diagnostics output
$safeDiag = AudioPilot.Cli.exe diagnostics status --json --show-paths --redact | ConvertFrom-Json
$safeDiag.data.logDirectory
$safeDiag.data.settingsPath
```

## Package Usage

```powershell
# Self-contained package: no separate .NET runtime install required
Expand-Archive .\AudioPilot-1.0.0-SelfContained-win-x64.zip -DestinationPath .\AudioPilot
.\AudioPilot\AudioPilot.Cli.exe status --json

# Framework-dependent package: requires the .NET Desktop Runtime
Expand-Archive .\AudioPilot-1.0.0-FrameworkDependent-win-x64.zip -DestinationPath .\AudioPilot-FD
.\AudioPilot-FD\AudioPilot.Cli.exe version
```

## Contributor Notes

- Command parsing: `AudioPilot/Cli/CliCommand.cs`
- Command execution: `AudioPilot/Cli/CliCommandExecutor.cs`
- Headless host entry point: `AudioPilot.CliHost/Program.cs`
- In-app CLI runtime bridge: `AudioPilot/ViewModels/AppViewModel.Cli.cs`

Related docs:

- User guide: [USER_GUIDE.md](USER_GUIDE.md)
- Developer guide: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)
