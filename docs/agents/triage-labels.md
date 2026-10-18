# Triage Labels

The skills speak in terms of five canonical triage roles. This file maps those roles to the actual label strings used in this repo's issue tracker.

| Label in mattpocock/skills | Label in our tracker | Meaning                                  |
| -------------------------- | -------------------- | ---------------------------------------- |
| `needs-triage`             | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`               | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent`          | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human`          | `ready-for-human`    | Requires human implementation            |
| `wontfix`                  | `wontfix`            | Will not be actioned                     |

When a skill mentions a role (e.g. "apply the AFK-ready triage label"), use the corresponding label string from this table.

Edit the "Label in our tracker" column to match whatever vocabulary you actually use.

## Lifecycle: not labels

The labels above say what an issue needs. Where it stands is held by GitHub's own fields, never by
a label or a line in the body (ADR-0020):

| State | Held by | Read from |
| --- | --- | --- |
| Claimed | An assignee: `gh issue edit <n> --add-assignee @me`, a session's first write | `assignees` |
| Blocked | An open blocked-by relationship (`docs/agents/issue-tracker.md` → Blocking) | `issue_dependencies_summary.blocked_by` above zero |
| Resolved | Closed, after an `## Answer` comment saying where the work or the answer is | `state` is `CLOSED` |

- **Declined** is resolved too: closed as not planned (`gh issue close <n> --reason "not planned"`),
  labelled `wontfix`, with a comment saying why.
- **Partial** is not a state. Work that landed in part leaves the issue open and assigned, and its
  `## Answer` carries an `Outstanding:` line naming what is left.
- **A follow-up is not a state of its origin.** It is an issue of its own, and holds the issue it
  came from open neither way (`docs/agents/issue-tracker.md` → "An issue is held open by its own
  scope").

The archive's `**Status:**` lines (`claimed`, `blocked`, `resolved`, `closed`, `partial`, `moved to #NNN`)
are dated records of the tickets filed before ADR-0020. Read them as history; never write one, and
never take one for the current state of a moved ticket, whose issue holds it.
