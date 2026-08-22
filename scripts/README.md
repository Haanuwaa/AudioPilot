# Scripts

This folder contains repo automation for local development, CI, release packaging, and release validation.

## Daily Development

- `build.ps1`: restore and build the solution.
- `run-tests.ps1`: run unit, integration, visual, stress, hardware-soak, or full test suites. Every run uses strict zero-test handling and writes a TRX report under `artifacts/testresults`. It refuses to stop a running AudioPilot UI unless `-StopRunningUi` is supplied explicitly. The hardware-soak category requires all four configured endpoint IDs, rapidly switches configured defaults, and creates real silent WASAPI sessions across every active output; it defaults to 30 minutes and can be overridden with `AUDIOPILOT_HARDWARE_SOAK_MINUTES` (1-120).
- `validate-all.ps1`: run the normal local validation chain.
- `validate-format.ps1`: run or fix solution formatting.
- `check-format-changed-files.ps1`: style-check changed C# files.
- `validate-line-endings.ps1`: check repository text files against `.gitattributes`, or normalize them with `-Fix`.
- `validate-doc-links.ps1`: validate markdown links.
- `validate-test-isolation.ps1`: audit static mutable test hooks; local runs warn, CI runs strict.
- `validate-coverage.ps1`: enforce the same unique production-line coverage baseline and ratchet used by CI.
- `profile-runtime.ps1`: collect bounded CPU, memory, handle, and thread samples from a running AudioPilot process. It defaults to a five-minute tray-idle capture under the user's temporary `AudioPilotDiagnostics` folder and never imposes arbitrary pass/fail thresholds.
- `update-cli-docs.ps1`: check generated CLI documentation blocks.
- `stop-audiopilot-and-test.ps1`: stop a running UI process, then run tests. Use `-CheckOnly` when you only want to fail if the UI is running.

## Release And Packaging

- `publish-release-profiles.ps1`: restore and publish all release profiles except `FolderProfile`; `-Version` applies one validated version to app and CLI outputs.
- `build-local-release-artifacts.ps1`: build local release artifacts end to end.
- `package-release.ps1`: package ZIP/MSI/winget outputs and write checksums, release manifest, SBOM, and provenance metadata.
- `validate-release-integrity.ps1`: validate packaged release artifacts, MSI metadata, SBOM, and provenance metadata.
- `validate-winget-manifests.ps1`: validate generated winget YAML.
- `release-body.ps1`: generate release notes. Use `-ChecksumTable` to print markdown checksum rows.
- `generate-wix-publish-fragment.ps1`: generate the WiX fragment from published app files.

## Specialized Checks

- `validate-release-gate-policy.ps1`: verify the release workflow gate topology and GitHub Actions security policy, including immutable external-action SHAs, environment-mediated command inputs, and least-privilege permissions.
- `validate-release-hardware.ps1`: preflight hardware device IDs for integration release checks.
- `benchmark-readytorun.ps1`: measure ReadyToRun publish size and repeated startup-to-window timing.
- `test-msi-smoke.ps1`: install, option-preserving upgrade, downgrade-rejection, PATH/shortcut, and uninstall MSI smoke helper used by release automation. It automatically attempts to uninstall a temporary product if an assertion fails.
- `test-msi-option-matrix.ps1`: exercises all eight desktop shortcut, Start menu shortcut, and CLI `PATH` combinations against an x64 MSI, including both data-preserving and clean uninstall behavior.

`lib/` contains shared helper code for scripts and is not intended as a direct command surface.
