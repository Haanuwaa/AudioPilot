# Project Website

The public project home is [haanuwaa.github.io/AudioPilot](https://haanuwaa.github.io/AudioPilot/).
The site uses HTML, CSS, and a small script for screenshot and architecture selection. There are no npm dependencies,
external fonts, analytics scripts, cookies, or browser requests to the GitHub API.

Build from the repository root with PowerShell 7 and the GitHub CLI:

```powershell
gh api repos/Haanuwaa/AudioPilot/releases/latest > artifacts/website-release.json
pwsh ./scripts/build-website.ps1 -ReleaseMetadataPath artifacts/website-release.json
python -m http.server 8765 --bind 127.0.0.1 --directory artifacts/website/public
```

Open `http://127.0.0.1:8765/`. The builder reuses the app logo and documentation screenshots, and derives version,
download URLs, and sizes from the published release. It rejects missing packages, preview releases, and unexpected
download URLs. It rejects linked output roots and ancestors, linked files/directories, and unexpected files or
directories before creating or copying content. Never publish the repository root or a data folder.

`.github/workflows/website.yml` validates website changes on pull requests using fixture releases. PR checks use
read-only permissions and do not upload or deploy a site. Production builds check out `main`, then read the latest
published stable release. The `github-pages` environment permits deployments from `main` only.
Source changes on `main` and manual runs from `main` also refresh the site.

A stable release's **published** event requests a `workflow_dispatch` on `main`. That short request job has
`actions: write`; it does not check out release-tag files. The deployment job remains on the approved
`main` ref. This avoids rebuilding too early when Release Artifacts creates a draft. No additional token or secret
is required. If publication is automated with `GITHUB_TOKEN` in the future, dispatch the website workflow explicitly
after publishing; GitHub suppresses ordinary release events created by that token.

Production deployments finish before the next one starts. PR checks have separate concurrency groups and cancel
obsolete checks for the same PR. The website has its own deployment and does not add work to application release gates.

For a manual refresh, use **Run workflow** on `main` (leave **Recovery attempt** unchecked), or
`gh workflow run website.yml --ref main --repo Haanuwaa/AudioPilot`. This starts a fresh run without a commit.
After a confirmed Pages `deployment_failed` response, the workflow can request one fresh run automatically.
Only the recovery job receives `pages: read` and `actions: write`; it has no Pages deployment or OIDC write permission.
The original run stays failed. The fresh run repeats the build checks and cannot request another recovery.
Content failures, missing deployment creation, unreadable status, pending/cancelled states, and rerun attempts do not
trigger automatic recovery. A failed dispatch is reported without resending an ambiguous request.

Run the focused build checks with `pwsh ./scripts/tests/test-website.ps1` and the recovery checks with
`node --test scripts/tests/pages-recovery.test.mjs`. The recovery checks use simulated HTTP responses and never deploy.

The presentation uses system fonts and a restrained audio-equipment/manual style. Preview copy and captions share
grid cells, with inactive panels hidden and inert. A fixed-aspect image area contains each full screenshot, so
switching views does not move the following sections. This needs no resize observers or measurement loop.
CSS and JavaScript URLs include their content hashes so a new layout requests the matching assets.
Full-size screenshot links open the original PNG in a new tab and follow the selected app theme or feature view.
