# Change request issue-358: the deployed continuity rows in the test id register

- **Raised by:** workstream 6, Verification and operations, during #358 (the deployed continuity
  class that #348's rows 3 to 9 run through)
- **Tasks:** #358; #348 (G6's deployed run); #332 (the `continuity` spec); the register decided on
  #322; #402 Q7 and Q8 as amended by #439 Q8, and #442 Q158 and Q159, which route these ids through
  a request
- **Date:** 2026-10-28
- **Status:** open

## What changed

#358 adds `tests/e2e/Deployed/ContinuityTests.cs`: one test per row of #348's "What to run", rows 3
to 9, each a driver that sends the row's requests on the two-task cell and an observer that judges
them from the gateway's CloudWatch lines (`tests/e2e/Stack/ContinuityObserver.cs`). The observer's
rules are proved against recordings in `tests/e2e/Deployed/ContinuityObserverTests.cs` and
`ContinuityRunTests.cs`, which run in CI; the row tests report skipped there, naming
`SPA_E2E_STACK`, and run in a sitting with `SPA_E2E_STACK=deployed`, one row per invocation. The
ticket's last box asks for the rows' ids through this route: a file under `contracts/changes/` until
#436 merges, which it has not.

## What is asked of the integrator

Number each row below in `contracts/test-ids.md` under the register's rules (next free ids as the
register stood on 2026-10-28: F-12 and N-28), with Tier `D`, Criterion None, and Recorded by "change
request issue-358 (deployed continuity rows); #358". The prefix is this request's recommendation:
an F row is what the cell keeps doing for a user across tasks, an N row is a refusal a stale value
meets. The integrator decides the prefix and may merge rows. Row 7 is not listed: it is #212's F-07
run on the two-task cell (#348 Q31), already F-07.

| #348 row | Prefix | Tier | Expect | Criterion | Test |
| --- | --- | --- | --- | --- | --- |
| 3 | F | D | A session created on one task is decrypted and used on the other: two session requests of one attempt, each placed by its client trace id, answer 200 on different task ids, within 10 attempts and 15 minutes | None | `ContinuityTests.Row3_a_session_created_on_one_task_is_decrypted_and_used_on_the_other` |
| 4 | F | D | After `aws ecs stop-task` on the task the tunnel does not use, the session answers 200 on the survivor and then on the replacement task; a connection error in the gap is missing coverage | None | `ContinuityTests.Row4_after_stop_task_on_the_other_task_the_session_continues_on_the_survivor_and_the_replacement` |
| 5 | F | D | A login's start and callback, and an antiforgery token's issue and the POST carrying it, each land on different task ids and succeed | None | `ContinuityTests.Row5_login_state_and_the_antiforgery_token_cross_tasks` |
| 6 | F | D | Through `deploy.sh` on the same commit: no observed call gets a 401 or a 5xx, one session's single Refreshed line falls inside the replacement interval, and a login started on an old task completes on a new one; both image digests recorded | None | `ContinuityTests.Row6_a_rolling_deployment_refreshes_once_inside_the_interval_with_no_401_and_no_5xx` |
| 8 | F | D | Fifty parallel calls on a due token answer 200 from both task streams, with exactly one Refreshed line for the session after the 90 s wait; no grant count is claimed | None | `ContinuityTests.Row8_fifty_calls_on_a_due_token_across_two_tasks_refresh_once` |
| 9 | N | D | The pre-logout cookie, replayed after a logout on one task, is refused 401 `unauthenticated` by the other, with the same replay just before logout answering 200 as the control | None | `ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other` |

Each test lives in `tests/e2e/Deployed/ContinuityTests.cs` (class
`Gateway.E2ETests.Deployed.ContinuityTests`), and a sitting runs one with
`dotnet test --project tests/e2e -- --filter-method "Gateway.E2ETests.Deployed.ContinuityTests.Row3_*"`.

## What #358 did without it

Everything else. The rows run under the names above, which carry #348's row number until the
register numbers them.

## Why this is a change request and not an edit

`contracts/` is the integrator's (`AGENTS.md`, `OWNERSHIP.md`), and the register's own rule is that
"the integrator adds it by deciding a request under `contracts/changes/`, never the workstream that
needs it" (`contracts/test-ids.md`, "How an id is added").
