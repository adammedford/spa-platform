# Coding standards

Read during review, not during implementation. `/code-review`'s Standards axis loads this file and
holds the diff to it.

Everything here is a **judgement call**: something no check could decide. A mechanical rule belongs
in a check instead, where it runs on every push rather than when a reviewer remembers it. The repo's
checks are `.githooks/pre-push`, `.github/workflows/ci.yml` and `tests/Gateway.ArchitectureTests`;
`AGENTS.md` holds the rules an implementation agent needs in context.

## A comment says why, and names what it answers to

The repository is full of decisions that look arbitrary until you know the finding behind them.
`ResponseCondition.Always`, a token step in the pipeline rather than a transform, `MapInboundClaims`
off, no clock skew on a listener: each is one line of code and one evidence id.

So a comment that restates the code earns nothing, and a comment that names its reason earns its
keep twice, because the next reader is deciding whether to change that line. Cite the plan row, the
ADR, the evidence id or the test id. The surrounding code is the standard for how much: match the
density of the file you are in, which in `src/Platform.Abstractions` and `fixtures/TestIdp` is high.

## An assertion must be able to fail

A test that cannot fail and a system that is correct look identical from the outside, and the test
costs more than nothing: it reports confidence it has not earned.

Two shapes recur here, and both passed review once before anyone noticed.

- **A rule asserted only against a tree that already satisfies it.** `tests/Gateway.ArchitectureTests`
  answers this by making each rule a function over a graph and feeding it a graph that breaks it. A
  reviewer should ask, of any invariant test, what the failing input is and whether anything supplies
  it.
- **A check that is vacuous today.** A-04 restricts a package `Platform.Identity` does not yet
  reference, so the rule passes whether or not the code that reads project files works at all. The
  answer is a second assertion that the mechanism works, against something that does exercise it.
  The hazard is the same whenever a rule's subject has not been written yet.

## A guard reads the thing it guards

A test that repeats the value it is protecting protects nothing: edit the source and the copy still
agrees with itself. The mock API's default ports are checked by reading `apis[].upstream` out of
`fixtures/apps/app1.json`, not by asserting `5080` beside a comment about `app1.json`.

The same applies to a fixture that must agree with a contract, or with another fixture: read the
other file.

## Code that rests on vendor behaviour cites the vendor

A bucket policy, a token-validation parameter or a library's default is load-bearing and invisible.
When behaviour depends on one, name the document that says so, in the code.

The cost of leaving it out is not a lost argument: a reviewer who cannot find
`s3:ObjectCreationOperation` reasonably concludes the statement never fires, and the failure mode if
they were right is a bucket that looks compliant and fails P-04 at a gate. One URL in a comment
retires the whole exchange.

## A reviewer verifies before reporting

The two worst defects found in task 5.3 were found by running the thing: a listener logging query
strings, and a claim that was always null. Reading the diff would not have found either.

So, for a finding about **runtime behaviour**, run it and quote what happened. For a finding about a
**vendor API**, check the vendor's own documentation before calling the code wrong; the same review
that found those two defects also reported a condition key as non-existent, and it exists.

Report what was checked and how, so the author can tell a verified finding from a suspicion. Both
are worth reporting. Only one of them is worth acting on without looking.
