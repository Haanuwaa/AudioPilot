# AudioPilot updater

Opt-in engineering harness. The helper is not shipped with AudioPilot or connected to public update checks. Lifecycle
entry points require isolated diagnostic launches or explicitly enabled Sandbox handoff tests. A separate [updater UI
harness](../AudioPilot.Updater.Ui/README.md) reuses the job controller and application theme; it is not part of normal
startup or release packages.

## Scope

Key custody, root rotation, expiry health and protected metadata renewal are
documented in [Update signing and staging](../docs/UPDATE_STAGING.md). Public live
updates remain disabled.

- An independent, self-contained helper, with no reference to AudioPilot or its WPF runtime.
- Streaming HTTPS downloads using 64 KiB buffers. Pause cancels a blocked read, keeps a checkpoint and partial file, and
  resumes with Range/If-Range. Cancel discards partial state. Length and SHA-256 verification precede promotion;
  inconsistent ranges, validators and hashes are rejected.
- Portable versioned payloads and a flushed recovery pointer. User data lives outside the replaceable payload. A failed
  startup acknowledgement restores the previous generation. An interrupted pointer switch is recovered on the next
  invocation.
- Real Windows Installer transactions held open through a startup probe, then explicitly committed or rolled back. MSI
  tests are restricted to Windows Sandbox. The production MSI authoring, options and compression are unchanged.
- A restart barrier that requires admission of new audio work to be suspended, a successful final save/drain, and no
  microphone ownership before and after that drain.

Pause applies to downloading, not to an MSI transaction or pointer commit. A caller must keep the old app running until
preparation succeeds, then wait for verified shutdown before deployment. The updater does not kill AudioPilot to
proceed.

Push-to-talk owns its microphone even between key presses. This updater **defers** while any push-to-talk or
hold-to-mute lease exists. Focused tests use the real hold service with fake endpoints and verify that deferral never
releases its lease. There is no cross-process microphone ownership handoff yet.

## Running

From the repository root, publish the independent helper:

```powershell
dotnet publish AudioPilot.Updater -c Release -r win-x64 --self-contained true -o artifacts/updater/helper
& artifacts/updater/helper/AudioPilot.Updater.exe portable-smoke artifacts/updater/new-portable-run
```

Use a new, empty workspace for each smoke run. The helper rejects an existing unowned folder. Its private marker is a
mistake-prevention guard, not a security boundary against another process running as the same user.

The portable smoke test creates app fixtures with the published helper as their executable. It tests 1.0.0 to 1.0.1, an
actual child process exiting before acknowledgement, and a separate helper process exiting immediately after the durable
pointer switch. Recovery launches the old fixture again. It does not yet test a real AudioPilot GUI startup.

The focused tests are part of `AudioPilot.Tests`:

```powershell
./scripts/run-tests.ps1 -Category unit -TestClass UpdaterDeploymentTests
./scripts/run-tests.ps1 -Category unit -TestClass MicrophoneHoldServiceTests -NoBuild -NoRestore
```

For real MSIs, build an isolated 1.0.1 payload and MSI. Do not install the fixture on the host:

```powershell
$fixture = Join-Path (Resolve-Path .).Path 'artifacts/updater'
dotnet publish AudioPilot/AudioPilot.csproj -c Release -r win-x64 --self-contained true `
  -p:BuildIsolationKey=UpdaterPayload -p:Version=1.0.1 `
  -p:AssemblyVersion=1.0.1.0 -p:FileVersion=1.0.1.0 -p:InformationalVersion=1.0.1 `
  -p:PublishReadyToRun=true -p:DebugSymbols=false -p:DebugType=None `
  -p:IncludePublishReferenceFiles=true -o "$fixture/msi-payload"
./scripts/validate-publish-payload.ps1 -PublishDir "$fixture/msi-payload" `
  -RuntimeIdentifier win-x64 -Version 1.0.1 -WriteManifest
dotnet build AudioPilot.Installer/AudioPilot.Installer.wixproj -c Release `
  -p:Platform=x64 -p:AppVersion=1.0.1 -p:UsePrebuiltPublish=true `
  -p:BuildIsolationKey=Updater-1.0.1 `
  "-p:PublishDirForMsi=$fixture/msi-payload" "-p:OutputPath=$fixture/msi"
```

Prepare a Sandbox using:

```powershell
./scripts/prepare-update-sandbox.ps1 `
  -BaselineMsi AudioPilot.Installer/bin/x64/Release/AudioPilot-1.0.0-x64.msi `
  -CandidateMsi artifacts/updater/msi/AudioPilot-1.0.1-x64.msi `
  -HelperDirectory artifacts/updater/helper `
  -OutputRoot artifacts/updater/new-sandbox-run
```

Open the generated `.wsb`. Inputs are mapped read-only; only its dedicated results directory is writable. Networking and
microphone access are disabled. Read `results/result.json` and preserve failure logs. Never substitute an ordinary host
invocation for this test.

`-EnableNetworking` allows Windows' online package-policy checks in a disposable guest. It does not disable or bypass
those checks. Use it when diagnosing slow offline verification; microphone access and input write access remain
disabled. Package installation uses the guest's local disk, not the host-mapped evidence folder.

The MSI matrix checks all eight Desktop/Start Menu/CLI PATH combinations, custom folders with spaces, unchanged user
data, precommit launch health, an injected missing health acknowledgement, rollback to the runnable baseline, and
successful commit. That probe launches the CLI, not the entire GUI. [Windows Installer's
rollback](https://learn.microsoft.com/en-us/windows/win32/api/msi/nf-msi-msiendtransaction) applies while its
transaction is open; it cannot undo an already committed upgrade after a later app crash.

For focused diagnosis, `-OptionMasks @(0, 7)` selects all options off and all options on; the default remains all eight
combinations. This does not replace the existing release installer gate. Unchecked MSI checkbox properties can be stored
as empty strings; the adapter preserves those as off, as well as explicit `0` values.

## Real-app lifecycle validation

The `real-app-smoke` mode uses the actual AudioPilot executable, shared schema-1 protocol, real settings service and
normal shutdown pipeline. Run it inside a disposable Sandbox, with networking and microphone input disabled:

```powershell
dotnet publish AudioPilot -c Release -r win-x64 --self-contained true `
  -p:BuildIsolationKey=UpdateLifecycle -p:DebugSymbols=false -p:DebugType=None `
  -o artifacts/update-lifecycle/app
dotnet publish AudioPilot.Updater -c Release -r win-x64 --self-contained true `
  -o artifacts/update-lifecycle/helper
./scripts/prepare-update-lifecycle-sandbox.ps1 `
  -AppDirectory artifacts/update-lifecycle/app `
  -HelperDirectory artifacts/update-lifecycle/helper `
  -OutputRoot artifacts/update-lifecycle/new-sandbox-run
```

Open the generated `Lifecycle.wsb`. The fixture clears audio actions, routines and shortcuts, disables update checks,
and uses its own disposable diagnostic data root. It verifies prepare/cancel, an actual pending theme edit surviving
final autosave, an uncommitted candidate exiting without changing settings, committed recovery preserving
visibility/settings, a tray-mode restart, and a disconnected helper releasing admission so normal CLI status/exit work
again. A separate helper fixture exits before connecting, verifying that process exit ends the connection wait promptly.

Focused tests also cover abort-before-automation, ordinary startup ordering, failed/manual saves denying readiness and
atomic microphone hold admission. See [the lifecycle contract](../AudioPilot.UpdateProtocol/README.md).

## Real-app deployment and recovery

`deployment-portable` and `deployment-msi` connect that lifecycle to local 1.0.0 and 1.0.1 packages. Rebuild **both**
fixtures with the current hooks; older release binaries cannot participate. The source product version stays unchanged.
Publish the app twice with separate `BuildIsolationKey`, `Version`, `AssemblyVersion`, `FileVersion` and
`InformationalVersion` values, then validate each payload and build its MSI with `UsePrebuiltPublish=true`, as above.

```powershell
dotnet publish AudioPilot.Updater -c Release -r win-x64 --self-contained true `
  -o artifacts/update-deployment/helper
./scripts/prepare-update-deployment-sandbox.ps1 `
  -BaselineDirectory artifacts/update-deployment/payload-1.0.0 `
  -CandidateDirectory artifacts/update-deployment/payload-1.0.1 `
  -BaselineMsi artifacts/update-deployment/msi-1.0.0/AudioPilot-1.0.0-x64.msi `
  -CandidateMsi artifacts/update-deployment/msi-1.0.1/AudioPilot-1.0.1-x64.msi `
  -HelperDirectory artifacts/update-deployment/helper `
  -OutputRoot artifacts/update-deployment/new-sandbox-run
```

Open the generated `Deployment.wsb`. It tests both formats with the real GUI runtime, pending autosave, rejected
startup, incompatible configuration writes, helper-process interruption, recovery to the previous version, and
successful replacement. A separate MSI case injects a malformed migration before launch, checks that initialization
actually fails, and restores the previous version. MSI checks preserve custom install/data folders and Desktop/Start
Menu/CLI PATH choices. The default focused masks are `0,7`; `-OptionMasks @(0,1,2,3,4,5,6,7)` runs every combination.
These development probes do not replace release installer gates. Networking and microphone input remain disabled;
installation only happens on the guest's local disk.

The portable updater has a stable root `AudioPilot.exe` launcher and its independent helper runtime,
`versions/<version_generation>/` payloads, and a shared `data/` folder. Diagnostic layouts require a matching identity;
an explicit `Managed` descriptor activates the shared data location for normal instances. Existing flat portable layouts
keep their behavior. Settings, settings backups and window placement remain outside replaceable versions. A pending
recovery is handled before launching a payload. The updater launcher can be tested without leaving the GUI open using
`AudioPilot.exe portable-launch-probe`.

Before replacement, a durable bounded snapshot captures configuration files and placement. It excludes logs and
unrelated data. A rejected candidate is stopped and awaited before normal rollback. An uncommitted candidate belongs to
a Windows kill-on-close job, so abrupt helper exit stops that candidate without targeting the original app or searching
by process name. Windows Installer also rolls back an ownerless outer transaction; recovery waits for Installer to
become idle before inspecting the settled version. `deployment-recover-msi <workspace>` resumes an interrupted MSI probe
with its recorded installer options.

Deployment commits **before** the app receives permission to enable automation. A durable committed deployment is
resumed as that version; a lost readiness acknowledgement cannot justify rolling back an already committed MSI.
Configuration restoration is repeatable and validates all backup hashes before replacing files. Incomplete recovery
stays journaled for retry, and diagnostic evidence is retained. Microphone ownership remains deferred, rather than
transferred or force-released.

## Maintained TUF integration

The real-app deployment probes use [go-tuf/v2 v2.4.2](https://github.com/theupdateframework/go-tuf/releases/tag/v2.4.2),
pinned by Go module checksums. A separate native executable runs its complete `updater.Refresh` workflow: root rotation,
timestamp, snapshot, targets and target lookup. It never falls back to the earlier custom verifier. The old P-256 format
remains only as a comparison fixture for existing tests.

The default fetcher reads the local fixture repository. Its synthetic HTTPS origin cannot make network requests. An
explicit loopback HTTPS test transport also supports a pinned temporary CA, normal chain/hostname verification, bounded
responses, exact-origin matching and redirect rejection. `UnsafeLocalMode` stays disabled. The SDK disk writer is
disabled; a Windows write-through, explicitly flushed, hash-chained journal owns trusted metadata instead. Bootstrap
requires the independently supplied SHA-256 pin, and later invocations begin from the journaled trusted root. Verified
advances are checkpointed before the next fetch and after success or failure. Missing or corrupt committed authority
fails closed; an independent anchor also detects loss of the cache directory. An incomplete final append is trimmed
without losing committed records. Exclusive handles serialize writers, and the cache directory permits only the current
user, administrators and SYSTEM. Disposable SDK working files are regenerated from exact journaled bytes and abandoned
working files are bounded and cleaned. Metadata sizes and rotation/delegation counts are bounded. The application
separately checks product, local channel, exact deployment variant, minimum helper version and application-version
rollback.

The maintained client's target-download API returns a byte array. This proof instead streams the authenticated
length/SHA-256 check in .NET, retaining the file read lease through deployment and rechecking TUF authority before
replacement. The Go process exits after each request; it is not a resident background service. The native x64, x86 and
ARM64 builds need no installed Go runtime. Build reports retain module versions, checksums, licenses and
vulnerability-scan evidence. A module-only OpenPGP advisory can remain in the dependency graph; OpenPGP is not imported
or reachable in this program.

```powershell
./scripts/build-tuf-bridge.ps1 -ScanVulnerabilities
dotnet publish AudioPilot.Updater -c Release -r win-x64 --self-contained true -o artifacts/updater/helper
Copy-Item artifacts/tuf-proof/bridge/win-x64/AudioPilot.TufBridge.exe artifacts/updater/helper/
& artifacts/updater/helper/AudioPilot.Updater.exe tuf-smoke <local-candidate.zip> <new-workspace>
```

Go adapter tests cover the complete workflow/cache, tampering, expiration, snapshot substitution, timestamp rollback,
root-key rotation, revoked targets keys, and local-fetcher containment. The process smoke checks package bytes, variant
selection and bootstrap identity. `prepare-update-deployment-sandbox.ps1` uses this same adapter for portable/MSI
recovery probes. Temporary Ed25519 role keys exist only in fixture-generator memory and are never exported.

## Authenticode pipeline scaffold

`sign-update-artifacts.ps1` accepts explicit first-party EXE/DLL/MSI paths and a certificate thumbprint in
`CurrentUser/My`. Normal mode requires a valid code-signing identity, SHA-256 file digests, an explicit HTTPS RFC 3161
timestamp service, Windows signature validation, the expected publisher, and a timestamp. Signing or verification
failure stops the pipeline. No certificate, production secret or public timestamp service is configured by default.

The required order is:

1. Publish all binaries, including the independent helper and TUF bridge; sign first-party files.
2. Regenerate payload validation metadata, package the signed payload into MSI/ZIP, then sign the outer MSI.
3. Verify Windows trust and publisher identity; generate TUF target lengths/hashes from those final bytes.
4. Publish only after all trust and recovery checks pass. Repacking or signing afterward invalidates the TUF target.

`prepare-update-signing-sandbox.ps1` stages a two-phase local proof. First, its guest creates a short-lived
**nonexportable** test key, trusts the public certificate only inside that disposable Sandbox, signs the first-party
files and exports only signed binaries/reports. The host packages those exact binaries using the existing installer
options. In the same guest, run the deployment preparation with `-SignInExistingSandbox`: it signs the outer MSIs,
verifies payload/helper signatures, and generates TUF metadata before exercising recovery. Keep networking and
microphone access disabled; map only dedicated proof inputs and results. The private key stays in the guest certificate
store and disappears when that Sandbox is destroyed.

Offline `-TestIdentity` mode is restricted to `WDAGUtilityAccount`, is clearly reported as test-only and does not claim
timestamp validation. Tests reject unsigned/tampered files, wrong publishers, self-signed production identities and
missing production timestamp configuration. A real public certificate, timestamp service, expiry/revocation behaviour
and signing-service integration still need production validation. Normal release workflows do not invoke this scaffold.

Local TUF state assumes an access-controlled cache and trusted caller. It does not defend against a process that can
replace the helper, rewrite its trust cache or control its clock. The wrapper requests Windows write-through/flush
barriers before acknowledging trust progress. The checksummed application-version floor also recovers a flushed pending
record before accepting another target. A corrupt floor never silently resets. Process-exit/fault tests exercise these
boundaries; they do not establish how every storage device behaves during a physical power cut. Candidate verification
still precedes preparation of the original app, settings recovery and installer choices remain intact, and microphone
ownership continues to defer updates.

## Local job controller

`LocalUpdateJob` owns one operation per workspace and persists checking, staging, pause, readiness, preparation,
deployment, recovery and terminal states. It accepts local files or an explicitly configured loopback HTTPS test feed.
Both paths use bounded streaming buffers, publish progress in memory, and persist phase changes rather than every chunk.
HTTPS downloads use the existing Range/If-Range downloader; interrupted transfers can retry after reopening, and changed
endpoints or signed targets require cancellation first. Pause flushes the partial file; reopening resumes from its
length and verifies the complete authenticated hash. Cancellation deletes managed staging files and preserves the
original input. Changed bytes cannot become Ready.

Preparation still uses the real app's admission, final autosave/drain and microphone barrier. Deferral cancels
reversible admission and keeps the package ready to retry. Once deployment is armed, cancellation is refused and
disposal joins completion/recovery. On restart, interrupted preparation or deployment requires explicit recovery. An
incomplete pending job record is quarantined and forces inspection of the actual deployment journal; this recovery rule
does not apply to TUF authority or application-version floors.

The Sandbox-only adapter reads the actual installed application version, PE architecture and runtime profile. MSI paths
and options are bound to the job and rechecked before shutdown. Recovery records capture the original process identity
before shutdown. Helpers never terminate an original app to proceed. Configuration restoration is checkpointed before
the recovered app resumes; durable completion records prevent a later retry from overwriting newer user edits. A
committed installation remains committed if the final acknowledgement is lost.

Publish the helper and copy the matching TUF bridge beside it, then prepare the existing local fixture matrix with
`prepare-update-deployment-sandbox.ps1 -UseJobController`. `-OptionMasks` retains its installer-option meaning. The
controller probes pause/reopen/resume, actual failed startup, helper interruption, successful commit, final autosave,
placement and installer options. Unit tests use gates instead of timing assumptions for cancellation, microphone
deferral, concurrent operations, disposal and persistence failure. This remains a local diagnostic adapter; normal app
startup and live update checks do not invoke it.

## Installed-runtime adapter and HTTPS Sandbox feed

The [signing and staging guide](../docs/UPDATE_STAGING.md) now covers the separate
operator publisher, protected staging authority, signing order, signed-package
approval and optional website feed. Public live updates remain disabled. Portable
deployment accepts both the flat engineering fixture and the official
`AudioPilot/` archive root; mixed layouts are rejected before the active pointer
changes.

`InstalledUpdateRuntime` derives deployment, version, PE architecture and runtime from the selected installation, and
binds MSI paths/options to its registry registration. It has no fixture version constants. An explicitly opted-in normal
app can accept a handoff using its exact PID, creation time and path; it uses the regular instance identity and
configuration paths. The app reports its active settings path and local-state root over authenticated IPC after the save
barrier. Installed local placement is backed up/restored separately, without deleting unrelated configuration.
Successful candidates remain running after the updater closes. A detached app can accept a later authenticated handoff.

All entry points remain restricted to Windows Sandbox. `AUDIOPILOT_UPDATE_TEST_SESSION` enables the attachment listener
only for that explicit guest session; ordinary app launches do not expose it. Existing uncontrolled or mismatched owners
are never terminated to proceed. The versioned portable `Managed` layout shares `data/` without diagnostic settings
overrides. Its launcher also uses the same deployment/recovery lock as the adapter.

Run `prepare-update-ui-sandbox.ps1 -HttpsFeed` with fresh baseline/candidate packages to exercise the connected
downloader, maintained TUF verifier, job controller, normal app handoff and themed UI. The generated guest disables
external networking and audio input; its feed binds only loopback. TUF role keys exist only in fixture-generator memory.
The TLS server uses a temporary guest Windows key container because Schannel does not accept ephemeral server keys on
this platform. Its in-memory PKCS#12 import buffer is cleared, no PFX is written, and certificate disposal releases its
temporary key. Only a public CA file is shared with clients; host/root certificate stores are not modified. Clients
verify the pinned CA, hostname and chain, reject redirects/public origins, and enforce metadata/package bounds. Public
live updates remain disconnected.

The connected UI proof rejects tampered metadata before downloading, pauses/reopens/resumes HTTPS transfers, retries a
dropped connection with ranges, cancels before preparation, preserves an actual pending autosave through failed-start
rollback, and closes safely during a successful install. It verifies installer options and leaves the restarted normal
app alive before a separate authenticated cleanup. The runner waits for each helper PID, rather than its descendants.
These are controlled x64 Sandbox results, not production signing or physical hardware validation.

## Before production integration

These remain acceptance requirements, not implemented features:

- Production verifier/repository review, embedded bootstrap authority, protected signing/key recovery, immutable release
  publication, Go-component production packaging/licenses/SBOM, and live catalog/variant selection. The current keys,
  channel and feed configuration are test fixtures only.
- A production bootstrapper/portable distribution layout, bounded retention/cleanup, disk-space checks, power-loss and
  interruption tests at additional commit boundaries.
- Explicitly consented production instances and migration policies for multiple authoritative configuration locations.
  Keep microphone ownership deferred until a separately tested safe handoff exists.
- Native x86/ARM64 runs, locked files/antivirus, reboot-required and interrupted MSI transactions, machine policy
  restrictions, multiple sessions and competing installations.
- Production UI integration, scheduling/consent policy and restart prompts. The local UI is a Sandbox fixture harness;
  no production signing or unrestricted installer execution is exposed.

Do not ship this project or enable silent updates until those requirements are met.
