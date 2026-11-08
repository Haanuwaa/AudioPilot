# Stable updates

The first public release is **1.0.0**. The next release will be **1.0.1**.
Never publish acceptance packages or keys.
Approved public versions and package bytes are immutable.

The stable feed has an independent TUF authority and pinned self-signed Windows
publisher. Self-signing does not establish public Windows trust. No certificate
is installed into a user's Trusted Root store. The client verifies Windows
signatures, the exact publisher and signed TUF package identity before restart.

## User flow

Settings retains **Check now** and the manual release link. A metadata check does
not download an installer. **Update and restart / Later** is the single approval
for the selected version and artifact. Download and verification finish while the
app runs. Pause, close or failure ends progression toward installation; resuming
requires a new approval. Final autosave and microphone ownership barriers remain
mandatory. An armed installation finishes or recovers even when its window closes.

MSI updates preserve installation/data locations and installer options. Managed
portable updates keep settings outside versioned payloads. Framework-dependent downloads require the
matching Windows Desktop runtime. A missing variant or newer helper requirement
offers manual-update guidance rather than choosing another package.

The updater checks Windows Installer availability before preparing a restart and
reserves its transaction before shutting down AudioPilot. Detected contention
defers the update while the app stays running; later installation failures use recovery.
MSI sources still registered for repair are retained, along with pending recovery
files. Cleanup removes only owned obsolete sources; failed or ambiguous source
discovery preserves them. Portable extraction uses at most two workers, retains
full-package SHA-256 verification and durable writes, and joins both workers
before rollback or cleanup.

## Authority and package approval

Use `publish-update-staging.ps1 -Channel stable` for the shared authority tool.
Staging is still its default. Signed root channel bindings reject cross-channel
packages and authority use. Root and targets keys stay in the local DPAPI-protected
authority and encrypted offline backups. Only snapshot and timestamp keys belong
in the protected `updater-stable-renewal` Actions environment.

`packaging/updater/stable` contains public bootstrap configuration and certificate
only. Private keys and passwords must never enter the checkout or release assets.
Use `build-updater-packages.ps1 -Bootstrap` to produce initial portable downloads,
separate `.update.zip` payloads, MSIs, checksums, dependency inventories and SBOMs.
The generated `update-targets.json` is a proposal, not signed approval.

Each portable ZIP extracts into an `AudioPilot` folder with two launchers, `ABOUT.txt`, `licenses`, `app` and `data`.
`licenses` contains `LICENSE`, `THIRD-PARTY-NOTICES.txt` and `UPDATER-NOTICES.txt`. The `app` folder owns
versioned binaries and recovery state; settings, logs and window placement stay in `data`. Startup and taskbar
registrations point to the root launcher. MSIs remain self-contained and retain their existing options.
`ABOUT.txt` and `licenses` mirror the settled generation and are refreshed after commit or rollback. A locked document is
retried at the next launch without changing the deployment result; unchanged files are left alone.
Framework-dependent packages require the matching Windows Desktop runtime.

Create the local packaging policy using the verified production root, its independently recorded hash and the existing
Windows publisher's certificate thumbprint. The local certificate must have its private key; do not create a new
publisher identity for each release. New policies default to public access disabled. `-EnablePublicUpdates` is the
explicit production build opt-in after validation; it does not approve a package or publish the feed.

```powershell
./scripts/new-update-bootstrap-policy.ps1 -BootstrapRoot <verified-public-root.json> `
  -BootstrapSha256 <independent-uppercase-SHA256> -PublisherThumbprint <existing-code-signing-thumbprint> `
  -OutputDirectory artifacts/bootstrap-policy -EnablePublicUpdates
./scripts/build-updater-packages.ps1 -Bootstrap -Version <unpublished-version> `
  -PolicyDirectory artifacts/bootstrap-policy -OutputDirectory artifacts/signed-build `
  -Runtimes win-x64,win-x86,win-arm64 -DeploymentModes SelfContained,FrameworkDependent `
  -SignTool <Windows-SDK-signtool.exe>
```

Initial ZIPs are public downloads. The separate `.update.zip` files are feed payloads; their proposed records still
require independent approval. Keep `Version.props` and the exact source commit aligned with the unpublished version.

Export public downloads from that same clean build with
`package-release.ps1 -SignedBuildRoot <build-directory> -Version X.Y.Z`.
This copies the signed initial portable archives and MSIs without repacking them,
then generates the standard checksums, provenance, SBOM and winget metadata.
Upload the thirteen public files to an unpublished `vX.Y.Z` draft. Dispatch
**Release Artifacts** on the matching source tag with `version` and
`signed_checksums_sha256` (the SHA256 of its `SHA256SUMS.txt`). The workflow checks
the pinned publisher/bootstrap, complete payloads and source identity, runs the
installer scenarios on those signed MSIs, and attests the final bytes before
publishing. An ordinary run performs validation only. Published versions cannot
be overwritten through this workflow.

`approve-update-package.ps1 -Channel stable -BootstrapSha256 <independent-pin>`
checks the actual Windows signatures through the shared verifier, the embedded
root/publisher policy, architecture, version, runtime and MSI cabinet contents.
Use its exact approved package records for the authority's Publish operation.
`publish-stable-update.ps1` performs that approval for all nine variants from the
same clean signed build and matching application draft, signs the catalog locally,
publishes with the timestamp last, and downloads all nine packages again for verification.
It compares MSI hashes directly and every portable payload file with the public
bootstrap ZIP. It never uses staging or exports signing keys to Actions.

```powershell
./scripts/publish-stable-update.ps1 -SignedBuildRoot artifacts/signed-build `
  -ReleaseRoot artifacts/release -AuthorityDirectory <existing-stable-authority> `
  -Publisher <staging-publisher.exe> -CertificateThumbprint <existing-thumbprint> `
  -SignTool <signtool.exe> -PublisherVerifier <trusted-AudioPilot.Updater.exe> `
  -OutputDirectory artifacts/stable-publication
```

Run this after local acceptance and validation-only CI pass, before promoting the
application draft. The signed release workflow checks that the stable feed matches
all nine final variants before it publishes the app. A missing or stale variant
blocks publication; renewal alone cannot repair it. Use a new application version
for future changes, and retain the publication export for an interrupted retry.
Do not execute downloaded candidate binaries to inspect their configuration.

Resume an interrupted upload with the same build, release directory and retained
publication directory. This verifies and reuses the signed export without opening
the signing authority or approving the version again:

```powershell
./scripts/publish-stable-update.ps1 -Resume -SignedBuildRoot artifacts/signed-build `
  -ReleaseRoot artifacts/release -Publisher <staging-publisher.exe> `
  -PublisherVerifier <trusted-AudioPilot.Updater.exe> -OutputDirectory artifacts/stable-publication
```

Resume also recognizes an already published catalog after online metadata renewal.
An incomplete local signing/export step needs explicit authority recovery; Resume
does not guess a replacement catalog, reset trust floors or sign the same version again.

Publish approved metadata/packages with
`publish-github-update-staging.ps1 -Channel stable`. The `updater-stable-feed`
release is labeled **Updater data - Stable** and remains a prerelease **container**,
separate from the main `v1.0.0` download;
its signed catalog contains stable application versions. Immutable assets are
uploaded first, with the timestamp last. Publication and renewal share the stable
channel's exclusive lease. An interrupted lease requires inspection.
Uploads reconcile their server digest before committing. Empty failed-upload
placeholders can be retried; completed immutable assets are never overwritten.
The replacement timestamp is uploaded and verified under a temporary name, then
switched using server-side renames. The switch has a brief availability gap,
not an atomicity guarantee. Failed switches restore the previous timestamp when
the new timestamp has not committed; retained recovery assets support a later retry.
No recovery overwrites an observed newer timestamp or trust root.

The active signed catalog keeps the newest three approved versions of each
deployment/architecture/runtime variant. Immutable historical metadata and package
files are preserved. A paused job older than that window must be cancelled and
checked again; installing a replacement still requires approval. Client trust and
version floors are retained. Settled configuration snapshots are removed only
after the durable recovery receipt matches its journal.

## Renewal and recovery

The stable workflow attempts hourly maintenance and successful main-branch
CI/release fallbacks. It renews online metadata when eighteen hours or less
remain, normally every six hours. Timestamp expiry stays at 24 hours. No-op checks
still verify signatures, hashes, root floors and package asset identity.

Configure `UPDATE_STABLE_ROOT_SHA256`, `UPDATE_STABLE_ROOT_VERSION` and
`UPDATE_STABLE_RENEWAL_ENABLED`, plus protected environment secret
`UPDATE_STABLE_RENEWAL_KEYS`. That document contains exactly snapshot and timestamp
keys. It cannot authorize new packages or prolong targets approval.

Optional repository secret `UPDATE_STABLE_HEARTBEAT_URL` uses a separate
[Healthchecks.io](https://healthchecks.io/) check: six-hour period, one-hour grace.
Only successful maintenance pings it. Configure and test its alert destination
before treating it as active. Missing Actions events cannot trigger Actions alerts.

Use a Simple check named **AudioPilot stable renewal** with Period **6 hours** and
Grace Time **1 hour**, attached to a verified email integration. Successful hourly
checks also ping when metadata is still fresh; the heartbeat means verified feed
health, not necessarily that a new timestamp was signed. After the first ping,
seven hours without another success causes a missed-maintenance alert.

Store only its UUID-based `https://hc-ping.com/<uuid>` ping URL in the Actions
secret. Treat that URL as a credential: do not put it in commits, issue bodies,
logs or chat. No Healthchecks management API key or signing key is needed.

Validate delivery with a separate temporary check using the same email integration:
Period **1 minute**, Grace Time **1 minute**. Ping it once, withhold further pings,
and confirm the actual Down email arrives. Ping it again and confirm recovery.
This exercises a missing job rather than only a test-message button, without
pausing production renewal. Remove or pause the temporary check afterward.
Then dispatch stable maintenance from `main` and verify its successful run appears
on the production check. A queued run, green Actions result alone, or a sent-email
record is not proof that the recipient received an alert.

Expiry blocks installation safely; it does not stop the installed app. Renew
valid authority metadata without resetting client trust history. Root and targets
renewal remain explicit operator ceremonies. Keep encrypted authority and Windows
signing-key backups offline and verify restoration. A replacement self-signed
publisher needs an explicit bootstrap/pin transition; do not silently replace it.

## Validation

Unit tests cover activation/pin boundaries and existing save/microphone barriers.
The stable acceptance mode uses a disposable authority and Sandbox-only loopback
HTTPS transport. Its bridge opt-in is absent from production builds. It checks
single consent, declined actions, pause/reopen/resume, Windows signatures,
failed-start rollback, autosave, installer options and safe closing during commit.
Keep that evidence distinct from physical microphone tests and native ARM64/x86
installer execution. Public publication remains conditional on passing validation.
