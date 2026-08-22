# Security Policy

This project is maintained by one person, so security handling is best-effort rather than backed by a formal team or a
24/7 response process.

## Supported Versions

Security fixes are prioritized for:

- the latest released version, and
- the current `main` branch when the issue has not shipped yet.

Older releases may not receive backported fixes.

## Release Verification

Current AudioPilot app binaries and MSI installers are unsigned. Download releases from
[Haanuwaa/AudioPilot](https://github.com/Haanuwaa/AudioPilot/releases), and compare downloads against the published
`SHA256SUMS.txt`. GitHub-built releases also include provenance and artifact attestations. These describe the build
and artifacts; they are not Authenticode publisher signatures and do not guarantee that Windows will allow execution.
See the [user guide](USER_GUIDE.md#unsigned-downloads-and-windows-warnings) for file unblocking and Windows warnings.

## Reporting A Vulnerability

Please do not open public GitHub issues for security vulnerabilities.

Preferred channel:

- [Report a vulnerability privately](https://github.com/Haanuwaa/AudioPilot/security/advisories/new) through GitHub.

Please include:

- a clear description of the issue,
- reproduction steps or a proof of concept,
- expected behavior versus actual behavior,
- affected version, commit, or environment,
- any notes about impact or likely exploitability.

## Response Expectations

- Initial triage target: within 7 days.
- Follow-up timing depends on severity, reproducibility, and available maintainer time.
- Coordinated disclosure is preferred until a fix or mitigation is available.

## Scope Notes

AudioPilot is a local Windows desktop application, not a network-facing service. That means the likely security surface
is narrower than a typical web app or daemon, but it does not mean the project is risk-free.

GUI and CLI communication uses local named pipes restricted to the same Windows user and elevation level. The client
also checks the server's ownership and limits impersonation to identification. These checks cannot distinguish an
AudioPilot binary from another process running as the same user at the same elevation level.

Areas that still matter include:

- unsafe import or file-handling behavior,
- path handling bugs,
- privacy leaks in logs, diagnostics, exports, or backups,
- incorrect privilege assumptions,
- vulnerable third-party dependencies,
- command or automation paths that can cause unintended local impact.

By contrast, large classes of remote-server vulnerabilities are less likely here simply because the app is not designed
as an Internet-exposed service.

Missing hardening ideas are still welcome, but some reports may be treated as enhancements rather than vulnerabilities.

## Privacy And Logging

- Background logs are redacted by default where practical.
- Absolute paths are stripped from normal exception logging.
- Redaction is best-effort, not encryption. Stable hashes preserve diagnostic correlation, but predictable names can
  still be guessed. Review exported diagnostics before sharing them publicly.
- CLI output is a separate surface and can be richer by design, especially when the user explicitly asks for detailed
  output.
- Privacy or logging issues that expose sensitive file paths, device names, routine names, or similar identifiers in
  places that should be redacted are in scope for private security reporting.

If you are reporting a privacy-related issue, include whether the problem appears in:

- background logs,
- CLI output,
- exported config or diagnostics output,
- backups or packaged artifacts.

## Dependency Scanning

- `.github/workflows/dependency-scan.yml` runs NuGet vulnerability checks on pushes and pull requests to `main`, and on
  a weekly schedule.
- The workflow fails when direct or transitive vulnerable packages are detected.

## Third-Party Dependencies

Reports that are primarily about a third-party dependency may be redirected upstream when appropriate, though dependency
impact on this repository is still useful to report privately.
