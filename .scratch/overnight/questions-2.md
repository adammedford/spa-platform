# Questions for Adam, overnight 2026-10-14 (second batch)

Every question from the "## Questions for Adam" sections of tickets 44 to 46, the paper grillings of the three change requests that exist only on the integration branch (`overnight/2026-10-13`). Numbered continuously from `questions.md`, which ends at Q55. A `Qn` inside a question's text is a question in `questions.md` (Q1 to Q55) or in this file; "question n of this ticket" is the ticket's internal numbering. The file's numbers are the `### Q` headings. Ordered so the questions that unblock the most work come first. Each recommendation was adopted provisionally during the paper grilling, and nothing is decided until you answer. Write `yes`, `no` or your own answer after "Answer: yes".

**Against Q1 to Q55:** no recommendation here contradicts an answered question. Three build on one: Q61 (CR-2.1's row) applies Q45 and Q52 to the same cell of `docs/dependencies.md`. Q63 makes Q34's schema change landable at I-6, because A-05 and the embedded schema copies otherwise stop it landing in one push. Q80 (CR-6.4's freeze status) relies on Q27 and Q34.

## Ticket 45: CR-2.1, Change request 2.1: JsonSchema.Net in the gateway, and the gateway image cannot see `contracts/`

File: `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/issues/45-CR-2.1-registry-schemas.md`

_Unblocks the most: questions 2 and 8 of this ticket are prerequisites for I-6 landing green once Q34 holds, and its answers settle D-1 and two follow-ups for workstreams 2 and 4._

### Q56

Close D-1 with JsonSchema.Net 8.0.5 running in the gateway through `Platform.Registry`, and reject D-1's proposed narrowing of task 2.1?

Recommended: Yes. Why: `contracts/README.md` makes one shared library the guarantee that the gateway and `spactl` cannot disagree, and the CSP and pattern rules exist only in the schema.

Answer: yes

### Q57

Add `COPY contracts/*.schema.json contracts/` to `src/Gateway/Dockerfile`'s build stage, so workstream 2 links the contract schemas and deletes its copies and `SchemaCopyTests`?

Recommended: Yes. Why: Q34 changes the schema bytes at I-6, and while the copies exist A-05 stops the integrator refreshing them in the same push.

Answer: yes

### Q58

Record `integer-out-of-range` as a row in `contracts/README.md` -> "Rules that JSON Schema cannot express", rather than adding a `maximum` to the frozen schema?

Recommended: Yes. Why: prose needs no gate, and a `maximum` narrows a G1a contract.

Answer: yes

### Q59

Treat the validator's `timeoutSeconds` bound (`TimeSpan.MaxValue`) as wrong until proven, and have workstreams 2 and 4 establish by test the largest value the proxy accepts?

Recommended: Yes. Why: YARP applies the timeout through `CancelAfter`, which .NET documents as throwing above `Int32.MaxValue` milliseconds (about 24.8 days). That is far below the validator's 922 billion seconds. This was read, not run.

Answer: yes

### Q60

Close the request in Q46's form?

Recommended: Yes. Why: same precedent, and no interface changes.

Answer: yes

### Q61

Make the `JsonSchema.Net` row read "Schema tests, `Fixtures.Tests`, `Platform.Registry` (the gateway and `spactl`)", in the same edit as CR-5.5's Q45 and Q52?

Recommended: Yes. Why: those are the three direct references, and `spactl` reaches the engine only through `Platform.Registry`.

Answer: yes

### Q62

Move D-1 to decided in `docs/dependencies.md` with the trigger "8.x stops receiving a fix we need", and write no ADR?

Recommended: Yes. Why: the one-file seam makes the choice cheap to reverse.

Answer: yes

### Q63

Make the Dockerfile line and workstream 2's follow-up prerequisites of I-6, added to ticket 01's "Blocked by"?

Recommended: Yes. Why: it is what lets Q34's schema change land in one push with CI green. This is a dependency on Q34, not a conflict with it.

Answer: yes

### Q64

Once the ceiling is proven, have the validator refuse above it under `integer-out-of-range`, with the schema left unbounded?

Recommended: Yes. Why: the limit belongs to the runtime, and putting it in the schema would turn a runtime upgrade into a contract change.

Answer: yes

### Q65

Amend the README's "`timeoutSeconds` has no upper bound" bullet to point at the new row?

Recommended: Yes. Why: otherwise the README contradicts itself.

Answer: yes

### Q66

Fold the stale `RegistryRules` comment ("Not a README row") into workstream 2's Dockerfile follow-up?

Recommended: Yes. Why: it is one line in the same project, and it goes stale in the same decision.

Answer: yes

### Q67

Confirm that task 2.1 is done once this request is decided, with the follow-ups as new tickets?

Recommended: Yes. Why: 2.1's done-when is met, and the follow-ups come from later integrator decisions.

Answer: yes

## Ticket 46: CR-6.4, Change request 6.4: a request feature for `rule` and `session_ref`, and the instrument names

File: `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/issues/46-CR-6.4-request-telemetry.md`

_Unblocks the rest of task 6.4, and one-line follow-ups in six landed stages (2.3, 2.4, 4.1, 4.3, 4.4, 4.6)._

### Q68

Accept a typed `IRequestTelemetryFeature` in `Platform.Abstractions`, rather than an `HttpContext.Items` key or `Activity` tags?

Recommended: Yes. Why: it follows `IAppFeature`, the idiom stage 4 already uses. An `Items` key is an untyped contract, and Activity tags would leak `session_ref` into traces.

Answer: yes

### Q69

Land it now, between gates, because it adds a type and changes no existing stage, owner or signature, rather than waiting for `contracts-v1`?

Recommended: Yes. Why: the fields are already required by the G1a telemetry contract, and waiting would hold 6.4 and six landed stages behind G0.

Answer: yes

### Q70

Have stage 2 count `security.rejections` from the feature's `Rule`, on its own `Platform.Telemetry` meter, tagged `rule` and `app_id`, so no rejecting stage needs a meter?

Recommended: Yes. Why: one owner keeps the count equal to the logged rejections. Otherwise six stages in three projects would each export their own stream.

Answer: yes

### Q71

Accept the `Platform.<Project>` naming rule for meters and activity sources, created through `IMeterFactory`, with the request's example changed from `Platform.Security` to `Platform.Registry`?

Recommended: Yes. Why: it records what `AddPlatformTelemetry` already subscribes to, and under Q70 the request's example would be wrong.

Answer: yes

### Q72

Accept a static `session_ref` helper in `Platform.Abstractions`?

Recommended: Yes. Why: the formula is in a G1a contract, and one implementation keeps every writer and test in agreement.

Answer: yes

### Q73

Close the request in Q46's form?

Recommended: Yes. Why: same precedent.

Answer: yes

### Q74

Should stages write the feature null-conditionally, and `Platform.Testing` gain a plain implementation for component tests?

Recommended: Yes. Why: stage tests run without stage 2, and each owner must be able to assert the rule it set.

Answer: yes

### Q75

Is `Rule` set only by the stage that ends the request, with no write-once enforcement?

Recommended: Yes. Why: a rejection ends the pipeline, and a throwing setter would turn a logging slip into a 500.

Answer: yes

### Q76

Define the helper as `SessionRef.Of(key)`: SHA-256 of the key's UTF-8 bytes, lowercase hex, first 12 characters?

Recommended: Yes. Why: the contract fixes neither encoding nor case, and two writers must agree.

Answer: yes

### Q77

Name workstream 3 as the writer of `SessionRef`, wherever it holds the key and the request, correcting the request's "3.4 and 3.9"?

Recommended: Yes. Why: 3.4's store has no request, and where to write it is workstream 3's design.

Answer: yes

### Q78

File follow-ups for the six landed stages that reject (2.3, 2.4, 4.1, 4.3, 4.4, 4.6), and add notes to workstream 3's tickets 3.3, 3.5 and 3.9?

Recommended: Yes. Why: that list comes from the code. The request leaves out 2.4 and 4.6 and names 4.5, which never rejects.

Answer: yes

### Q79

Keep each stage's existing Warning line for its rejection?

Recommended: Yes. Why: those lines carry detail the request line does not, and tests assert on them.

Answer: yes

### Q80

Freeze the feature as soon as it lands, outside the G1b draft set, with no `[Freeze("G1b")]` marker?

Recommended: Yes. Why: the `session_ref` formula is in a G1a contract, and under Q34 the draft set empties at G1b anyway.

Answer: yes

### Q81

Write the between-gates rule into `contracts/README.md`: prose and new types that change no existing stage, owner or signature land on the integrator's decision, and anything else waits for a gate?

Recommended: Yes. Why: I-7 stated half of it, this request uses the other half, and the README is where a reader of a frozen file looks.

Answer: yes

### Q82

Leave `Rule` null on 4.2's 401 when no token can be had, with no new rule id?

Recommended: Yes. Why: it reports the session's state rather than a broken rule, and a new id would change a G1a contract.

Answer: yes

### Q83

Have 2.3 set `host.unknown` on the feature only, with no Warning line of its own?

Recommended: Yes. Why: a Warning line for each unknown host would let anyone flood the log, and the request line already records it.

Answer: yes

### Q84

Adopt "rejection" as the glossary term (avoid "refusal" in contracts) and define "rule id"?

Recommended: Yes. Why: the contracts and `security.rejections` already say "rejection", and no file defines "rule id".

Answer: yes

### Q85

Is task 6.4 done once stage 2 sets, logs and counts from the feature, with `RequestLineTests` covering it, without waiting for the follow-ups?

Recommended: Yes. Why: 6.4 owns stage 2, and 6.3 proves the composed behaviour.

Answer: yes

## Ticket 44: CR-1.1, Change request 1.1: record the `hashicorp/random` provider in `docs/dependencies.md`

File: `/Users/adammedford/Documents/Projects/spa-platform/.scratch/overnight/issues/44-CR-1.1-random-provider.md`

_Unblocks nothing else; it clears the integrator's only item against task 1.1._

### Q86

Add the `hashicorp/random` row as proposed (3.9.1, `~> 3.7`, MPL-2.0, task 1.1's test-user passwords)?

Recommended: Yes. Why: both lock files pin it and both `versions.tf` files require it.

Answer: yes

### Q87

Accept a managed `random_password`, which keeps the password in state, over the AWS provider's generators?

Recommended: Yes. Why: `terraform providers schema` confirms that `aws_cognito_user.password` is not write-only in 6.68.0, so the password is in state whatever generates it.

Answer: yes

### Q88

Accept `~> 3.7` as written, without asking workstream 1 to justify the floor?

Recommended: Yes. Why: the lock files pin 3.9.1. The reason for 3.7 is unverified, so the Decision should not invent one.

Answer: yes

### Q89

Keep the licence in the Note, and add "MPL-2.0" to the `hashicorp/aws` row too?

Recommended: Yes. Why: the Terraform table has no Licence column, and both cached provider licence files read MPL-2.0.

Answer: yes

### Q90

Close the request as I-7 and CR-5.5 (Q46) were closed, with the row added in the same commit?

Recommended: Yes. Why: it is a table edit with no interface change.

Answer: yes

### Q91

Date the row in its own Note, leave the file's 11 October header alone, and add the registry page to Sources?

Recommended: Yes. Why: moving the header would claim that every row was rechecked.

Answer: yes

### Q92

Leave the SSM parameter on `value` rather than asking for `value_wo`?

Recommended: Yes. Why: the password stays in state through `random_password` and the Cognito user either way.

Answer: yes

### Q93

Confirm that this request is all the integrator owes task 1.1, and that applying 1.1 stays with workstream 1?

Recommended: Yes. Why: 1.8 has no `random` in its lock file, and nothing else in 1.1 touches an integrator path.

Answer: yes
