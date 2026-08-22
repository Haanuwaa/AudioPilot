# Scripts

This folder contains repo automation for local development, CI, release packaging, and release validation.

## Daily Development

- `build.ps1`: restore and build the solution.
- `run-tests.ps1`: run unit, integration, visual, stress, hardware-soak, or full test suites. Every run rejects empty
  discovery and writes a TRX report under `artifacts/testresults`.
  Integration allows deliberate skips when hardware is disabled or optional; strict hardware mode and other categories
  require executed tests. It refuses to stop a running AudioPilot
  UI unless `-StopRunningUi` is supplied explicitly. Routine validation clears inherited desktop/audio opt-ins. Visible
  windows, popups, and focus changes require `-Category visual -AllowDesktopInteraction`;
  real audio changes require `-Category integration -AllowHardwareAudio`. Neither is implied by a broad project review.
  The hardware-soak category also requires `-AllowHardwareAudio` and all four configured endpoint IDs,
  alternates both output and input defaults, creates silent WASAPI playback sessions, and repeatedly opens a microphone
  while restarting recording-session monitoring. Microphone data is discarded without files or speaker monitoring.
  `AUDIOPILOT_TEST_INPUT_DEVICE_ID` selects the recording-soak microphone; otherwise it uses the Console default.
  Each timed soak defaults to 30 minutes (three timed tests run sequentially); override with
  `AUDIOPILOT_HARDWARE_SOAK_MINUTES` (1-120). One-minute runs validate the harness but do not establish long-term stability.
  The runner timeout covers all three durations plus ten minutes for setup, cleanup, and the shorter hardware tests.
  Session soaks reuse explicit session identities: Windows may retain inactive sessions until process exit.
  They test repeated stream activation/disposal and subscription cleanup without growing the native session topology.
  Resource checks compare early and late post-warm-up medians, log growth rates, and count retained session registrations.
  The switch soak restores and verifies all six original default-role assignments, including after failure.
  The script owns the category filter and rejects extra filter arguments. For focused tests, invoke `dotnet test` with
  one combined `--filter-query`; xUnit does not support mixing query and simple filters.
  Crash mini dumps and test-sequence capture are enabled by default. Pass `--crashdump-type Full` or `--hangdump`
  through `-DotnetTestArgs` for deeper diagnostic collection. Reruns archive existing dumps and crash sequences under
  `artifacts/testresults/diagnostics` before resetting category results.
- `tests/test-runner.ps1`: verify test-runner argument transfer, category isolation, coverage retention, environment
  restoration, and failure handling with a fake dotnet process. CI runs this suite.
- `validate-all.ps1`: build once, run unit tests and script checks, and validate formatting and documentation.
  `-IncludeIntegration` and `-IncludeStress` add isolated suites; `-Coverage` also enforces the unit coverage baseline.
- `validate-format.ps1`: run or fix solution formatting. `-ChangedOnly` checks staged, unstaged, and untracked C# files
  locally, or changes against the pull-request base in CI. Changes to build/analyzer configuration trigger a full check.
- `validate-line-endings.ps1`: check repository text files against `.gitattributes`, or normalize them with `-Fix`.
- `validate-doc-links.ps1`: validate markdown links.
- `validate-test-isolation.ps1`: audit static mutable test hooks; local runs warn, CI runs strict.
- `validate-coverage.ps1`: enforce the same unique production-line coverage baseline and ratchet used by CI. Defaults
  to `artifacts/testresults/coverage/unit`; use `-CoverageRoot` to select another report directory.
- `test-application-smoke.ps1`: launch isolated GUI/CLI processes, verify tray startup, command forwarding,
  show/refresh/hide, and graceful shutdown; retain JSON/Markdown performance reports and redacted logs.
  Requires `-AllowDesktopInteraction`. Use `-BaselinePath` for median comparisons, or the `application_smoke`
  manual CI input. It blocks startup registration and does not change audio levels or devices.
- `profile-runtime.ps1`: collect bounded CPU, memory, handle, and thread samples from a running AudioPilot process. It
  defaults to a five-minute tray-idle capture under the user's temporary `AudioPilotDiagnostics` folder and never
  imposes arbitrary pass/fail thresholds.
- `update-cli-docs.ps1`: update generated CLI documentation blocks, or verify them with `-Check`. Uses Release by
  default; `-NoBuild` reuses an existing build.

## Release And Packaging

- `publish-release-profiles.ps1`: restore and publish all release profiles except `FolderProfile`; `-Version` applies
  one validated version to app and CLI outputs.
- `build-local-release-artifacts.ps1`: build local release artifacts end to end. Release MSIs reuse the self-contained
  profiles published earlier in the same invocation; Debug MSIs remain opt-in with `-IncludeDebugInstallers`.
  Prints step durations and writes `artifacts/local-build-timings.json`.
- `package-release.ps1`: package ZIP/MSI/winget outputs and write checksums, release manifest, SBOM, and provenance
  metadata.
- `validate-release-integrity.ps1`: validate packaged release artifacts, MSI metadata, SBOM, and provenance metadata.
- `validate-winget-manifests.ps1`: validate generated winget YAML.
- `release-body.ps1`: generate release notes. Use `-ChecksumTable` to print markdown checksum rows.
- `generate-wix-publish-fragment.ps1`: generate the WiX fragment from published app files.

## Specialized Checks

- `validate-release-gate-policy.ps1`: verify the release workflow gate topology and GitHub Actions security policy,
  including immutable external-action SHAs, environment-mediated command inputs, and least-privilege permissions.
- `validate-release-hardware.ps1`: preflight hardware device IDs for integration release checks.
- `benchmark-readytorun.ps1`: measure application-only ReadyToRun publish size and repeated startup-to-window timing.
  Select `benchmark_readytorun` during manual CI dispatch to run it. Local runs require AudioPilot to be closed and open
  test windows; startup registration is restored and temporary publish copies are removed. Use it after SDK, dependency,
  or startup changes.
- `test-msi-smoke.ps1`: install, repair, option-preserving upgrade, downgrade-rejection, PATH/shortcut, and uninstall
  MSI smoke helper used by release automation. It automatically attempts to uninstall a temporary product if an
  assertion fails.
  Use `-VerifyUpgradeRollback` with `-UpgradeFromMsiPath` to force an upgrade failure after old-product removal and
  verify restoration of its registration, binaries, and saved data. `-VerifyUninstallRollback` checks that a failed
  uninstall also preserves the startup Run entry and scheduled task. `-ResultsRoot` redirects evidence output.
- `test-msi-option-matrix.ps1`: exercises all eight desktop shortcut, Start menu shortcut, and CLI `PATH` combinations
  against an x64 MSI, including both data-preserving and clean uninstall behavior.
- `new-msi-rollback-probe.ps1`: create a new test-only MSI copy with an intentional failure inside an upgrade
  or uninstall transaction (`-Operation Upgrade|Uninstall`). It refuses to overwrite the source or an existing output.
  Never distribute the probe as a release.
- `prepare-msi-sandbox.ps1`: prepare x64 installers and scripts for Windows Sandbox, with networking and device
  redirection disabled, read-only input mappings, and a dedicated writable results folder.
  Supply `-InstallMsiPath` and an older `-UpgradeFromMsiPath`. Open the generated `AudioPilot-validation.wsb` after
  Sandbox is installed and Windows has restarted; inspect `results/result.json` and `results/validation.log` afterward.
  The guest runs the eight option combinations, repair, upgrade and uninstall rollback, downgrade rejection,
  and uninstall checks.
  Preparation alone does not execute installations.

`lib/` contains shared helper code for scripts and is not intended as a direct command surface.
`tests/test-msi-helpers.ps1` creates a small test database without installing anything and checks query results and
immediate file-lock release, including missing-table queries.
Pass `-InstallerPath` with a built x64 MSI to check option defaults, saved unchecked values, and explicit overrides in
safe Windows Installer sessions. This does not install the package or open its dialogs.
`tests/test-validation.ps1` checks local/PR formatting selection, failure propagation, optional test suites, and
coverage enforcement using disposable fixtures.
