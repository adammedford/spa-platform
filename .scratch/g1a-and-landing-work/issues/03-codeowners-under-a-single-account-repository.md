# CODEOWNERS under a single-account repository

Type: grilling
Status: resolved

## Question

**Not moot, and it matters more than when it was written.** I predicted while resolving ticket 06
that this would collapse to "leave it alone"; the opposite happened. Ticket 01 observed, and
unblocked this:

- `GET /repos/adammedford/spa-platform/codeowners/errors` returns **26 errors**, one per rule line,
  every one `kind: "Unknown owner"`, each suggesting the team "exists, is publicly visible, and has
  write access". So GitHub does evaluate the file, even on a plan without code owners.
- The repository is now **public**, so code owners is a **working feature** here rather than a gated
  one. The seven unresolvable handles are a live defect producing 26 visible errors, not an inert
  annotation on a feature nobody can use.
- Option 4's cost question is settled differently too: an organisation would now be weighed against a
  public repository that already has the feature for free.

Still untested, because it needs the file this ticket would create: whether a `.github/CODEOWNERS`
suppresses the errors against the root file. Option 5 depends on it.

Ticket 02 is resolved; its facts are below the options. Two corrections it made to this ticket: there
are **seven** distinct handles across 26 rule lines, not nine; and GitHub reads
`.github/CODEOWNERS` **in preference to** the root file, while check A-05 reads the root file. That
precedence difference is the basis of option 5, which did not exist when this ticket was written.

`CODEOWNERS` is doing two jobs at once, and the single-account repository splits them apart:

- It is the **record of who owns what**, cited by `AGENTS.md`, by
  `docs/plan/03-workstreams-and-tasks.md`, and by check A-05, which parses it as the source of truth
  for path ownership. In this job the nine team handles are meaningful: they say which workstream
  owns which paths.
- It is a **GitHub feature** that resolves handles to accounts and assigns reviewers. In this job the
  nine handles are broken, because the organisation does not exist and a personal repository has no
  teams.

Rewriting every line to `@adammedford` fixes the second job and destroys the first: the file would
say nothing about which workstream owns `src/Platform.Registry`, and A-05's output would become nine
identical owners, which makes the check vacuous. Leaving the handles keeps the first job and leaves
the second broken.

So: how do the two jobs get separated?

Options to put to the human, with what ticket 02 found:

1. **Leave `CODEOWNERS` as it is** and accept whatever GitHub does with it. A-05 keeps working
   because it only reads the file as text.
2. **Move the ownership record** to a file GitHub does not read, with A-05 reading that instead, and
   either delete `CODEOWNERS` or reduce it to `@adammedford`. Costs: every existing reference to
   `CODEOWNERS` has to move, including `AGENTS.md`, the plan export (which cannot be edited), and the
   header comment in the file itself.
3. **Keep both**: `CODEOWNERS` as the ownership record it already is, and accept that GitHub's
   review-assignment feature goes unused, since review is `/code-review` from a separate session
   rather than a GitHub reviewer assignment anyway.
4. **Create the organisation** after all, with six teams of one member. Reverses a charting decision,
   so it needs a reason that the charting grilling did not have — and it probably also costs money:
   the organisation itself is free, but code owners on a *private* organisation repository is gated
   behind GitHub Team. Ticket 02 did not establish that gate, so check it before choosing this.
5. **Split the two jobs across the two paths GitHub's precedence gives us.** Keep the root
   `CODEOWNERS` exactly as it is, as the ownership record that A-05 reads and that `AGENTS.md` and
   the plan cite. Add a minimal `.github/CODEOWNERS` naming `@adammedford`, which GitHub reads
   *instead of* the root file. GitHub then has a file it can resolve, A-05 keeps the record it needs,
   and no existing reference moves.

   The cost is a second file that looks redundant and will puzzle a reader, so it needs a comment in
   both files saying why. Check first whether GitHub reports `Unknown owner` against a root file it
   is no longer reading — ticket 02 could not establish that, and ticket 01 can observe it.

Note which existing text would have to change under each option. The plan export is read-only, so an
option that contradicts it needs an ADR, not an edit.

### What ticket 02 established

- An unresolvable owner is **annotated, not fatal**: kind `Unknown owner`, visible in the file view
  and at `GET /repos/{owner}/{repo}/codeowners/errors`. It blocks nothing and does not invalidate the
  other lines. So option 1 costs an error annotation and nothing else.
- A broken file is **inert** with respect to required status checks, approval counts, push and
  deletion rules, linear history and signed commits. The only coupling is the optional "Require
  review from Code Owners", which this project does not use: review is `/code-review` from a separate
  session.
- **Commenting out the owner lines fails closed.** A-05 would have no rules, every path would resolve
  to no owner, and the check fails. So any option that empties the root file has to move A-05's input
  in the same change.
- On a **private personal** repository, code owners needs GitHub Pro at all, and only `@username` can
  ever resolve there. That makes option 4's organisation the only way the nine-handle record could
  ever resolve as written — and ticket 06 is now deciding whether the repository stays private, which
  this ticket should not pre-empt.

## Answer

**Option 3, with the two jobs separated by declaring one of them unused rather than by moving a
file.** The root `CODEOWNERS` stays exactly as it is: the ownership record, cited by `AGENTS.md`, by
the plan, and parsed by check A-05. GitHub's handle-resolution and review-assignment job is declared
**unused**, not broken. The 26 `Unknown owner` errors stand, because they are **true**: the handles
do not resolve, and nothing in this project wants them to.

That reframing is the actual decision. Options 1 and 3 were never distinct once the second job has no
consumer — review is `/code-review` from a separate session, and "Require review from Code Owners" is
not used — so the only question was whether to pay something structural to silence a cosmetic
annotation. The answer is no.

### Option 5 is rejected, on evidence it did not have when it was written

Option 5 was the front-runner and the probe killed it. Observed on a throwaway branch:

| ref | errors | file GitHub evaluated |
| --- | --- | --- |
| `main` | 26, all `Unknown owner`, each citing `path: "CODEOWNERS"` | root `CODEOWNERS` |
| branch + `.github/CODEOWNERS` (`* @adammedford`) | **0**, body literally `{"errors":[]}` | `.github/CODEOWNERS` |

So yes, a `.github/CODEOWNERS` suppresses the root file's errors — by **shadowing the root file
completely and silently**. GitHub "will search for them in that order and use the first one it finds"
(<https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners>),
and emits **zero** diagnostics to say the root file is being ignored.

Those 26 errors are currently the only visible signal anywhere that the handles do not resolve.
Option 5 would have bought a green errors API by deleting the one honest indicator, leaving the root
`CODEOWNERS` as dead documentation that `AGENTS.md` and the "Who owns what" table still cite, with
nothing to warn the next reader. It makes the repository *look* correct instead of *being* correct.
Rejected for that reason, not on cost.

### The other options

- **Option 2 (move the record to a file GitHub does not read)** solves a problem that does not exist
  once the GitHub job is unwanted, and costs every reference: `AGENTS.md:23`, `GLOSSARY.md:115`,
  `README.md:42`, `.github/path-ownership.py:39`, and two lines of the frozen plan export that cannot
  be edited and would therefore need an ADR to contradict.
- **Option 4 (create the organisation)** is **out of scope**, not merely rejected: it is a decision
  about how the account is structured, which sits past this map's destination. Q1 also removed its
  only remaining benefit. Its GitHub Team pricing was never checked, and now does not need to be.

### Two defects found while resolving, both fixed here

1. **A vocabulary conflict.** `GLOSSARY.md:115` defines a workstream as having its own paths in
   `CODEOWNERS` and ends **"Not 'team'."** — yet the record writes every workstream as a GitHub
   *team* handle, `@spa-platform/ws2-hosting`, 26 times. Resolved in favour of keeping the handles:
   `@org/name` is the only form that is simultaneously valid `CODEOWNERS` syntax, parseable by A-05,
   and legible as a workstream id. Dropping the prefix is a syntax error; collapsing to
   `@adammedford` makes A-05 vacuous by giving every path one owner. The **glossary** changes
   instead, to distinguish a *handle* (a syntactic carrier for a workstream id) from a *team* (which
   does not exist here and never will). The ban governs prose.
2. **The file's header comment has gone stale.** `CODEOWNERS:4` says to point the handles at real
   teams or users "before turning on branch protection". Ticket 01 turned branch protection on and
   left the placeholders, so the file now instructs against the state it is in, and its instruction
   is unfollowable. The comment is where a reader learns what the file is *for*, so it is the place
   this whole decision gets explained.

### The shadowing trap, and its guard

The probe exposed a failure that is invisible on both sides. If anyone later adds a
`.github/CODEOWNERS` — a plausible act, done precisely to silence the 26 errors — GitHub silently
stops reading the root file, **A-05 keeps passing** because `.github/path-ownership.py:39` reads the
root file directly by path, and no diagnostic fires anywhere.

Guarded by one assertion, in **both** `.githooks/pre-push` and CI: fail if `.github/CODEOWNERS`
exists. Both, because the hook is per-clone opt-in via `git config core.hooksPath .githooks` and so
cannot be relied on alone. This converts a silent trap into a loud one.

### What text has to change, at handoff

Nothing is edited from this session; these are integrator paths and the map writes its artifacts at
handoff.

| File | Change |
| --- | --- |
| `CODEOWNERS` (header) | Replace the stale "placeholders / before turning on branch protection" line. State that the handles are deliberately unresolvable, that the file is the ownership record and not GitHub's input, that the 26 `Unknown owner` errors are expected and accurate, and point at ADR-0014. |
| `GLOSSARY.md` (**Workstream**) | Distinguish handle from team, so a reader does not read the file as violating the "Not 'team'" ban. |
| `README.md:42` | "placeholders for real teams" is now false — they are permanent. |
| `.githooks/pre-push`, `.github/workflows/ci.yml` (or `path-ownership.py`) | Add the `.github/CODEOWNERS` guard. |
| `docs/adr/0014-*.md` | New. The decision, the rejected options, and the probe's finding — which otherwise dies with this session, since `.scratch/` is not committed. |

No change to `.github/path-ownership.py:39`'s reading of the root file: that stays the source of
truth, and is now load-bearing by choice rather than by accident.

### Not absorbed

`README.md`'s I-1 status table is stale beyond row 42 — it also claims "Git | Not initialised" and
that `dotnet test` "has not been run yet", both falsified by ticket 01 (306 tests, CI green on a
runner). Left alone deliberately; it is not a CODEOWNERS question, and it is now on the map's **Not
yet specified** as probably belonging with G1a's evidence checklist.

## Addendum (ticket 08)

`Status: resolved` stands. One deliverable changed scope.

**ADR-0014 widens, and is retitled.** This ticket queued it as "why `CODEOWNERS` carries 26
deliberate errors". Ticket 08 found that A-05's integrator declaration has the **same root cause**:
one account, no resolvable handles, author-based ownership dead. The check tests that the trailer
*says* `integrator` and nothing can verify who wrote it. Two ADRs would split one decision across
two files and leave each half a story, so ADR-0014 becomes *path ownership under a single account*
and records both halves:

- the handles do not resolve, and that is true rather than broken (this ticket);
- A-05 reads the file as text, so the errors are inert with respect to it (this ticket);
- the declaration names a role nothing can verify, and that is the floor of one-author ownership
  rather than a defect to fix (ticket 08);
- the mitigations are a cited task id, the tip-commit scope and the log (ticket 08);
- **33eb5fb as the worked example**: the only cross-owner merge in the repository landed with
  nothing in the permanent record, because the old rule matched the whole `merge-base..head` range
  and the branch holding the evidence is deleted (ticket 08).

This keeps the map's destination at **six** artifacts rather than seven. The `.github/CODEOWNERS`
guard this ticket specified is unaffected, and ticket 09 — which cites ADR-0014 rather than
attesting it — should meet the widened scope.
