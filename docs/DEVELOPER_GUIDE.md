# Developer Guide

This guide explains the AudioPilot architecture, repo layout, and contributor guardrails.

Use this page when you need implementation context. Use [CONTRIBUTING.md](CONTRIBUTING.md) for the contributor workflow and [CLI.md](CLI.md) for the exact CLI contract.

## Platform Baseline

- The compile-time API and installer floor is `net10.0-windows10.0.19041.0`, corresponding to Windows 10 version 2004 (build 19041).
- Official support covers Windows 11 and currently supported Windows 10 Enterprise/LTSC editions that meet that build floor, subject to the Microsoft and .NET 10 support lifecycles.
- The build floor expresses technical compatibility; it does not make every newer Windows build or edition vendor-supported.
- Windows 8 and 8.1 are not supported.
- Do not raise the Windows SDK floor casually. Guard newer APIs explicitly if support policy changes.

## Architecture At A Glance

- UI: WPF windows plus view models.
- Coordinators: multi-service orchestration for app and workflow behavior.
- Services: domain behavior grouped by audio, Bluetooth, configuration, hotkeys, UI, and internal coordination.
- Platform: low-level OS integration, COM, single-instance handling, and cache/materialization helpers.
- Threading: STA UI thread plus a dedicated Core Audio COM worker.
- Hotkeys: hybrid WM_HOTKEY plus low-level keyboard hook support.

Registered visibility shortcuts use `WM_HOTKEY` delivery so Windows can grant foreground activation permission. The keyboard hook must pass those shortcuts through instead of consuming them; media keys and other hook-delivered actions retain their existing routing. See [Windows hotkey activation behavior](https://devblogs.microsoft.com/oldnewthing/20090226-00/?p=19013).

Window restoration uses normal activation. When Windows denies foreground permission, a normal window stays behind the foreground window and logs `window-activation-deferred` at Debug level. Do not use temporary topmost promotion to simulate activation: WPF's `IsActive` can be true while another process still owns the native foreground window.

## Repository Taxonomy

### `Platform/`

Use this for low-level OS-facing helpers such as COM, threading, single-instance activation, and cache materialization.

Do not put workflow orchestration here.

### `Coordinators/`

Use coordinators when one behavior spans multiple services or UI concerns.

Keep core domain logic in services whenever possible.

### `Services/`

The repo separates services by responsibility, including audio, Bluetooth, configuration, hotkeys, internal coordination, and UI concerns.

`Services/Internal` is reserved for service-layer coordination that should not become a broad app-facing entry point.

## JSON Serialization

All application JSON uses the .NET runtime's `System.Text.Json`. Settings and
routine persistence share the cached, read-only `SettingsJson.Options`; CLI
response envelopes and IPC retain their separate protocol options. Imports use
`SettingsJson.ImportOptions` to reject unmapped and non-persisted properties at
every nesting level using the serializer's model contracts.

Settings and routine imports accept case-insensitive property names and require
valid JSON syntax and value types. Duplicate names, including case variants, are
rejected in imports. Partial settings imports merge nested objects, replace
arrays, and ignore null values for known properties. Unknown null-valued keys
still fail validation. Full replacements are normalized after reading.
Use `JsonDocument` for read-only inspection and `JsonNode` when modifying a tree.

`AudioRoutine` defers trigger-dependent normalization through the JSON
deserialization callbacks so property order cannot discard trigger configuration.
Persisted model enums use generic `JsonStringEnumConverter<TEnum>` attributes.
`DefinedEnumJsonConverterFactory` wraps the runtime converters to reject
undefined enum values before model setters can normalize them, while preserving
named model enums and numeric schedule weekdays. UI-only properties use
`JsonIgnore`. Settings extension data uses `JsonElement`
and survives cloning without converting date-like strings or large numbers.

### Settings Schema Evolution

`Settings.CurrentSchemaVersion` is the settings storage contract, independent of
`AudioPilotVersion` and the CLI response envelope version. The first released
contract is `1.0.0`. Freeze its key paths, types, and meanings when releasing it.
Pre-release layouts are not migration sources.

Currently, missing settings receive model defaults. `SettingsMigrationService`
validates versions; it does not implement conversions. Older schemas are rejected,
and a newer schema is not rewritten during loading. Saves reject newer schemas
in both the model and the destination file, checking the file under the settings
lock before replacing it. This also protects against a newer app saving between
load and save; schema refusal does not redirect the write to the fallback path.

When a future change needs migration, implement and test it in the same release
as the change, before distribution. Users should not need to edit released
settings manually. Advance the schema when adding persisted keys, renaming or
moving keys, changing types, or changing stored meanings. Current-schema loading
removes unknown keys, and imports reject them, so even additive storage changes
need a new schema to protect those values from older builds. A migration for an
additive change may only need to retain existing values and supply safe defaults.
App changes that do not alter the storage contract need no schema bump.

- Transform versioned JSON before deserializing into the new model, so old keys
  and values are not discarded before migration can read them.
- Use the same ordered migration steps for startup, backup recovery, and imports.
  Partial imports must preserve omitted values instead of introducing defaults.
- Preserve a recoverable original before replacing a file. Validate the migrated
  result and write atomically under the settings lock; conversion or write
  failures must leave the original available and report an actionable error.
- Define downgrade handling explicitly: never silently save over an unsupported
  newer schema and discard fields the running version does not understand.
- Keep fixed JSON fixtures from released schemas in `AudioPilot.Tests/Fixtures/Settings/`.
  The `1.0.0.json` fixture checks non-default values through loading, saving, and
  both import modes. Do not regenerate old fixtures from changed models. Test
  multiple-step upgrades, repeated loads, imports, and failure recovery. Add the
  first conversion when the first real schema change is known.

## Windows And UI Shape

AudioPilot uses four main windows:

- `MainWindow`: the on-demand primary view for device lists, mixer, settings, and restore behavior.
- `OverlayWindow`: transient visual feedback for switching, volume, media, and similar quick actions.
- `RoutineEditorWindow`: modal editor for creating or updating routines.
- `PackagedAppPickerWindow`: modal picker used when an app-start routine targets a packaged app rather than a normal desktop `.exe`.

### UI Layer Rules

- `MainWindow` should stay focused on window wiring, dispatcher boundaries, and concrete WPF interactions.
- `AppRuntimeHost` owns eager runtime composition, hotkeys, hot-plug/resume subscriptions, forwarded CLI work, and ordered shutdown. It must not depend on a materialized main window.
- `AppTrayIconService` owns the taskbar icon and tray menu. `AppMainWindowManager` is the only production path that constructs, shows, hides, or closes `MainWindow`.

- `ViewModels/AppViewModel*.cs` owns application state, settings drafts, command entry points, and orchestration across services.
- `AudioTestingViewModel` owns endpoint-test commands, monitor preferences, meter updates, and test-service cleanup. Device panels bind through `AppViewModel.AudioTesting`; switching the default device remains application orchestration.
- Resume-registration results belong to the coordinator layer, keeping recovery and registration coordinators independent of nested `AppViewModel` types.
- `MainWindow*Helper` and `AppViewModel*Helper` types are the preferred extraction seam for narrow UI-adjacent behavior.
- Services should not reach up into WPF controls or assume a specific window state.

Practical rule: concrete-control behavior belongs in a window or window helper;
tray/menu behavior belongs in `AppTrayIconService`. If logic reasons about
switching policy, persistence sequencing, or app state, it probably belongs in
`AppViewModel`, a coordinator, or a service.

### Windows Sign-In Registration

`StartupService` manages either the per-user Run value or the optional Task Scheduler logon task selected by `Miscellaneous.UseScheduledStartup`. `WindowsStartupTaskStore` owns `AudioPilot Startup <SID>` in the root task folder, scopes COM objects to each operation, and registers with the interactive user token and least privilege. It adds no package dependency.

The task uses a three-second logon delay, normal process priority, no idle/network/battery requirement, and no execution time limit. `StartupTaskDefinition` compares effective task values because Windows omits schema defaults and converts the trigger SID to an account name. Initialization and path repair preserve a manually disabled task or trigger; an explicit startup-enable action restores it.

The live Run at Startup toggle, UI saves, CLI configuration, and imports persist startup changes with a registration rollback if registration or settings persistence fails. The debounced live toggle checks for newer user intent again after acquiring the settings write lock and runs registration off the UI thread. Manual JSON edits are reconciled at the next app launch. MSI removes the task only after successful uninstall, excluding major upgrades. Portable copies must disable startup before deletion. Live tests and benchmarks must preserve both the Run value and the task, including its disabled state. The production task launches the GUI executable directly, without a shell. Console test helpers must use `ProcessStartInfo.CreateNoWindow` with `UseShellExecute=false` to prevent console flashes.

`StartupTaskIntegrationTests` is explicit-only: it briefly registers a uniquely named task, checks Windows normalization and disabling, and removes it in `finally`. It does not launch the application or affect the user's AudioPilot task. Run it with `dotnet test --project AudioPilot.Tests/AudioPilot.Tests.csproj -c Release -- --filter-query '/*/*/StartupTaskIntegrationTests/*' --explicit on`. Actual sign-in timing needs measurement across real logon sessions.

### Windowless Tray Startup

The tray service uses H.NotifyIcon.Wpf and explicitly creates its native icon with
`ForceCreate(enablesEfficiencyMode: false)`. AudioPilot must not opt into process
throttling when hidden: hotkeys, overlays, monitoring, and scheduled routines remain
active. Readiness requires native registration, so failed registration keeps the
main window accessible. Cached bitmap frames are converted synchronously through
the library's bitmap stream API, avoiding its URI-based `IconSource` conversion.
Mouse and keyboard menus share signed screen-coordinate handling; Escape returns
focus to the notification area. Native tray regression tests are opt-in visual tests.
If dynamic menu population fails, Show and Exit remain available for recovery.

Configured launches initialize `AppRuntimeHost` without constructing `MainWindow`
or a main HWND. The tray icon is published only after startup initialization has
produced a usable runtime. At the next dispatcher-idle opportunity, the tray
service resolves and measures an unattached representative menu template once;
the real menu is still rebuilt from current device, routine, hotkey, and window
state every time it opens.

Show, Settings, and activation requests go through `AppMainWindowManager`, which
serializes creation and retains one successfully created window for the rest of
the process. The default Output panel is created with that window, while Input,
Routines, and Settings are materialized only when first selected. Hide before
first Show is a no-op and must never call the window factory. A failed on-demand
construction leaves the tray runtime operational; another attempt occurs only
after a later explicit request.

The overlay service starts disabled until saved settings are applied. When enabled,
both visible and tray startup prepare a reusable overlay without showing it or
creating its native window handle. Disabling overlays releases presenters and
invalidates queued preparation/presentation work. Routine scheduling and hotkeys
remain independent of the views; the routine status display timer runs only while
the Routines tab is visible. Deferred tab content is retained after first selection
so tab changes preserve edits and do not repeatedly construct controls.

Overlay HWNDs use native input transparency and no-activation styles so they cannot
intercept clicks or steal focus. Display-change work is marshalled to the overlay
dispatcher. Fade-out storyboards are controllable and removed when interrupted or
completed, allowing rapid successive hotkeys to reuse the same window safely.

New windows are themed, measured, and positioned while their opacity is zero.
`WindowFirstPresentationHelper` reveals them after Loaded and Render dispatcher
passes. Modal AudioPilot windows use the same hidden-first path through
`DialogWindowHelper`, preventing default-white client frames and intermediate
placement from appearing during first presentation.

Normal runtime shutdown closes admission paths first, drains owned runtime work,
then disposes dialogs and the asynchronous logger before stopping the application
dispatcher. `App.OnExit` retains an idempotent logger/resource fallback for early
startup failures and OS-driven termination.

### App Dialogs

User-facing modal prompts go through the application-owned `IAppDialogService`.
Callers build app-native requests and await the result; production code outside
`NativeAppDialogFallback.cs` must not reference WPF message-box APIs.

- The service serializes dialogs on the UI dispatcher. Confirmations remain FIFO.
- Repeated acknowledgement dialogs share one window. Identical messages increase
  its repetition count, while a newer acknowledgement replaces older content.
- Caller cancellation removes an unobserved queued request. An active dialog
  closes only when its final coalesced caller cancels. Presentation rechecks the
  request on the UI dispatcher, including cancellation and newer content received
  while waiting for that dispatcher.
- Dynamic acknowledgement updates and cancellation-driven closes use one tracked
  presenter-operation pump. Keep presenter calls outside the dialog state lock
  and drain that pump during shutdown. Revalidate the target and revision inside
  the dispatched callback so stale operations cannot affect a later dialog.
- An explicit visible owner takes precedence, followed by the active AudioPilot
  window and the visible main window. A dialog is standalone and appears in the
  taskbar when no safe owner exists.
- Dialog text is selectable. Error and diagnostic requests can expose a Copy
  action that copies only the displayed message. Long messages scroll within the
  available space while the action buttons remain visible. Dismissing a custom
  confirmation without a cancel action returns Cancelled, never its first action.
- `IAppDialogSoundPlayer` maps dialog kinds to Windows system sounds. The
  presenter signals it only after the custom window first renders, so active
  acknowledgement updates do not replay a sound and failed custom presentation
  cannot double-sound before native fallback. The tray service reads the same
  live `IAppDialogService.SoundsEnabled` preference when sending a notification
  and passes it to H.NotifyIcon's `sound` option. `Miscellaneous.PlayAppSounds`
  is the persisted key for both dialog and tray notification sounds. Tests
  must inject or retain the noninteractive presenter rather than invoking the
  Windows sound backend.
- Logs contain kind, result, ownership mode, timing, repetition count, and
  fallback reason only. Never add message text or user-selected paths to dialog
  logs.
- Native Windows presentation is an emergency boundary for early startup,
  dispatcher shutdown, and custom XAML or theme failures. It uses a safe active
  owner when one is available and otherwise remains standalone. It is not a
  normal UI path.
- Tests install a process-wide noninteractive presenter in the assembly module
  initializer. New tests should inject `RecordingAppDialogService` when they need
  to assert requests or results.

Keep dialog workflows asynchronous end to end. Do not add synchronous wrappers,
dispatcher waits, `.Result`, or `.GetAwaiter().GetResult()`.

## Audio Implementation Split

The audio stack is intentionally split:

- default output and input switching uses an in-project `IPolicyConfig` COM bridge,
- enumeration, device notifications, and audio sessions use NAudio,
- process naming, enrichment, filtering, and cache behavior live in app services and helpers.

This keeps switching reliability under direct control while still using library support for discovery and session objects.

Endpoint tests and live microphone monitoring honor WASAPI silent-packet flags; packet bytes are ignored when the driver marks them silent.
Canceling a monitor configuration preserves the running test state. Hiding the window or leaving the device tab stops the test and cancels pending monitor changes.

NAudio endpoint tests request optional low latency with `WithLowLatency()`. The boolean parameter means `required`, not enabled; requiring it can fail when recording starts, after the builder has returned.
Let NAudio fall back to standard shared mode and inspect `LowLatencyUnavailableReason` in stream-start diagnostics. Keep player ownership outside `Init` so failed format negotiation follows the normal cleanup path.
Default-output monitoring uses Windows stream routing, which cannot be combined with low-latency mode. Keep capture callbacks span-based and consume packet data before returning.

`DeviceReferenceFileWriter` owns deterministic `DEVICES.txt` output and unchanged-content suppression. Its cache advances only after an atomic write succeeds; a failed write is retried on the next refresh, and a deleted export is recreated.
Export mode, device names and IDs, and destination changes all invalidate the cached output.

## Switch Architecture Seams

- `AppSwitchCommandCoordinator`: end-to-end output and input switch orchestration, including operation ids, reconnect fallback, deferred auto-switch logic, and overlay decisions.
- `AppSwitchRequestCoordinator`: request gating only, including debounce, in-progress suppression, and coalesced retry admission.
- `AppSwitchIntentTracker`: shared input/output request versions, cancellation tokens, and reconnect state. Admission and token replacement are atomic; cancellation callbacks run outside its lock. It has no audio-device or UI dependencies.
- `BluetoothReconnectCoordinator`: reconnect eligibility, cooldown, timeout-circuit behavior, and reconnect attempt summaries.
- `AudioDeviceService`: enumeration, active and default device lookup, direct switch execution, and stable audio-facing helpers.
- Both switch directions preserve request options and intent cancellation through retries and delayed Bluetooth switching. Reconnect progress uses the matching input/output tracker; successful deferred input switches also show completion feedback.
- `DeviceRoleSwitchEngine` applies and verifies configured roles for both directions. Rollback attempts every original role even if an earlier role cannot be restored.
- Input switches look up the selected endpoint directly. Microphone volume restoration targets that endpoint and does not depend on playback availability or playback retry state; stale or shutdown work skips restoration.
- Endpoint lookups used by switching and mixer mutations validate the requested playback/capture direction.

When adding switch behavior, extend the narrowest seam that owns the decision already. Do not widen the command coordinator unless the decision genuinely belongs at workflow level.

## Important Components

- `Platform/ComThreadingHelper.cs`: dedicated MTA worker for Core Audio calls, queue backpressure, and worker restart handling.
- `Platform/AudioPolicyConfig.cs`: default endpoint policy COM bridge.
- `Platform/DeviceCacheHelper.cs`: immutable cache snapshots, on-demand device materialization, and topology fingerprint short-circuits.
- `Services/Audio/AudioDeviceService.cs`: switch execution, retries, and device event handling.
- `Services/Audio/AudioSessionService.cs`: session enumeration and no-controls fast-path snapshots.
- `Coordinators/AppSwitchCommandCoordinator.cs`: orchestration for switch flows and reconnect preflight.
- `ViewModels/AppViewModel*.cs`: lifecycle, command orchestration, and cancellation-aware app behavior.

## Hotplug, Refresh, And Resume Behavior

### Hotplug And Refresh Coalescing

- Device notifications arrive in bursts for one physical change.
- `AppRuntimeHost` coalesces hotplug signals before invoking refresh.
- `AppViewModel` refresh is single-flight with pending-rerun coalescing.
- Hotplug refresh updates device lists even when hidden, but mixer refresh is deferred unless the window is visible.

Preserve these guarantees. Do not reintroduce one-refresh-per-notification behavior.

### Suspend And Resume

- `AppRuntimeHost` subscribes to power-mode and session changes.
- Resume events are forwarded into app-view-model recovery.
- `AudioDeviceService` performs controlled post-resume recovery to reduce stale endpoint and session state.
- `AppRuntimeStartupResumeCoordinator` uses a monotonic cooldown for duplicate notifications. A new sleep/wake cycle bypasses that cooldown and requests one trailing recovery if an earlier cycle is still running.
- Queued recovery does not start while suspended or after disposal. Audio cache invalidation coalesces callers already waiting for the same recovery, without suppressing a subsequent cycle.
- Audio, hotkey registration, and device refresh failures are isolated; remaining phases still run. Failure logs include the phase and operation id. Shutdown cancellation stops the pipeline.
- Suspend stops temporary output/input tests. Persistent Windows Listen settings remain owned by Windows; resume does not toggle them.

If you change user-visible behavior here, update [USER_GUIDE.md](USER_GUIDE.md) troubleshooting guidance in the same PR.

### Windows Listen Verification

- Listen mutations use the same input endpoint for read, write, and verification, under a mutation lock.
- A confirmed mismatch in either enabled state or output target fails verification even when the other property cannot be read. Unreadable properties without a known mismatch produce an applied-but-unverified result.
- An unresolved explicit monitor output is a read failure. The overlay reports **Output unavailable**; only a successfully read empty target uses **Default output**.

## Bluetooth Reconnect Preflight

- Reconnect preflight is a best-effort recovery path for likely Bluetooth endpoints that are configured but currently disconnected.
- Core implementation lives in `Services/Bluetooth/BluetoothReconnectService.cs`, `Services/Bluetooth/BluetoothReconnectCoordinator.cs`, `Coordinators/AppSwitchCommandCoordinator.cs`, and `AudioPilot.CliHost/LocalHeadlessCommandRunner.cs`.
- Reconnect is bounded. Each attempt gets a `1200 ms` timeout budget, uses a `5000 ms` cooldown, and opens a timeout circuit for `180000 ms` after `2` consecutive timeout-class failures.
- The reconnect flow reserves `250 ms` for the pairing and discovery side before KS fallback work spends the remaining linked budget. Do not let fallback paths run past the remaining per-attempt deadline.
- Successful reconnects are rechecked and stabilized for up to `12000 ms` before the workflow treats the endpoint as healthy again.
- Timeout or failure falls back to normal precondition handling.
- Eligibility is intentionally conservative. Do not broaden heuristics without tests for false-positive devices.
- Remembered association endpoints are scoped to the audio endpoint ID when available, preventing same-named devices from sharing reconnect history. Cached and full-scan KS paths honor cancellation before issuing reconnect requests.

## Routines

Routines are persisted audio workflows that reuse the same core switch pipeline as manual switching.

- Trigger entry points live in `ViewModels/AppViewModel.Routines.cs`, `ViewModels/AppViewModel.RoutineAppStart.cs`, and `ViewModels/AppViewModel.RoutineStateful.cs`.
- Supported primary triggers include hotkey, Application, Steam Big Picture, device change, and AudioPilot startup.
- Application routines may keep a process-scoped lease for per-app routing.
- Application and Steam Big Picture routines can also open stateful restore sessions.
- Application trigger detection uses process start/stop monitoring plus focus monitoring and process metadata lookup.

`Helpers/RoutineScheduleCalculator.cs` owns time-zone, weekday, and daylight-saving calculations shared by the scheduler, reminders, editor, conflict detection, and CLI previews.
`ScheduleTriggerCoordinator` owns timer lifetime, execution windows, and occurrence/reminder deduplication. New schedule consumers should use the calculator without depending on the timer coordinator.

When changing routine behavior, keep [README.md](../README.md), [USER_GUIDE.md](USER_GUIDE.md), and [CLI.md](CLI.md) aligned because the same routine can be invoked from tray, hotkey, automation, or background triggers.

## CLI Architecture

The CLI project and output names differ intentionally:

- project: `AudioPilot.CliHost`
- shipped executable: `AudioPilot.Cli.exe`

Key files:

- `AudioPilot/Cli/CliCommand.cs`: parser model.
- `AudioPilot/Cli/CliCommandExecutor.cs`: execution layer.
- `AudioPilot.CliHost/Program.cs`: headless process host entry point.
- `AudioPilot/ViewModels/AppViewModel.Cli.cs`: in-app runtime bridge.

Behavior rules:

- `AudioPilot.Cli.exe` attempts single-instance forwarding first.
- If a UI instance is available, commands are forwarded to that process.
- If no UI instance is running, only automation-safe commands execute headlessly.
- Routine list, get, next, run, enable, disable, create, update, delete, import, and export support both forwarded and headless paths.
- Routine queries obtain detached snapshots through `ICliCommandRuntime.GetRoutinesSnapshot`. The shared executor handles selection and formatting; the UI adapter only marshals the snapshot read, and the headless adapter only loads settings.
- UI-only commands such as `show`, `hide`, and `startup open` fail with exit code `3` if no UI host is running.
- Forwarding failures return exit code `4`.
- Forwarded requests are limited to 64 KiB and responses to 4 MiB. Request writes
  and response reads share a 30-second deadline so a stalled peer cannot leave
  the CLI blocked indefinitely.

Use [CLI.md](CLI.md) for the full command and JSON contract.

## Interop Conventions

- Prefer `LibraryImport` over `DllImport`.
- Use explicit string marshalling when interop strings must be deterministic.
- Use generated COM interop where adopted and avoid mixing runtime COM release helpers into that path.
- Keep `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` enabled because the source-generated interop path requires it.

Practical rule: change interop declarations first, preserve call flow, then validate with a strict build and broader tests.

## Logging And Privacy

- Default logs should avoid raw user-configured identifiers when practical.
- Reuse `AudioPilot/Logging/LogPrivacy.cs` for routine names, ids, device labels, session labels, and similar diagnostics.
- `Logger` already strips absolute paths from exception details. Do not add raw paths back into normal diagnostics without a strong reason.
- Pass the exception to the logger when a failure needs diagnosis, so its stack, HRESULT, and inner failures remain available. Exception records are bounded to eight exceptions and 4,096 characters, plus a truncation marker.
- CLI output is separate from background logs. Richer CLI output is acceptable only when that distinction is intentional and documented.
- The CLI disables console logging and drains an initialized logger before exit. Keep command output and JSON error envelopes separate from background diagnostics, including when file logging fails.
- The UI and CLI can write the same log. A path-specific named mutex covers append, rotation, and deletion before opening the file; acquisition is bounded to one second and recovers abandoned ownership.
  Keep this cross-process protection when changing logging: separate append streams can otherwise overwrite records at the same file offset.
- Explicit export redaction must not depend on the current `LogPrivacy` setting. Support-bundle metadata, including user-chosen filenames, must follow the selected export privacy mode too.
- For multi-step flows, prefer a shared correlation token such as `opId` across started, skipped, completed, and failed events.
- Keep at least one focused real log-file assertion when adding a new logging pattern.
- Prefer stable event-style messages with short key/value context like `reason=`, `result=`, and `count=` over prose-only strings.
- Use `Trace` for frequent internal churn, `Debug` for support-useful state transitions, and `Info` when the event helps explain a user-visible outcome.
- Avoid silent `catch (Exception)` unless the path is intentionally best-effort and the missing log would not matter during support diagnosis.
- Handle failures at the boundary that owns the outcome. Expected shutdown cancellation and disappearing-device probes should stay quiet; a failed network read must preserve the previous state instead of impersonating a disconnect.
- Error reporting can fail too. Background callbacks and UI error dialogs must not leave a task unobserved, a command disabled, or startup shutdown unfinished when their failure handlers throw.

## Snapshot And Cache Fast Paths

- `DeviceCacheHelper` computes topology fingerprints and skips cache rewrites when nothing changed.
- `AudioSessionService` supports a recent snapshot fast path for `includeSessionControls=false` calls.
- Mixer refresh uses separate cache windows for interactive and background contexts.
- Snapshot scans release unvisited playback and capture endpoints after cancellation or failure. Device materialization reads the native collection count once.
- Mixer cleanup prevents queued volume/mute mutations and their completion callbacks from applying after disposal. Shared endpoint rows retain one active write owner across the two mixers.
- Duplicate endpoint notifications retain existing immutable snapshot arrays when row state is unchanged; changed rows still publish a new array.
- Mixer session and process mappings update only changed entries. Fuzzy volume matching starts with the smallest word-index bucket while preserving the original candidate order for ambiguous matches.

Use these paths before adding new polling, delay, or debounce layers.

## Tunable Controls

High-impact constants live in `Constants/AppConstants.cs`.

Examples:

- switch debounce and retry timing,
- cleanup timing budgets,
- mixer refresh debounce,
- hotkey debounce and diagnostics windows,
- hotplug refresh debounce,
- session snapshot fast-path windows,
- Bluetooth reconnect limits and timing bounds.

Prefer adjusting centralized constants over scattering sleeps or magic numbers through workflow code.

## Known Limits And Constraints

- Process cache: `2048` max entries with a `10` minute TTL.
- Hotkey debounce trackers: `1024` max entries with `HotkeyDebounceTicks` set to `50 * 10000` ticks and retention kept for `8x` that window.
- Volume cache: `4096` max entries with a `30` minute TTL and `75 ms` write throttling.
- Media overlay single-candidate trace retention: `128` entries retained for `300` seconds, with `1000 ms` trace throttling.
- Packaged app inventory: `2048` max cached entries.
- Session cache short TTL: `5` seconds; session snapshot fast-path windows remain intentionally short (`100` to `300 ms`) to avoid stale overlay and mixer decisions.
- Settings import max size: `256 KB`.
- Settings backup retention: `5` backup files.
- Settings cross-process lock timeout: `5000 ms`.
- Hotplug refresh debounce: `350 ms`.
- Shutdown step timeout: `5000 ms`.

When updating behavior around any of these limits, prefer adjusting `AppConstants` and documenting the reason rather than hard-coding new local exceptions.

## Media Overlay Behavior

Key implementation points:

- `AudioPilot/Services/UI/MediaOverlayCommandService.cs`
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayEngine.cs`
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayEngine.SnapshotProvider.cs`
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayEngine.CandidateResolver.cs`
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayTrackNavigationRecoveryCoordinator.cs`
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayStateStore.cs`

Behavior notes:

- media overlays are inferred from before and after GMTC snapshots,
- source selection prefers a currently playing session with usable metadata,
- sticky-source reuse is valid only while that source still resolves to an active session,
- all retry phases are deadline-aware and share an `8000 ms` maximum capture budget,
- extended track-load recovery does not start if its initial delay no longer fits inside the remaining deadline budget,
- session-drop recovery logs whether extended track-load recovery was actually attempted and whether the phase ended because the deadline budget was exhausted,
- same-app multi-session behavior remains a distinct edge case and should be tested before heuristic changes.

Submitted media commands that time out have an unknown result. They must not be retried through another session or synthetic media input, which could toggle playback twice or skip two tracks.
The overlay reports an unknown result, and history records `media-command-unconfirmed`. Discovery failures and explicit command rejection can still use fallback.
Queued global hotkey callbacks become inactive when their registration is removed or replaced, including hook snapshots captured before the change.

## Testing Strategy

Default loop:

```powershell
pwsh ./scripts/run-tests.ps1 -Category unit
```

This default path is non-visual and does not change real audio. Real audio tests require both `AUDIOPILOT_RUN_INTEGRATION=1` and `AUDIOPILOT_RUN_AUDIO_HARDWARE=1`; stress tests require `AUDIOPILOT_RUN_STRESS=1`.
The scripts clear inherited desktop/audio permissions, including during `full` and `validate-all.ps1 -IncludeIntegration`. Run hardware checks separately with `-Category integration -AllowHardwareAudio` on a dedicated test system.

Visible WPF tests, including native popup tests, require `-Category visual -AllowDesktopInteraction`. Direct runs require `AUDIOPILOT_RUN_INTEGRATION=1`, `AUDIOPILOT_RUN_VISUAL_WPF=1`, and
`AUDIOPILOT_TEST_SHOW_WINDOWS=1`. These tests can change focus and must be explicitly requested on an occupied desktop.

Broader coverage:

```powershell
pwsh ./scripts/run-tests.ps1 -Category integration
pwsh ./scripts/run-tests.ps1 -Category visual -AllowDesktopInteraction
pwsh ./scripts/run-tests.ps1 -Category stress
pwsh ./scripts/run-tests.ps1 -Category full
```

### Test Philosophy

- Prefer focused tests around coordinators, helpers, and services.
- Preserve debounce, coalescing, retry, and cancellation behavior when refactoring.
- Keep unit tests deterministic and hardware-independent where possible.
- Use integration and stress suites for hardware-sensitive coverage.
- When changing resume, hotplug, reconnect, or shutdown behavior, validate both success and failure or timeout paths.

Good test targets include pure decision helpers, coordinator sequencing, settings persistence and recovery, log privacy, and CLI machine-readable output contracts.

### Runtime Profiling

Use the bounded sampler when investigating idle CPU, retained memory, handles, or threads without requiring a profiler installation:

```powershell
pwsh ./scripts/profile-runtime.ps1 -DurationSeconds 300 -Phase tray-idle
```

Pass `-TargetProcessId` when multiple AudioPilot processes exist. Samples are written atomically to a CSV under the temporary `AudioPilotDiagnostics` directory by default; `-OutputPath` selects another destination. CPU is
normalized across logical processors to match the percentage shown by Task Manager. Compare stable phases such as visible idle, tray idle, mixer activity, and repeated test start/stop rather than treating one working-set value or
a brief scheduler wake-up as a leak. For managed-heap attribution, pair the samples with `dotnet-counters` or `dotnet-gcdump` and confirm that growth survives a full collection before changing lifetime ownership.

Keep fractional CPU samples when processing the CSV; rounding to whole percentages hides light background activity. Compare a fresh process without tracing or UI inspection attached as well: profiling tools and UI Automation
clients can change CPU, memory, and thread activity. Separate first-use view warmup from repeated operations, and measure published-package startup separately from a framework-dependent development build.

For tray and overlay lifetime checks, measure repeated use of the existing windows as well as creation/disposal stress. Compare native handle growth against a minimal WPF window with the same transparency settings before
attributing it to AudioPilot. Keep audio and startup-registration snapshots around live runs and restore them afterward; an isolated settings directory does not isolate Windows startup registration (the Run entry or scheduled task).

For method-level attribution, collect a bounded [dotnet-trace](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace) capture from the selected process:

```powershell
dotnet-trace collect --process-id <PID> --duration 00:00:00:30 --profile dotnet-sampled-thread-time,dotnet-common --output artifacts/runtime.nettrace
dotnet-trace report artifacts/runtime.nettrace topN --inclusive
```

Sampled thread time includes waiting threads; its percentages are not equivalent to CPU utilization. Use traces to locate expensive call paths, then compare identical Release workloads with warmup and repeated measurements.
Measure allocations separately from retained heap size. `GC.GetAllocatedBytesForCurrentThread()` is useful for synchronous work but excludes allocations made by other threads or asynchronous continuations. Record the runtime,
JIT settings, input size, and measurement range. Keep synthetic workloads isolated from audio devices and visible windows, and do not turn timing measurements into flaky pass/fail unit tests.

## Release Validation

Use [RELEASING.md](RELEASING.md) as the source of truth.

Release artifact builds run alongside the unit, integration, and stress gates. Publication depends on every gate and artifact build succeeding.
MSI uploads contain only the installer, and already-compressed release bundles are uploaded without another compression pass.

For local release builds, `scripts/build-local-release-artifacts.ps1` reuses the freshly published self-contained Release binaries for MSI packaging.
Explicit `UsePrebuiltPublish=true` builds validate the payload version, runtime architecture, and Release configuration; standalone MSI builds publish their own payload by default.
MSI cabinets use MSZIP for faster builds; `-p:DefaultCompressionLevel=high` trades build time for smaller installers. Debug installers are optional and keep their own Debug payload.

Local release builds record step timings in `artifacts/local-build-timings.json`. The ReadyToRun comparison runs when `benchmark_readytorun` is selected during manual CI dispatch, or through `scripts/benchmark-readytorun.ps1`.
Use it after SDK, dependency, or startup changes to reassess the x64 precompilation tradeoff; it is not a release gate. It opens isolated test windows, preserves both forms of Windows startup registration, and removes its temporary publish copies.
Regular push validation still includes coverage and all required checks.

Generated application manifests are keyed by assembly version and rebuilt when the template or project changes. ABOUT files retain their timestamps when their contents are unchanged.
This preserves incremental compilation during repeated builds and publishes.

Release validation should confirm:

1. build and default tests pass,
2. stress and integration coverage passes,
3. manual churn checks on real hardware pass,
4. publish artifacts are correct for the target architectures.

## Related Docs

- Contributor workflow: [CONTRIBUTING.md](CONTRIBUTING.md)
- User guide: [USER_GUIDE.md](USER_GUIDE.md)
- CLI reference: [CLI.md](CLI.md)
- Releasing: [RELEASING.md](RELEASING.md)
