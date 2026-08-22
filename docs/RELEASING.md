# Releasing

This guide describes how to ship a new AudioPilot release.

Use this page for maintainer release work. Use [CONTRIBUTING.md](CONTRIBUTING.md) for normal development workflow and [CHANGELOG.md](CHANGELOG.md) for release-facing change notes.

## Versioning

- Follow Semantic Versioning.
- Update `AudioPilotVersion` in `Version.props` before release.
- Keep `docs/CHANGELOG.md` with a live `Unreleased` section between releases.

## Pre-Release Checklist

1. Confirm all release-targeted work is merged.
2. Move notable items from `Unreleased` into the new version section in [CHANGELOG.md](CHANGELOG.md).
3. Re-create an empty `Unreleased` section immediately after cutting release notes.
4. Run local validation:

   ```powershell
   dotnet build AudioPilot.sln
   pwsh ./scripts/run-tests.ps1 -Category unit
   dotnet format AudioPilot.Format.slnf --verify-no-changes --severity info
   ./scripts/validate-doc-links.ps1
   ./scripts/build-local-release-artifacts.ps1 -Clean
   ```

5. Ensure CI is green on `main`.
6. Complete a privacy and logging review so default logs remain useful without exposing unnecessary sensitive identifiers.

## Docs Consistency Check

Before tagging:

- `README.md`, `docs/USER_GUIDE.md`, and `docs/CLI.md` should not contradict one another.
- Detailed command, JSON, and exit-code behavior belongs only in [CLI.md](CLI.md).
- Contributor and maintainer workflow docs should still match the real script names and workflow names.

## Coverage Policy

- CI coverage policy is defined in `.github/quality/coverage-policy.json`.
- Coverage must stay above `minimumCoveragePercent`.
- Once measured coverage reaches or exceeds `nextTargetPercent`, CI fails until the policy file is ratcheted in the same PR.
- On a release milestone, raise `minimumCoveragePercent` toward `nextTargetPercent` and set the next target.
- Keep the policy update in the same PR as the coverage ratchet.
- Keep unit, integration, and stress coverage runs separated. Do not collapse them into one collector session; the known hardware-plus-stress combination can trigger CLR abort `0x80131506` under combined coverage collection.

CI uploads `artifacts/testresults/coverage` on push runs for diagnostics and trend review. Reports are separated into category subdirectories; the coverage gate uses `artifacts/testresults/coverage/unit`.

## Release Gate In CI

Normal `CI` and release automation do different things:

- `.github/workflows/ci.yml` stays unit-oriented and does not set `AUDIOPILOT_RUN_INTEGRATION` or `AUDIOPILOT_RUN_STRESS`,
- `.github/workflows/release-artifacts.yml` splits unit, integration, and stress into separate jobs before packaging,
- the dedicated stress job enables `AUDIOPILOT_RUN_STRESS=1`,
- the integration job enables `AUDIOPILOT_RUN_INTEGRATION=1` only after device-id preflight succeeds,
- the small set of visible WPF window tests remains manual-only because release automation does not set `AUDIOPILOT_RUN_VISUAL_WPF=1`.

If you intentionally need the visible WPF checks during local release prep, use:

```powershell
pwsh ./scripts/run-tests.ps1 -Category visual
```

That path is manual-only and intentionally enables visible test windows for the dedicated WPF shell checks.

The `Release Artifacts` workflow runs the broader gate before publishing:

```powershell
pwsh ./scripts/run-tests.ps1 -Category unit
pwsh ./scripts/run-tests.ps1 -Category integration
pwsh ./scripts/run-tests.ps1 -Category stress
```

Plain `dotnet test --project AudioPilot.Tests/AudioPilot.Tests.csproj` is no longer the release-oriented superset by itself because integration and stress tests discovery-skip unless explicitly enabled.

Required integration device variables:

- `AUDIOPILOT_TEST_OUTPUT_DEVICE_A`
- `AUDIOPILOT_TEST_OUTPUT_DEVICE_B`
- `AUDIOPILOT_TEST_INPUT_DEVICE_A`
- `AUDIOPILOT_TEST_INPUT_DEVICE_B`

These values must be exact Core Audio endpoint IDs from the self-hosted Windows runner, not friendly names.

Tag behavior:

- GitHub-hosted `windows-latest` runs remain valid for release automation, but hardware validation is advisory only when no dedicated hardware runner or device-id secrets are available,
- when the required device secrets are present and preflight succeeds, the integration gate is enabled,
- when secrets are missing or preflight fails on a GitHub-hosted runner, the workflow warns and continues with software-only validation,
- for strict hardware enforcement, run the workflow on a self-hosted Windows runner with the required device configuration.

## Disposable Installer Validation

Before shipping installer changes, exercise installation and repair, all eight shortcut/CLI PATH combinations,
upgrade and forced-failure rollback, downgrade rejection, normal uninstall, and explicit saved-data removal.
Use a disposable Windows system; the smoke scripts refuse to replace an existing installation for the current user.

With Windows Sandbox installed and its required restart complete, prepare a bundle using a current x64 MSI and an older baseline:

```powershell
./scripts/prepare-msi-sandbox.ps1 -InstallMsiPath <current-x64.msi> -UpgradeFromMsiPath <older-x64.msi>
```

Open the generated `AudioPilot-validation.wsb`. The guest runs the checks automatically and writes
`results/result.json`, `results/validation.log`, and individual MSI logs alongside the configuration.
Inputs are read-only, networking and device redirection are disabled, and only the dedicated results folder is writable from the guest.
Bundle preparation and successful MSI compilation are not installation test results.

The forced-rollback scenario modifies a separate test-only MSI copy to fail after removal of the old product inside the transaction.
It checks the intended failure, restored product registration and binary hashes, and preserved saved data before attempting the normal upgrade.
Never distribute this probe as a release artifact. CI requests the same scenario with `test-msi-smoke.ps1 -VerifyUpgradeRollback`.

## Hardware Runner Setup

Configure the required device IDs as repository secrets in GitHub Actions.

Current repository automation does not assume a dedicated self-hosted
Windows hardware runner. If you want release tags to fail on missing
hardware validation instead of falling back to warnings and software-only
coverage, provide a self-hosted runner plus the required device-id
secrets.

Name note:

- validation commands use the `AudioPilot.CliHost` source project,
- the shipped executable inside release artifacts is always `AudioPilot.Cli.exe`.

Validate the configured IDs locally or on the runner with:

```powershell
pwsh ./scripts/validate-release-hardware.ps1 -Configuration Release
```

If you need to refresh the IDs on the runner:

```powershell
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices list output --json
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices list input --json

# After choosing an exact id, verify it resolves cleanly:
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices get output "<endpoint-id>" --json
dotnet run --project AudioPilot.CliHost/AudioPilot.CliHost.csproj -c Release -- devices get input "<endpoint-id>" --json
```

Use the `id` values from those commands, not the friendly `name` values.

Run the NAudio/Core Audio hardware suites after configuring the endpoint IDs:

```powershell
./scripts/run-tests.ps1 -Category integration

# Rapidly switches configured defaults, then creates and tears down real silent WASAPI
# sessions across every active output while scanning endpoints and sessions.
# Defaults to 30 minutes; accepted range is 1-120.
$env:AUDIOPILOT_HARDWARE_SOAK_MINUTES = "30"
./scripts/run-tests.ps1 -Category hardware-soak
```

Direct endpoint-test certification also accepts these optional endpoint IDs:

```powershell
$env:AUDIOPILOT_TEST_OUTPUT_DEVICE_ID = "non-default-render-endpoint-id"
$env:AUDIOPILOT_TEST_INPUT_DEVICE_ID = "capture-endpoint-id"
# Optional and intended for a headphone output to avoid feedback:
$env:AUDIOPILOT_TEST_MONITOR_DEVICE_ID = "monitor-render-endpoint-id"
./scripts/run-tests.ps1 -Category hardware-soak
```

The direct-output check verifies that the Windows multimedia default does not change. The microphone check verifies real packet metering and, only when the optional monitor endpoint is supplied, bounded live-monitor startup and
teardown. Use headphones for the monitor endpoint.

The integration suite validates managed endpoint notifications and real session-wrapper callbacks. The hardware soak also validates rapid default-device switching and checks managed memory, private memory, and process-handle
growth while alternating session creation across active output endpoints. A physical unplug/replug, Bluetooth reconnect, and real sleep/resume still require suitable hardware and operator control on the runner.

## Manual Release Validation

Before tagging, run the local validation flow when possible. Then do real-hardware checks:

- hotplug churn: unplug and replug output and input devices while the app is running,
- Bluetooth recovery: disconnect/reconnect an active headset and verify endpoint, session, mute, and mixer state recover,
- Windows lifecycle: change defaults, create/end application sessions, and sleep/resume while monitoring handle count and private memory,
- rapid hotkey bursts: trigger output, input, and reverse actions repeatedly,
- shutdown soak: minimize, restore, and exit repeatedly,
- settings recovery: corrupt the primary settings file and verify backup recovery.
- ARM64 certification: on native ARM64 Windows, install the ARM64 MSI and smoke-test startup, CLI device enumeration, default-device switching, and uninstall.

Confirm recovery candidates come from `backups/settings.json.bak*` and that the newest valid payload wins.

## Create The Release

1. Create and push the version tag:

   ```powershell
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```

2. Open or update the GitHub release notes.
3. Review the draft release created by the workflow before publishing.

The `Release Artifacts` workflow creates releases as drafts so maintainers can adjust highlights before publication.

If needed, regenerate the release body locally:

```powershell
./scripts/release-body.ps1 -Version X.Y.Z
```

## Publish Artifacts

Use the publish profiles under `AudioPilot/Properties/PublishProfiles`.

Each packaged publish output should include both:

- `AudioPilot.exe`
- `AudioPilot.Cli.exe`
- generated `ABOUT.txt` build and shortcut information

### Supported Profiles

- `FrameworkDependent-win-x64`
- `FrameworkDependent-win-x86`
- `FrameworkDependent-win-arm64`
- `SelfContained-win-x64`
- `SelfContained-win-x86`
- `SelfContained-win-arm64`

ReadyToRun is currently disabled for every release profile. The win-x64 benchmark measured a 17.55% publish-size increase for a modest and variable startup improvement, with no repeatable p95 gain. Re-enable it only when repeated
measurements justify the size tradeoff for a specific runtime identifier.

To repeat the size and startup-to-window comparison, close AudioPilot and run:

```powershell
./scripts/benchmark-readytorun.ps1 -Configuration Release
```

### Publish Commands

```powershell
./scripts/publish-release-profiles.ps1
```

The publish script restores each selected profile before publishing so the required runtime-identifier packs are available for self-contained builds. It intentionally skips `FolderProfile` because that profile points at a personal
local folder. The CLI host executable is included by the app publish flow through `PublishCliHostAlongsideApp` in `AudioPilot/AudioPilot.csproj`.

To publish one profile:

```powershell
./scripts/publish-release-profiles.ps1 -TargetProfile SelfContained-win-x64
```

### MSI Build Commands

Build the WiX installer after the publish outputs are ready:

```powershell
dotnet build AudioPilot.Installer/AudioPilot.Installer.wixproj -c Release -p:Platform=x64
dotnet build AudioPilot.Installer/AudioPilot.Installer.wixproj -c Release -p:Platform=arm64
```

For the full local artifact flow, including publish profiles, both Release MSIs, release ZIPs, winget manifests, and integrity validation, use:

```powershell
./scripts/build-local-release-artifacts.ps1 -Clean
```

Local MSI validation note: if `dotnet build` fails with `WIX1105` because Windows Installer validation is blocked by the machine policy or current shell, rerun from an elevated shell when you need local ICE validation. For local
artifact-only checks on that machine, you can add `-p:SuppressValidation=true` or run `./scripts/build-local-release-artifacts.ps1 -Clean -SuppressMsiValidation`; do not make that the default project setting because CI and release
builds should keep MSI validation enabled.

If you want to open `AudioPilot.Installer/AudioPilot.Installer.wixproj` inside Visual Studio, install FireGiant HeatWave for your Visual Studio version. This is optional IDE support only and is not required by the command-line build or CI.

Current installer coverage:

- `x64`: MSI and ZIP
- `arm64`: MSI and ZIP
- `x86`: ZIP only

Note: x86 MSI is excluded because x86 architecture is legacy for modern Windows systems. The app is primarily targeted at x64 and arm64 which are the dominant architectures. x86 users can still install via the portable ZIP package.

## Packaging And Integrity Validation

After publish completes, generate packages:

```powershell
./scripts/package-release.ps1 -Version X.Y.Z
```

This writes:

- `artifacts/release/SHA256SUMS.txt`
- `artifacts/release/release-manifest.json`
- `artifacts/release/AudioPilot-X.Y.Z-sbom.spdx.json`
- `artifacts/release/AudioPilot-X.Y.Z-provenance.json`
- `artifacts/release/*.zip`
- `artifacts/release/*.msi`
- `artifacts/release/winget/**/*.yaml`

Then validate package integrity:

```powershell
./scripts/validate-release-integrity.ps1 -ReleaseRoot artifacts/release
```

Validation covers:

- manifest and checksum count consistency,
- SHA256 parity between release artifacts, `SHA256SUMS.txt`, and `release-manifest.json`,
- required executables inside each ZIP package,
- expected MSI outputs and generated winget manifests,
- SPDX SBOM and provenance metadata shape, subject coverage, and checksum parity.
- MSI-only winget manifest shape, including x64/arm64 installer URLs, hashes, ProductCodes, UpgradeCode, platform baseline, and manifest version.

`release-manifest.json`, the SPDX SBOM, and the provenance JSON are attached to the public GitHub release alongside checksums and binaries.

If you need checksum rows for the release body:

```powershell
./scripts/release-body.ps1 -ChecksumTable
```

Optional packaging flags:

```powershell
./scripts/package-release.ps1 -Version X.Y.Z -Clean
./scripts/package-release.ps1 -PublishRoot artifacts/publish -OutputRoot artifacts/release
```

## GitHub Actions Automation

Use `.github/workflows/release-artifacts.yml` to automate publish and packaging on:

- manual runs through `workflow_dispatch`,
- tag pushes matching `v*`.

The workflow publishes all non-folder profiles through `scripts/publish-release-profiles.ps1`, builds `x64` and `arm64` MSI installers, runs `scripts/package-release.ps1`, validates the result, generates GitHub artifact
attestations for checksum subjects, and uploads `artifacts/release` as a workflow artifact.

Release creation happens on tag push only. Public release assets currently include:

- packaged ZIP files,
- MSI installers for `x64` and `arm64`,
- `SHA256SUMS.txt`,
- `release-manifest.json`,
- `AudioPilot-X.Y.Z-sbom.spdx.json`,
- `AudioPilot-X.Y.Z-provenance.json`.

Generated winget manifest YAML files remain in `artifacts/release/winget` and the workflow artifact for submission and audit, but they are not attached as user-facing GitHub release assets.

## Distribution Guidance

- Recommend the self-contained ZIP builds by default, especially `win-x64`, while releases remain unsigned.
- Keep the `x64` and `arm64` MSI installers available as an alternate per-user install path for users who want shortcuts and Add/Remove Programs integration.
- Use ZIP artifacts when you want a portable layout or need `x86`.
- Framework-dependent ZIP builds are smaller but require the matching .NET runtime.
- Self-contained ZIP builds are larger but include the runtime.

MSI uninstall behavior:

- MSI installs keep runtime settings, logs, backups, and generated device references under `%APPDATA%\AudioPilot`; the install folder is reserved for installed binaries and shortcuts.
- Portable ZIP runs keep runtime data beside `AudioPilot.exe` when that directory is writable, and fall back to `%APPDATA%\AudioPilot` when it is not.
- Normal uninstall removes installed binaries, shortcuts, Add/Remove Programs entries, and installer registry values while preserving user-created settings, logs, backups, and device data.
- Windows app-list removal is routed through MSI maintenance UI by exposing Modify/Change instead of direct ARP remove, so the clean-uninstall choice is visible before removal.
- The MSI maintenance uninstall flow includes an unchecked `Also delete saved AudioPilot data` option before the final remove confirmation.
- The Start Menu includes `Change or uninstall AudioPilot`, which opens MSI maintenance mode so the cleanup option is visible before Remove.
- The Start Menu also includes `Uninstall AudioPilot and delete settings` for an explicit clean uninstall of `%APPDATA%\AudioPilot`.
- Silent clean uninstall can be run with `msiexec /x {ProductCode} AUDIOPILOT_CLEAN_UNINSTALL=1`.
- Upgrades and uninstalls ask a running `AudioPilot.exe` process to close before changing files to reduce locked-file failures.

## Post-Release

- Verify installation and launch on a clean Windows environment.
- Confirm the GitHub release body, assets, and checksums are correct.
- Start a new `Unreleased` section in [CHANGELOG.md](CHANGELOG.md) if needed.

## Winget Submission

After the GitHub release is published, submit the winget manifests to the official winget repository:

1. Fork the [winget-pkgs repository](https://github.com/microsoft/winget-pkgs)
2. Create a new branch: `Haanuwaa.AudioPilot.X.Y.Z`
3. Create the manifest directory structure:

   ```text
   manifests/h/Haanuwaa/AudioPilot/X.Y.Z/
   ```

4. Copy the generated winget manifests from `artifacts/release/winget/` to the new directory
5. Submit a pull request to the winget-pkgs repository with the title `Haanuwaa.AudioPilot version X.Y.Z`
6. Include the release notes in the PR description

The winget manifests are automatically generated by `scripts/package-release.ps1` during the release process and include:

- Version manifest (Haanuwaa.AudioPilot.yaml)
- Installer manifest (Haanuwaa.AudioPilot.installer.yaml)
- Locale manifest (Haanuwaa.AudioPilot.locale.en-US.yaml)

These manifests are generated dynamically from the actual MSI files, including correct SHA256 hashes and ProductCodes.

Current winget policy:

- Winget manifests are MSI-only for `x64` and `arm64`.
- Upgrades use `UpgradeBehavior: install` so the MSI can preserve installation options and restore the previous version if an upgrade fails.
- ZIP artifacts remain available from GitHub releases, but they are not included in winget manifests.
- Generated manifests target winget manifest schema `1.12.0`.
- The minimum compatible OS version is `10.0.19041.0`, matching the app target and installer floor.
- Release messaging must describe this as a technical minimum, not as blanket support for every Windows release at or above that build. Official support is limited to Windows 11 and currently supported Windows 10 Enterprise/LTSC
  editions that meet the floor, subject to the Microsoft and .NET 10 support lifecycles.

Validate generated winget manifests locally with:

```powershell
./scripts/validate-winget-manifests.ps1 -ManifestRoot artifacts/release/winget -ReleaseRoot artifacts/release
```

Before submitting, run `winget validate --manifest artifacts/release/winget/h/Haanuwaa/AudioPilot/X.Y.Z` against the manifests generated from the final published MSI files.
The repo-local validator does not require winget and cannot verify that release download URLs are publicly accessible. Publish the release before submitting; draft release assets are not public downloads.

## Related Docs

- Changelog: [CHANGELOG.md](CHANGELOG.md)
- Contributor workflow: [CONTRIBUTING.md](CONTRIBUTING.md)
- Developer guide: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)
