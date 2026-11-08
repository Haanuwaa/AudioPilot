# Updater design and validation

The updater is an optional component of AudioPilot. [Stable bootstrap packages](UPDATE_STABLE.md) embed their
independent root and publisher pins. Development builds without a policy retain manual updates. The
[staging pilot](UPDATE_PILOT.md) remains a separate x64 self-contained build.

## Trust and transfer

The maintained `go-tuf/v2` client verifies the pinned bootstrap, root rotation, role thresholds, metadata versions,
expiration, target identity, package length and SHA-256. Durable trust state prevents rollback after restart. There
is no fallback to a custom verifier. Self-signing does not establish public Windows trust; see
[signing](UPDATE_STAGING.md).

The pilot fixes its origin, Windows publisher, deployment variant and candidate version at build time. Ordinary builds
cannot acquire that authorization through an environment variable. TLS validation and permitted redirect hosts stay
enabled. The operator publisher and its private keys are not part of the client payload.

Downloads stream through bounded buffers to a partial file. Response headers have a 30-second deadline; body reads
have a separate 30-second inactivity deadline. Either timeout retains resumable bytes. A progressing download has no
absolute 30-second limit. Pause cancels the current request and preserves a checkpoint; resume revalidates the target
and uses Range/If-Range. Cancel discards partial state.
One **Update and restart** approval covers the exact release for an uninterrupted flow and defaults to Later.
No app preparation occurs until the complete package and its Windows publisher signatures are verified. Pausing,
closing, failing or deferring ends progression toward installation; continuing requires a new explicit approval.
The engine retains independent staging and installation operations for controlled tooling.
CLI cancellation revokes the approved flow from its metadata check onward. A late check result cannot start a new
download or installation. Pause and safe close keep resumable bytes; cancellation discards managed staging even when
verification is finishing. An armed installation still settles through commit or recovery.

Static stable-feed configuration is validated once per immutable feed instance. TUF discovery, package verification
and the final deployment authorization remain operation-scoped checks.

## Application lifetime

The app stages the packaged helper outside the replaceable payload. A ready cached runtime can be shared under a read
lease after full SHA-256 verification of every file. Copying or repairing a runtime requires an exclusive lease;
an active incomplete or corrupt cache is not modified. Active leases also prevent trimming. The helper and app
authenticate exact PID, creation time, executable path, Windows user and session over bounded named-pipe messages.
An executable name alone never establishes ownership.

Before replacement, the original app stops admitting conflicting work and checks its save and microphone barriers.
Final autosave must drain; manually unsaved changes must be resolved. Microphone ownership defers installation.
The original app exits through normal shutdown. It is not forcibly terminated to make an update proceed.
The final online authorization finishes before quiescing the app. Its open, verified package has a two-minute monotonic
handoff deadline; replacement checks during that handoff are local. An expired handoff requires retry/recovery,
not an online request after the original app closes.

A candidate starts with routine activation and microphone ownership deferred. Only a verified commit resumes normal
operation. Aborting before commit does not replay startup routines or rewrite saved settings. A committed restart
preserves visibility and does not replay startup routines already run by the original instance. Only an uncommitted,
microphone-deferred candidate belongs to the helper's kill-on-close job.

## Deployment and recovery

MSI replacement retains the registered installation/data folders, shortcut choices and CLI PATH option. Its transaction
commits after candidate health succeeds; a failed start rolls back. Reboot-required and uncertain installer outcomes
are not treated as confirmed success. Recovery waits for Windows Installer instead of killing it.
The journal records the last confirmed deployment, architecture, runtime and version before replacement. Recovery UI
uses those facts while candidate files may be missing or incomplete; it does not inspect a half-replaced installation
before the recovery barrier. Installation identity and recorded generation paths must still match.

Portable installs use stable native launchers, versioned payload folders and a durable current/previous generation
pointer. Settings and window placement live outside the replaced generation. The launcher routes an interrupted update
through recovery before normal startup. Configuration snapshots and the recovery journal settle an approved interrupted
installation without requesting another install approval.
Marked portable generations are trimmed after settlement, preserving the current payload, its rollback payload and
all pending recovery references. Configuration snapshots are removed only after a durable settlement receipt matches
the completed journal. Unmarked folders, redirected trees and busy files are left alone for a later cleanup.

An idle portable installation can refresh its binding after a folder move; MSI Modify can refresh saved installer
options. Both retain job identity and trust/version floors. Pending installation or recovery remains bound to its
recorded paths and must settle before rebinding. A different copy cannot adopt that workspace.

Closing the updater pauses a reversible transfer, cancels reversible preparation, or hides an armed installation while
its controller finishes. The helper remains alive until installation/recovery settles. A failed recovery retains its
journal and offers retry; it never guesses that an uncertain deployment succeeded.

## Validation

Unit, integration and stress tests run through the normal test runner. Real audio changes and visible desktop tests
remain opt-in. Sandbox preparation scripts create read-only public inputs and a dedicated results mapping; preparing a
configuration does not itself run an installation. Signing keys and host settings are not mapped into the guest.

| Check | Entry point |
| --- | --- |
| Synthetic portable/MSI transactions and interruption | `scripts/prepare-update-sandbox.ps1` |
| Real-app save, microphone and startup barriers | `scripts/prepare-update-lifecycle-sandbox.ps1` |
| Real-app deployment, rollback and helper interruption | `scripts/prepare-update-deployment-sandbox.ps1` |
| Windows signatures and approved package ordering | `scripts/prepare-update-signing-sandbox.ps1` |
| Local TUF approval and publication | `scripts/prepare-update-staging-sandbox.ps1` |
| UI, local packages and loopback/public HTTPS | `scripts/prepare-update-ui-sandbox.ps1` |
| Packaged normal-app MSI/portable pilot | `scripts/prepare-update-pilot-sandbox.ps1` |
| Disposable key backup, rotation and renewal | `scripts/tests/test-update-authority.ps1` |

The [pilot acceptance flow](UPDATE_PILOT.md#staging-publication-and-validation) verifies normal app entry, a single
explicit update/restart approval, HTTPS pause/reopen/resume, failed-start recovery, final autosave, installer options
and safe closing. Its driver uses files for child logs because a deliberately surviving app can inherit its helper's
standard handles.

Keep physical-device, native ARM64/x86, restricted-policy, power-loss and additional installer interruption coverage
distinct from mocked or x64 Sandbox results. Stable scheduled renewal is verified only by an actual scheduled run,
not a manual dispatch. Staging maintenance is manual-only. Stable selection uses the newest approved package matching
the exact deployment variant; a publicly trusted
signing identity remains separate from the finite pilot. Stable public activation is a reviewed build and publication
step; it does not relax those gates.

## Operation logs and recovery backups

The window and CLI-driven helper share `updater.log` in the owned updater workspace. Installed builds use
`%LOCALAPPDATA%\AudioPilot\updater\installations\<installation-id>`; staging builds use `updater-pilot` instead of
`updater`. A managed portable copy keeps the workspace in its root's `app` folder. Logs rotate at 1 MiB and retain two
archives under `backups/updater.log.bak` and `backups/updater.log.bak.1`, with the existing age-based pruning.

The existing queued writer records session, approval, phase, preparation, restart, recovery and failure events.
Progress updates and status polls do not generate log records. Each log instance has a run ID. Private messages and
unrecognized fields are always hashed, independently of the main app's redaction option; raw paths, feed URLs,
settings contents, pipe identities and approval credentials are omitted. Error types, HRESULTs, native/HTTP codes and
method origins remain available. Logging failure does not change update outcomes. Shutdown gives log draining a
one-second budget after runtime cleanup; an interrupted log is never used to decide whether an update committed.

The native TUF bridge returns only a schema and an allowlisted failure reason. Expired metadata, unavailable or
rate-limited feeds, network failures, damaged trust state and a target no longer offered remain distinct in job feedback.
Raw native error text, server-provided text and local paths do not cross that boundary. An unknown reason becomes a
generic verification failure, preserving the fail-closed result without guessing that the network caused it.

`job.json` and `recovery.json` remain the durable state authority. Configuration snapshots under `snapshots/<id>` keep
settings, settings recovery backups and window placement for rollback. They are separate from log archives and contain
original configuration data. Windows Installer's separate `install.log` may contain installation paths; it does not use
the updater's redacted formatter.

Snapshots include only `settings.json`, its hashed location markers, window placement and recognized settings recovery
backups. Failed captures are removed immediately. On reopening a job, unreferenced marked snapshots are reclaimed;
pending or not-yet-durably-settled recovery snapshots remain available. Unreadable journals defer pruning. Temporary
UI-helper logs are removed only from that launch's own directory after their writer drains.

Staged helper copies are marked before copying and protected by runtime leases. Interrupted, idle copies are reclaimed;
the current helper and one completed previous copy are retained. Settled portable package ZIPs are removed when the job
closes. Paused transfers, unresolved recovery and trust state remain available. MSI sources use hash-specific paths and
remain available while registered for repair or needed by recovery. Obsolete owned sources are trimmed only after
successful source discovery; failed or ambiguous discovery preserves them. Checking another release must not remove
or overwrite an installed package's repair source.

Normal uninstall continues to preserve user data. Clean uninstall also removes an installed updater workspace only when
its ownership marker matches the exact executable, no job or deployment lease is active, and any recovery journal has a
matching durable completion receipt. Other installations, portable roots and unfinished recovery are preserved. Shared
helper caches are reclaimed only when no installed workspaces remain, and active helper leases always prevent deletion.
