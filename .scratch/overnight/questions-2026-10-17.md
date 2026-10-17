# Questions for Adam — overnight run of 2026-10-17

**55 questions, Q1..Q55, grouped by ticket, the ones that unblock the most work first.**

- **52 come from the three tickets grilled tonight** (70, 69 and 66), from their `## Questions for Adam` sections. Each carries the line its ticket records, `Answer: yes, pre-accepted by Adam, 2026-10-15`, which is your standing answer for this run. The morning session applies them as written unless you override one.
- **Two are open and need an answer.** Q55 was resolved during PR #47 review by reusing the existing dead-session recorder in the token source; its answer is recorded below. Q20 and Q21 are in no ticket section: this report raises them because two pre-accepted answers contradict each other (Q20), and because a decided change request reads as a loop (Q21). Each says so on its Answer line.
- **Every other `## Comments` section** holds status, prep or filing notes, and no question. The one Comments line in ticket 66 asks what its own question 9 (Q51 here) answers, so it is not asked twice.
- **Numbering.** Each question names the ticket's own number. Where a recommendation points at another question, this file's number follows as "Qn here". "Option (b)" means the option with that letter in the ticket's grilling record.
- **No grill tonight was carried from an earlier night.** The closing section lists the tickets whose questions were asked and answered on earlier nights.

---

## Ticket 70 — task: none (integrator) — What enforces review before a merge

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/70-what-enforces-review-before-a-merge.md`

This ticket goes first. Its answers decide how this morning's pull request merges (Q6, Q9) and what the G2 tag waits for (Q11, Q15). After tonight, stage S4 owes no plan row, so G2 is the next gate, and it unblocks the first gateway deployment and task 2.1b. The grill proposes ADR-0020, five glossary terms and one amendment to the Integrator entry.

**Q1.** (ticket's question 1) Should a review cover exactly the head its record names, so that commits pushed after it, fix commits included, need their own review by an agent session that wrote none of them? That review may cover only the commits since the last reviewed head.
Recommended: yes (option b). Why: #34 and #35 merged on fix commits only their writer verified, and a delta review keeps a fix round cheap.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q2.** (question 2) Should the integrator's carve-out ("merges on CI plus the human") be removed, so that every pull request into `main`, the integrator's included, gets a review from an agent session that wrote none of its commits?
Recommended: yes (option b), with both "weakest link" paragraphs. Why: its premise conflates a workstream with an agent session, and all four pull requests in the incident were integrator work it exempted.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q3.** (question 3) Should the guard aim at haste and accept a self-attested record, since every agent session and the human act through one GitHub account?
Recommended: yes (option a): a sequencing guard, not a permission. Why: every instance was haste, and a forged record is undetectable under one account, the floor A-05 already reached.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q4.** (question 4) Should branch protection requiring approving reviews (the ticket's option B) be rejected for this repository?
Recommended: yes. Why: satisfying it needs a second identity the writing session could also reach, or `--admin`, which drops the two required CI checks too.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q5.** (question 5) Should `landing-work.md` state that only generated roadmap refreshes reach `main` without a pull request, as prose until the rule is first broken?
Recommended: yes (option b). Why: it is true today (six direct pushes ever: the import and five refreshes), and it closes the hole beneath the rule.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q6.** (question 6) Should `tools/land.sh` refuse to merge unless the latest review record naming the pull request's current head says `merge`, then merge with `--match-head-commit` pinned to that head, and never with `--admin`?
Recommended: yes (option a). Why: it would have refused all four incident pull requests, and a decidable rule belongs in a check at the one sanctioned merge path.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q7.** (question 7) Should the review record be the fixed line `Reviewed <40-hex head> against <40-hex base>: merge` (or `: hold`) in a PR comment or `COMMENTED` review, specified in `docs/agents/review.md`, full shas only?
Recommended: yes (option a). Why: it fails closed. GitHub's `commit_id` defaults to the newest commit and fails open, and "any mention of the head" would have passed `a80e906`.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q8.** (question 8) Should only an agent session that ran `/code-review` and wrote none of the commits write a review record, the human's reading being the decision to land rather than a review?
Recommended: yes (option b). Why: "CI plus the human" was the weakest link, and a review session costs the human one command.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q9.** (question 9) Should no pull request into `main` be exempt by content or origin, so that tracker-only, docs-only, generated and overnight integration pull requests all need a record naming their head?
Recommended: yes (option a), plus an optional local edit to the overnight workflow's morning step. Why: #36 was tracker-only and its ticket carried four wrong claims, and the overnight pull request carries commits no lane review saw.
Answer: yes, pre-accepted by Adam, 2026-10-15
*This morning:* applied before tonight's pull request merges, this means `overnight/2026-10-17` needs a review record naming its head first.

**Q10.** (question 10) Should the rejection of option B be scoped "while this repository has one human and one GitHub account", reopening when a second person with their own account joins or a regulated adopter takes the platform into an organisation? Should the governance audit's gap 8 be corrected to say protection requires the two checks and no review?
Recommended: yes. Why: gap 8's required reviews are right once there are two people, and its "no branch protection declared" is inaccurate.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q11.** (question 11) Should no gate tag be cut while any review of a change it covers is open? Open would mean a review running or holding, commits behind the last reviewed head, a merge with no review, or a re-review in flight, with "open review" defined as in the grilling record's Q11.
Recommended: yes (option a). Why: a tag is permanent, and `contracts-v1` still freezes sentences that `main` corrected 34 and 71 minutes after it.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q12.** (question 12) Should `land.sh` take `--unreviewed "<reason>"` as its one escape, writing `Unreviewed: <reason>` into the merge body and leaving the review open until a record names the merged head?
Recommended: yes (option b). Why: it keeps `land.sh` the one merge path (overnight ticket 66, user story 6) and turns haste into a debt the next gate pays.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q13.** (question 13) Should every merge `land.sh` makes end with a `Reviewed: <head sha> <record URL>` line, or the `Unreviewed:` line, with nothing asserting it?
Recommended: yes. Why: the evidence rides in git and tells `land.sh` merges from the rest, while g1a-and-landing-work ticket 11's decision that the body is not checked stands.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q14.** (question 14) Should the record-reading logic be a Python module `land.sh` calls, with a fixture-based test companion run in CI, rather than untested bash?
Recommended: yes (option b). Why: a guard nobody has seen fail is an unfailable assertion, and this need not wait on ticket 69.
Answer: yes, pre-accepted by Adam, 2026-10-15
*Conflict:* ticket 69's question 6 (Q27 here) says the opposite about the order. See Q20.

**Q15.** (question 15) Should the five merges since `contracts-v1` that no record names (#38, #39, #40, #44, #45) get a retroactive review before G2 is tagged?
Recommended: yes (option b). Why: otherwise the first tag the rule governs covers exactly the incident's unreviewed fix-forwards.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q16.** (question 16) Should the tag rule be held by prose in `landing-work.md` → Gates plus one `git log` reading command, with no hook and no tag script?
Recommended: yes (option a). Why: an `Unreviewed:` merge's follow-up record lives on GitHub, "a review is running" is decidable from nothing, and only four Phase 1 tags remain.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q17.** (question 17) Should the decision be recorded as ADR-0020 (or the next free number on `origin/main`), with option B, a required status check and prose only as its rejected alternatives?
Recommended: yes. Why: the rejection is non-obvious, and the governance audit has already proposed it.
Answer: yes, pre-accepted by Adam, 2026-10-15
*Checked tonight:* ADR-0020 is still free; the last ADR on the branch is ADR-0019.

**Q18.** (question 18) Should it land as one integrator pull request (the ADR, both agent guides, `land.sh`, the module and its tests, CI, the glossary, gap 8, and this ticket's Answer), reviewed by a fresh agent session and held by convention this once?
Recommended: yes. Why: no document may claim a check before the check exists, which is the defect class overnight ticket 66 was about.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q19.** (question 19) Should `GLOSSARY.md` gain Review, Review record, Open review, Agent session and Gate tag, and the Integrator entry gain "a role that agent sessions play, not a person"?
Recommended: yes, worded as in the ticket's "Proposed glossary / ADR". Why: the ticket's vocabulary was fuzzy in four places, and "session" collides with the gateway's sessions.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q20.** (raised by this report; in no ticket section) Ticket 70's question 14 (Q14) says its `land.sh` check "does not wait on ticket 69". Ticket 69's question 6 (Q27) says "if ticket 70 picks the land.sh check, land that check after this ticket, with its case", and ticket 70's question 6 (Q6) does pick it. Both carry your pre-accepted yes. Should ticket 70 land first, as its question 14 says, with ticket 69 then adding a case for 70's refusal, the way its question 6 already treats cross-reference-checks ticket 07 if 07 lands first?
Recommended: yes. Ticket 70's record-reading logic has fixture tests of its own (Q14), so only the few lines of `land.sh` that call it go untested, and only until 69 lands. Ticket 69's closing completeness case (its question 16, Q37 here) then fails while any refusal above the merge has no case, so 69 cannot land without one for 70's. The other order keeps `land.sh` free of untested lines, but every merge until 69 lands stays held by convention, which is the gap ticket 70 exists to close.
Answer: open (raised by this report from the two grilling records; Adam has not answered it)

---

## Ticket 65 — CR-1.5a — Change request 1.5a: the bucket name in two frozen examples, and Object Lock in ADR-0009

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/65-CR-1.5a-bucket-name-and-object-lock.md`

Its own twelve questions were answered on 2026-10-16 (`questions.md` Q1–Q12), and the ticket is resolved. This question is new. It decides when the first platform-root apply can happen, and that apply heads stage S5: tasks 1.3, 1.4, 1.6b, 1.7, 1.9, 6.9 and 6.7, and gate G3. Tickets 04 (1.3) and 08 (1.7) are the rows it touches.

**Q21.** (raised by this report; in no ticket section) The Decision in `contracts/changes/1.5a-bucket-name-and-object-lock.md` ends "Every one lands before the first platform-root apply". Its list includes `teardown.sh` reading `bucket` from the platform root's state, which it folds into task 1.7. Task 1.7 waits on 1.4, whose "service steady" needs that same apply, and `teardown.sh` does not exist yet. Tonight 1.5's Terraform follow-ups landed on the branch (`3773d0a`), so `teardown.sh` is the only one left. Task 1.2, the apply's other wait, landed too (`820adf5`). Should the sentence be read, and amended to say, that the Terraform follow-ups land before the first platform-root apply, and that `teardown.sh`'s state read lands with task 1.7, before the first teardown?
Recommended: yes. The ordering exists because the bucket's name and namespace are free to change only until the bucket exists (`questions.md` Q1), and those are in. The hazard the `teardown.sh` half guards, a derived name pointing teardown at the wrong bucket, cannot arise before the script exists. Read literally, the sentence is a loop: the first apply waits on 1.7, which waits on 1.4, which waits on the first apply. The other way out is to split a 1.7a that writes `teardown.sh` before any stack exists, and that lands a script nothing can run yet.
Answer: open (raised by this report from the run's blocked list; Adam has not answered it)

---

## Ticket 69 — task: none (integrator) — A test harness for the repository's shell scripts

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/69-a-shell-test-harness.md`

Once these are applied the ticket becomes `ready-for-agent` (Q42). It also decides the order for cross-reference-checks ticket 07 and for ticket 70's `land.sh` check (Q27, and Q20 above). The grill found two `land.sh` defects to fix test-first (Q28, Q29), and that `preflight.sh` calls gh optional and skips python3, though `land.sh` needs both. No ADR candidate.

**Q22.** (ticket's question 1) Use Python's `unittest`, driving the real script as a subprocess, as the test tool?
Recommended: stdlib `unittest`, run as `python3 -I`, with each case starting the tracked script in a subprocess. Not bats-core, and not a plain-bash runner. Why: no new tool, since python3 already runs the hook, CI, land.sh and the roadmap. It has the family's shape, and bats would run land.sh as a subprocess anyway.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q23.** (question 2) Scope this ticket to land.sh's parsing, its pre-merge refusals and the arguments of its merge call, and file preflight.sh and land.sh's post-merge path as follow-ups?
Recommended: yes. The merge call's arguments join the Done-when. preflight.sh and the post-merge path become their own tickets (question 15, Q36 here). Why: 2c9962f shows only in what gh receives, and preflight's test needs different doubles, a file copy and no network.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q24.** (question 3) Run the same tests in a macOS job under `/bin/bash` 3.2.57, as well as on Linux?
Recommended: a separate macOS job beside the Linux step, both running the same companion. Why: the Linux runner has bash 5.2.21, so it can never show a 3.2-only failure, such as an empty `"${a[@]}"` under `set -u` (probed). The job is free while the repository is public.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q25.** (question 4) Test against real git in a throwaway repository with a bare `origin`, with a double for gh alone?
Recommended: yes. No git double, and no clone of the live repository. Why: every git call land.sh makes works offline against a bare origin, so its refusals are tested against git itself, and the git-double refusal the ticket notes stops mattering.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q26.** (question 5) Define "running the script itself" as the tracked file's current bytes, run whole through a caller's interface (arguments, standard input, working directory, PATH, environment), with a whole-file copy allowed when a script reads beside itself, no extracted fragment, and no test-only switch?
Recommended: yes, written into the companion's docstring. Why: it rules out the copied parse loop 2c9962f was verified with, and a test seam that would give preflight's guard a second way to read the wrong file.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q27.** (question 6) Land every pending change to land.sh's refusals after this ticket, with its cases?
Recommended:
- If cross-reference-checks ticket 07 has not landed, block it on overnight ticket 69 and replace its last checkbox with three cases in `tools/land_tests.py`: a FIFO from `os.mkfifo`, a `/dev/fd/N` passed with `pass_fds`, and `-` refused with its reason.
- If 07 has landed, add those three cases to question 16's list (Q37 here).
- If ticket 70 picks the land.sh check, land that check after this ticket, with its case.

Why: 07 prescribes "running the script's own parse loop", the method this ticket retires, on the lines 2c9962f fixed.
Answer: yes, pre-accepted by Adam, 2026-10-15
*Conflict:* the third bullet contradicts ticket 70's question 14 (Q14). See Q20.

**Q28.** (question 7) Fix line 47 in this ticket's branch, test first, so an exported `CDPATH` or a name starting with `-` cannot defeat it?
Recommended: line 47 becomes `body_file="$(CDPATH= cd -- "$(dirname -- "$2")" && pwd)/$(basename -- "$2")"`, in its own commit, after the cases that show both red. preflight.sh line 116's copy of the idiom goes with preflight's ticket. Why: probed. With `CDPATH` exported, gh receives a two-line path naming another directory, and `-x.md` dies in `dirname`'s option parser: 2c9962f's defect, surviving its fix.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q29.** (question 8) Make end of input at the subject prompt print its refusal, with `read -r reply || reply=""`?
Recommended: yes, in its own commit, after the case that shows it red. A piped `y` still confirms. Why: probed. Under `set -e` the failed `read` exits 1 before "stopped. Pass --subject ..." prints, so a session with no terminal never learns why.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q30.** (question 9) Put the companion at `tools/land_tests.py`, stdlib `unittest`, run as `python3 -I`, Python 3.9-compatible, with no shared module of its own?
Recommended: yes. Why: `tools/roadmap_tests.py` is the precedent, and 3.9.6 is this Mac's python3. `-I` keeps the script's directory off `sys.path`, so sharing would mean loading by path, for a second consumer that does not exist yet.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q31.** (question 10) Run every case under `/bin/bash`, named explicitly, print its version, make the macOS job fail unless it is 3.2, and run one case through the shebang?
Recommended: yes. A variable only the macOS job sets (for instance `SCRIPT_TESTS_REQUIRE_BASH=3.2`) makes a mismatch a failure, not a skip. Why: the shebang picks Homebrew's bash 5 on some Macs, and an image change must not quietly turn the 3.2 job into a 5.x run.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q32.** (question 11) Give each case's process a constructed environment (PATH from the case's bin directory, HOME in the throwaway directory, `GIT_CONFIG_NOSYSTEM=1`, `GIT_CONFIG_GLOBAL` at an empty file, `LC_ALL=C`), with nothing inherited unless a case sets it?
Recommended: yes. Why: `GH_REPO` in a caller's shell, plus a reachable real gh, is the one route from a test to a real merge, and `GIT_DIR` or `CDPATH` change what git and `cd` do.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q33.** (question 12) Make the gh double answer only land.sh's three call shapes, record every call and the body file's bytes, stop every case at `pr merge` with status 1, and fail any other call; and give python3 a double that fails if run?
Recommended: yes, with the doubles written in bash per case, citing gh's manual for the shapes they imitate. Why: the bytes are what expose a wrong path, and a double that answered anything would let a new gh call pass unseen.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q34.** (question 13) Turn off detached housekeeping (`gc.auto=0`, `maintenance.auto=false`) in every throwaway repository, clone and bare origin, through ticket 71's setup if it has landed one?
Recommended: yes. Why: the cases fetch and commit, the operations ticket 71 suspects in the A-05 flake, and 71 was claimed tonight.
Answer: yes, pre-accepted by Adam, 2026-10-15
*Since this grill:* ticket 71 landed in part tonight (`e273366`). Its throwaway repositories set `maintenance.auto=false` alone, and its review showed that `gc.auto=0` alone does not stop git 2.54's background repack.

**Q35.** (question 14) Record the choice as D-3 under "Decided" in `docs/dependencies.md`, and add a Python 3.9 or later row to "Needed on day one"?
Recommended: D-3 in D-1's form, with:
- options bats-core 1.14.0 (MIT), plain bash, and Python's `unittest` (chosen);
- the reason, no new tool;
- bash 3.2.57 as the floor CI holds on macOS, with bash 5 on Linux;
- revisit when the repository goes private ($0.062 a macOS minute) or a script needs a test a subprocess cannot observe;
- a plain statement that CI checks the Python floor for none of the family;
- sources read 16 October 2026.

Plus the row "Python 3.9 or later, 3.9.6 with macOS's developer tools, `xcode-select --install`". Why: the Done-when names the file, and D-3's "no new tool" rests on a python3 the file never mentions.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q36.** (question 15) File two follow-ups in the overnight tracker, each blocked by this one: preflight.sh's test companion and land.sh's post-merge path?
Recommended:
1. preflight.sh's companion: a whole-file copy beside a crafted `aws-project.env`, an `aws` double and no `curl` on PATH. Every parse form 7a364fe, eb39fec and 9ec9aa1 list, compared with `set -a; .`. The nine guard cases 7a364fe lists, and line 116's `CDPATH` idiom. Rows for gh (needed, not optional) and for python3.
2. land.sh after the merge: the fast-forward refusal, the "blocks did not move" exit, and the refresh commit and push, against a bare origin.

Each gets a number only when it is filed. Why: question 2 (Q23 here) keeps them out of this ticket, and an unfiled gap is the one nobody sees.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q37.** (question 16) Cover every refusal above line 99 with a case, add the merge-call and `--help` cases, and close with a case that fails when a `die` above the merge has no case?
Recommended:
- the fifteen `die` lines, with the stale worktree "behind `origin/main`", the prompt answered `n` and given end of input, and the unreadable case failing as root rather than skipping;
- the two exits with no `land:` line (outside a repository, `gh pr view` failing), each asserting no merge;
- `--subject` passed through, and no subject plus `y` reaching a merge without `--subject`;
- a relative body path from a subdirectory reaching gh as the caller's bytes, and the same under `CDPATH` and for `-x.md`;
- a failed merge ending the run;
- `--help` compared with the header read from land.sh at test time;
- the closing completeness case.

Why: land.sh exists to refuse before the merge, and the closing case makes a refusal added without a case fail the companion.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q38.** (question 17) Assert each case's exit status, its `land:` line by a fragment naming the refusal, and what the gh double recorded, and name the mutations in the merge body?
Recommended: yes. Name at least these mutations, each with its count: line 47 reverted to before 2c9962f, one refusal deleted, and the lines of questions 7 and 8 (Q28, Q29 here) as they stood before their fixes. Why: CODING_STANDARDS.md asks a new invariant test to name the mutation that made it fail, and a fragment pins which refusal fired without making every rewording a test edit.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q39.** (question 18) Run the companion in CI as a build-and-test step right after "A-05's two rules hold", and in a new `macos-latest` job with the 3.2 requirement and `permissions: contents: read`, but not in the pre-push hook?
Recommended: yes. The step is named for the property ("tools/land.sh refuses before it merges, and hands gh the body its caller named"), with a comment saying why. The macOS job runs on every push, with no path filter. Why: the Done-when says beside the Python checks, the macOS job is the only 3.2 run, and the hook keeps tests in CI.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q40.** (question 19) Rewrite this ticket's text to match the answers?
Recommended:
- Title: "Test companions for the integrator's shell scripts, starting with tools/land.sh", with the file name unchanged.
- Problem: place land.sh on the landing path and preflight.sh off it. Cite 7a364fe, eb39fec and 9ec9aa1 (eight fixes) instead of "five defects". Say "doubles". Record the defects found while grilling.
- Done-when, replaced by:
  - D-3 and the Python row recorded;
  - `tools/land_tests.py` running land.sh itself through the cases of question 16 and the assertions of question 17 (Q37, Q38 here), and failing when a refusal has no case;
  - line 47 and the prompt fixed in their own commits, after their red cases;
  - CI on Linux and on macOS under bash 3.2;
  - both follow-ups filed, and cross-reference-checks ticket 07 blocked by this one or covered by its cases.

Why: the title names a harness this design does not build, and the Problem miscounts and misgroups.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q41.** (question 20) Add Test companion, Harness and Landed to GLOSSARY.md, as worded under "Proposed glossary / ADR"?
Recommended: yes, in "Work, gates and evidence". Why: "harness" already means two other things here, and landed, part-landed and done are defined only in `docs/roadmap.md`.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q42.** (question 21) Set this ticket's status to ready-for-agent once the answers are applied?
Recommended: yes. Why: it was ready-for-human only for the tool decision, and everything left is mechanical.
Answer: yes, pre-accepted by Adam, 2026-10-15

---

## Ticket 66 — task: none (integrator) — Hold cross-document claims with a check, not with review

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/66-cross-reference-checks-and-land-body.md`

Its whole proposal landed on `origin/main` in `37fdca2`, as cross-reference-checks tickets 01 to 06. These answers resolve it, along with cross-reference-checks ticket 09, and file three tickets: workstream 1's infra stage numbers (Q46), the `aws-rules.md` import (Q47) and the numbered stage comments in `src/Gateway/Program.cs` (Q49). No ADR candidate.

**Q43.** (ticket's question 1) Is a ticket held open by its own scope only? A spec broken into tickets would resolve when every ticket naming it on a `Source:` line is settled. A follow-up raised from its work, review or Out of Scope list would hold nothing open.
Recommended: yes, and tickets get no "parent" relation. Why: an Out of Scope list is what a ticket excludes, so waiting on it contradicts the ticket's own text, and overnight tickets 69 and 70 already point back here.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q44.** (question 2) Are a ticket's only standing claims its `Status:` and `Blocked by:` lines, corrected in place? Everything else would be a dated record and never rewritten: the spec as filed, its Comments, its Answer and its grilling record. A later truth would be appended as a dated Comment. `docs/agents/issue-tracker.md` → Conventions would say so in one line.
Recommended: yes. Why: this ticket's false "remain future work" was a status claim outside its Status line, which no check reads. It outlived both the commit that resolved its six tickets and the merge that landed them.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q45.** (question 3) Should cross-reference-checks ticket 06's unticked third box be ticked as met, keeping its Answer's note, rather than reopened?
Recommended: yes. Why: both prose rules already say why they are prose, and `CODING_STANDARDS.md` cites no ticket anywhere.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q46.** (question 4) Should workstream 1's five numbered stages under `infra/modules/cell` get one new overnight ticket? It would take the next free number, with owner ws1-infra, status `ready-for-agent` and the `Task:` line `Task: none (raised by overnight ticket 66, "Out of Scope")`. Each "stage 1" would become `UsePlatformForwardedHeaders`. It would be done when `git grep -n -i -P '\bstages? \d+\b' -- infra` finds nothing, and when fmt, validate and the module's `terraform test` pass unchanged.
Recommended: yes. Why: they have named the wrong stage since ADR-0018, whose sweep listed only workstreams 2, 4 and 6. This ticket's Out of Scope bullet is their only record.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q47.** (question 5) Should the `@docs/agents/aws-rules.md` import get its own `ready-for-human` overnight ticket, owned by the integrator, rather than be decided while closing this one? The ticket would carry three options:
- keep it, and have `.github/steering.py` follow the import or exempt the file;
- scope it to where AWS work starts;
- drop it.

Recommended: yes. File the question here, and decide it in that ticket. Why: it is your own toolkit setup from 2026-10-15, so a blanket pre-accepted "yes" should not remove it. Every session loads a file the steering check never reads, and the file's two dates would fail that check's "No progress" rule.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q48.** (question 6) Should the fourth cross-reference rule in Further Notes, a check on a deletion obligation, be declined, with the reason in this ticket's Answer?
Recommended: yes. Why: its one instance completed in PR41 and is kept as history. Its trigger is a code change, not a fact decidable from the tree.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q49.** (question 7) Should the ten "Stage N." comment prefixes in `src/Gateway/Program.cs` be removed, through one new `ready-for-agent` integrator ticket? Each comment would keep its relation. The alternative is to name them as an exception to "A stage is named, not numbered".
Recommended: yes. Why: `GLOSSARY.md` says "never 'stage 6'". A comment block moved with its call carries its old number into the new position, which is why ADR-0018's commit had to rewrite two of them by hand.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q50.** (question 8) Should `docs/agents/triage-labels.md` gain a table of the lifecycle states the trackers write: `claimed`, `blocked` and `resolved`? `resolved` would be the one closing status, with `## Answer` saying where the work or the answer is. The table would say `closed` and `partial` are not states. The three `g1a-and-landing-work` tickets that say `closed` would change to `resolved`.
Recommended: yes. Why: that file is where a session is sent for status strings, and 67 of 92 tickets on origin/main carry a status it does not list.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q51.** (question 9) Is overnight ticket 66 resolved now, rather than kept open as the parent of overnight tickets 69 and 70? It would be set to `Status: resolved`, recorded under `## Answer`.
Recommended: yes. Why: everything it proposed landed in `37fdca2`, and the six tickets it was broken into are resolved. After this ticket's questions 1 to 6 (Q43 to Q48 here), every Out of Scope item has a home or is declined.
Answer: yes, pre-accepted by Adam, 2026-10-15
*Also asked by the ticket's `## Comments`* (the collect step's 2026-10-17 line), which this question answers.

**Q52.** (question 10) Should `docs/agents/issue-tracker.md` → Conventions name the two ticket relations the tree already writes, with no "parent" between tickets?
- A `**Source:**` line would mean "part of that spec". The source resolves when every ticket naming it is settled, and its Answer lists them and anything declined.
- "raised by" in a `Task:` line would mean a follow-up, which holds nothing open.

Recommended: yes. Why: otherwise the next session that splits a spec asks cross-reference-checks ticket 09's question again. "Parent" already means a plan row in `tools/roadmap.py`.
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q53.** (question 11) Should ticket 61's Comments line "62 to 65 are taken, so 66 is the next free number" stay as written?
Recommended: yes. Why: it is a dated note and was true when written (this ticket's question 2, Q44 here).
Answer: yes, pre-accepted by Adam, 2026-10-15

**Q54.** (question 12) Should the morning session apply all of this in one integrator tracker commit, with the steering check green before the push? In order, the commit would:
1. file the tickets from this ticket's questions 4, 5 and 7 (Q46, Q47, Q49 here);
2. tick cross-reference-checks ticket 06's box;
3. replace this ticket's "remain future work" sentence with the one in round 3;
4. set `Status: resolved` and add the draft Answer from round 3;
5. resolve cross-reference-checks ticket 09, pointing here;
6. add the lines from this ticket's questions 2, 8 and 10 (Q44, Q50, Q52 here).

Recommended: yes. Why: the Answer cites the new tickets by number, so they have to land in the same push. One commit also leaves no state in which this ticket and the tree disagree.
Answer: yes, pre-accepted by Adam, 2026-10-15

---

## Ticket 27 — task 4.2 — Proxy pipeline: token attach and header stripping

File: `/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/27-4.2-proxy-pipeline.md`

From the ticket's `## Comments`, written by tonight's 4.2 reviewer for the integrator. It does not block row 4.2, which landed complete (`12afb09`). The 6.3c review raised the same gap by reading the code, and the 4.2 review confirmed it by running a test.

**Q55.** (Comments, reviewer, 2026-10-17) Which writer records `auth.session` when a cookie that resolves to no session reaches `/api`? Today nothing does. A verified run gave that cookie 401 with a null rule on `/api`, and `auth.session` on an asset. The options:
- **(a) The token source notes it.** Before returning `Unauthenticated`, the source records the dead session the way `/bff/session` does (`Bff.NoteDeadSession`). This is one line in workstream 3's `AccessTokenSource`. It keeps one writer of the rule and needs no contract change. The source runs inside the stage that ends the request.
- **(b) A new contract outcome.** `AccessTokenResult` gains an outcome for a presented cookie that names no session, and the token step writes `auth.session` for it. A new type can land between gates, as change request 4.2 showed. It needs changes in both workstreams 3 and 4.
- **(c) The token step checks the cookie itself.** It authenticates the app's cookie scheme and writes `auth.session` on a failure. Only workstream 4 changes, but this makes a second copy of workstream 3's dead-session rule.
- **(d) The contract exempts `/api`.** `contracts/telemetry.md` and `IRequestTelemetryFeature.Rule` exempt `/api`. No code changes, but `security.rejections` stops counting dead cookies on API calls.

Recommended: (a), as a workstream 3 ticket. Pair it with a composed test that sends the same dead cookie to an asset and to `/api`, the pair this review ran. The rule and its only writer then stay in one place.
Answer: (a), resolved during PR #47 review on 2026-10-17. `AccessTokenSource` calls the existing `Bff.NoteDeadSession` recorder when authentication fails. Composed regressions distinguish removed and malformed cookies (`auth.session`) from no cookie (no rejection rule). The fresh independent review treated the existing telemetry contract as binding; no contract exemption was introduced.

---

## Carried, already answered

No grill tonight says "carried from an earlier night", so none contributes here. These tickets hold `## Questions for Adam` sections from earlier nights. Each was answered yes in the file named, which records the answers (the tickets themselves carry no Answer lines), and none is asked again:

- Ticket 01 — I-6 — Close G1b: fold G0 findings in and tag contracts-v1 (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/01-I-6-close-g1b.md`): `questions-2026-10-14-answered.md` and `questions-2026-10-15-answered.md`. Now resolved.
- Ticket 03 — 1.2 — modules/cell network (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/03-1.2-cell-network.md`): `questions-2026-10-14-answered.md`, `questions-2026-10-15-answered.md` and `questions.md` (Q13–Q23). Tonight's 1.2 carried them out (`820adf5`), all but questions 6 and 11, which are the integrator's (see the report's Needs you).
- Ticket 17 — 3.0 — G0: the Cognito items (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/17-3.0-g0-cognito.md`): `questions-2026-10-14-answered.md` and `questions-2026-10-15-answered.md`. Now resolved.
- Ticket 43 — CR-5.5 — Change request 5.5: TestApps no longer mirrors app1.json (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/43-CR-5.5-testapps-mirror.md`): `questions-2026-10-14-answered.md`. Now resolved.
- Ticket 44 — CR-1.1 — Change request 1.1: record the `hashicorp/random` provider in `docs/dependencies.md` (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/44-CR-1.1-random-provider.md`): `questions-2.md`.
- Ticket 45 — CR-2.1 — Change request 2.1: JsonSchema.Net now runs in the gateway, and the gateway image cannot see `contracts/` (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/45-CR-2.1-registry-schemas.md`): `questions-2.md`.
- Ticket 46 — CR-6.4 — Change request 6.4: a request feature for `rule` and `session_ref`, and the instrument names (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/46-CR-6.4-request-telemetry.md`): `questions-2.md`.
- Ticket 57 — CR-1.5 — Change request 1.5: who the teardown role is, and two files `.gitignore` does not cover (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/57-CR-1.5-teardown-role-and-ignored-tfvars.md`): `questions-2026-10-14-answered.md` and `questions-2026-10-15-answered.md`. Now resolved.
- Ticket 58 — 1.4a — The cell's ECS resources, written and planned (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/58-1.4a-cell-ecs-terraform.md`): `questions-2026-10-15-answered.md`.
- Ticket 59 — 6.2a — The functional rows that need no session (F-06, F-11) (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/59-6.2a-functional-rows-no-session.md`): `questions-2026-10-15-answered.md`.
- Ticket 60 — 6.3a — The negative rows that need no session (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/60-6.3a-negative-rows-no-session.md`): `questions-2026-10-15-answered.md`, and `questions.md` Q30.
- Ticket 65 — CR-1.5a — Change request 1.5a: the bucket name in two frozen examples, and Object Lock in ADR-0009 (`/Users/adammedford/Projects/spa-platform/.scratch/overnight/issues/65-CR-1.5a-bucket-name-and-object-lock.md`): `questions.md` (Q1–Q12). Tonight's 1.5 carried out its workstream 1 half (`3773d0a`). Q21 above is new and is not one of them.

`questions.md` also holds Q24–Q30. That night's results raised them, under tickets 26, 27, 49, 22, 24, 61 and 60, and all were answered yes. Tonight's 3.9, 4.2 and 6.2 carried out Q24 and Q25.
