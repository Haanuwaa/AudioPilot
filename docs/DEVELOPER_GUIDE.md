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
- `MainWindow*Helper` and `AppViewModel*Helper` types are the preferred extraction seam for narrow UI-adjacent behavior.
- Services should not reach up into WPF controls or assume a specific window state.

Practical rule: concrete-control behavior belongs in a window or window helper;
tray/menu behavior belongs in `AppTrayIconService`. If logic reasons about
switching policy, persistence sequencing, or app state, it probably belongs in
`AppViewModel`, a coordinator, or a service.

### Windowless tray startup

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
  closes only when its final coalesced caller cancels.
- Dynamic acknowledgement updates and cancellation-driven closes use one tracked
  presenter-operation pump. Keep presenter calls outside the dialog state lock
  and drain that pump during shutdown.
- An explicit visible owner takes precedence, followed by the active AudioPilot
  window and the visible main window. A dialog is standalone and appears in the
  taskbar when no safe owner exists.
- Dialog text is selectable. Error and diagnostic requests can expose a Copy
  action that copies only the displayed message.
- `IAppDialogSoundPlayer` maps dialog kinds to Windows system sounds. The
  presenter signals it only after the custom window first renders, so active
  acknowledgement updates do not replay a sound and failed custom presentation
  cannot double-sound before native fallback. Tests must inject or retain the
  noninteractive presenter rather than invoking the Windows sound backend.
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

## Switch Architecture Seams

- `AppSwitchCommandCoordinator`: end-to-end output and input switch orchestration, including operation ids, reconnect fallback, deferred auto-switch logic, and overlay decisions.
- `AppSwitchRequestCoordinator`: request gating only, including debounce, in-progress suppression, and coalesced retry admission.
- `BluetoothReconnectCoordinator`: reconnect eligibility, cooldown, timeout-circuit behavior, and reconnect attempt summaries.
- `AudioDeviceService`: enumeration, active and default device lookup, direct switch execution, and stable audio-facing helpers.

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

If you change user-visible behavior here, update [USER_GUIDE.md](USER_GUIDE.md) troubleshooting guidance in the same PR.

## Bluetooth Reconnect Preflight

- Reconnect preflight is a best-effort recovery path for likely Bluetooth endpoints that are configured but currently disconnected.
- Core implementation lives in `Services/Bluetooth/BluetoothReconnectService.cs`, `Services/Bluetooth/BluetoothReconnectCoordinator.cs`, `Coordinators/AppSwitchCommandCoordinator.cs`, and `AudioPilot.CliHost/LocalHeadlessCommandRunner.cs`.
- Reconnect is bounded. Each attempt gets a `1200 ms` timeout budget, uses a `5000 ms` cooldown, and opens a timeout circuit for `180000 ms` after `2` consecutive timeout-class failures.
- The reconnect flow reserves `250 ms` for the pairing and discovery side before KS fallback work spends the remaining linked budget. Do not let fallback paths run past the remaining per-attempt deadline.
- Successful reconnects are rechecked and stabilized for up to `12000 ms` before the workflow treats the endpoint as healthy again.
- Timeout or failure falls back to normal precondition handling.
- Eligibility is intentionally conservative. Do not broaden heuristics without tests for false-positive devices.

## Routines

Routines are persisted audio workflows that reuse the same core switch pipeline as manual switching.

- Trigger entry points live in `ViewModels/AppViewModel.Routines.cs`, `ViewModels/AppViewModel.RoutineAppStart.cs`, and `ViewModels/AppViewModel.RoutineStateful.cs`.
- Supported primary triggers include hotkey, Application, Steam Big Picture, device change, and AudioPilot startup.
- Application routines may keep a process-scoped lease for per-app routing.
- Application and Steam Big Picture routines can also open stateful restore sessions.
- Application trigger detection uses process start/stop monitoring plus focus monitoring and process metadata lookup.

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
- Routine list, run, enable, disable, create, update, delete, import, and export support both forwarded and headless paths.
- UI-only commands such as `show`, `hide`, and `startup open` fail with exit code `3` if no UI host is running.
- Forwarding failures return exit code `4`.

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
- CLI output is separate from background logs. Richer CLI output is acceptable only when that distinction is intentional and documented.
- For multi-step flows, prefer a shared correlation token such as `opId` across started, skipped, completed, and failed events.
- Keep at least one focused real log-file assertion when adding a new logging pattern.
- Prefer stable event-style messages with short key/value context like `reason=`, `result=`, and `count=` over prose-only strings.
- Use `Trace` for frequent internal churn, `Debug` for support-useful state transitions, and `Info` when the event helps explain a user-visible outcome.
- Avoid silent `catch (Exception)` unless the path is intentionally best-effort and the missing log would not matter during support diagnosis.

## Snapshot And Cache Fast Paths

- `DeviceCacheHelper` computes topology fingerprints and skips cache rewrites when nothing changed.
- `AudioSessionService` supports a recent snapshot fast path for `includeSessionControls=false` calls.
- Mixer refresh uses separate cache windows for interactive and background contexts.

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
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayEngine.RetryPipeline.cs`
- `AudioPilot/Services/UI/MediaOverlay/MediaOverlayStateStore.cs`

Behavior notes:

- media overlays are inferred from before and after GMTC snapshots,
- source selection prefers a currently playing session with usable metadata,
- sticky-source reuse is valid only while that source still resolves to an active session,
- all retry phases are deadline-aware and share an `8000 ms` maximum capture budget,
- extended track-load recovery does not start if its initial delay no longer fits inside the remaining deadline budget,
- session-drop recovery logs whether extended track-load recovery was actually attempted and whether the phase ended because the deadline budget was exhausted,
- same-app multi-session behavior remains a distinct edge case and should be tested before heuristic changes.

## Testing Strategy

Default loop:

```powershell
pwsh ./scripts/run-tests.ps1 -Category unit
```

This default path is intentionally non-visual and non-hardware-sensitive. Integration and stress tests are opt-in and discovery-skip during plain `dotnet test` runs unless their enabling environment variable is set.

The 5 real visible WPF window tests are even more restrictive: they also require `AUDIOPILOT_RUN_VISUAL_WPF=1` on top of `AUDIOPILOT_RUN_INTEGRATION=1`. The `visual` script category also enables `AUDIOPILOT_TEST_SHOW_WINDOWS=1` so those windows are intentionally shown for manual debugging.

Broader coverage:

```powershell
pwsh ./scripts/run-tests.ps1 -Category integration
pwsh ./scripts/run-tests.ps1 -Category visual
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

Pass `-TargetProcessId` when multiple AudioPilot processes exist. Samples are written atomically to a CSV under the temporary `AudioPilotDiagnostics` directory by default; `-OutputPath` selects another destination. CPU is normalized across logical processors to match the percentage shown by Task Manager. Compare stable phases such as visible idle, tray idle, mixer activity, and repeated test start/stop rather than treating one working-set value or a brief scheduler wake-up as a leak. For managed-heap attribution, pair the samples with `dotnet-counters` or `dotnet-gcdump` and confirm that growth survives a full collection before changing lifetime ownership.

## Release Validation

Use [RELEASING.md](RELEASING.md) as the source of truth.

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
