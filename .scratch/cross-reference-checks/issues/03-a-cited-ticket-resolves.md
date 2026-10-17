# 03: A cited ticket number resolves to a file

**Source:** `.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`
**Owner:** integrator

**What to build:** Following a ticket citation lands on a spec rather than on nothing.
`docs/agents/issue-tracker.md` says a document citing a ticket "is citing a file every other
session and CI can open, which is the whole point of citing it", and nothing holds that: merged
code once cited overnight tickets 63 and 64 while `origin/main` stopped at 61. After this ticket a
citation whose number resolves to no file fails before it is pushed.

**Resolution is directory-relative, and that is the design decision this ticket makes.** Numbering
restarts per feature directory, and the tree shows both readings in use: a document inside
`.scratch/<slug>/issues/` citing "ticket 06" means its own sibling, while `docs/adr/0018` citing
"overnight ticket 53" means the overnight tracker. A single global number-to-file map mis-resolves
both. Decide and document which directory a citation resolves against from where it is written,
and make the check follow that.

Note for whoever picks this up: overnight tickets 63 and 64 exist now, so the dangling citation that
motivated this rule is already gone and the allow-list may ship empty. That is fine -- the rule is
for the next one.

**Blocked by:** 01 (the test companion). Shares a file with 02 and 04, so they land one at a time,
but neither gates this.

**Status:** resolved

- [x] The check scans tracked files for ticket citations and fails when a number resolves to no
      file under the feature directory the citation resolves against.
- [x] All three citation shapes in the tree are matched: `ticket NN`, `tickets NN and NN`, and
      `overnight ticket NN`. The session that introduced the dangling citation wrote two of them
      and a narrower grep found neither.
- [x] The resolution rule -- which directory a citation resolves against, and how a document
      outside the tracker names one -- is written into `docs/agents/issue-tracker.md`.
- [x] A ticket named before it is filed goes on an explicit allow-list in the script, following
      `PLANNED`'s precedent, and the check fails once that ticket appears so the list cannot
      outlive its entries.
- [x] The failure names the citing file and line and the number that resolved to nothing.
- [x] A red case per citation shape and a green case on the current tree, in
      `.github/steering_tests.py`. Each red case is built from a shape actually seen in the tree:
      a rule whose pattern misses the real shape passes silently and reads identical to a clean
      repository.

## Answer

Landed on `integrator/g2-prep` in d2523bf. A citation inside `.scratch/<slug>/` resolves against its own tracker; one anywhere else resolves against `.scratch/overnight/issues/`; `<tracker> ticket NN` crosses trackers. Written into `docs/agents/issue-tracker.md`. Scans whole files, so a citation wrapped across one line break is read. `UNFILED` ships empty.
