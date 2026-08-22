# Documentation Style Guide

This guide defines writing standards for project docs so updates stay clear, consistent, and easy to maintain.

## Scope

Use this guide for:

- `README.md`
- `docs/*.md`
- meaningful XML summaries/remarks on non-obvious behavior

Do not use this guide to justify broad comment additions in straightforward code.

## Audience And Ownership

- `README.md`: first-time users evaluating, downloading, and starting AudioPilot.
- `USER_GUIDE.md` and generated `ABOUT.txt`: everyday tasks, settings, limitations, and troubleshooting.
- `CLI.md`: command behavior, scripting contracts, and automation examples.
- `CONTRIBUTING.md` and `DEVELOPER_GUIDE.md`: validation, architecture, and implementation rules.
- `RELEASING.md`: release operators and packaging automation.
- Release notes: changes and download choices for users; link to detailed guides instead of repeating them.

Edit generated documentation at its source: `ABOUT.txt` in `AudioPilot.csproj`, `DEVICES.txt` in
`DeviceReferenceFileWriter`, CLI reference blocks through the CLI metadata and sync script, and release notes through
the changelog and `scripts/release-body.ps1`.

## Tone and writing style

- Use direct, plain language.
- Prefer short paragraphs (1-3 sentences).
- Prefer active voice.
- Avoid marketing language and speculation.
- Describe behavior and intent, not implementation trivia.

## Heading and structure conventions

- Use Title Case for section headings.
- Keep heading depth shallow when possible.
- Prefer task-oriented sections (`Troubleshooting`, `CLI Quick Reference`, `Pre-release Checklist`).
- Put the most-used information first.

## CLI documentation ownership

`docs/CLI.md` is the single detailed CLI source of truth for:

- command matrix,
- JSON behavior,
- exit codes,
- automation examples.

`README.md` and `docs/USER_GUIDE.md` should keep CLI sections brief and link to `docs/CLI.md`.

## Cross-file consistency rules

When a PR changes any of the following, update all affected docs in the same PR:

- command behavior, flags, JSON shape, or exit codes,
- startup/tray/minimize behavior,
- switch/retry/debounce/resume recovery behavior,
- settings keys/default behavior,
- interop model conventions (`LibraryImport`, generated COM, marshalling/lifetime expectations).

## Markdownlint policy

- Markdownlint is required for docs changes.
- `MD013` limits prose and headings to 120 characters in `.markdownlint.jsonc`.
- Code blocks and tables are exempt so command examples and table rows remain intact. Wrap prose at word boundaries;
  Markdown renders those soft line breaks as spaces.
- Do not perform broad line-wrap-only rewrites in unrelated docs.
- Continue enforcing all other markdownlint rules.

## Commenting and XML docs

Add XML docs only when behavior is non-obvious, especially for:

- lifecycle/state transitions,
- threading/synchronization,
- debounce/retry semantics,
- interop marshalling/lifetime ownership.

Avoid comments for obvious getters/setters and trivial forwarding methods.

## Validation checklist

Before merging docs changes:

1. Run `./scripts/validate-doc-links.ps1`.
2. Ensure `README.md`, `docs/USER_GUIDE.md`, and `docs/CLI.md` are not contradictory.
3. Keep changelog entries aligned with `Version.props` and `docs/RELEASING.md`. The v1.0.0 changelog contains only the
   initial release entry.

## Examples

Good:

- "Commands forward to a running UI instance when available."
- "Without a running UI host, `show` returns exit code `3`."

Avoid:

- "This super-convenient command usually works in most scenarios."
- long implementation-heavy paragraphs in user-facing sections.
