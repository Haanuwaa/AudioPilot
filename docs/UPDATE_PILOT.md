# Opt-in updater pilot

Ordinary AudioPilot releases still use manual installation. They contain no embedded pilot policy, updater window or
normal-instance updater listener. Public live updates remain disabled. The pilot is a separate x64 self-contained build
authorizing one specific candidate version from the pinned staging authority and publisher.

## User flow

Open **Settings > Updates > Open updater...** in an opt-in build. Opening or closing the window does not approve a
download, prepare the app for replacement or install anything. Checking verifies metadata; **Update and restart / Later**
then asks permission for the exact version and package. AudioPilot stays running during download and verification,
then the approved flow installs and restarts it without a second prompt. Later remains the default choice.
Pause, Cancel, closing the updater, a failure or a safety deferral stops progression toward installation. Continuing
an interrupted download or a deferred update requires a new explicit approval; approval is not restored from disk.

**Check now** in Settings works independently of automatic checking and never downloads a package. The manual release
link remains available. CLI `update status` and `update check` share that release feedback. A normal build reports that
integrated updating is disabled; CLI flags cannot enable it.

Opt-in builds also support `update apply --version 1.0.2 --approve`, `update pause`, `update cancel` and
`update resume --version 1.0.2 --approve`, each with optional `--json`. The version must be exact. Approval covers the same
single update/restart flow as the window. Accepted means admitted, not installed: inspect `update status` for progress
and the final result. Starting replacement needs a running app; an already-running helper can still accept pause/cancel
and status after the app exits. The control endpoint is current-user-only and bound to the staged helper's exact process.
Closing, cancellation and pause revoke approval; installation already armed and recovery are allowed to settle safely.

Installation keeps the saved settings, configured data folder and installer options. Final autosave must drain, manually
unsaved edits must be resolved, and active microphone ownership defers replacement. Startup routines retain their
existing ordering. A failed candidate startup rolls back through the existing MSI transaction or portable recovery
journal. An approved interrupted recovery does not ask for another installation approval.

Exit any existing AudioPilot instance before starting the pilot. The initial pilot MSI advances the current 1.0.0
installation to 1.0.1; the controlled candidate is 1.0.2. Use a fresh
folder for the initial portable pilot ZIP and import existing settings if needed. Its stable native launchers open the
current version under `versions`, while settings and placement stay under `data`. Subsequent pilot updates preserve
that data. Do not extract the initial versioned ZIP over an existing flat portable installation.
Pilot builds are marked `-pilot` in their informational version. Returning to a lower-numbered stable MSI can require
uninstalling the pilot while retaining settings, then installing the stable package; the updater does not bypass Windows
Installer's downgrade rules.

## Packaging and trust

`new-update-pilot-policy.ps1` creates or reuses a code-signing identity in Windows' CurrentUser personal certificate
store. Its private key is not exported, committed or sent to Sandbox. Keep a recoverable maintainer backup before using
this identity beyond the pilot. This is separate from the encrypted TUF-key backup.

Self-signing does not establish public Windows trust or remove SmartScreen warnings. No certificate is added to the
host's trusted-root store. The disposable test guest imports only the public certificate for full Authenticode and MSI
cabinet verification. TUF authorizes exact package hashes; the pilot independently pins the expected Windows publisher,
bootstrap root, origin, deployment variant and candidate version.
The client also checks actual Windows signatures before preparing AudioPilot. MSI signatures and all AudioPilot
entry points in portable packages must match the pinned certificate. The pilot permits that exact self-signed
identity through a private verification chain without importing it into a Windows certificate store.

```powershell
./scripts/new-update-pilot-policy.ps1 -BootstrapRoot <verified-bootstrap.root.json> -OutputDirectory <fresh-policy-folder>
./scripts/build-updater-packages.ps1 -PolicyDirectory <policy-folder> -OutputDirectory artifacts/pilot-build -SignTool <signtool.exe>
./scripts/prepare-update-pilot-sandbox.ps1 -BuildDirectory artifacts/pilot-build -OutputDirectory artifacts/pilot-proof -SignTool <signtool.exe>
```

Pilot builds require an isolated MSBuild output key. Payload validation rejects a pilot policy in an ordinary release,
checks all four .NET entry points and the native verifier, and retains Release ZIP/MSI compression. The packages share
one .NET runtime. Native portable launchers avoid duplicating that runtime. Dependency notices and SPDX 3.0.1
inventories include the compiled Go client and its modules; the signing/publishing operator is not shipped.

Published staging targets are immutable. For a rebuilt candidate, pass a new `-CandidateVersion` when creating its
policy. Do not replace an approved version with different package bytes, including after committing its source.

The normal app verifies and caches the packaged updater outside the replaceable installation before launching it.
Cache files are checked against their packaged counterparts and can recover an interrupted copy. Active runtimes hold
a lease against trimming; at most the current and one inactive generation are retained, with busy generations kept
until a later cleanup. Closing the updater releases its runtime lease after installation/recovery has settled.

## Staging publication and validation

The Sandbox first verifies both candidate packages, checks both normal-app entry points without downloading, and writes
`approved-packages.json`. Remap only its approved paths
to the unchanged host package files, checking every SHA-256 before publication. `publish-update-pilot.ps1` holds the
shared staging lease across reading current public metadata, signing and publication. Verified external renewals are
adopted into a fresh local repository generation; regressed checkpoints are rejected. Root keys remain outside Actions.
Only snapshot and timestamp keys are used by unattended renewal.

After publication, write `feed-ready.json` in the dedicated Sandbox results folder. The guest tests the normal app's
entry point, single approval, fresh approval after pause/reopen, real HTTPS Range resume, failed-start rollback,
final autosave, installation options,
safe closing and reopening the updater after commit. No candidate package from its input folder is used for the HTTPS
transfer. Preserve `result.json`, approval receipts, source/payload manifests and failure logs.

This controlled pilot does not establish native ARM64/x86, restrictive-policy, physical-microphone or broad hardware
coverage. Scheduled renewal verification remains a separate operational acceptance item until an actual scheduled run
is observed. Do not count a manual dispatch as scheduled proof.

## Stable bootstrap packages

The shared `build-updater-packages.ps1` also supports `-Bootstrap`. A stable bootstrap embeds a public TUF root,
its independently supplied SHA-256 pin and the existing self-signed publisher pin. It does not authorize a fixed
candidate. After authenticated TUF refresh, selection uses the newest stable AudioPilot version and the exact
installed architecture, deployment type and runtime. A missing variant or newer helper requirement produces
manual-update guidance; the client never substitutes an older release or another variant.

Public access is disabled by default. Explicitly reviewed production policies can enable it in both the app and
native verifier. Packaging alone does not publish a feed. Stable production uses a separate authority and publisher;
acceptance identities must never be distributed. See [Stable updates](UPDATE_STABLE.md) for production approval,
backup, renewal and validation requirements.

```powershell
./scripts/new-update-bootstrap-policy.ps1 -BootstrapRoot <verified-public-root.json> `
  -BootstrapSha256 <independent-uppercase-SHA256> -PublisherThumbprint <existing-code-signing-thumbprint> `
  -OutputDirectory artifacts/bootstrap-policy
./scripts/build-updater-packages.ps1 -Bootstrap -Version <unpublished-version> `
  -PolicyDirectory artifacts/bootstrap-policy -OutputDirectory artifacts/bootstrap-packages `
  -Runtimes win-x64,win-x86,win-arm64 -DeploymentModes SelfContained,FrameworkDependent `
  -SignTool <Windows-SDK-signtool.exe>
```

MSIs remain self-contained and retain the existing installer options. Framework-dependent portable builds require
the matching Windows Desktop runtime. The initial portable ZIP includes native launchers and a versioned payload;
settings and placement use its separate `data` folder. Startup registrations and taskbar links use the stable root
launcher, so they do not retain a replaceable generation path. Extract it into a fresh folder. Existing flat portable folders
are not converted or replaced in place.

The package receipt includes each variant, SHA-256 sums, dependency inventory and SBOM. Initial portable ZIPs are
for users; the separate `.update.zip` payloads are for the feed. `update-targets.json` describes those payloads and
MSIs but is explicitly **unapproved**: it is not signed TUF metadata and cannot authorize a client download.
Independent package approval and feed publication remain separate operations. Use a new unpublished version for
changed package bytes; do not replace the approved pilot versions.

The approval flow stays **Update and restart / Later**. Check does not download a package. Approval binds the
exact artifact and version; resume and final verification reauthorize that same package even if a newer release
appears. Transfer and verification finish while AudioPilot runs. Final autosave, microphone deferral, startup ordering
and MSI/portable rollback continue through the existing lifecycle protocol.
