# Updater

AudioPilot's updater lives here. Stable bootstrap packages use independent root and self-signed publisher pins, all three
Windows architectures and exact runtime selection. Development builds without a policy retain manual installation;
the separate x64 staging pilot authorizes one candidate. A single **Update and restart** approval covers
download, verification, installation and restart; AudioPilot keeps running until replacement is ready.

See [stable release operations](../docs/UPDATE_STABLE.md) for production activation, approval and recovery.

`AudioPilot.Updater.csproj` is the updater's root project, alongside the main app, CLI and installer projects in
`AudioPilot.sln`. Its supporting projects stay in this component folder:

| Project | Responsibility |
| --- | --- |
| `AudioPilot.Updater.csproj` | `AudioPilot.Updater.Core.dll`: controller, TUF adapter, installation and recovery; no WPF dependency |
| `Ui/AudioPilot.Updater.Ui.csproj` | One `AudioPilot.Updater.exe` for themed UI, CLI automation and portable launching |
| `Protocol/AudioPilot.UpdateProtocol.csproj` | Small shared lifecycle contract, portable layout and build-time update policy |

Only one managed updater executable is shipped. Its entry point dispatches engineering and portable-launch modes
before constructing WPF; automation runs without opening a window and can reveal the same session on request.
The Go TUF verifier remains a separate native executable. Publish the `Ui` project to obtain the complete runtime.

The main app references only the shared protocol. Keeping these boundaries avoids loading installation code into
AudioPilot and lets the helper survive replacement of the running application. `Validation/` folders contain engineering
drivers, separate from the runtime code. The operator-only TUF publisher is under `TufBridge/cmd`;
private signing keys are never distributed.

## Working on the updater

Use [Contributing](../docs/CONTRIBUTING.md) for full validation. Updater projects are included in the solution's build,
formatting and dependency checks. From the repository root:

```powershell
./scripts/build.ps1
./scripts/run-tests.ps1 -Category unit -NoBuild -NoRestore
./scripts/build-tuf-bridge.ps1 -OutputRoot artifacts/tuf-proof/bridge -ScanVulnerabilities
```

| Need | Guide |
| --- | --- |
| Update the installed app or automate an approved update | [User guide](../docs/USER_GUIDE.md#install-an-update) and [CLI](../docs/CLI.md#update-with-approval) |
| Ship public packages and maintain the production feed | [Stable update operations](../docs/UPDATE_STABLE.md) and [Releasing](../docs/RELEASING.md) |
| Understand lifecycle, trust, cleanup and test boundaries | [Design and validation](../docs/UPDATER_DESIGN.md) |
| Run a disposable, fixed-candidate staging pilot | [Pilot packaging](../docs/UPDATE_PILOT.md) |
| Back up or rotate keys and maintain staging | [Signing and staging](../docs/UPDATE_STAGING.md) |

Do not rebuild a published candidate under the same version. Its approved hashes and versioned TUF assets are immutable.
