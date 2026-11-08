# Local updater UI

Opt-in engineering harness using AudioPilot's theme resources, window presentation helpers and job controller.
Local packages, loopback HTTPS and one independently pinned GitHub staging feed are supported.
Public live updates and installation outside Windows Sandbox remain disabled. This project is not included
in release payloads or normal application startup. The headless deployment helper remains independent of WPF.

Remote metadata checking does not download a package. The themed Download/Later dialog shows the signed
version and transfer size. Approval is scoped to that exact job and package and survives pause/reopen/resume.
Installation always needs a separate Install and restart/Later confirmation; Later is the safe default.
Closing a pending confirmation cancels it. Recovery for an approved installation completes without another prompt.

The window shows installed and candidate versions, deployment/runtime, real copy progress, pause/resume, cancellation,
verification, explicit installation, recovery and compact technical details. Install never becomes the default Enter
action after verification. It uses the fixture's saved theme unless overridden with `--theme dark|light|system`.

Closing pauses a copy where possible, cancels reversible preparation, or hides an armed installation while the
controller finishes. The process stays alive until installation/recovery settles. Failed recovery returns the window
with attention feedback; an explicit subsequent close retains its recovery journal. No original app is killed to make an
update proceed. The real app still owns final autosave, microphone admission and startup ordering. Microphone ownership
defers installation.

## Preview and Sandbox validation

Build this project explicitly. `--preview` opens a read-only window on the host; `--render-preview <output-directory>`
renders dark/light client previews without capturing the desktop. Neither mode accepts a package or installs anything.

For local installs, publish this project and `AudioPilot.Updater` separately as self-contained applications. Copy the
architecture-matched `AudioPilot.TufBridge.exe` beside both. Use the baseline/candidate app fixtures from the
[deployment guide](../AudioPilot.Updater/README.md), including their lifecycle entry points; official release packages
are not interchangeable with these fixtures.

```powershell
./scripts/prepare-update-ui-sandbox.ps1 `
  -BaselineZip <baseline.zip> -CandidateZip <candidate.zip> `
  -BaselineMsi <baseline.msi> -CandidateMsi <candidate.msi> `
  -HelperDirectory <published-headless-helper> -UiDirectory <published-ui> `
  -OutputRoot <new-evidence-directory>
```

Open the generated `UpdaterUi.wsb`. It disables networking, microphone, camera, clipboard and printer access, and maps
only dedicated input/results folders. It tests the actual window's buttons for both deployment formats: close during
copying, reopen/resume, failed-start rollback, and closing while installation is armed. It checks final autosave, window
placement and custom installer options. The portable case includes a 95-second idle interval between reversible
preparation attempts, exceeding the helper's active-operation deadline. Idle waiting does not expire that connection;
subsequent operations retain their timeout. Reports and rendered windows are written to `results`. Fixture apps are shut
down normally after health verification so successive probes cannot interfere. MSI cleanup resolves the exact product
from its bound installation folder, rather than assuming a registry location.

To explore the window manually inside a fresh Sandbox, run the headless helper's `job-ui-prepare <portable|msi>
<baseline> <candidate> <settings-fixture> <new-workspace> <options-mask>`, then launch `AudioPilot.Updater.Ui.exe
--local-ui <workspace>`. The options mask retains Desktop shortcut/Start Menu shortcut/CLI PATH bits. Do not run an
automatic smoke and a manual installation concurrently in the same guest.

Focused tests exercise safe closing, failed recovery, microphone deferral, and explicit installation in both themes with
controlled gates. They do not replace physical microphone, native ARM64, power-loss, production signing or live
repository validation.

## Connected HTTPS test

Add `-HttpsFeed` to the Sandbox preparation command to test the production-style installation adapter with the
downloader, maintained TUF client, controller and UI. The normal app uses its actual instance identity and settings
locations, with an explicitly enabled Sandbox attachment endpoint. The proof checks tampered metadata,
pause/reopen/resume, interrupted transfer retry, cancellation, final autosave, failed-start recovery, installer options
and closing during commit. The restarted app remains running until a separate authenticated cleanup. External networking
stays disabled; the feed binds only loopback and uses a temporary guest TLS key, without modifying host/root certificate
stores.

For manual exploration inside Sandbox, use `AudioPilot.Updater.exe feed-prepare <baseline.zip|baseline.msi>
<candidate.zip|candidate.msi> <settings-fixture> <new-workspace>`, then `AudioPilot.Updater.Ui.exe --feed-demo
<workspace> <candidate-package>`. This starts a temporary test feed for the lifetime of the window. Reopening a demo
creates a new temporary endpoint, so a paused attempt from its previous endpoint must be cancelled before rechecking.
`--feed-ui <workspace>` connects to an already-running test feed described by the workspace's `feed.json`.

Public release authority, production signing, stable feed publication and end-user activation remain separate acceptance
requirements in the [updater guide](../AudioPilot.Updater/README.md#before-production-integration).

## Real HTTPS staging

Use `prepare-update-ui-sandbox.ps1 -StagingFeed` with baseline ZIP/MSI packages, the public bootstrap root
and the approved public publisher certificate. No candidate packages are mapped into the guest.
Networking is enabled only for this mode; audio input, camera and clipboard stay disabled.
`--staging-ui <workspace>` opens the user-approved flow, while `--staging-smoke <workspace> <output>`
explicitly drives the real themed dialogs as test automation. Both require Windows Sandbox.
The connection uses normal TLS validation and permits GitHub release redirects only to its release CDN.
See the [staging guide](../docs/UPDATE_STAGING.md) for publication and trust custody.
