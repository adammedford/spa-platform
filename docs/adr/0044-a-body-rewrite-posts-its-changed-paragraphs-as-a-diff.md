# ADR-0044: A body rewrite posts its changed paragraphs as a diff, and the issue's edit history holds the old body

- **Status:** Accepted.
- **Date:** 2026-11-25
- **Deciders:** integrator, on the [decision comment on #1063](https://github.com/adammedford/spa-platform/issues/1063#issuecomment-6767125127)
- **Plan ref:** No decision row. **Amends** ADR-0021 decision 6, its clause "preserving the previous
  body in a comment"; the rest of that decision is unchanged. Ticket #968, change request #1063.

## Context

ADR-0021 decision 6 has whoever rewrites a ticket's body name the questions applied and preserve
the previous body in a comment. `docs/agents/tracker-policy.md` carried it as "preserves the
previous body verbatim in a comment headed with the date", and the morning's Apply appliers posted
it as a `## Body before <date>` comment.

#968 measured what that costs: 142 such comments hold 461 KB, the largest 42 KB, and the median
preserved body is 80% identical to the body that replaced it. GitHub keeps the edits of an issue
body in the issue's edit history, so the verbatim record exists without the copy, and the copy
buries the change it records under the text that did not change.

Since #968 the morning's Apply appliers post the changed paragraphs as a unified diff in a comment
headed `## Body rewritten <date>`. The diff's context stops at a blank line, so a paragraph the
rewrite leaves alone is not quoted.

## Decision

1. **A body rewrite posts the changed paragraphs as a unified diff** in a comment that names every
   question number applied and names the issue's edit history as the verbatim record of the previous
   body.
2. **The previous body verbatim is the issue's edit history.** No comment copies it; the
   `## Body before <date>` comment is no longer written. Those already posted stay, as records.
3. The rest of ADR-0021 decision 6 stands: a ticket's body is rewritten only to carry confirmed
   decisions, a spec's only by the session that grilled it, and the overnight grill comments and
   never rewrites.

## Alternatives rejected

- **Keep the verbatim copy.** It is what decision 6 asked for. Rejected on #968's measure: the
  record it adds is one GitHub already keeps, and it costs 461 KB of comments that every reader of
  an issue's comments pays for.
- **A plain `diff -u` with three lines of context.** The tool every reader knows. Rejected because
  its context crosses paragraph boundaries and quotes paragraphs the rewrite left alone, which is
  the noise #968 removes.
- **No comment at all, the edit history alone.** Cheapest. Rejected because the history does not
  say which confirmed questions a rewrite applied, which decision 6 requires be named.

## Consequences

- The record of a rewrite depends on GitHub's edit history. An author, or anyone with write access
  to the repository, can delete a revision's content; who made the edit and when stay visible. A
  deleted revision is a lost record, and the diff comment still says what changed.
- GitHub retains the original and the latest 99 edits of an issue body, and removes the oldest
  intermediate edits past that. The most-edited body on the tracker had 19 edits on 2026-11-07, so
  this is a bound and not a present loss. Past it, the diff comments still say what each rewrite
  changed.
- With no copy on the issue to compare, a drafted rewrite is guarded by its own two files:
  `docs/agents/tracker-policy.md` -> "Drafting tracker writes" posts them only while the diff,
  applied to the issue's body, gives the edit's body exactly. Asking only whether the diff still
  applies would pass when another session had changed a different paragraph, and the edit, which
  replaces the whole body, would overwrite that change.
- ADR-0021's Status line gains the amendment note: decision 6's comment carries the changed
  paragraphs as a diff, and the previous body verbatim is the issue's edit history.

## Evidence

- #968, which holds the measure and the acceptance criteria, and its merge `9ab05ec`.
- GitHub, "Tracking changes in a comment", on viewing and deleting a revision and the limit on edits:
  https://docs.github.com/en/communities/moderating-comments-and-conversations/tracking-changes-in-a-comment
- The grilling record, the draft and the decision comment on #1063.
