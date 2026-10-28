# ADR-0032: A change request is an issue decided by the human's comment; a steering change is proposed as one, and each decision has one layer to live in

- **Status:** Accepted
- **Date:** 2026-10-28
- **Deciders:** integrator, on Adam's answers to #408, #409, #410, #411, #412, #413 and #419
  (wayfinder map #406)
- **Plan ref:** No decision row. **Amends** ADR-0020 decision 2, ADR-0021 decision 4 and ADR-0023
  decisions 2, 3 and 4. The spec #424, its ticket #431 and that ticket's grill, confirmed as Q2 to
  Q13 on #442.

## Context

A session that needs a change on an integrator path files a change request. Until now that was a
file under `contracts/changes/`, and the map #406 measured what that costs (#407):

- **Its state is a prose `**Status:**` line**, which two readers parse differently: the steering
  check reads any state but `open` as decided, and gate-evidence reads anything but `decided`,
  `rejected` or `withdrawn` as open.
- **The tracker cannot see it.** Since ADR-0020 a ticket is an issue, and a ticket waiting on a
  request file has no blocked-by edge, so it stays on the frontier every night with nothing to
  build, as #148 did.
- **Its decision has no author.** The guides say "the integrator decides", and the glossary defines
  the integrator as an agent role, yet only the human's comment can authorise the change.
- **The steering route has two paths.** A lane proposes a steering change under
  `## Proposed steering changes` in its Answer comment (ADR-0023 decision 4) and the morning commits
  it on a `Confirm`, while a request file is exempt from the steering check and from the
  path-ownership check (ADR-0023 decision 2).
- **The agent docs state 58 rules twice, and 15 of those pairs disagree**, and the tracker guide
  holding the repository's policy is a file a setup skill rewrites.
- **A decision can live in five layers**, the contract, the ADR, the merge body, the issue comment
  and the plan, with no rule for which holds what. ADR-0020 decision 2 names four homes and no
  reading order.

Five ADRs (0007, 0009, 0014, 0016, 0024) also carry Status-line amendments made by a change request
itself, with no ADR recording why.

## Decision

1. **A change request is a GitHub issue labelled `change-request`.** It is filed `ready-for-human`,
   and the ticket that needs it is blocked by it, so that ticket leaves the frontier until the change
   lands. Its process lives in `docs/agents/tracker-policy.md`. Reason: the tracker already holds
   state, edges and claims for every other ticket (ADR-0020 decision 1), and a second parser of a
   prose state line is what disagreed (#408).
2. **The human decides each request in a decision comment, and that comment is a confirmed answer**,
   because the human wrote it. A change-request issue's own number is never authority: anyone may
   file one, so citing it would let a session authorise itself (#410). Amends ADR-0023 decision 3,
   whose two kinds of authority are unchanged. The decision comment's form lives in
   `tracker-policy.md`, and the refusal in `.github/steering.py`.
3. **A steering change that a session needs and that its ticket's acceptance criteria do not ask for
   is proposed as a change request, never committed.** A change those criteria ask for lands under
   the ticket, as ADR-0023 decision 3 already allows. ADR-0023 decision 4 is replaced by this
   decision, so steering changes take one route (#411). A decided request is committed as an
   integrator ticket on its decision comment, and a request may be filed from a pre-accepted answer,
   because it asks and decides nothing (#419 Q12). Amends ADR-0021 decision 4, whose list loses a
   change request's status line.
4. **`contracts/changes/` is a read-only archive, closed to new files.** The decided files stay, so
   the roughly 236 citations of them keep resolving, as ADR-0020 decision 5 did for the ticket
   directories. An added file is refused, and an edit to an archived one needs an authority like any
   steering change. Amends ADR-0023 decision 2's exemption for an added request. The rule lives in
   `.github/steering.py`.
5. **The steering set gains `docs/agents/tracker-policy.md` and loses `issue-tracker.md`,
   `triage-labels.md` and `aws-rules.md`**, because the setup skills rewrite those three, and a
   re-run would delete a rule the steering check guards (#412, #411). Amends ADR-0023 decision 2. The
   set's home is `docs/agents/landing-work.md` → "A commit that changes a steering document", and
   `.github/steering.py` holds it.
6. **A decision is written as up to four statements, the rule as it holds now, its why, its
   provenance and its authority, and each has one layer**, read in a stated order, with a threshold
   that decides when a decision gets an ADR. The plan is a snapshot and holds none of them going
   forward. Amends ADR-0020 decision 2, whose four homes become those layers, adding the steering
   documents and a reading order. The rule lives in `docs/adr/README.md` → "Where a decision lives"
   (#413).
7. **From now on only a new ADR amends an ADR**, recorded by a Status-line note on the older one and
   no edit to its body. The five in-place "amended by change request" notes stay as history (#419
   Q7). The rule lives in `docs/adr/README.md` → "Where a decision lives".

## Alternatives rejected

- **Keep the files and fix the Status parser** (#407, #408). The tracker still could not see a
  request, and the two readers would still need one shared definition of "open".
- **A `Confirm` on the Overnight issue as a request's authority** (#411 Q8). A `Confirm` decides a
  night's grills, not requests, and a request can wait longer than one night.
- **The morning filing a request from an Answer heading** (#409 Q1). The request would exist for
  hours with no edge, and its ticket would stay on the frontier.
- **A change-request issue's number as authority** (#410 Q1). Anyone files one, so citing it would
  be a session authorising itself.
- **Two ADRs**, one for change requests and the ceremony, one for the decision layers (#419 Q2). The
  amendments ship in one spec and cite the same grills, and two ADRs would put two notes on files
  one change touched.
- **Amending an ADR in place from a change request** (#419 Q7). It is the pattern decision 7 ends: a
  note with no ADR behind it records no why.
- **A check that tells a pointer from a paraphrase** (#412 Q6). It cannot be made mechanical, and the
  review covers it.

An agent identity apart from the human's account is out of scope here, not rejected (#418).

## Consequences

- Decisions 6 and 7 are in force from this commit, because nothing else carries them.
- Decisions 1 to 5 take effect as the spec's tickets land, and until a decision's ticket lands, the
  route the steering documents give still holds:
  - decision 1 when the overnight run files requests as issues (#436), the docs say so (#438) and
    the open files move (#439), with gate-evidence reading the issues (#435);
  - decision 2 when the steering check refuses a request's own number (#437) and the docs name the
    decision comment (#438);
  - decision 3 when the overnight run's prompts file a proposal as a request (#436);
  - decision 4 when the steering check refuses an added file (#437);
  - decision 5 when the steering check's set changes (#437); `tracker-policy.md` itself landed with
    #432.
- `docs/agents/domain.md`'s "Two decision surfaces" duplicates "Where a decision lives" until #440
  makes it a pointer.
- ADR-0020's status line records the amendment.
- ADR-0021's status line records the amendment.
- ADR-0023's status line records the amendment.

## Evidence

- #407, the measured problems (58 rules stated twice, 15 disagreeing, about 236 citations):
  https://github.com/adammedford/spa-platform/issues/407#issuecomment-6411688382
- #408: https://github.com/adammedford/spa-platform/issues/408#issuecomment-6417015399
- #409: https://github.com/adammedford/spa-platform/issues/409#issuecomment-6419903580
- #410: https://github.com/adammedford/spa-platform/issues/410#issuecomment-6420205689
- #411: https://github.com/adammedford/spa-platform/issues/411#issuecomment-6420357412
- #412: https://github.com/adammedford/spa-platform/issues/412#issuecomment-6420979130
- #413: https://github.com/adammedford/spa-platform/issues/413#issuecomment-6421424640
- #416, cited only where its D11 leaves ADR-0021 decision 2 unchanged:
  https://github.com/adammedford/spa-platform/issues/416#issuecomment-6421764742
- #419, its record and Answer:
  https://github.com/adammedford/spa-platform/issues/419#issuecomment-6422671499
- #420: https://github.com/adammedford/spa-platform/issues/420#issuecomment-6422672053
- #421: https://github.com/adammedford/spa-platform/issues/421#issuecomment-6422672560
- #422: https://github.com/adammedford/spa-platform/issues/422#issuecomment-6423145286
- #431's grill, confirmed as Q2 to Q13 of #442:
  https://github.com/adammedford/spa-platform/issues/442#issuecomment-6432297940
