# ADR-0041: Issues carry three derived label groups, kind, area and vendor, that a workflow writes from the issue alone and no session chooses

- **Status:** Accepted
- **Date:** 2026-10-31 (decided); committed 2026-11-18
- **Deciders:** Adam, in a foreground session on 2026-10-31, recorded in the decision comment on
  #686
- **Plan ref:** No decision row. #680, which built the labels tool and its workflow; #686, which
  records the decision. **Extends** ADR-0020 decision 1, whose triage labels were the only labels
  it named.

## Context

ADR-0020 put tickets in GitHub Issues and read an issue's state from GitHub's own fields: the five
triage roles as labels, a blocker as a blocked-by edge, a claim as an assignee, resolved as closed.
It named no other label. By 2026-10-31 the tracker held over a hundred open issues, and nothing on
an issue said what it was: a run's report, a spec with sub-issues or a single ticket; whether its
work falls in the product, the deployment, the verification suites, the build, the process or the
domain docs; or whether it ties the work to AWS or to one identity provider. A reader worked that
out from each body. The repository also carried the nine labels GitHub creates by default, seven of
which no issue had ever carried.

A label an agent picks is a judgement, and two sessions judge the same issue differently. A label
that says where an issue stands is a second copy of a state GitHub's fields already hold, and
`docs/agents/tracker-policy.md` → "Lifecycle: not labels" rules that out (ADR-0020).

## Decision

1. **Issues carry three label groups beside the triage labels:** `kind:` (report, parent,
   ticket), `area:` (product, deployment, verification, build, process, docs) and `vendor:` (aws,
   cognito, pingfederate). They say what an issue is, never where it stands, so ADR-0020's rule
   that state lives in GitHub's own fields is unchanged.
2. **They are derived deterministically from the issue alone, and no agent or session chooses
   them.** Kind comes from the title and the sub-issues, area from the `**Touches:**` paths, vendor
   from those paths and a `**Vendor:**` line. The `**Vendor:**` line (`none`, or the vendors the
   work is tied to) is the one call a path cannot make, written once by whoever files the ticket.
   An issue the tables cannot judge, a path no entry covers or a Vendor line its paths contradict,
   is skipped and named, never given a guess. The derivation is `tools/issue_labels.py` over the
   tables in `tools/issue_labels.json`.
3. **A GitHub Actions workflow writes them when an issue is opened, edited, reopened, labelled or
   unlabelled, and a daily run over every open issue reconciles the rest** (the decision's "paths 1
   and 3"). Adding a sub-issue fires no event on its parent, so a parent's `kind:parent` waits for
   the daily run, up to a day; the same run catches any drift a missed event leaves and carries a
   change to the tables to every open issue. The writes add and remove labels by name and leave
   the rest, so a triage relabel made on the same event is never undone. The workflow is
   `.github/workflows/issue-labels.yml`.
4. **The seven default labels no issue had ever carried are retired**: `good first issue`,
   `help wanted`, `question`, `invalid`, `duplicate`, `documentation` and `accessibility`.
   `enhancement` and `bug` stay.

## Alternatives rejected

- **Labels the filer or an agent chooses.** Cheapest to build, nothing to maintain. Rejected
  because two sessions label the same issue two ways, so the labels could not be queried, and a
  wrong one would have no rule to be corrected against.
- **Labels written only when a session files or edits an issue (no workflow).** Rejected because
  every filing route, the overnight run, the morning, the skills and a human, would have to call
  the tool, and one that forgot would leave an issue unlabelled with nothing to notice it.
- **The workflow alone, with no daily run.** Rejected because a sub-issue added to a parent fires
  no event on the parent, so `kind:parent` would wait until the parent was next edited, and a
  change to the tables would reach only the issues edited afterwards.
- **A daily run alone.** Rejected because a new or edited issue would wait up to a day for its
  labels.
- **A GitHub Projects board's fields in place of labels.** ADR-0020 already rejected a board:
  labels, sub-issues and blocked-by answer every query the workflow makes.
- **Keeping the unused defaults.** Free, but a label no issue carries reads as a category the
  tracker uses; `enhancement` and `bug` stay because, unlike the seven, issues have carried them.

## Consequences

- `docs/agents/tracker-policy.md` → "Lifecycle: not labels" holds the rule as it stands now: what
  the three groups are for, that a workflow derives them, and what the `**Vendor:**` line records
  (#686).
- A wrong label is fixed in the issue's title, its Touches or Vendor line, or the tables, never on
  the label; a hand-set one is undone at the next event or daily run.
- A parent's `kind:parent` can lag its first sub-issue by up to a day; a reader who needs it sooner
  runs the workflow by hand (`gh workflow run issue-labels.yml -f command="apply --all"`).
- A new path the tables do not cover makes the daily run fail and name the issue, so the table
  grows in the pull request that needs it.
- The triage labels and `change-request` are outside the three groups, and the tool never touches
  them.

## Evidence

- The decision comment on #686:
  https://github.com/adammedford/spa-platform/issues/686#issuecomment-6553021983
- #680, which built `tools/issue_labels.py`, `tools/issue_labels.json` and
  `.github/workflows/issue-labels.yml`.
- `labels apply` created the 12 labels and deleted the 7 (Actions run 41269928263); `apply --all`
  labelled 115 open issues with none skipped (run 41269969883), and a check over the 111 then open
  found no drift (the decision comment above).
