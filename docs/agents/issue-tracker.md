# Issue tracker: Local Markdown

Issues and specs for this repo live as markdown files in `.scratch/`.

The tracker is **tracked**, not scratch. A merge body, an ADR or a contract that cites a ticket by
path is citing a file every other session and CI can open, which is the whole point of citing it.
Session logs under `.scratch/<feature-slug>/log/` stay ignored: they are a transcript, not a spec.

## Conventions

- One feature per directory: `.scratch/<feature-slug>/`
- The spec is `.scratch/<feature-slug>/spec.md`
- Implementation issues are one file per ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01`, never a single combined tickets file
- Triage state is recorded as a `Status:` line near the top of each issue file (see `triage-labels.md` for the role strings)
- Comments and conversation history append to the bottom of the file under a `## Comments` heading

## Which directory a cited number resolves against

A ticket number means nothing on its own: every feature directory numbers from `01`, so `06` names
a file in several of them. Where a citation resolves is therefore read off **where it is written**.

- **A document inside `.scratch/<feature-slug>/`** -- a spec, a map, a report, a questions file --
  cites its own directory. `.scratch/g1a-and-landing-work/issues/05-...md` saying "ticket 06" means
  `.scratch/g1a-and-landing-work/issues/06-...md`, its sibling.
- **A document anywhere else** -- an ADR, a contract, a merge body, a comment in Terraform or C# --
  cites `.scratch/overnight/issues/`, the plan-wide tracker. That is already what every such
  citation in the tree means, and it is the only tracker a document outside `.scratch/` has reason
  to cite.
- **To cite across directories, name the directory**: write the slug immediately before the
  citation, as `docs/adr/0018-...md` does with "overnight ticket 53". The name wins over both
  defaults above, and it is the only way a document inside one feature directory can cite another.
  Without it the number resolves to a sibling that may not exist, or worse, to the wrong file.

`.github/steering.py` holds this, over every file git tracks, in the three shapes the tree writes:
`ticket NN`, `tickets NN and NN` and `<slug> ticket NN`. A longer list enumerates the same way, by
`and`, a comma or a slash, and a citation that wraps over a line break is read whole. A number that
resolves to no file fails the push. A ticket named before it is filed goes on that script's
`UNFILED` list with the reason, and the check fails once the ticket appears, so the list cannot
outlive its entries.

Two things it deliberately does not read. A dotted number is a plan task id, not a ticket:
"workstream 3's tickets 3.3, 3.5 and 3.9" cites rows in
`docs/plan/03-workstreams-and-tasks.md`. And a range -- "tickets 62-65", "tickets 01 to 08" -- is
held to its first number only, because the numbers inside it were never written down.

## When the number you want is already claimed

Two sessions that cannot see each other's unlanded work will both pick the next free number, and
both will be right on their own branch. `.github/steering.py` fails the push once two files in one
`.scratch/<feature-slug>/issues/` share a leading number, naming the directory and both files.
The same number in two different feature directories is not a collision: numbering restarts per
directory by design.

- **Before filing**, fetch and take the next number after the highest on `origin/main` in that
  directory, not only the highest in your checkout.
- **When the check fails**, the ticket that reached `origin/main` second renumbers, and in practice
  that is yours, since you are the one pushing. The ticket already on trunk keeps its number because
  commit messages and merge bodies cite it by that number, and history cannot be edited.
- **Renumber in one commit**: `git mv` the file to the next free number in its directory, change
  the number in its `# NN:` heading, and move every citation that meant it -- `ticket NN`, a wrapped
  `ticket` / `NN` over a line break, and the `NN-<slug>` file name. Add a line under its
  `## Comments` saying which number it had, so a commit message that still says the old number can
  be traced.
- **Read every citation of the old number in context and decide which ticket it meant.** After the
  rename the old number still resolves, to the ticket that kept it, so a citation left behind does
  not dangle: it lands on the wrong spec, and the citation check passes it. Nothing mechanical
  catches that, which is why this step is a reading and not a grep-and-replace.

Overnight ticket 68 is the worked example: task 6.1a's ticket was filed as 61 beside the security
suite's flake ticket, and was renumbered when this check landed.

## When a skill says "publish to the issue tracker"

Create a new file under `.scratch/<feature-slug>/` (creating the directory if needed).

## When a skill says "fetch the relevant ticket"

Read the file at the referenced path. The user will normally pass the path or the issue number directly.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a file with one **child** file per ticket.

- **Map**: `.scratch/<effort>/map.md` (the Notes / Decisions-so-far / Fog body).
- **Child ticket**: `.scratch/<effort>/issues/NN-<slug>.md`, numbered from `01`, with the question in the body. A `Type:` line records the ticket type (`research`/`prototype`/`grilling`/`task`); a `Status:` line records `claimed`/`resolved`.
- **Blocking**: a `Blocked by: NN, NN` line near the top. A ticket is unblocked when every file it lists is `resolved`.
- **Frontier**: scan `.scratch/<effort>/issues/` for files that are open, unblocked, and unclaimed; first by number wins.
- **Claim**: set `Status: claimed` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set `Status: resolved`, then append a context pointer (gist + link) to the map's Decisions-so-far in `map.md`.
