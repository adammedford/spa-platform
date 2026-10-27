# Coding standards

Read during review, not during implementation. `/code-review`'s Standards axis loads this file and
holds the diff to it.

`docs/agents/review.md` holds the rest of what a review needs here: how to read a branch without
touching the shared tree, which files each axis is held to, and what makes a finding worth
reporting.

Everything here is a **judgement call**: something no check could decide. A mechanical rule belongs
in a check instead, where it runs on every push rather than when a reviewer remembers it. The repo's
checks are `tools/check.sh` (which `.githooks/pre-push` runs), `.github/workflows/ci.yml` and
`tests/Gateway.ArchitectureTests`; `AGENTS.md` holds the rules an implementation agent needs in
context.

## A comment says why, and names what it answers to

The repository is full of decisions that look arbitrary until you know the finding behind them.
`ResponseCondition.Always`, a token step in the pipeline rather than a transform, `MapInboundClaims`
off, no clock skew on a listener: each is one line of code and one evidence id.

So a comment that restates the code earns nothing, and a comment that names its reason earns its
keep twice, because the next reader is deciding whether to change that line. Cite the plan row, the
ADR, the evidence id or the test id. The surrounding code is the standard for how much: match the
density of the file you are in, which in `src/Platform.Abstractions` and `fixtures/TestIdp` is high.

A comment cites a row for why the code is this way, never for what a row has still to do. "Stage 2
does not install this yet; that is the remainder of task 6.4" was true for one afternoon, then sat
in an integrator file that the task which made it false was not allowed to edit. What a row still
owes goes in its merge body's `Outstanding:` line, where `docs/agents/landing-work.md` puts it.

## An assertion must be able to fail

A test that cannot fail and a system that is correct look identical from the outside, and the test
costs more than nothing: it reports confidence it has not earned.

Three shapes recur here.

- **A rule asserted only against a tree that already satisfies it.** `tests/Gateway.ArchitectureTests`
  answers this by making each rule a function over a graph and feeding it a graph that breaks it. A
  reviewer should ask, of any invariant test, what the failing input is and whether anything supplies
  it.
- **A check that is vacuous today.** A-04 restricts a package `Platform.Identity` does not yet
  reference, so the rule passes whether or not the code that reads project files works at all. The
  answer is a second assertion that the mechanism works, against something that does exercise it.
  The hazard is the same whenever a rule's subject has not been written yet.
- **A test that skips on the machine it runs on.** A file flag, an operating system or a missing tool
  turns the test into a pass that checked nothing, on exactly the machine where someone is reading
  the result. Make the test independent of the condition; if a tool is genuinely required, fail and
  name it.

**A new invariant test names the mutation that made it fail.** Break the thing it guards on purpose,
watch it go red, revert, and put the mutation and its count in the merge body: "swapping the two ids
failed 4 of 153". It costs one run and it is the only evidence that separates a guard from a
decoration — a reviewer can then check the mutation instead of re-deriving the hazard. The nine
pull requests reviewed on 15 October 2026 split cleanly on this: the three that stated a mutation
reproduced exactly when the reviewer re-ran it, and each of the five that stated none carried an
assertion that could not fail, including a Terraform suite that stayed green with the attribute
N-09 depends on deleted.

## A check fails closed

A check that cannot read its input, or exits early, reports a failure, with its verdict line.

A check is read by its verdict: the pre-push hook's exit status, the last line of
`tools/check.sh --full`'s log, a test row's result. A path that ends without judging anything and
still reads as a pass, or that ends with no verdict at all, says nothing about the code while
looking as if it did, and the reader has no line above the verdict to tell them otherwise.

The reviews of 21 October 2026 caught three checks that failed open, each on the reviewer's own
judgement:

- **A failed listing read as an empty one.** #205's file-mode check fed `git ls-files` through
  `xargs` and `find` behind an outer `|| true`, so a git or xargs failure passed with nothing
  judged. A failure of the listing now fails the check (db61e99).
- **A stop with no verdict line.** #210's `tools/image-check.sh` could end on a bare `set -e` exit,
  from a failed copy, image inspect or `docker run`, and later from the container-log dump under
  `pipefail`, so docker's error was the last line where a reader looks for the verdict. Every
  failure now ends through `fail()` on a verdict (66b2e9a, ba9dac1).
- **A clean-up that replaced the row's own failure.** #212's F-07 offboarded in a `finally`, so a
  failing clean-up threw over the row's failure and the reader saw only the clean-up's. It now
  reports both (f234abc).

So ask of any check, and of any test's setup and clean-up: what happens when the input is missing,
unreadable or empty, and when a step before the verdict fails? "It passes" and "it stops" are both
failing open.

## A guard reads the thing it guards

A test that repeats the value it is protecting protects nothing: edit the source and the copy still
agrees with itself. The mock API's default ports are checked by reading `apis[].upstream` out of
`fixtures/apps/app1.json`, not by asserting `5080` beside a comment about `app1.json`.

The same applies to a fixture that must agree with a contract, or with another fixture: read the
other file.

## An authorisation is read from its author

A comment, a label or a file that unlocks an action counts only when the account the rule names
wrote it. A `Confirm` comment is the human's act (`docs/agents/tracker-policy.md`), so code that
matches its first word and not its author lets anyone who can comment on the repository take an
action out of its dry run. #271's morning script read every comment beginning `Confirm`, and an
outsider's `Confirm Q1` dispatched a commit applier (fixed in 58f5858).

So ask of any code that acts on what a person wrote: who may write it, and whether the code checks
that writer everywhere the writing is used, in what it confirms, what it overrides and what it
switches on.

## A reuse key covers every input the reused phase reads

A phase whose saved result is reused while its key is unchanged is only as fresh as the key. A key
built from the launch arguments, or from the inputs a test happened to change, reuses a stale result
the first time another input moves. #271's retro script took two review rounds on this: the first
fix fingerprinted the log files and missed the lane worktrees and ticket comments, and the second
reads the record on every launch and reuses the later phases only on an unchanged index (59673e4).
Rank's and File's keys had also left out the index both read.

So ask of any cache or resume key: list what the phase reads, from its prompt or its code, and check
that each item moves the key. Where listing them costs as much as the phase, run the phase again and
key what follows on its result.

## Code that rests on vendor behaviour cites the vendor

A bucket policy, a token-validation parameter or a library's default is load-bearing and invisible.
When behaviour depends on one, name the document that says so, in the code.

The cost of leaving it out is not a lost argument: a reviewer who cannot find
`s3:ObjectCreationOperation` reasonably concludes the statement never fires, and the failure mode if
they were right is a bucket that looks compliant and fails P-04 at a gate. One URL in a comment
retires the whole exchange.

## A stage is named, not numbered

Outside `contracts/pipeline.md`, a stage is its entry point: `UsePlatformRequestGate`, not "stage 6".
The number is the row's position in that one table, and a position moves when the order does: it
did at G1b, when ADR-0018 put `UsePlatformTelemetry` first and every "stage 1" and "stage 2" in the
tree started naming the other stage. A number in a comment, a test name or a document is a copy of
the table, and a copy is wrong one reorder after it is written.

The relation is still worth stating — "runs after host resolution, which pins the app" — and the
name carries it. A reviewer reads a bare number as a copy to be replaced.

## A universal names its exceptions by the writer

A sentence stating a universal — every rejection reaches `security.rejections` — names each
exception by the code that writes it, never by an identifier that write happens to carry. An
identifier may have several writers and only some of them be excepted, so a qualifier scoped to the
identifier excepts writes nobody meant to, and still reads as true to anyone who checks it against
the one writer they had in mind.

A false universal in the contracts cost four review rounds on 16 October 2026 for exactly that
reason: each fix scoped the exception to the id the excepted write carried rather than to the stage
that wrote it, and that id had a second writer the sentence never meant to cover. Whether a scope is
the thing that actually has the property is a judgement about whether two files agree, which no
check decides; the syntactic half of this family — a cell that disagrees with the file it indexes —
belongs in a check instead.

## A reviewer verifies before reporting

The two worst defects found in task 5.3 were found by running the thing: a listener logging query
strings, and a claim that was always null. Reading the diff would not have found either.

So, for a finding about **runtime behaviour**, run it and quote what happened. For a finding about a
**vendor API**, check the vendor's own documentation before calling the code wrong; the same review
that found those two defects also reported a condition key as non-existent, and it exists.

Report what was checked and how, so the author can tell a verified finding from a suspicion. Both
are worth reporting. Only one of them is worth acting on without looking.
