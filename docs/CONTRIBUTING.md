# Contributing

Thanks for contributing to AudioPilot.

This guide keeps changes predictable, reviewable, and easy to validate. Use [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) for architecture details and [DOCS_STYLE_GUIDE.md](DOCS_STYLE_GUIDE.md) for documentation standards.

## Development Setup

1. Install .NET SDK 10.
2. Fork and clone the repository.
3. Create a feature branch from `main`.
4. Run the default local loop:

   ```powershell
   pwsh ./scripts/build.ps1
   pwsh ./scripts/run-tests.ps1 -Category unit
   ```

### Optional Installer Tooling

If you want to open and edit `AudioPilot.Installer/AudioPilot.Installer.wixproj` inside Visual Studio, install FireGiant HeatWave for your Visual Studio version.

This is optional IDE integration only. Normal app build/test workflows and command-line MSI builds do not require HeatWave.

## Recommended Workflow

1. Open an issue, or comment on an existing issue, before large changes.
2. Keep each PR scoped to one feature, fix, or cleanup topic.
3. Write commit messages that explain intent, not just the file touched.
4. Update docs in the same PR when behavior, commands, or user workflows change.

## Required Checks Before A PR

Run these locally:

```powershell
pwsh ./scripts/build.ps1
pwsh ./scripts/run-tests.ps1 -Category unit
pwsh ./scripts/validate-format.ps1 -Action check
./scripts/validate-line-endings.ps1
./scripts/validate-test-isolation.ps1
./scripts/validate-release-gate-policy.ps1
./scripts/validate-doc-links.ps1
```

If you want the aggregate local pre-PR flow, use:

```powershell
pwsh ./scripts/validate-all.ps1
```

## Test Categories

- `unit`: default fast loop for most changes.
- `integration`: hardware-aware or broader workflow coverage.
- `visual`: manual-only visible WPF tests for window presentation and tray activation.
- `stress`: churn-oriented reliability coverage.
- `hardware-soak`: strict real-device switching and WASAPI session lifecycle soak; all four configured endpoint IDs are required.
- `full`: aggregate suite used for broader validation and release-oriented work.

Useful commands:

```powershell
pwsh ./scripts/run-tests.ps1 -Category integration
pwsh ./scripts/run-tests.ps1 -Category visual
pwsh ./scripts/run-tests.ps1 -Category stress
pwsh ./scripts/run-tests.ps1 -Category full
pwsh ./scripts/run-tests.ps1 -Category full -Coverage
```

For local IDE or one-off command-line runs, integration and stress suites also expose xUnit traits:

```powershell
dotnet test --project AudioPilot.Tests/AudioPilot.Tests.csproj --filter-trait "Category=Integration"
dotnet test --project AudioPilot.Tests/AudioPilot.Tests.csproj --filter-trait "Category=Stress"
```

The PowerShell categories remain the authoritative workflow because they also set the required environment guards. In particular, `-Category visual` enables `AUDIOPILOT_RUN_INTEGRATION=1`, `AUDIOPILOT_RUN_VISUAL_WPF=1`, and
`AUDIOPILOT_TEST_SHOW_WINDOWS=1`, so the visible WPF tests are both runnable and intentionally shown. `-Category hardware-soak` enables strict hardware enforcement and fails before starting unless all four
`AUDIOPILOT_TEST_*_DEVICE_A/B` variables are configured.

Plain `dotnet test` is now unit-oriented by default:

- integration and stress tests discovery-skip unless `AUDIOPILOT_RUN_INTEGRATION=1` or `AUDIOPILOT_RUN_STRESS=1` is set,
- the 5 visual WPF window-show tests additionally require `AUDIOPILOT_RUN_VISUAL_WPF=1`,
- the default repo scripts and CI also exclude those categories explicitly,
- this keeps hardware-sensitive and visible-window integration tests out of the normal local and PR loop.

If you intentionally want the real visible WPF window tests, opt in explicitly:

```powershell
pwsh ./scripts/run-tests.ps1 -Category visual
```

Default GitHub Actions CI in `.github/workflows/ci.yml` stays unit-oriented:

- pull requests run the normal solution test pass without setting `AUDIOPILOT_RUN_INTEGRATION` or `AUDIOPILOT_RUN_STRESS`,
- pushes to `main` add coverage collection for that same unit-oriented pass,
- manual runs execute unit tests and the full formatting check,
- stress and integration suites are reserved for explicit local runs and the separate release workflow.

Practical implication: adding or expanding stress coverage does not change default PR CI behavior unless the workflow itself is updated.

Plain unit tests can run from Visual Studio or `dotnet test` while the installed AudioPilot UI is open. The repository test scripts still fail cleanly when the UI is running because integration, global-hotkey, and shared-resource
checks require stricter process isolation; they do not terminate the test host with an assembly-initialization exception.

When starting the UI while another instance is running, the new process only forwards a Show request to the existing instance and exits; it does not create a second full application instance. Windows cannot overwrite an executable
while that exact build output is running, so exit a repository-launched instance before rebuilding the same configuration. Prefer Debug for Visual Studio development launches and reserve Release for release validation and
packaging.

Before a UI release, exercise the tray menu at 100%, 125%, 150%, and 200% display scaling. Verify mouse hover, keyboard navigation, theme changes, and changing display scale while AudioPilot is already running; WPF popups use
their own native window and can expose DPI defects that do not appear in the main window.

To explicitly stop the UI before a test run:

```powershell
pwsh ./scripts/run-tests.ps1 -Category unit -StopRunningUi
```

## Script Reference

- `scripts/build.ps1`: restore and build the solution.
- `scripts/run-tests.ps1`: run unit, integration, visual, stress, or full suites, with optional coverage. Unit is the default non-integration, non-stress path; `visual` runs only the manual visible WPF tests and intentionally
  shows their windows.
- `scripts/validate-format.ps1`: run formatter checks or fixes against the SDK-style project solution filter. Use `-ChangedOnly` for staged, unstaged, and untracked C# files locally, or changes against the pull-request base in CI.
- `scripts/validate-line-endings.ps1`: verify tracked and untracked text files follow `.gitattributes`; pass `-Fix` to normalize violations without changing file encoding.
- `scripts/validate-doc-links.ps1`: verify markdown links across repo docs.
- `scripts/validate-test-isolation.ps1`: audit static mutable test hooks. Local runs report findings; CI runs the same audit with `-Strict`.
- `scripts/validate-release-gate-policy.ps1`: verify the required release gates, immutable external-action SHAs, environment-mediated command inputs, and least-privilege workflow permissions.
- `scripts/validate-release-hardware.ps1`: verify `AUDIOPILOT_TEST_*` values resolve to exact endpoint IDs on the current runner.
- `scripts/benchmark-readytorun.ps1`: measure ReadyToRun publish size and repeated startup-to-window deltas.
- `scripts/validate-all.ps1`: build once, run unit tests and script checks, and validate formatting/docs. `-IncludeIntegration` and `-IncludeStress` add isolated suites; `-Coverage` also enforces the unit coverage baseline.
- `scripts/publish-release-profiles.ps1`: restore and publish all release profiles while intentionally skipping `FolderProfile`.
- `scripts/build-local-release-artifacts.ps1`: build local release ZIP/MSI artifacts and run release integrity validation.
- `scripts/package-release.ps1`: create packaged release artifacts, MSI staging, winget manifests, checksums, and a manifest.
- `scripts/validate-release-integrity.ps1`: validate release ZIPs, MSI installers, winget manifests, checksums, and manifest entries.
- `scripts/validate-winget-manifests.ps1`: validate generated winget YAML against the expected MSI-only release shape.
- `scripts/release-body.ps1`: generate release notes from packaged release metadata; use `-ChecksumTable` to render markdown checksum rows.

Shared release-script helpers live under `scripts/lib/`; they are implementation details for packaging and validation scripts rather than standalone commands.

If you use VS Code, `.vscode/tasks.json` exposes the common local entry points.

## Pull Request Expectations

- Explain what changed and why.
- Include the validation commands you ran.
- Attach screenshots or GIFs for UI changes when possible.
- Call out tradeoffs, limitations, or behavior changes explicitly.
- Link related issues.

## Code Expectations

- Follow the existing structure, naming, and layering.
- Prefer root-cause fixes over short-lived workarounds.
- Avoid broad refactors in feature or bug-fix PRs unless the scope requires them.
- Preserve existing behavior unless the change is intentionally behavioral.

### Performance And Reliability Guardrails

- Preserve event coalescing in high-churn paths such as hotplug, session-created, and refresh loops.
- Prefer centralized timing and cadence constants in `AppConstants` over ad-hoc delays.
- For high-frequency diagnostics, prefer sampled or windowed summaries over per-item logging.

## Documentation Expectations

Use [DOCS_STYLE_GUIDE.md](DOCS_STYLE_GUIDE.md) as the source of truth.

Docs updates are required in the same PR when you change:

- command behavior, flags, exit codes, or JSON output shape,
- startup, tray, or minimize behavior,
- switch recovery, retry, debounce, or resume handling,
- hotplug refresh or mixer refresh behavior,
- cache or snapshot fast-path behavior,
- interop conventions or lifetime rules,
- settings keys or defaults.

Keep these ownership boundaries intact:

- `README.md`: landing page and high-level orientation.
- `docs/USER_GUIDE.md`: user workflows and troubleshooting.
- `docs/CLI.md`: detailed CLI reference.
- `docs/DEVELOPER_GUIDE.md`: architecture and implementation guidance.

## Testing Expectations

- Add or update tests when behavior changes.
- Keep tests focused and deterministic.
- Do not mix unrelated test refactors into the same PR.
- When you change logging patterns, keep at least one focused real log-file assertion where practical.

For intermittent test-host crashes or hangs, enable the test-only diagnostic extensions from PowerShell:

```powershell
./scripts/run-tests.ps1 -Category unit -Coverage -DotnetTestArgs @('--crashdump', '--hangdump', '--hangdump-timeout', '2m')
```

The crash collector records a dump and the active-test sequence when the worker process crashes, including under coverage.
The hang timeout measures inactivity; increase it for deliberately long integration or hardware scenarios.
Diagnostic files stay under the selected category's results directory. Memory dumps can contain process data, so inspect them before sharing.
Before a rerun clears that directory, the script moves existing dumps or crash sequences and their accompanying results into `artifacts/testresults/diagnostics`.
See Microsoft's [test-platform dump documentation](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-crash-hang-dumps).

WPF tests should normally use the shared STA dispatcher. Tests that require a fresh STA thread must shut down any dispatcher
on its owning thread before exiting; `TestExecutionGuards.RunIsolatedSta` owns that cleanup, including when an assertion fails.

## Coverage Policy

Coverage policy is defined in `.github/quality/coverage-policy.json`.

CI enforces both rules:

- coverage must stay above `minimumCoveragePercent`,
- once coverage reaches `nextTargetPercent`, CI fails until the policy file is ratcheted in the same PR.
- `scripts/validate-coverage.ps1` is the single local/CI implementation of that calculation and counts each production source line once,
- generated XAML/build output is excluded through `.github/quality/coverage.settings.xml`; async state machines and other compiler-generated user code remain included.

Keep unit, integration, and stress coverage runs separated. The known hardware-sensitive stress combination can still trigger CLR abort `0x80131506` if those categories are forced through one combined coverage collection session.

Every scripted category writes an xUnit TRX report under `artifacts/testresults/<category>`. Coverage runs use `artifacts/testresults/coverage/<category>`, so full-suite runs preserve all three categories' reports.

CI uploads the coverage directory for each push run. `validate-coverage.ps1` checks the unit report by default; pass `-CoverageRoot` to select a different category. Scripted runs reject zero executed tests and categories in which
every test is skipped.

## Release Trust Posture

- Release verification currently relies on artifact integrity checks plus CI validation gates.
- Code signing remains a future improvement and is not currently a release priority.
- If you modify packaging or release automation, preserve checksum generation, release manifest, SBOM, provenance, and validation behavior.

## Communication

Be respectful and constructive in issues and reviews. The project optimizes for clarity, maintainability, and reliable behavior.

Use [../CODE_OF_CONDUCT.md](../CODE_OF_CONDUCT.md) for the baseline community expectations.

## Related Docs

- User guide: [USER_GUIDE.md](USER_GUIDE.md)
- Developer guide: [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)
- Docs style guide: [DOCS_STYLE_GUIDE.md](DOCS_STYLE_GUIDE.md)
- Releasing: [RELEASING.md](RELEASING.md)
