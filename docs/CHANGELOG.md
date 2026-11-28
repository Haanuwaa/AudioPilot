# Changelog

## [1.0.0] - 2026-11-28

Initial release of AudioPilot, a Windows app for audio control and automation.

### Device Switching

- Build separate output and input switch orders. Cycle with global shortcuts or from the tray, or choose a specific
  device in the app, Device Picker, or CLI. Shortcuts can also cycle in reverse.
- Choose which Windows audio roles to switch, including Communications. Optionally preserve applicable volume levels
  and mute states across device changes.
- Use the keyboard-friendly Device Picker to search saved devices or show every available output or input. The list
  refreshes on device changes, and eligible Bluetooth devices can reconnect before switching.

See [device switching and the Device Picker](USER_GUIDE.md#device-picker).

### Mixers and App Routing

- Adjust output, microphone, system sounds, and individual app levels in the main mixers. Select several playback apps
  to adjust their volume or mute together while preserving their relative volume differences.
- Use Quick Mixer for playback and recording controls, live Windows signal meters, and keyboard search. Select
  playback groups independently of the main window. Pin apps on either tab and optionally sort Output by **Playing first**.
- Set an app's preferred output or input and see which device its current audio session uses. Use **Windows default**
  to clear an assignment. Recording controls can affect other apps that share the same microphone level.

See [Quick Mixer](USER_GUIDE.md#quick-mixer) and the [main mixers](USER_GUIDE.md#mixer-view).

### Global Shortcuts and Microphone Controls

- Open AudioPilot, Device Picker, or Quick Mixer with global shortcuts, including when a focused app prevents native
  hotkey delivery. If Windows declines to focus the main window, it appears briefly and can be clicked to activate it.
- See the current Output and Input devices in Device Picker, with role labels when Windows uses different defaults.
- Assign shortcuts for output, input, and foreground-app volume or mute, with volume steps as small as 0.1 percentage
  point. Use **Deafen** to mute the Windows default-role output and input devices.
- Configure push-to-talk or hold-to-mute for temporary microphone control. Push-to-talk keeps the default microphones
  muted between presses while it is enabled.
- Test speakers, headphones, and microphones without changing Windows defaults. Check live mic levels and monitor
  microphone input through a chosen output.

See [hotkeys and actions](USER_GUIDE.md#hotkeys-and-actions) and [audio tests](USER_GUIDE.md#test-speakers-headphones-or-a-microphone).

### Routines

- Save named setups for default devices, Communications devices, per-app routes, fractional volume levels, and mute.
  Run them from the app, tray, CLI, or a shortcut. Multiple routines can share a shortcut and cycle in order.
- Trigger routines when an app opens, gains focus, or exits, or while Steam Big Picture is active. Use device changes,
  network connections, schedules, AudioPilot startup, Windows unlock, or system resume as triggers.
- Combine automatic triggers and require all selected device, app, network, and time conditions to pass. Temporarily
  pause automatic routines without losing your setup.
- Restore previous audio after supported stateful routines end, with safeguards for newer manual changes and
  microphone controls.
- Optionally play a chime when a routine changes audio. When AudioPilot is hidden, genuine automatic routine failures
  share one Windows notification until they clear.

See [routines, triggers, and conditions](USER_GUIDE.md#routines).

### Media and Desktop Controls

- Play, pause, skip, and show the current track in supported Windows media sessions. Seek forward or backward by a
  configurable step from 0.1 seconds to one hour. Optional overlays show action feedback, track details and cover art
  when available, or app icons for foreground volume controls.
- Run in the tray and optionally start with Windows. Open Device Picker, Quick Mixer, and Routines from the taskbar
  Jump List, and use media buttons in the taskbar thumbnail.
- Pin Device Picker and Quick Mixer as independent windows with saved layouts and separate **Always on top** controls.
  Switch between English and Spanish live, and choose a light, dark, or system theme.

See [media controls](USER_GUIDE.md#media-controls) and [startup and tray](USER_GUIDE.md#startup-and-tray).

### CLI, Settings, and Diagnostics

- Script device, mixer, media, routine, and configuration tasks with `AudioPilot.Cli.exe`. Use JSON output and shell
  completion for automation. Supported commands work through the running app or headlessly.
- Enable optional auto-save, import or export settings and routines, and use CLI validation and dry runs before
  importing configuration.
- Review current-session operation history, shortcut registration, save and logging health, and on-demand audio
  defaults. Copy redacted diagnostics or export a support bundle for troubleshooting.

See the [CLI reference](CLI.md), [common settings](USER_GUIDE.md#common-settings), and
[bug-report diagnostics](USER_GUIDE.md#safe-diagnostics-for-a-bug-report).

### Installation and Updates

- Choose a per-user MSI or a portable ZIP for x64, ARM64, or x86. The installer includes the runtime and offers a CLI
  PATH option. Portable builds are available with the runtime included or as a smaller framework-dependent download.
- Check for updates in official stable packages and approve the exact version from the app or CLI. AudioPilot stays
  open during downloading and verification, and the transfer can be paused, resumed, or canceled.
- Use smaller portable update packages when available, with full package fallback. Installation preserves settings
  and applies autosave and microphone safeguards. MSI and portable installs can roll back if the new version fails
  its startup health check.

See [downloads and system requirements](../README.md#get-audiopilot) and [installing an update](USER_GUIDE.md#install-an-update).
