# ADR-0037: A foreground session may post the human's confirmation

- **Status:** Accepted
- **Date:** 2026-11-13
- **Deciders:** Adam, recorded in
  [#1187](https://github.com/adammedford/spa-platform/issues/1187#issuecomment-6865276367)
- **Plan ref:** No decision row. Amends ADR-0021 decision 2.

## Context

ADR-0021 decision 2 keeps confirmation with the human and uses a self-attested comment to guard
against haste. The repository uses one GitHub account for Adam and his sessions. A foreground
session can already record a change-request decision Adam makes in that session. Reviews of the
confirmations on #799 and #1173 exposed a mismatch between how Adam gave those confirmations and
the rule for who could post the comment.

## Decision

Amend ADR-0021 decision 2. No scripted session writes a `Confirm`. A foreground session posts
one only on the human's explicit words given in that session. It runs the morning's parser on
the draft and the issue's comments first. It posts only when the parser exits 0 or Adam explicitly
accepts its report. The last line reads "Posted from a foreground session on Adam's words in it."
The confirmation is the human's act and remains self-attested.

## Alternatives rejected

- Require Adam to type and post every confirmation personally. This adds a separate posting step
  to the foreground session where he already gives the confirmation.

## Consequences

- The tracker and landing guides carry the same foreground posting rule.
- The morning and overnight workflows keep their scripted-posting prohibition.
- Every session still uses the shared account. The prompts and review enforce the convention.

## Evidence

- [#1187 decision](https://github.com/adammedford/spa-platform/issues/1187#issuecomment-6865276367)
- [#1185 review](https://github.com/adammedford/spa-platform/pull/1185#issuecomment-6843946811)
- [Adam's statement on #1173](https://github.com/adammedford/spa-platform/issues/1173#issuecomment-6862969619)
