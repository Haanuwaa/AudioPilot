# Update lifecycle contract

A small BCL-only contract shared by AudioPilot and its independent updater. The helper does not load `AudioPilot.dll` or
depend on WPF.

The integration is opt-in through `AUDIOPILOT_UPDATE_LIFECYCLE`, with either a matching diagnostic session or a
Sandbox-only managed launch. A normal running instance can also expose an attachment listener when
`AUDIOPILOT_UPDATE_TEST_SESSION` explicitly names its Sandbox session. That listener verifies the helper's pipe client
PID, path, creation time and session before connecting back to the helper's nonce-bound pipe. Ordinary launches expose
neither channel. These are development boundaries; public updates remain disabled.

Messages use schema 1, an 8 KiB maximum frame, duplicate-property rejection, a per-launch random nonce, and exact
process IDs. Both ends restrict the pipe to the same Windows account and elevation. The app also verifies the helper's
executable path, creation time and session before composing runtime services. The helper verifies the connecting child
process and its reported application version.

Lifecycle:

1. A fresh launch authenticates, completes normal startup ordering, then reports `ready`.
2. `prepare` atomically suspends idle microphone admission, blocks new controls and routine activations, and flushes
   accepted autosave edits. Manual unsaved work, failed saves, active microphone ownership, audio tests,
   interaction/dialogs, routine work and resume recovery defer preparation.
3. `cancel`, helper disconnection or a preparation deadline release reversible admission. No installer is started by the
   app.
4. `shutdown` repeats the save check, uses the normal shutdown pipeline, and reports `stopped` only when its tracked
   owners drained. The helper must also wait for actual process exit. An uncertain drain never authorizes deployment.
5. A restart candidate loads settings read-only and reports `staged-ready` after shell initialization and a responsive
   dispatcher. It waits for the authenticated helper's commit before enabling automation. Aborting before commit exits
   without replaying startup routines or rewriting saved settings.
6. A committed restart restores requested visibility and resumes monitoring. It does not replay AudioPilot-startup
   routines already run by the original instance.

Push-to-talk ownership is deliberately not transferred between processes. Updates defer while it is enabled/owned. The
original app is not forcibly terminated to satisfy the handshake. Only an uncommitted, microphone-deferred candidate
belongs to the helper's kill-on-close job.

Local MSI and portable deployment probes now use this gate, configuration snapshots and candidate-only job ownership;
see [the updater](../AudioPilot.Updater/README.md#real-app-deployment-and-recovery). Versioned portable layouts use a
matching diagnostic identity or an explicit `Managed` descriptor for normal-instance data paths.

`inspect` reports the authenticated app's active settings path and local-state root; the adapter reads these after final
autosave. `detach` ends control without exiting the committed app. Existing user work and microphone ownership still
govern preparation. The same-owner channel permits future handoffs after a completed update.

This protocol is not release authentication. The loopback HTTPS feed and maintained TUF verifier provide the separate
test-package trust path. Production bootstrap authority, signing, broader deployment validation and end-user consent
remain separate requirements.
