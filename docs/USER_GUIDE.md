# User Guide

This guide is for people who want to use AudioPilot day to day.

Use this page for setup, routines, settings, troubleshooting, and recovery. Use [CLI.md](CLI.md) for command-line automation.

## Start Here

Use this first-run path if you are new to AudioPilot:

1. Open AudioPilot.
2. Add the devices you want in the **Output cycle** and **Input cycle** lists.
3. Remove devices you do not want to rotate through.
4. Use **Up** and **Down** to set the exact switching order.
5. Configure your hotkeys.
6. Save settings.
7. Minimize to tray and test switching.

This is the core workflow. Most other features build on it.

## Everyday Tasks

### Switch Between Speakers And A Headset

Use this when you regularly move between two or more playback devices during work, gaming, or calls.

1. Add only the playback devices you actually use to **Output cycle**.
2. Put them in the order you want to rotate through.
3. Set an output switch hotkey.
4. If useful, also set an output reverse-switch hotkey.
5. Save settings and test the cycle order twice.

### Monitor Microphone Input

Use this when you want to hear your microphone or another input source through speakers or headphones.

1. Make sure the correct microphone is active as the current default input.
2. In **Settings**, assign a **Listen input** hotkey if you want fast on and off control.
3. If monitoring should always go to a specific playback device, set **Listen monitor output**.
4. Test listen on and off once before relying on it in a call or stream.

This changes the Windows listen state for the current default input device. It does not switch your default output or input device by itself.

### Build A Routine For A Game Or App

Use this when you want AudioPilot to react automatically when a desktop app or packaged app launches, or when its window gains focus.

1. Create a routine and choose **Application** as the primary trigger.
2. Pick the output target, input target, or both.
3. Select the exact desktop `.exe` path or the saved packaged-app target.
4. If you only want to reroute that app's audio, enable **Switch only this app's output audio**.
5. If you want your previous defaults restored when the app closes, enable **Restore previous audio settings when this routine deactivates**.

Application-trigger matching uses the saved target, not just the file name. If the routine does not trigger, verify the exact app path or packaged-app identifier first.

## How The App Is Organized

### Dialogs And Confirmations

AudioPilot confirmations and error messages follow the active light, dark,
system, or high-contrast theme. Use `Enter` for the emphasized default action,
`Esc` for the safest cancel or decline action, and `Alt+F4` to close with that
same safe result. Underlined access-key letters can be used with `Alt`.

Error and diagnostic dialogs include **Copy**. It copies only the message shown
in the dialog; AudioPilot does not automatically copy or log that text. During a
very early startup or shutdown failure, Windows may show a native fallback
dialog so the error is still visible even when the themed UI cannot load.

Enable or disable **Play dialog sounds** under **Settings > Miscellaneous**.
AudioPilot plays one Windows system sound when a themed dialog first appears;
updates to an already open dialog do not replay it. The sound follows the
Windows sound scheme and system volume, with no separate AudioPilot volume.
Emergency native fallback dialogs are controlled by Windows and may still make
a sound when the AudioPilot setting is disabled.

### Output And Input Tabs

These tabs control the ordered cycle lists used by output and input switching.

- Top-to-bottom order is the switch order.
- Forward hotkeys move to the next device in the list.
- Reverse hotkeys move backward through the same list.
- If a configured device is disconnected, switching fails or skips safely and records diagnostics.

Right-click an individual row for actions that apply specifically to that device:

- **Test output** plays a short left, right, and combined chime directly through that endpoint. It does not change the Windows default device or endpoint master volume.
- **Test microphone** opens an inline level panel. Audio remains local, is never written to a recording file, and is retained only briefly in memory when live monitoring is enabled.
- **Set as default output/input** performs a persistent, role-aware switch using the same switching pipeline as the rest of AudioPilot.

Microphone monitoring starts off the first time. Use headphones before enabling **Hear myself**, because speakers can create loud feedback. For later tests in the same AudioPilot session, the app remembers the last Hear myself choice, monitor output, and monitor volume without writing those test preferences to settings. The monitor volume starts at 50% and affects only AudioPilot's temporary test session. Bluetooth microphones and headsets can add noticeable latency or cause Windows to select a headset profile. Stop the test with **Stop** or `Esc`; it also stops automatically when AudioPilot is hidden, the tab changes, Windows suspends or locks, or a device used by the test disconnects. Use the existing **Remove** button or `Ctrl+W` to remove one or more selected rows from the switch order.

The microphone test is separate from AudioPilot's persistent Windows **Listen to this device** setting. Testing never changes that Windows property; the Listen controls described below do.

### Settings Tab

The Settings tab controls the rest of the daily workflow, including:

- reverse switch hotkeys,
- media, mute, deafen, and window show/hide hotkeys,
- listen monitoring,
- output and input role targeting,
- overlay behavior,
- startup behavior,
- logging and privacy,
- Bluetooth reconnect preflight,
- preserve-audio-levels behavior,
- tray restore behavior.

Use **Apply Settings** to persist staged Settings-tab changes.

If you enable **Auto-save**, click **Apply Settings** once to activate it. After that, AudioPilot automatically saves device, routine, and settings changes after a short debounce instead of requiring separate manual saves.

### Mixer View

Use the mixer when switching devices is not enough and you need to rebalance active apps.

- It shows active audio sessions.
- It lets you adjust per-session volume without opening Windows sound panels.
- It works well after a device switch when you want to rebalance a game, browser, music app, or voice app.
- Right-click a session's slider knob to mute or unmute that session.

### Overlay Feedback

AudioPilot can show overlays for switching, volume changes, media actions, and similar quick controls.

- Use overlays when you want instant confirmation without restoring the main window.
- Disable overlays if you prefer a quieter tray-first workflow.

## Routines

Routines save named output, input, and/or endpoint volume targets, and let you trigger that saved setup in one step.

### Routine Trigger Modes

- `Hotkey`: runs when you press the routine hotkey.
- `Application`: runs when AudioPilot detects a matching desktop app or packaged app launching, or when a matching application window gains focus if you choose the focus mode. A focus activation ends when focus moves to another window; a launch activation ends when the process exits.
- `Steam Big Picture`: stays active while Steam Big Picture is open.
- `Device change`: runs after AudioPilot finishes a hotplug or default-device refresh.
- `AudioPilot startup`: runs once after AudioPilot finishes starting.
- `Scheduled`: runs at a specific time daily or on specific days of the week. The editor follows the clock format shown in the Windows system tray: it uses the regional long-time pattern when tray seconds are enabled and the regional short-time pattern otherwise. This includes the selected 12-hour or 24-hour convention and whether hours use one or two digits. An open Routine Editor updates when Windows reports a regional clock-format change without altering the selected schedule time. It shows a compact Windows time-zone label and standard UTC offset; hover it for the complete Windows display name and ID. New routines capture the configured schedule time zone, which defaults to the current system time zone; use CLI config `schedule-timezone` to override it. Scheduled routines run at minute precision. A delayed timer or resume catches up once, repeated clock intervals do not duplicate an occurrence, and a time inside a daylight-saving gap runs at the first valid local instant.
- `Network`: runs when your PC connects to or disconnects from a network (supports Ethernet, WiFi, VPN). You can choose to trigger on connect, disconnect, or both. Connect and both modes match the named network. Disconnect mode is intentionally broad and runs when the machine goes from connected to no connected networks.

### App-Only Routing vs Default Switching

Use normal routine output switching when you want Windows defaults to change for the whole system.

Use **Switch only this application's audio** when you want one app's output, input, or both routed differently without moving the system defaults. This is useful for chat apps, music apps, or one game that should stay on another device.

App-only routing is still part of the routine system. It does not create a separate mode or second configuration file.

### Restore-On-Deactivate

Stateful triggers can restore affected default devices and endpoint levels after the routine ends. For app-only focus routines, restore-on-deactivate also clears the selected per-app routes so the application follows the current system defaults again.

Use this when you want a temporary profile such as:

- a game that should move output to a headset while it is running,
- Steam Big Picture that should use a couch setup until it closes,
- a focus-based app route that should return to system defaults when focus moves away,
- an application-triggered workflow that should restore speakers after the app exits.

### Packaged Apps

Application routines support both normal desktop `.exe` targets and packaged apps such as Microsoft Store or MSIX-style apps.

Packaged apps do not use a normal file path picker. Use the packaged-app picker flow when the target is not a plain desktop executable.

### Tray Behavior For Routines

Right-click the AudioPilot notification-area icon to open its theme-aware quick menu. The first action changes between **Show AudioPilot** and **Hide AudioPilot** based on the current window state and shows the configured Show/Hide hotkey alongside it. Device switching, routines, settings, and exit actions follow below it when available.

Enabled routines can also appear in the tray menu when **Show this routine in the tray menu** is enabled.

That tray entry is optional. It does not replace the routine's primary trigger.

## Common Settings

### Preserve Audio Levels

- When enabled, AudioPilot captures current output mixer and session volumes before an output switch.
- After the switch, it applies those levels to sessions on the newly selected output device.
- This helps keep app volumes more consistent when moving between playback devices.

### Run At Startup

- Controlled by the **Run at Startup** checkbox.
- When enabled, AudioPilot registers itself in the current user's Windows startup registry.

### Auto-Save

- Controlled by **Settings > Enable auto-save**.
- It becomes active after you apply that setting once.
- After it is active, AudioPilot automatically saves device, routine, and settings changes after a short debounce.
- Routine edits that would require a confirmation dialog still stay manual instead of being auto-confirmed.

### Theme

- `Light`, `Dark`, or `System`.
- `System` follows Windows theme changes.

### Logging Privacy

- `LogLevel` controls how much log detail the app writes.
- `RedactLogContent` controls whether logs anonymize sensitive names, labels, app targets, and similar identifiers.
- Keep redaction enabled unless you intentionally need raw identifiers for local troubleshooting.

### Switch Roles

Role targeting controls which Windows default roles are changed during a switch.

- `Multimedia`: used by most media playback apps.
- `Communications`: used by chat and voice apps that target communications devices.
- `Console`: general/default role used by many games and desktop apps.

Keep the defaults unless you have a specific role-routing need.

### Bluetooth Reconnect Preflight

- Controlled by **Settings > Bluetooth > Enable reconnect preflight**.
- Applies to likely Bluetooth endpoints that are configured but currently disconnected.
- This is best-effort behavior. If reconnect cannot establish in time, normal switch precondition handling continues.
- The same reconnect path is used by routines and headless automation-safe switching.

### Listen To This Device

- AudioPilot can toggle Windows **Listen to this device** for the current default input endpoint.
- Use a hotkey or `AudioPilot.Cli.exe listen toggle|on|off`.
- If **Listen monitor output** is empty, Windows uses the current default output.
- If a specific monitor output is configured and available, AudioPilot routes listen audio there.

### Master And Microphone Volume Hotkeys

- AudioPilot can raise or lower the current default output and current default microphone level from global hotkeys.
- `MasterVolumeStepPercent` and `MicVolumeStepPercent` control the step size.
- Step values accept whole percentages from `1` to `100`.

## Hotkeys And Actions

Release builds include an `ABOUT.txt` file beside `AudioPilot.exe`. It records the release and build target, lists every configurable global hotkey action, and summarizes the fixed in-window keyboard shortcuts. Configurable bindings in that file are release defaults; your saved settings remain authoritative.

Fixed in-window shortcuts include `F1` for local About or online help, `Ctrl+1` through `Ctrl+4` for direct tab selection, `Ctrl+Tab` for the next tab, `Ctrl+S` to save the current context, `Ctrl+N` to create a routine, `Enter` to edit a routine when the routine list is focused, `Ctrl+F` to focus search in a searchable picker, and `F5` to refresh devices. List-specific management shortcuts remain documented in `ABOUT.txt`.

Supported hotkey actions:

- Output switch
- Input switch
- Output reverse switch
- Input reverse switch
- Show/hide app (shows AudioPilot when hidden and hides it to the tray when visible)
- Show current track
- Play/Pause, Next, Previous
- Mute mic
- Mute sound
- Deafen
- Listen input
- Master volume up
- Master volume down
- Microphone volume up
- Microphone volume down

Important rules:

- Duplicate hotkey combinations are blocked.
- A short debounce helps avoid accidental repeated triggers.
- Runtime labels for number-row, punctuation, and international keys follow the active Windows keyboard layout and refresh when the input language changes. For example, the US `OemPlus` key is shown as `=` rather than an ambiguous second `+`.
- Saved settings and CLI values remain canonical and do not change when the keyboard layout changes.
- Mouse-button and vertical or tilt-wheel hotkeys require at least one modifier.
- Bare text-producing keys such as `A`, `1`, or `/` also require a modifier.
- Standalone function keys and dedicated media keys are allowed.
- Advanced users can allow a small set of extra standalone keys through `AdditionalStandaloneHotkeyKeys`.

## Settings File

Most people should manage settings from the UI or CLI instead of editing JSON directly.

AudioPilot stores settings in:

- MSI-installed app: `%AppData%/AudioPilot/settings.json`.
- Portable ZIP or source run: app directory as `settings.json` when writable.
- Portable ZIP or source fallback: `%AppData%/AudioPilot/settings.json` when the app directory is not writable.

If the active settings file is corrupted or unreadable, recovery is attempted from `backups/settings.json.bak*`.

MSI uninstall keeps `%AppData%/AudioPilot` by default so your settings can be reused later. From Windows Apps/Programs, use Modify/Change to open AudioPilot maintenance mode, choose Remove, then select `Also delete saved AudioPilot data` if you want a clean uninstall. The Start Menu shortcut named `Change or uninstall AudioPilot` opens the same maintenance flow, and `Uninstall AudioPilot and delete settings` runs the clean-uninstall path directly.

Commonly edited keys by UI or CLI include:

- `OutputCycleDevices`, `InputCycleDevices`
- `OutputSwitchHotkey`, `InputSwitchHotkey`
- `OutputReverseSwitchHotkey`, `InputReverseSwitchHotkey`
- `RunAtStartup`
- `PreserveAudioLevels`
- `Theme`
- `LogLevel`, `RedactLogContent`
- `ListenToInputHotkey`, `ListenMonitorOutputDeviceId`, `ListenMonitorOutputDeviceName`
- `MasterVolume*`, `MicVolume*`
- `Routines`

Advanced tuning values such as Bluetooth reconnect timing and Steam Big Picture detection timing can also be managed through CLI config keys and are stored under the nested `AdvancedTuning` section in `settings.json`.

For full config and runtime key coverage, use [CLI.md](CLI.md).

## Logs And Device Reference Files

Log file:

- `AudioPilot.log`

Optional device reference file:

- `DEVICES.txt`

Privacy notes:

- Default logs try to avoid leaking raw sensitive identifiers when practical.
- CLI diagnostics can expose richer details when you explicitly request them.
- If you are sharing diagnostics publicly, prefer `--redact` and avoid `--show-paths` unless a maintainer asks for exact paths.

## Troubleshooting

### Hotkey Does Nothing

1. Check for a conflict with another app.
2. Confirm the hotkey is saved.
3. Reopen the app from tray and verify the relevant hotkey is still assigned.

### Switch Fails

1. Confirm the target device is connected and active.
2. If it is wireless, wait for reconnect preflight to finish.
3. Verify the device is still in the configured cycle list.
4. Check `AudioPilot.log` or `AudioPilot.Cli.exe diagnostics status --json --redact`.

### Resume Or Hotplug Behavior Seems Stale

1. Wait briefly for recovery or refresh coalescing to finish.
2. Reopen the app from tray or run `AudioPilot.Cli.exe refresh`.
3. Retry the switch after recovery settles.

### Application Routine Does Not Trigger

1. Confirm the routine is enabled.
2. Verify the exact desktop path or packaged-app target.
3. If you are using launch mode, make sure the target app starts after AudioPilot is already running.
4. If using app-only routing, make sure the app actually starts playing audio.
5. Check the log for routine started, skipped, completed, or restore events.

### Settings File Looks Wrong Or Missing

1. If installed from MSI, check `%AppData%/AudioPilot` first.
2. If running from ZIP or source, check the app directory first, then `%AppData%/AudioPilot` if that directory is not writable.
3. Backup the current file before editing anything.
4. Prefer UI or CLI reconfiguration before manual JSON edits.
5. If the file is unreadable, let the app attempt backup recovery first.

## Safe Diagnostics For A Bug Report

Good first-pass diagnostics:

1. Describe what you expected and what happened instead.
2. Note whether the problem is switching, mixer behavior, listen monitoring, routines, or startup/tray behavior.
3. Prefer `AudioPilot.Cli.exe diagnostics export-bundle .\support-bundle.zip --json`; it is redacted by default and includes recent history, status, media state, config validation, and sanitized logs. This is especially helpful for routine and media-hotkey issues because those paths include extra current-session diagnostics.
4. If you only need a quick snapshot, include `AudioPilot.Cli.exe status --json --redact` and `AudioPilot.Cli.exe diagnostics status --json --redact`.
5. Attach raw log excerpts only if you are comfortable sharing them, or rerun the bundle with `--include-sensitive` for private local troubleshooting.

If a maintainer needs exact machine-specific paths or identifiers, they can ask you to rerun diagnostics with less redaction.

## CLI Summary

AudioPilot accepts CLI commands through `AudioPilot.Cli.exe`.

Behavior summary:

- With a running UI instance, commands are forwarded to that instance.
- Without a running UI instance, automation-safe commands run headlessly.
- UI-only commands such as `show`, `hide`, and `startup open` require a running UI host.
- CLI `show` and `hide` remain explicit actions for predictable scripts; the configured window hotkey is the toggle action.

Quick examples:

```powershell
AudioPilot.Cli.exe status --json
AudioPilot.Cli.exe switch output
AudioPilot.Cli.exe switch input --reverse
AudioPilot.Cli.exe cycle test output --json
AudioPilot.Cli.exe diagnostics status --json --redact
AudioPilot.Cli.exe diagnostics export-bundle .\support-bundle.zip --json
AudioPilot.Cli.exe network list
```

Use [CLI.md](CLI.md) as the full command reference.

## Related Docs

- Landing page: [../README.md](../README.md)
- CLI reference: [CLI.md](CLI.md)
- Contributor workflow: [CONTRIBUTING.md](CONTRIBUTING.md)
