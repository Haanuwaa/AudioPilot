# Stable updates

The initial public bootstrap is **1.0.0**. **1.0.1** is the isolated acceptance
candidate until the next real release. Never publish acceptance packages or keys.
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
portable updates keep settings outside versioned payloads. Existing flat portable
folders are not converted in place: extract the initial bootstrap into a fresh
folder and import existing settings. Framework-dependent downloads require the
matching Windows Desktop runtime. A missing variant or newer helper requirement
offers manual-update guidance rather than choosing another package.

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

`approve-update-package.ps1 -Channel stable -BootstrapSha256 <independent-pin>`
checks the actual Windows signatures through the shared verifier, the embedded
root/publisher policy, architecture, version, runtime and MSI cabinet contents.
Use its exact approved package records for the authority's Publish operation.
Do not execute downloaded candidate binaries to inspect their configuration.

Publish approved metadata/packages with
`publish-github-update-staging.ps1 -Channel stable`. The `updater-stable-feed`
release is a prerelease **container**, separate from the main `v1.0.0` download;
its signed catalog contains stable application versions. Immutable assets are
uploaded first, with the timestamp last. Publication and renewal share the stable
channel's exclusive lease. An interrupted lease requires inspection.

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
