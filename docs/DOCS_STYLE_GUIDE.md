# Documentation Style Guide

This guide defines writing standards for project docs so updates stay clear, consistent, and easy to maintain.

## Scope

Use this guide for:

- `README.md`
- `docs/*.md`
- meaningful XML summaries/remarks on non-obvious behavior

Do not use this guide to justify broad comment additions in straightforward code.

## Audience order

When content serves multiple audiences, optimize in this order:

1. Contributors and maintainers
2. End users
3. Release operators/automation maintainers

README exception:

- `README.md` is the repository landing page and should optimize for first-time evaluators and end users first.
- Keep contributor entry points visible in `README.md`, but route detailed build, architecture, and workflow guidance to `docs/CONTRIBUTING.md` and `docs/DEVELOPER_GUIDE.md`.

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
- `MD013` (line-length) is enforced with a high threshold in `.markdownlint.jsonc`.
- The repo currently allows longer lines in code blocks, tables, and headings, but normal prose should still wrap before that threshold.
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
3. Keep changelog workflow aligned with `docs/RELEASING.md` (`Unreleased` section maintained).

## Examples

Good:

- "Commands forward to a running UI instance when available."
- "Without a running UI host, `show` returns exit code `3`."

Avoid:

- "This super-convenient command usually works in most scenarios."
- long implementation-heavy paragraphs in user-facing sections.
