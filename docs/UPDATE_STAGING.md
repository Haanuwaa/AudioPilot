# Update signing and staging

Public live updates remain disabled. Engineering tests run in Windows Sandbox; explicitly packaged pilot builds can
also attach to a normal app instance. Normal releases still use manual installation.

Windows Authenticode and TUF serve different purposes. Authenticode identifies a
Windows publisher; TUF authorizes exact package bytes and prevents metadata
rollback, substitution and expiry attacks. Self-signing is free, but does not
create public Windows trust or guarantee that SmartScreen warnings disappear.

## Signing order

1. Publish the application and CLI with their dependencies. Run
   `scripts/sign-update-payload.ps1` on the first-party binaries. Keep its receipt
   outside the payload. Sign the updater helper and Go client separately with
   `scripts/sign-update-artifacts.ps1`.
2. Regenerate the payload manifest with `validate-publish-payload.ps1 -WriteManifest`.
   Build the ZIP and MSI from those exact signed bytes using the existing release
   packaging and installer checks. Release compression stays unchanged.
3. Sign the outer MSI, then run `approve-update-package.ps1` for each ZIP and MSI.
   ZIP approval verifies all first-party binaries. For MSI approval, pass that ZIP
   as `-PayloadArchive`: the existing cabinet reader compares every installed file
   with the ZIP, alongside MSI version, architecture and outer-signature checks.
   Existing installer scenarios still validate behavior. Merge the `packages` arrays
   into one schema-1 staging approval manifest.
4. Publish TUF metadata only after these steps. Never sign or modify an artifact
   after its TUF hash has been published.

Publicly trusted signing requires a trusted identity, SHA-256 signatures and a trusted
RFC 3161 timestamp. `-TestIdentity` permits self-signed, untimestamped signatures
only inside Windows Sandbox. It cannot enable production signing.

The separate [opt-in pilot](UPDATE_PILOT.md) uses a pinned self-signed identity,
verified packages and an embedded build policy. It does not add Windows trust,
enable public updating or relax the ordinary release gates.

## Local staging authority

Build the operator tool from the pinned Go module:

```powershell
Push-Location AudioPilot.Updater/TufBridge
go build -mod=readonly -trimpath -o ../../artifacts/staging-publisher.exe ./cmd/staging-publisher
Pop-Location
```

Initialize once, choosing a durable directory **outside the checkout and build
artifacts**. For example:

```powershell
$authority = Join-Path $env:LOCALAPPDATA 'AudioPilot.UpdateSigning/staging'
./scripts/publish-update-staging.ps1 -Operation Initialize -AuthorityDirectory $authority -Publisher artifacts/staging-publisher.exe
```

Save the printed public bootstrap SHA-256 independently. Four distinct Ed25519
keys are generated with Go's cryptographic library. Private seeds are encrypted with
Windows CurrentUser DPAPI and protected by a restricted directory ACL. No private
keys are written to the repository or exported with the feed. Publishing reads the
targets, snapshot and timestamp keys. Ordinary renewal reads only snapshot and
timestamp keys and cannot change or extend the approved targets metadata.

DPAPI is tied to the Windows profile; copying these files alone is **not** a
portable recovery backup. The root key is excluded from routine publishing, but
storing it on the same computer is not offline custody. Before public updates,
establish recoverable offline root custody, a rotation procedure and an approved
online signing identity. A newly initialized test authority has one root signer;
the rotation ceremony below replaces it with a two-of-three root policy.

```powershell
./scripts/publish-update-staging.ps1 -Operation Publish -AuthorityDirectory $authority -Publisher artifacts/staging-publisher.exe -PackageManifest packages.json -OutputDirectory artifacts/staging-public-1
./scripts/publish-update-staging.ps1 -Operation Renew -AuthorityDirectory $authority -Publisher artifacts/staging-publisher.exe -OutputDirectory artifacts/staging-public-2
```

The publisher uses go-tuf serialization and verification, immutable versioned
metadata, hash-prefixed targets and a timestamp written last. Deployment variants
must advance their application version. Rebuilding an already published version
is rejected. A checkpoint detects missing or regressed publication state; damaged
history fails closed. If a publisher is interrupted, inspect retained locks and
partial metadata before clearing anything. Never delete the authority to bypass a
failure. Verified complete target files can be reused after interruption.

## Encrypted recovery backups

`Backup` uses the maintained [age library](https://pkg.go.dev/filippo.io/age) and
its passphrase-encrypted format. It prompts locally, authenticates a read-back,
and refuses to overwrite an existing backup. Passwords and private keys go to the
operator process through stdin, never command arguments. The archive contains the
current signing keys and public identity/version checkpoints, not application
packages. Keep a strong passphrase separately from the encrypted archive.

```powershell
./scripts/publish-update-staging.ps1 -Operation Backup -AuthorityDirectory $authority -Publisher artifacts/staging-publisher.exe -BackupPath <offline-drive>/AudioPilot-authority.age
./scripts/publish-update-staging.ps1 -Operation Restore -AuthorityDirectory <new-authority-folder> -Publisher artifacts/staging-publisher.exe -BackupPath <offline-drive>/AudioPilot-authority.age -PublicRepository <current-verified-export> -BootstrapHash <independent-original-pin> -MinimumVersion <recorded-metadata-floor> -MinimumRootVersion <recorded-root-floor>
```

Restore requires a new directory, verifies the current public repository and
independent recovery floors, then protects the recovered keys with the receiving
Windows account's DPAPI. A backup containing revoked keys is rejected. Refresh
backups after rotation. Never reset a version floor just to make an old backup
work. Losing the current keys can require recovery under the surviving root
quorum; an old backup alone is not authority to bypass revocation. An operator can
select surviving protected keys with `Rotate -RootSignerRoles root-1,root-2`.

`scripts/tests/test-update-authority.ps1` creates disposable authorities and a
network-disabled Sandbox proof. The proof rejects the original account's DPAPI
blob, restores the age archive, renews metadata, rotates and renews again under
the separate account. Its generated passphrase and copied keys are **test-only**.
Do not substitute a real authority into that test.

An encrypted backup on removable storage provides recovery; active signing keys
can still remain on the PC. Tested backup software and DPAPI protection do not
establish offline custody. Before rollout, verify backups on the chosen offline
media and separate root signers and their recovery material into independent
custody. Three keys in one Windows profile are not three independent custodians.

## Root rotation and revocation

```powershell
./scripts/publish-update-staging.ps1 -Operation Rotate -AuthorityDirectory $authority -Publisher artifacts/staging-publisher.exe -OutputDirectory artifacts/rotated-staging-export
./scripts/publish-update-staging.ps1 -Operation Status -AuthorityDirectory $authority -Publisher artifacts/staging-publisher.exe
```

Rotation replaces all four roles, using three new root keys with a threshold of
two. The maintained library verifies signatures satisfying both the old and new
thresholds. Consecutive numbered roots remain published, and the original
bootstrap hash stays unchanged. Existing clients keep their verified cache.

The operation builds and verifies a new repository and stores the replacement
keys before atomically selecting the authority generation. An interrupted
preparation leaves the previous generation selected. Inspect abandoned candidate
directories; never publish one merely because it exists. Publication uploads
immutable roots and catalog files before replacing the timestamp. Readers fail
closed during a partial publication and recover when it finishes. Export and
retry the selected generation rather than generating another rotation to repair
an interrupted upload.

The encrypted old generation remains retained for inspection; its revoked keys
cannot renew the new authority. Rotate again using the current quorum. Keep
independent root-version and metadata-version recovery floors alongside the pin.

## Protected metadata renewal

`.github/workflows/update-staging.yml` attempts maintenance on `main` hourly at
minute 47, and after successful main-branch CI or Release Artifacts runs. It checks
authenticated freshness on each attempt and renews when the timestamp has eighteen
hours or less remaining, normally every six hours. Fresh metadata is checked
without signing or uploading replacements. Publication is serialized, and active
runs are not canceled. A shared
exclusive GitHub tag lease also serializes local publication with workflow runs;
an abandoned lease requires inspection before removal. It does not create a
branch or alter `main` protection.

Enable it only after reviewing and committing the workflow:

- Create environment `updater-staging-renewal`, restricted to `main`.
- Set independent variables `UPDATE_STAGING_ROOT_SHA256` and
  `UPDATE_STAGING_ROOT_VERSION`; advance the version floor after a reviewed
  rotation, keeping the original hash.
- Provision environment secret `UPDATE_STAGING_RENEWAL_KEYS` with exactly the
  current snapshot and timestamp seeds. Use a local secure stdin-based secret
  setup; never paste keys into chat or shell arguments. Root and targets keys
  must not enter Actions secrets. Rotate this secret after authority rotation.
- Set `UPDATE_STAGING_RENEWAL_ENABLED=true` after verifying that setup. Leaving it
  unset disables signing; a configured pin still permits read-only health checks.

Renewal downloads metadata only. It verifies signatures, package asset digests,
root floors and monotonic versions, then publishes the timestamp last. It cannot
introduce packages or prolong targets approval. Root and targets expiry warnings
begin fourteen and seven days ahead respectively. One open maintenance issue
reports renewal failures or approaching expiry and closes after recovery. Failed
checks remain failed workflow runs; retries do not reset trust or hide failures.
GitHub schedule delivery is not guaranteed. More attempts and successful-workflow
fallbacks tolerate missed events but cannot guarantee delivery during a GitHub
outage or an idle repository. An independent monitor should check the public
timestamp's expiry or expect a verified-maintenance heartbeat; an alert in this
workflow cannot run when no event arrives.
Do not extend expiration or suppress a verification failure to hide a missed run.

An optional [Healthchecks.io](https://healthchecks.io/) check can expect a success
heartbeat at least every six hours, with one hour of grace. Configure its alert
destination in that service, then add its `https://hc-ping.com/<check-uuid>` URL as
repository secret `UPDATE_STAGING_HEARTBEAT_URL`. No account or URL means no
external request. Only successful verification/publication sends a heartbeat;
an expiry warning does not. Heartbeat failures remain failed maintenance runs,
and the private URL is omitted from error messages. This secret is separate from
the protected environment's two signing keys; signing material never leaves
Actions. Monitor setup and delivery must be tested before treating it as active.

An operator can explicitly renew the unchanged targets approval using
`-Operation RenewCatalog`; this reads the targets key and belongs in the manual
approval process. Ordinary `Renew` fails when targets approval has less than a
day remaining. Expired root authority requires a valid root ceremony, not an
online-renewal bypass.

This automation renews feed metadata only. Users still approve Update and restart
before a package download and installation. Public live updates remain disabled.

## Command interfaces

The operator tool's `--help` lists its JSON-stdin operations. The PowerShell
interface supplies secure prompts, DPAPI storage, backups, status, rotation,
manual catalog approval and export. These are maintainer commands; raw initialize,
restore and rotate replies contain keys and must never be printed in public logs.

Packaged updater builds expose `update status`, `check`, `pause` and `cancel` in
the normal AudioPilot CLI. Version-bound `apply` and `resume` require `--approve`
and use the same controller, consent and recovery ownership as the window.
Ordinary builds without an updater policy retain the manual release link.

Timestamp metadata expires in 24 hours; snapshots in seven days and targets in
30 days. Protected unattended renewal handles timestamp and snapshot freshness;
targets approval remains an operator responsibility. This staging feed is separate
from a production update service. Keep historical metadata and targets for
concurrent readers; the bounded staging export currently
supports up to 900 MB. Production retention still needs an explicit policy.

## Real HTTPS staging publication

The isolated test client uses the dedicated
[updater staging prerelease](https://github.com/Haanuwaa/AudioPilot/releases/tag/updater-staging-feed).
It pins the staging bootstrap and approved test publisher independently, reads public metadata over normal
HTTPS, and downloads hash-prefixed package assets. GitHub's redirects are restricted to the HTTPS release CDN;
credentials and cookies are not forwarded. The regular app's public live-update path remains disabled.

`publish-github-update-staging.ps1` verifies an export before uploading. Versioned assets are immutable;
the timestamp is uploaded last, with a version/byte collision check. Server-side SHA-256 digests are checked
after publication. The release must remain a prerelease and is explicitly excluded from latest stable downloads.
The first publication uses a draft until all files are present. Updating an existing timestamp has a brief
asset replacement window; clients fail closed on a missing file and retain their trust history for retry.

```powershell
./scripts/publish-github-update-staging.ps1 -FeedDirectory artifacts/staging-public-1 -BootstrapHash <independent-root-hash> -Publisher artifacts/staging-publisher.exe -ReportPath artifacts/staging-publication.json
```

The signed engineering packages and temporary Windows certificate are for Sandbox. Test identities expire;
renewing TUF metadata does not extend an untimestamped Windows signature's validity. Rebuild and approve a new
package version when its signing identity must change. Private signing material is never uploaded.

## Optional Pages mirror

The website workflow can host the verified feed at
`https://haanuwaa.github.io/AudioPilot/updates/staging/` without replacing the
project website or changing stable downloads:

1. ZIP the **contents** of a verified public export as `feed.zip`, without an outer
   directory. Upload it to the explicitly separate GitHub prerelease
   `updater-staging-feed`; never include the authority directory or signing keys.
2. Set repository variable `UPDATE_STAGING_ROOT_SHA256` to the independently saved
   bootstrap hash and explicitly set `UPDATE_STAGING_PAGES_ENABLED=true`. Leaving
   the Pages flag unset keeps the mirror disabled, independently of renewal.
3. Run the Project website workflow on `main`. It checks the prerelease, rejects
   unsafe ZIP entries, verifies metadata and every package using the pinned root,
   and refuses a version lower than the currently published timestamp. Unchanged
   metadata versions must contain the identical timestamp. A non-404 failure
   reading the current timestamp stops deployment.

The staging files are public when published. Self-signed downloads belong only in
the isolated test environment. Only the explicit Sandbox test entry point accepts
the pinned staging origin and channel. Hosting the feed does not enable public
app updates; public rollout requires a separately reviewed client trust configuration.
Local authority/export scripts do not change GitHub or host certificate trust.
The GitHub publication script changes only the dedicated prerelease.

## Sandbox proof

`prepare-update-staging-sandbox.ps1` extends the existing signing proof. It exports
signed first-party payload files, waits for an MSI built from those exact files,
then signs the MSI and approves both packages. It publishes, verifies, exports and
renews the feed, including a separate-process DPAPI check and renewal with the root
key file held inaccessible. Only public files and reports leave Sandbox. The VM
has no network, microphone, camera or clipboard access; discard it afterward.

For trusted public signing without buying a certificate,
[SignPath Foundation](https://signpath.org/terms.html) offers free signing to
qualifying open-source projects. Acceptance is required; this repository's MIT
license alone does not establish eligibility. No application or account setup has
been submitted. Temporary staging needs no purchased certificate or user-supplied
private key.
