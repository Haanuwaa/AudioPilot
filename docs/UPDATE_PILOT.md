# Opt-in updater pilot

This guide is for the separate x64 self-contained staging pilot, which authorizes one specific candidate version from
the pinned staging authority and publisher. Use [stable update operations](UPDATE_STABLE.md) for official public
packages. Development builds without an embedded policy retain manual installation; the pilot does not enable the
production feed or change its trust pins.

## User flow

Opt-in builds use the same [approval and restart flow](USER_GUIDE.md#install-an-update) and
[CLI controls](CLI.md#update-with-approval) as stable builds. For CLI acceptance checks, pass the exact pilot candidate
version to `update apply` and `update resume` with `--approve`.

Use a disposable Windows Sandbox installation for the pilot. The default fixtures test 1.0.1 updating to 1.0.2;
these are test versions, not public release announcements. Use a fresh
folder for the initial portable pilot ZIP and import existing settings if needed. Its stable native launchers open the
current version under `app/versions`, while settings and placement stay under `data`. Subsequent pilot updates preserve
that data.
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
Only snapshot and timestamp keys are used by the manually dispatched staging maintenance workflow.

After publication, write `feed-ready.json` in the dedicated Sandbox results folder. The guest tests the normal app's
entry point, single approval, fresh approval after pause/reopen, real HTTPS Range resume, failed-start rollback,
final autosave, installation options,
safe closing and reopening the updater after commit. No candidate package from its input folder is used for the HTTPS
transfer. Preserve `result.json`, approval receipts, source/payload manifests and failure logs.

This controlled pilot does not establish native ARM64/x86, restrictive-policy, physical-microphone or broad hardware
coverage. Staging maintenance is manual. Verify production stable renewal and its independent heartbeat separately,
as described in [stable operations](UPDATE_STABLE.md#renewal-and-recovery); manual staging dispatch is not scheduled proof.

## Stable bootstrap packages

Follow [stable package approval](UPDATE_STABLE.md#authority-and-package-approval) to build production bootstraps and
publish with the independent stable authority. Keep the pilot's test identity and acceptance packages in staging.
