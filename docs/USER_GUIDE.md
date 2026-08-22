# User Guide

This guide is for people who want to use AudioPilot day to day.

Use this page for setup, routines, settings, troubleshooting, and recovery. Use [CLI.md](CLI.md) for command-line
automation.

## Contents

- [Start here](#start-here)
- [MSI installation and command-line options](#msi-installation-and-command-line-options)
- [Everyday tasks](#everyday-tasks)
- [Tabs, mixers, media, and notifications](#how-the-app-is-organized)
- [Routines](#routines)
- [Common settings](#common-settings)
- [Hotkeys and actions](#hotkeys-and-actions)
- [Settings and data files](#settings-file)
- [Troubleshooting](#troubleshooting)
- [Safe diagnostics for a bug report](#safe-diagnostics-for-a-bug-report)

## Start Here

Use this first-run path if you are new to AudioPilot:

1. Open AudioPilot.
2. Add the devices you want in the **Output Switch Order** and **Input Switch Order** lists.
3. Remove devices you do not want to rotate through.
4. Drag devices to set the switching order, or use the **Move up** and **Move down** arrow buttons.
   Ctrl-click or Shift-click to select several devices and drag them together. Hold near the top or bottom of the list
   to scroll; press Escape to cancel a drag.
5. Configure your hotkeys.
6. Save settings.
7. Minimize to tray and test switching.

This is the core workflow. Most other features build on it.

## MSI Installation And Command-Line Options

The x64 and ARM64 MSI packages install for the current Windows user and include the .NET runtime. Use the package
matching your Windows architecture; use a ZIP package for x86 or a portable installation. Run setup as the Windows
account that will use AudioPilot, not as SYSTEM or another administrator account.

The default install location is `%LOCALAPPDATA%\AudioPilot`. Settings, routines, logs, and backups are stored separately
under `%APPDATA%\AudioPilot`. Close AudioPilot before an upgrade. Normal uninstall preserves this saved data.

The MSI accepts standard [Windows Installer options](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/msiexec)
and these AudioPilot properties. Property names are case-sensitive; use uppercase names and `0` or `1` for switches.

| Property | Purpose | Fresh-install default |
| --- | --- | --- |
| `INSTALLFOLDER="C:\path\AudioPilot"` | Choose a writable app folder for a new installation. | `%LOCALAPPDATA%\AudioPilot` |
| `INSTALLDESKTOPSHORTCUT=0` or `1` | Create a desktop shortcut. | `1` |
| `INSTALLSTARTMENUSHORTCUT=0` or `1` | Create Start Menu shortcuts. | `1` |
| `ADD_CLI_TO_PATH=0` or `1` | Add the app folder to your user PATH. Open a new terminal afterward. | `0` |
| `AUDIOPILOT_CLEAN_UNINSTALL=1` | Also delete saved AudioPilot data during uninstall. | Off; data is preserved |

Upgrades retain the previous install location and installer preferences when options are omitted. These properties
are installation options, not AudioPilot configuration commands. Configure startup, hotkeys, and other app preferences
in Settings or through [the CLI](CLI.md). The maintenance wizard does not offer an editor for shortcut/PATH choices.

Examples below are for **Command Prompt**. Substitute the downloaded MSI filename if using another version or ARM64.

Silent installation with CLI access and no desktop shortcut:

```bat
msiexec /i "AudioPilot-1.0.0-x64.msi" /qn /norestart ADD_CLI_TO_PATH=1 INSTALLDESKTOPSHORTCUT=0 /L*v
"%TEMP%\AudioPilot-install.log"
```

Use `/passive` instead of `/qn` for a progress display without wizard prompts. `/norestart` prevents an automatic
restart; `/L*v` records a verbose log. Silent installation does not launch AudioPilot afterward. The log folder must
already exist.

Silent uninstall while keeping settings:

```bat
msiexec /x "AudioPilot-1.0.0-x64.msi" /qn /norestart /L*v "%TEMP%\AudioPilot-uninstall.log"
```

Use the MSI for the installed product, or its Windows Installer product code in place of the filename. Add
`AUDIOPILOT_CLEAN_UNINSTALL=1` only if you also want to permanently remove settings, routines, logs, backups, and device
reference files. The interactive uninstall offers the same choice.

For automation, wait for `msiexec` to finish and check its exit code: `0` means success, `3010` means success with a
restart required, `1602` means cancelled, `1618` means another installation is running, and `1603` means installation
failed—inspect the verbose log. In PowerShell, use `Start-Process -Wait -PassThru` to wait and read `ExitCode`.

## Everyday Tasks

### Switch Between Speakers And A Headset

Use this when you regularly move between two or more playback devices during work, gaming, or calls.

1. Add only the playback devices you actually use to **Output Switch Order**.
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

This changes the Windows listen state for the current default input device. It does not switch your default output or
input device by itself.

### Build A Routine For A Game Or App

Use this when you want AudioPilot to react automatically when a desktop app or packaged app launches, or when its window
gains focus.

1. Create a routine and choose its playback target, microphone target, or both.
2. If you only want to reroute that app's audio, choose **An application** under **Route devices for** and select its
   target. Otherwise, keep **System defaults**.
3. Under **Automatic triggers**, choose **Add trigger**, then **Application**. Select when it should activate
   and choose the exact desktop `.exe` path or packaged app. Confirm with **Add trigger**.
4. If you want your previous audio restored when the app closes or loses focus, enable **Restore previous audio when
   this routine deactivates**.
5. Add the routine, then save your routines unless auto-save is enabled.

Application-trigger matching uses the saved target, not just the file name. If the routine does not trigger, verify the
exact app path or packaged-app identifier first.

## How The App Is Organized

### Startup And Tray

Once a device cycle, enabled switch hotkey, or enabled routine is configured, AudioPilot normally starts in the tray.
Unconfigured setups open the main window so you can finish setup.
Choose **Show AudioPilot** from the tray menu whenever you need the window; launching `AudioPilot.exe` again shows the
existing instance.

Configured hotkeys, routine triggers, and enabled overlay feedback work before you open the main window. Hiding the
window does not stop them. Use **Exit** in the tray menu to stop AudioPilot.

### Dialogs And Confirmations

AudioPilot confirmations and error messages follow the active light, dark,
system, or high-contrast theme. Use `Enter` for the emphasized default action,
`Esc` for the safest cancel or decline action, and `Alt+F4` to close with that
same safe result. Underlined access-key letters can be used with `Alt`.

Error and diagnostic dialogs include **Copy**. It copies only the message shown
in the dialog; AudioPilot does not automatically copy or log that text. During a
very early startup or shutdown failure, Windows may show a native fallback
dialog so the error is still visible even when the themed UI cannot load.

Enable or disable **Play app sounds** under **Settings > Miscellaneous** to
control sounds for dialogs and tray notifications, including routine reminders.
Turning it off keeps tray notifications visible but silent. AudioPilot plays one
Windows system sound when a themed dialog first appears; updates to an already
open dialog do not replay it. Sounds follow Windows sound and notification
settings, with no separate AudioPilot volume.
Emergency native fallback dialogs are controlled by Windows and may still make
a sound when the AudioPilot setting is disabled.

### Output And Input Tabs

These tabs control the ordered cycle lists used by output and input switching.

- Top-to-bottom order is the switch order.
- Forward hotkeys move to the next device in the list.
- Reverse hotkeys move backward through the same list.
- If a configured device is disconnected, switching fails or skips safely and records diagnostics.

Right-click an individual row for actions that apply specifically to that device:

- **Test output** plays a short four-note chime directly through that endpoint: left, right, then both channels. It does
  not change the Windows default device or endpoint master volume.
- **Test microphone** opens an inline level panel. Audio remains local, is never written to a recording file, and is
  retained only briefly in memory when live monitoring is enabled.
- **Set as default output/input** performs a persistent, role-aware switch using the same switching pipeline as the rest
  of AudioPilot.

Microphone monitoring starts off the first time. Use headphones before enabling **Hear myself**, because speakers can
create loud feedback. For later tests in the same AudioPilot session, the app remembers the last Hear myself
choice, monitor output, and monitor volume without writing those test preferences to settings. The monitor volume starts
at 50% and affects only AudioPilot's temporary test session. Bluetooth microphones and headsets can add
noticeable latency or cause Windows to select a headset profile. Stop the test with **Stop** or `Esc`; it also stops
automatically when AudioPilot is hidden, the tab changes, Windows suspends or locks, or a device used by the test
disconnects. Use the existing **Remove** button or `Ctrl+W` to remove one or more selected rows from the switch order.

For **Set as default output/input**, Windows roles determine whether a change is needed: a device can already serve one
role while another role still points elsewhere.

The microphone test is separate from AudioPilot's persistent Windows **Listen to this device** setting. Testing never
changes that Windows property; the Listen controls described below do.

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

If you enable **Auto-save**, click **Apply Settings** once to activate it. After that, AudioPilot automatically saves
device, routine, and settings changes after a short debounce instead of requiring separate manual saves.

### Mixer View

Use the mixer when switching devices is not enough and you need to rebalance active apps.

- It shows one row per process with active or idle audio sessions.
- Each process slider controls all of its sessions across output devices, or input devices in the recording mixer.
  Expired sessions are ignored. The row displays active sessions when present, otherwise idle sessions: it shows their
  highest volume and is marked muted when all of those sessions are muted. Idle sessions left on other devices do not
  hide the mute indicator for active playback.
- It works well after a device switch when you want to rebalance a game, browser, music app, or voice app.
- Right-click a process slider knob to mute or unmute all of its sessions in that mixer.
- **Refresh** re-reads mixer names, including changing browser window titles, without replacing the existing sliders. A
  window title labels the process; it does not identify the tab receiving media hotkeys. Windows may show several
  sessions for one process as separate sliders, while AudioPilot groups them into one process control.
- Tab to a session's slider and use any arrow key for 0.1-point volume adjustments. Hold an arrow to accelerate
  smoothly; release it or move focus away to stop. Each new press starts with fine control again.
- Use Page Up/Down for 5-point changes, Home/End for minimum/maximum volume, and Space to mute or unmute once per press.
- The exact volume follows the pointer above the slider during mouse interaction and stays centered during keyboard
  adjustment. Clicking the same slider keeps it open.
  Moving focus, clicking or scrolling elsewhere, or leaving the slider after dragging dismisses it.

### Overlay Feedback

AudioPilot can show overlays for switching, volume changes, media actions, and similar quick controls.
Icons identify the action or device alongside its text; mute states also retain their written labels and colors.
Audio status uses speaker and microphone icons to distinguish its sections. Media feedback uses a neutral information
icon when the player has not confirmed the requested state, rather than implying that playback changed.

- Use overlays when you want instant confirmation without restoring the main window.
- Disable **Enable overlays** in Settings and apply the change if you prefer a quieter tray-first workflow. This closes
  existing overlays and suppresses further visual feedback; hotkeys and routines keep working.

### Media Controls

Media track details depend on the browser or player sharing a media session with Windows.
If an app or a private browsing mode does not expose one, AudioPilot cannot display its track metadata.
**Show current track** displays **No media session detected** when it cannot obtain a session,
or **Track information unavailable** when a session is readable but lacks track details.
Neither message means that no audio is playing. Windows may have no session for a playing browser tab.
Next/previous and play/pause also show **No media session detected** after a successful command dispatch
when no session context is readable; this does not confirm that the player handled the command.
If this suddenly happens in a browser, fully restarting the browser and resuming playback can restore its Windows media
registration.
Hotkeys and overlay feedback remain available when AudioPilot starts in the tray, before you open its main window.

Browsers choose which tab they expose to Windows media controls. When several tabs are playing, a muted tab can still be
selected: Windows' media-control API does not expose tab mute state or every browser tab. AudioPilot cannot redirect
commands to a tab the browser does not expose. In Firefox-based browsers, try pausing and resuming the desired tab;
picture-in-picture or fullscreen playback can take priority. Twitch's theatre mode is not fullscreen.

For next/previous track actions, "loading" means metadata or confirmation is still pending. "Unchanged" means AudioPilot
did not observe a confirmed track change within its recovery window. When track details are missing after recovery, the
overlay reports **Track information unavailable** instead of claiming the track is unchanged.

**Seek Forward** and **Seek Backward** move within the selected track or video by the **Seek Step** set in Settings
under **Media Playback → Seek**. Seek, Master Volume, and Microphone Volume start expanded when their hotkeys are
configured and collapsed when empty. You can expand or collapse each section yourself; editing or clearing a binding
keeps it open.

Enter seconds or minutes: `90`, `90s`, `1.5m`, `1m30s`, or `1:30` all mean 90 seconds. Plain numbers mean seconds;
decimal minutes use a dot. Use a duration that equals whole seconds, from 1 second to 60 minutes.

After the player accepts a seek, the overlay shows the track and artist with the estimated starting position and
requested destination below them, such as `1:24 → 1:34`. Missing track metadata does not prevent the times from being
shown. **Waiting for player position** means its timeline has not caught up with previous accepted seeks.

Seeking is limited to the player’s available range; some live streams and browser sessions do not support it. AudioPilot
shows **Seeking unavailable** when the player does not expose seeking support and **Player timeline unavailable** when
it exposes no usable position range. These hotkeys work across windows and displays; the browser does not need focus. A
browser can stop reporting its timeline after a seek even while playback continues. Try pausing and resuming the video
or reloading its tab to restore that data. AudioPilot resumes seeking when a valid timeline returns; it does not guess a
position, seek another player, or send arrow keys to the foreground app.

### Tray Notifications

Tray warnings report automatic-save failures and incomplete audio or hotkey recovery after sleep. Click the warning to
open AudioPilot and review the problem.
Repeated warnings are limited; normal playback, volume changes, and successful routines continue to use their existing
overlay feedback.

Scheduled routines can also show an optional reminder: enable **Notify me 1 minute before** in the routine editor's
Schedule section and save the routine. It is off by default.
AudioPilot must be running and awake; reminders missed during sleep or shutdown are not replayed. Routines due together
share a notification, which opens the Routines tab when clicked.

## Routines

Routines save playback, microphone, communications-device, volume, and mute actions so you can apply a named setup
in one step.

Drag routines in the saved list to change their order. Ctrl-click or Shift-click to select a group and move it together.
A line shows where the group will land; hold near a list edge to scroll, or press Escape to cancel.
The **Move up**/**Move down** arrow buttons and **Alt+Up**/**Alt+Down** remain available for one selected item. Save
the routines
afterward, unless auto-save is enabled. Reordering does not run a routine or change audio devices.

Routine names can contain up to 64 characters.

Hover a truncated routine name, trigger summary, or last-run status to read the full text. Select a routine to see
all its actions, conditions, triggers, and restoration options in the details card. Each scheduled trigger includes
its saved time zone and whether a reminder is enabled.

The routine editor keeps a selected device target if that device becomes unavailable, and shows a notice below the
selector. Editing another field will not silently remove that target. Choose **Leave unchanged**
to remove a target deliberately. Volume-only routines can also be added or updated without a device currently connected.
**Cancel** discards the editor's changes.
Choose the routing scope first, then devices and volume/mute actions. Communications targets require **System
defaults**;
clear those targets before changing to application routing.
Choose **Add trigger**, select its type, configure it, and confirm **Add trigger**. Only confirmed triggers appear
in the configured rows. Each row has **Edit** and **Remove**; editing requires **Save trigger**, while **Cancel edit**
discards the draft. Trigger confirmation and cancellation stay in the bottom bar while editing.
Finish or cancel a trigger edit before saving the routine. Escape cancels the trigger edit first.
With no automatic triggers, use **Manual shortcuts** to assign a hotkey or tray entry.
Both network triggers and network conditions let you select an available network or type a name.
The circular-arrow button refreshes the shared network list without clearing saved names.

### Multiple Automatic Triggers

A routine can have up to 16 automatic triggers. Each configured row has **Edit** and **Remove** buttons.
All triggers use the same actions and optional manual shortcuts. Any matching trigger can activate the routine;
they do not all have to match.

Application launch/focus and Steam Big Picture triggers remain active until their matching state ends. If another
trigger activates the same routine in the meantime, AudioPilot keeps the original activation instead of applying its
actions again. With restoration enabled, audio restores only after the last active stateful trigger ends. For example,
a routine watching a game launch and Steam Big Picture remains active until both close.

Schedules, network transitions, startup, unlock/resume, and device changes are one-time events. They do not hold a
routine active,
and are skipped while that routine is already active. Two schedules for the same routine at the same instant run it
once. A manual hotkey, tray command, or CLI run can still explicitly reapply the actions.

### Routine Trigger Modes

- `Application`: runs when AudioPilot detects a matching desktop app or packaged app launching, or when a matching
  application window gains focus if you choose the focus mode. A focus activation ends when focus moves to another
  app or its title stops matching; a launch activation ends when the process exits. Title matching also updates
  when the already-focused window changes its title, such as when switching browser tabs.
- `Steam Big Picture`: stays active while Steam Big Picture is open.
- `Device availability`: watches one output or input device becoming available, unavailable, or either. This means
  Windows reports an active audio endpoint; a Bluetooth radio connection alone may not be enough. Opening AudioPilot
  or editing the trigger establishes its starting state and does not fire a connection event. Matching uses the saved
  device ID or stable ID; another device with the same name does not count.
- `Device change`: runs after AudioPilot finishes a hotplug or default-device refresh.
- `AudioPilot startup`: runs once after AudioPilot finishes starting.
- `Windows unlock`: runs when you unlock your Windows session, including after sleep.
- `System resume`: runs after Windows resumes and AudioPilot finishes audio recovery. It does not wake the computer.
  Both system-event triggers require AudioPilot to be running. A routine configured for both runs once during the
  same wake/unlock cycle. Pending work is cancelled by another suspend, session lock, or shutdown; changed or disabled
  routines are checked again before execution. AudioPilot briefly waits for target devices to reappear, then uses its
  normal switching/reconnect path. These events do not hold a routine active or restore it on the next lock.
- `Scheduled`: runs at a specific time daily or on specific days of the week. The editor follows the clock format shown
  in the Windows system tray: it uses the regional long-time pattern when tray seconds are enabled and the
  regional short-time pattern otherwise. This includes the selected 12-hour or 24-hour convention and whether hours use
  one or two digits. An open Routine Editor updates when Windows reports a regional clock-format change without
  altering the selected schedule time. It shows a compact Windows time-zone label and standard UTC offset; hover it for
  the complete Windows display name and ID. New routines capture the configured schedule time zone, which
  defaults to the current system time zone; use CLI config `schedule-timezone` to override it. Editing an existing
  scheduled routine preserves its saved time zone. Scheduled routines run at minute precision. A delayed timer or resume
  catches up once, repeated clock intervals do not duplicate an
  occurrence, and a time inside a daylight-saving gap runs at the first valid local instant.
- `Network`: runs when your PC connects to or disconnects from a network (supports Ethernet, WiFi, VPN). You can choose
  to trigger on connect, disconnect, or both.
  Both mode reacts when the named network is added or removed, even if another network remains connected. Disconnect
  can watch one named network too. Leave its network name blank to run only when all connected networks disappear.

The editor separates **What changes**, **When it runs**, and **Only when…**. Manual shortcuts and automatic
triggers can be used together. Optional communications, volume/mute, conditions, and cycling sections open when
already configured. The **Routine summary** reflects confirmed triggers, audio actions, and conditions as you edit;
an unfinished trigger draft is not included until you confirm it.

### Routine Conditions

Expand **Only when… (all must pass)** to require a day/time window, an audio device to be **Available** or
**Unavailable**, a running application, and/or a connected network. Leave unused requirements blank or disabled. Every
selected requirement must pass before
any actions run, including hotkey, tray, and CLI runs. A failed check skips the routine without reconnecting devices,
switching audio, or changing levels. The overlay, last-run status, and diagnostics explain the skip.

**Require day and time** adds a weekday and time window. Leave all weekdays unchecked for every day, or choose
**All day** to filter by weekday alone. Times include the start and exclude the end. An overnight window belongs
to its starting day: Monday 22:00–02:00 includes Tuesday before 02:00. Equal start and end times are rejected;
use **All day** instead. The saved time zone is shown in the editor. Daylight-saving changes follow its local clock,
so a repeated hour is eligible twice and a skipped hour has no matching instant.

Conditions are checked when actions run, not continuously. They do not independently trigger a routine or terminate
one that is already active. Another matching trigger can join an already-active routine without reapplying its actions;
restoration still waits for its final active stateful trigger to end. Changing a requirement in the editor follows the
normal settings-edit lifetime rules.

For example, combine a game-launch trigger with a headset-availability requirement to switch only when that headset
is ready. Add a device-availability trigger too if connecting the headset should run the actions, and require the game
to be running so connecting it while the game is closed does nothing. Use a second available device as a condition on
a disconnect trigger when you want to switch only if the replacement is ready. To keep a dock from taking over while
your headset is connected, require that headset to be **Unavailable**. An unreadable or ambiguous device identity
skips the routine; it does not count as unavailable.

### Playback, Microphone, and Communications Targets

With **System defaults** routing, the playback and microphone targets set the normal Windows defaults
(Console and Multimedia roles). **Communications devices** independently selects playback and microphone defaults
for calls. Leave a target empty or choose **Leave unchanged** to preserve its roles. These routine targets are explicit
and do not inherit the global device-cycle role checkboxes.

For example, select speakers for playback and a headset for communications playback and microphone. Calling apps
must follow Windows communications defaults; an app with its own fixed device selection may ignore them.
Communications targets are system-wide and cannot be combined with **An application** routing in the same routine.
The section opens automatically for saved communications targets. Saved unavailable targets remain selected.

When a stateful routine restores devices, each role is checked separately. A later routine assignment or an external
default-device change prevents restoration of that role, including changes away and back. A missing previous default
cannot be restored. Volume and mute actions use the regular playback/microphone targets; there are no separate
communications-volume actions.

### App-Only Routing vs Default Switching

Use normal routine output switching when you want Windows defaults to change for the whole system.

In **What changes**, choose **System defaults** under **Route devices for** to change Windows defaults, or **An
application**
to route one app's output, input, or both. Select the target with **Browse**, **Pick App**, or an executable path/AUMID.
This works with every trigger, including schedules and manual-only routines. The action app can differ from the app
that activates an Application trigger.

The target app must already be running. If it is closed, the routine skips without changing devices or volume levels.
AudioPilot does not launch it or switch system defaults as a fallback. Volume targets are device-wide even when routing
one app; leave them blank if other apps' levels should stay unchanged.

Existing application-routing routines retain their original app as the action target. Changing the automatic trigger
later does not change that target.

App-only routing is still part of the routine system. It does not create a separate mode or second configuration file.

### Routine Volume and Mute Actions

In **Volume and mute**, output and microphone each offer **Leave unchanged**, **Mute**, and **Unmute**.
These actions affect the selected endpoint, or the current default if no endpoint is selected, even for app-only
routing. You can create a mute-only routine without selecting a device or volume level.

Mute preserves the level. Volume targets preserve mute state, including at zero; select **Unmute** explicitly if a
routine should make a muted device audible. Levels apply before mute actions.

Microphone mute actions are skipped while push-to-talk or hold-to-mute controls the microphone. Unmute is blocked
while deafen is active. Other routine actions can still complete; the overlay and execution history explain the
blocked action. These actions are not queued for later.

For stateful automatic triggers, **Restore previous audio** also restores a mute action after the final active trigger
ends. Restoration belongs to the endpoint that was changed. A newer endpoint notification, including a manual mute or
volume change, cancels that mute restoration. A newer routine activation or active microphone control can also prevent
restoration. Manual hotkey, tray, and CLI runs apply actions once and do not create a restore-on-deactivate session.

Explicit volume actions restore the level on the endpoint they changed, even if a different endpoint becomes the
Windows default. A later volume change, including changing away and back, prevents that restoration. Volume actions
leave mute unchanged; use a separate mute action when needed.

### Restore-On-Deactivate

Stateful triggers can restore affected default devices and endpoint levels after the routine ends. For
application-routing routines with a stateful trigger, restore-on-deactivate also clears the selected target app routes
so it follows the current system defaults again, unless another routine owns those routes. The trigger app and routed
app can differ.

Use this when you want a temporary profile such as:

- a game that should move output to a headset while it is running,
- Steam Big Picture that should use a couch setup until it closes,
- a focus-based app route that should return to system defaults when focus moves away,
- an application-triggered workflow that should restore speakers after the app exits.

### Packaged Apps

Application routines support both normal desktop `.exe` targets and packaged apps such as Microsoft Store or MSIX-style
apps.

Packaged apps do not use a normal file path picker. Use the packaged-app picker flow when the target is not a plain
desktop executable.

Search the picker by app name or app ID. **Ctrl+F** returns to search, **Down Arrow** moves into the results, **Enter**
selects the highlighted app, **F5** refreshes the installed-app list, and **Escape** cancels. Hover a shortened app name
to read it in full. An empty search result is distinguished from an empty installed-app list; Refresh keeps your search
and retains the selected app if it is still available.

### Tray Behavior For Routines

Right-click the AudioPilot notification-area icon to open its theme-aware quick menu. The first action changes between
**Show AudioPilot** and **Hide AudioPilot** based on the current window state and shows the configured
Show/Hide hotkey alongside it. Device switching, routines, settings, and exit actions follow below it when available.

Enabled routines can also appear in the tray menu when **Show this routine in the tray menu** is enabled.

Any automatic routine can also have a hotkey, a tray entry, or both. These manual actions apply the saved setup once;
there is no need to duplicate the routine. Application-routing routines target their configured action app, which must
already be running, even when another app has focus. System-default routines do not require their trigger app for a
manual run.

A manual run does not start a new restore-on-deactivate lifetime. If the same routine is already active automatically,
its original restoration state is retained and its active automatic triggers still determine when it ends.

### Shared-hotkey routine cycling

Under **When it runs**, expand **Cycle routines with one hotkey** and enter the same **Hotkey cycle group** for the
routines you want to cycle. Selecting an
existing group fills in its hotkey. Changing that hotkey in the editor updates all group members when you accept
and save the routine changes. Group names ignore letter case; unrelated groups cannot share the same hotkey.

Each press runs the next eligible routine in the saved list order, wrapping at the end. Disabled routines, unmet
conditions, and application-routing routines whose target app is closed are skipped. The position advances only
on success and resets when AudioPilot restarts. A failed action stops that press and is reported; it does not
silently run another routine. Presses during an ongoing cycle are ignored rather than queued.

Tray entries and CLI `routine run` still run one specific routine. Cycling is a manual run and follows the same
restoration rules described above. Leave the group blank for an individual shortcut.

## Common Settings

Hover help remains available on disabled controls. Slider values and truncated names
also remain readable; hovering does not enable a control or change its value.

### Preserve Audio Levels

Find this option under **Settings > Device Switching > Preserve audio levels**.

- When enabled, AudioPilot captures current output mixer and session volumes before an output switch.
- After the switch, it applies those levels to sessions on the newly selected output device.
- This helps keep app volumes more consistent when moving between playback devices.
- Input switching preserves the microphone level on the selected input, even when no playback device is available.
  Retries and delayed Bluetooth switches honor the same **Preserve audio levels** setting.

Restoration stays tied to the selected endpoint. If another application or Windows changes the default during a switch,
AudioPilot skips stale restoration instead of applying the old levels to the new default. CLI switching waits for its
immediate post-switch work before exiting; sessions that an application creates later still depend on that application
moving its audio stream.

Saved device cycles and routine targets also retain an optional Windows stable endpoint ID when available. This helps
recover the same device after driver or Windows updates change its ordinary ID or name. Older Windows versions and
endpoints without this property continue to use the ordinary ID and unambiguous name fallback.

### Run At Startup

- Controlled by the **Run at Startup** checkbox.
- By default, AudioPilot uses the current user's Windows startup registry. Windows decides when to launch these apps
  after sign-in.
- To try starting sooner after sign-in, enable **Settings > Miscellaneous > Task Scheduler for startup**, then apply
  settings. **Run at Startup** must also be enabled.
- This optional mode uses a Task Scheduler logon task with no configured delay, normal priority, and your usual user
  permissions. No administrator rights or password are required. Startup still depends on Windows load and has no
  guaranteed completion time.
- Once configured, both modes start in the tray and work on battery power. Switching modes replaces the previous
  registration; disabling **Run at Startup** removes it while remembering your preferred mode.
- Manage scheduled startup in Windows Task Scheduler under **Task Scheduler Library**, in the task named
  `AudioPilot Startup <your user SID>`. A task you disable there stays disabled until you explicitly enable startup
  again in AudioPilot. MSI uninstall removes the task; portable users should disable startup before deleting their app
  folder.

### Auto-Save

- Controlled by **Settings > Enable auto-save**.
- It becomes active after you apply that setting once.
- Turning it off also requires **Apply Settings**.
- After it is active, AudioPilot automatically saves device, routine, and settings changes after a short debounce.
- Edits made during a save remain pending and are saved in the next pass.
- Routine edits that would require a confirmation dialog still stay manual instead of being auto-confirmed.

### Reset to Defaults

**Reset to Defaults** asks for confirmation, then clears saved settings, settings recovery backups, device lists,
routines, and custom hotkeys. It restores default app options and turns off Windows startup, including Task Scheduler
startup. Log backups are retained.

Pending auto-saves are paused during confirmation. Cancelling resumes auto-save; confirming discards pending edits. If
settings files cannot be removed, AudioPilot reports the failure so you can resolve it and retry.

### Theme

- `Light`, `Dark`, or `System`.
- `System` follows Windows theme changes.

### Logging Privacy

- `LogLevel` controls how much log detail the app writes.
- `RedactLogContent` controls whether logs anonymize sensitive names, labels, app targets, and similar identifiers.
- Keep redaction enabled unless you intentionally need raw identifiers for local troubleshooting.

The default level is `Info`. For a media or hotkey issue, temporarily set `Debug` or `Trace`, reproduce it once, and
export a support bundle before restarting so current-session history is included. Restore `Info` afterward.
At `Debug` level, `media-current-snapshot` records whether Windows supplied a current session, the fallback candidate
count when needed, metadata availability, and capture time. `no-session` means no usable session was exposed;
`metadata-unavailable` means a session exists without track details. Timeouts and capture failures have separate
outcomes. Track titles, artists, and albums are not included in this diagnostic. Media commands also log
`reason=no-session-detected` at `Debug` level; command diagnostics distinguish `media-overlay-no-session` from
`media-overlay-metadata-unavailable`.

`media-command-routing-diagnostics` at `Debug` records the exposed session count, supported controls, and selection
reason. `candidateRoutes` counts possible command routes, not distinct tabs; `Trace` adds per-session status.
`mixer-names-refreshed` records how many existing rows were renamed without logging their titles.

Logs rotate at approximately 5 MiB or after three days; up to five backups are retained, with backups older than 21 days
removed during cleanup. Diagnostic export does not upload anything.

### Check for Updates

Enable **Settings > Miscellaneous > Check for updates** and apply the change to check for newer stable GitHub releases.
It is off by default. Enabling it while AudioPilot is running starts a background check; on later launches,
the first check waits 30 seconds so it does not delay startup. Successful checks repeat daily while the app remains
running. Failed checks retry after an hour, without a dialog. Reapplying settings or toggling the option does not bypass
a pending retry or daily interval.

When a newer version is available, the **Project repository** link turns green and a release link appears beneath it,
for example **Update available: 1.0.0 → 1.1.0**. High-contrast themes use the system text color instead.
Click the update link to open that release in your browser. Download and install it manually; AudioPilot never downloads
or installs updates. Replacing a release with the same version number does not count as an update.

Disabling the option cancels pending checks and hides the notice. Checks fetch public metadata from `api.github.com`;
GitHub receives the network request, including your IP address and an AudioPilot user-agent.
Settings, device names, logs, and other configuration are not sent. The checkbox is also available as JSON
`Miscellaneous.CheckForUpdates` or CLI `config set check-for-updates true`.

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
- Reconnect prefers the configured endpoint and its physical device identity. Ambiguous name matches are rejected, and a
  failed request does not try a different same-named device. Other audio profiles belonging to the same physical
  Bluetooth device may still be requested.

### Listen To This Device

- AudioPilot can toggle Windows **Listen to this device** for the current default input endpoint.
- Use a hotkey or `AudioPilot.Cli.exe listen toggle|on|off`.
- If **Listen monitor output** is empty, Windows uses the current default output.
- If a specific monitor output is configured and available, AudioPilot routes listen audio there.
- If Windows accepts the change but its state cannot be fully read back, the overlay identifies it as unverified. A
  confirmed state or output mismatch is reported as a failure.
- **Output unavailable** means the active monitor target could not be read or resolved. Reconnect the output or select
  an available **Listen monitor output** and enable Listen again.

### Master And Microphone Volume Hotkeys

- AudioPilot can raise or lower the current default output and current default microphone level from global hotkeys.
- CLI keys `master-volume-step-percent` and `mic-volume-step-percent` control the step size, matching the settings shown
  in the UI.
- Step values accept whole percentages from `1` to `100`.

### Foreground App Audio

In Settings, expand **Foreground App** to assign volume up, volume down, and mute hotkeys. All are unassigned by
default. The step defaults to five percentage points and accepts `1` to `100`.

The shortcuts control playback sessions belonging to the app you're using, including its child processes, across
active output devices. Each session keeps its own volume offset; raising volume does not unmute it. Mute silences
all matching sessions; pressing it again unmutes them. If the app has no controllable audio session, the overlay
says so. With the desktop or taskbar in front, it shows **No foreground app to control**. It never falls back to
changing master volume or another app. App labels use the session name or window title when available, like the mixer.
File Explorer only targets its own sessions, not applications it launched.

Browsers can share audio sessions across tabs and windows, so these are browser-volume controls, not per-tab controls.
Apps running with restricted access or unusual audio-process arrangements may not expose a match.

### Push-to-Talk And Hold-to-Mute

Expand **Microphone Hold Controls** in Settings. Assign a **Push-to-Talk** hotkey, check **Enable push-to-talk**, and
apply the settings. The current default microphones mute immediately, unmute while held, and mute again on release.
The mode also applies at startup and to newly selected default microphones. While enabled, it reasserts mute after
other controls unmute an idle microphone. Disabling it or exiting normally restores each microphone's pre-mode state,
unless you made a newer explicit microphone-mute or deafen choice in AudioPilot.

**Hold-to-Mute** works independently: hold its shortcut to mute temporarily, then release to restore the prior state.
An already-muted microphone stays muted. Both hotkeys are unassigned by default, and push-to-talk mode is off.
The overlay shows **Microphone muted** in red and **Microphone live** in green. After a hold ends, its message reflects
the restored mute state; mixed microphone states use a neutral message.

Use a keyboard key or mouse button. Wheel input, Pause, and Print Screen are not suitable for holding. A modified
shortcut ends when its key/button or a required modifier is released. If both holds overlap, hold-to-mute takes
priority. Deafen prevents push-to-talk from unmuting.

AudioPilot cancels a hold when its binding changes, audio devices change, or Windows switches desktops or suspends.
Push-to-talk returns to its muted idle state; hold-to-mute restores the original endpoints
only while their mute state still matches the temporary value and no external mute change superseded the hold.
Changing microphone volume alone does not cancel restoration. An explicit mute/deafen choice cancels temporary
hold-to-mute restoration. After cancellation, release the shortcut before pressing it again. Keep AudioPilot running
while using these controls; force-closing the process cannot perform restoration.

## Hotkeys And Actions

Release builds include an `ABOUT.txt` file beside `AudioPilot.exe`. It records the release and build target, lists
every configurable global hotkey action, and summarizes the fixed in-window keyboard shortcuts. Configurable
bindings in that file are release defaults; your saved settings remain authoritative.

Fixed in-window shortcuts include `F1` for local About or online help, `Ctrl+1` through `Ctrl+4` for direct tab
selection, `Ctrl+Tab` for the next tab, `Ctrl+S` to save the current context, `Ctrl+N` to create a routine, `Enter` to
edit a routine when the routine list is focused, `Ctrl+F` to focus search in a searchable picker, and `F5` to refresh
devices. List-specific management shortcuts remain documented in `ABOUT.txt`.

Supported hotkey actions:

- Output switch
- Input switch
- Output reverse switch
- Input reverse switch
- Show/hide app (shows AudioPilot when hidden and hides it to the tray when visible)
- Show audio status (unassigned by default)
- Show current track
- Play/Pause, Next, Previous (unassigned by default)
- Seek forward/backward (unassigned by default; shared seek step defaults to 10 seconds)
- Mute mic
- Mute sound
- Deafen
- Listen input
- Master volume up
- Master volume down
- Microphone volume up
- Microphone volume down

### Show Audio Status

Assign **Settings > Global Hotkeys > App > Show Audio Status**, then apply settings. The binding is
empty by default. Press it to show the current output and microphone names, volume
percentages, and mute states without opening AudioPilot or changing audio. It works
while AudioPilot is in the tray and uses the configured default-device roles, like
the volume hotkeys.

The overlay reads fresh values for each request and follows the existing overlay
enablement, position, and duration settings. A missing default device is shown as
**No default device**; a failed read is shown as **Status unavailable**. One endpoint
can still be displayed when the other is unavailable. A volume of 0% and a muted
device remain distinct states.
The volume and mute-status line is green when unmuted and red when muted. Unknown
mute states keep neutral text, and the status is always written out alongside the color.

The CLI binding key is `show-audio-status-hotkey`, stored in
`Hotkeys.App.ShowAudioStatus`. For textual or JSON audio status, use the existing
`AudioPilot.Cli.exe status` command.

### Hotkey Rules

- Duplicate hotkey combinations are blocked.
- A short debounce helps avoid accidental repeated triggers.
- Runtime labels for number-row, punctuation, and international keys follow the active Windows keyboard layout and
  refresh when the input language changes. For example, the US `OemPlus` key is shown as `=` rather than an ambiguous
  second `+`.
- Saved settings and CLI values remain canonical and do not change when the keyboard layout changes.
- Left, right, middle-button, and vertical or tilt-wheel hotkeys require at least one modifier.
- Mouse 4/5 (the side buttons) can be assigned without modifiers. Assigning one replaces its usual Back/Forward action;
  leaving it unassigned preserves normal behavior. The hotkey field explains this tradeoff.
- Bare text-producing keys such as `A`, `1`, or `/` also require a modifier. The field keeps a rejected key visible
  and explains how to correct it. Scrolling over a hotkey field without a modifier scrolls the page normally.
- Function keys, dedicated media/volume keys, Pause, Scroll Lock, Print Screen, Insert, and Num Lock can be assigned
  without a modifier, directly in any hotkey field. An informational warning explains that the global shortcut may
  override the key's usual action. The highlight clears after a successful save/apply; with auto-save, it clears when
  you leave the field after saving. Saved bindings reopen without that highlight, and the explanation stays in the
  tooltip. Conflicts with another hotkey or application remain highlighted until resolved.
- Navigation keys and Delete still require a modifier. Plain Delete clears a hotkey field; Tab moves to the next field.

## Settings File

Most people should manage settings from the UI or CLI instead of editing JSON directly.

AudioPilot stores settings in:

- MSI-installed app: `%AppData%/AudioPilot/settings.json`.
- Portable ZIP or source run: app directory as `settings.json` when writable.
- Portable ZIP or source fallback: `%AppData%/AudioPilot/settings.json` when the app directory is not writable.

If the active settings file is corrupted or unreadable, recovery is attempted from `backups/settings.json.bak*`.

MSI uninstall keeps `%AppData%/AudioPilot` by default so your settings can be reused later. From Windows Apps/Programs,
use Modify/Change to open AudioPilot maintenance mode, choose Remove, then select `Also delete saved
AudioPilot data` if you want a clean uninstall. The Start Menu shortcut named `Change or uninstall AudioPilot` opens the
same maintenance flow, and `Uninstall AudioPilot and delete settings` runs the clean-uninstall path directly.

A settings file from a newer schema is left unchanged during loading, and saving
is refused to protect fields this version does not understand. Use the newer
AudioPilot version to edit those settings.

The file uses nested, PascalCase sections. Common JSON paths include:

| Setting | JSON path or section |
| --- | --- |
| Output/input cycles, roles, and switch hotkeys | `DeviceSwitching.Output`, `DeviceSwitching.Input` |
| Global hotkeys and volume steps | `Hotkeys.App`, `Hotkeys.Media`, `Hotkeys.Mute`, `Hotkeys.Volume` |
| Listen hotkey and monitor output | `Hotkeys.Listen` |
| Overlay enablement, position, and duration | `Overlay` |
| Saved routines | `Routines.Items` |
| Preserve audio levels | `DeviceSwitching.PreserveAudioLevels` |
| Bluetooth reconnect preflight | `DeviceSwitching.BluetoothReconnectEnabled` |
| Logging level and privacy | `Miscellaneous.LogLevel`, `Miscellaneous.RedactLogContent` |
| Startup and theme | `RunAtStartup`, `Miscellaneous.UseScheduledStartup`, `Theme` |

CLI config keys use hyphenated names instead: for example, `output-switch-hotkey` maps to
`DeviceSwitching.Output.SwitchHotkey`. Do not put CLI key names or old flat property names at the root of
`settings.json`.

Advanced tuning values such as Bluetooth reconnect timing and Steam Big Picture detection timing can also be managed
through CLI config keys and are stored under the nested `AdvancedTuning` section in `settings.json`.

For full config and runtime key coverage, use [CLI.md](CLI.md).

## Logs And Device Reference Files

Log file:

- `AudioPilot.log`

Optional device reference file:

- `DEVICES.txt`

Generation is off by default. CLI config `generate-device-reference-file` accepts `false`, `true`, or `hashed`.
The file is a generated reference, not a settings file; editing it does not change AudioPilot. Each entry includes the
endpoint ID and friendly name, plus a stable ID when Windows provides one. Hashed IDs cannot be used as CLI device
selectors. The hashed mode anonymizes both endpoint and stable IDs but keeps friendly device names; it is not a fully
redacted support report. Use a diagnostic bundle when sharing troubleshooting information.

Privacy notes:

- Default logs try to avoid leaking raw sensitive identifiers when practical.
- CLI diagnostics can expose richer details when you explicitly request them.
- If you are sharing diagnostics publicly, prefer `--redact` and avoid `--show-paths` unless a maintainer asks for exact
  paths.

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

After wake, AudioPilot recovers audio state, re-registers hotkeys, and refreshes devices. Failure in one phase does not
block the others. A second sleep/wake cycle requests another recovery even if the earlier one is still running.

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
2. If running from ZIP or source, check the app directory first, then `%AppData%/AudioPilot` if that directory is not
   writable.
3. Backup the current file before editing anything.
4. Prefer UI or CLI reconfiguration before manual JSON edits.
5. If the file is unreadable, let the app attempt backup recovery first.

## Safe Diagnostics For A Bug Report

Good first-pass diagnostics:

1. Describe what you expected and what happened instead.
2. Note whether the problem is switching, mixer behavior, listen monitoring, routines, or startup/tray behavior.
3. Prefer `AudioPilot.Cli.exe diagnostics export-bundle .\support-bundle.zip --json`; it is redacted by default and
   includes recent history, status, media state, config validation, and sanitized logs. This is especially helpful
   for routine and media-hotkey issues because those paths include extra current-session diagnostics.
4. If you only need a quick snapshot, include `AudioPilot.Cli.exe status --json --redact` and
   `AudioPilot.Cli.exe diagnostics status --json --redact`.
5. Attach raw log excerpts only if you are comfortable sharing them, or rerun the bundle with `--include-sensitive` for
   private local troubleshooting.

If a maintainer needs exact machine-specific paths or identifiers, they can ask you to rerun diagnostics with less
redaction.

## CLI Summary

AudioPilot accepts CLI commands through `AudioPilot.Cli.exe`.

Behavior summary:

- With a running UI instance, commands are forwarded to that instance.
- Without a running UI instance, automation-safe commands run headlessly.
- UI-only commands such as `show`, `hide`, and `startup open` require a running UI host.
- CLI `show` and `hide` remain explicit actions for predictable scripts; the configured window hotkey is the toggle
  action.
- CLI `media play` and `media pause` request a specific playback state without showing overlays. They succeed without
  sending a command when the session already has that state, and report unsupported requests instead of toggling. Use
  `--json` and check `confirmed` when your automation needs observed playback state; a successful exit can also mean the
  player accepted the request without confirming its effect yet.

In PowerShell, use .\AudioPilot.Cli.exe from the app folder unless you added it to PATH.

Quick examples:

```powershell
AudioPilot.Cli.exe status --json
AudioPilot.Cli.exe media play --json
AudioPilot.Cli.exe media pause --json
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
