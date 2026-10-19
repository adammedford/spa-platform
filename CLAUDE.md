@AGENTS.md

## Agent skills

### Issue tracker

Issues and specs are GitHub issues on this repository, worked through `gh`; `.scratch/<feature-slug>/issues/` is the read-only archive of tickets filed before ADR-0020. See `docs/agents/issue-tracker.md`.

### Triage labels

The five canonical triage roles, each label string equal to its name. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` at the root, the numbered decisions indexed by `docs/adr/README.md` in `docs/adr/`, and `docs/plan/` as a read-only export. See `docs/agents/domain.md`.

## AWS

<!-- BEGIN AWS Agent Toolkit rules -->

`docs/agents/aws-rules.md` holds the new AWS experience's rules (one Region, managed IAM, spend limits), installed by the AWS Agent Toolkit setup, which rewrites it. It is not imported here: a session reads it when its work touches AWS, from the "Where to look" table in `AGENTS.md` (#94). A re-run of that setup may put an `@` import back between these markers; the decision on #94 is to take it out again.

<!-- END AWS Agent Toolkit rules -->
