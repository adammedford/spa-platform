# Test id register

The test ids named after the 11 October 2026 export, each with its tier and the decision that named
it, the tiers of the export's rows that carry none, and the checks still owed an id.

The export's own ids, F-01 to F-11, N-01 to N-27, P-01 to P-06 and A-01 to A-05, are listed in
[`docs/plan/04-tests.md`](../docs/plan/04-tests.md) and are not copied here. Evidence ids (`S-`,
`R-`, `T-`, `C-`) and rule ids are not test ids and are not listed here either.

## Why this file is here

Decided on #322. The export is frozen and cannot take a new row, so an id named after it needs a
live home, and one register under `contracts/` is the one place a session looks. The rejected
alternative was a convention that the ADR owning each check records its id, which is where P-07
first lived (ADR-0016 decision 7). This file never freezes.

**How an id is added.** The integrator adds it by landing a decided change request
(`docs/agents/tracker-policy.md` → "Change requests"), never the workstream that needs it. A row
lands between gates as prose, under [`README.md`](README.md)'s rule, except that a row whose check
waits for a gate (one needing a new rule id, say) lands in that gate's integrator commit.

**Numbering.** A new id continues its prefix's sequence: F-24, N-33, P-08 and A-06 are next. A number
is given only when the row's change request is decided, is never reused and never takes a letter
suffix. A dropped check keeps its row, marked retired, so old merge bodies, tags and review records
stay true.

**Row shape.** The export's columns for the prefix, plus Tier (`H` hermetic, `D` deployed) on every
table and a Recorded by column naming the ADR, change request or confirmed answer that named the id.

## Functional (F)

| ID | Test | Expect | Tier | Criterion | Recorded by |
| --- | --- | --- | --- | --- | --- |
| F-12 | Two-host harness: `tests/Gateway.IntegrationTests/Continuity/TwoHostHarnessTests.cs`: `P2_5_two_hosts_start_over_the_shared_doubles_and_the_real_ring_and_each_readyz_names_its_own_instance`, `P2_5_the_stand_in_load_balancer_alternates_between_the_hosts` | Two gateway hosts start over one bucket, one secret source, one session store, one lease table and the real `AddPlatformHosting` ring over one Parameter Store stub, which sits under the ring's own shield as a client registered first would; each `/readyz` names its own deterministic instance; the stand-in balancer alternates between them | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-13 | P2-2 across hosts, session cookie, matrix row F2 (#332): `tests/Gateway.SecurityTests/Continuity/AcrossHostsTests.cs`: `P2_2_F2_a_session_cookie_issued_by_A_is_accepted_by_B_and_by_a_fresh_third_host` | A session cookie issued by A is accepted by B and by a third host started afterwards | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-14 | P2-2 across hosts, login state, matrix row F2 (#332): `tests/Gateway.SecurityTests/Continuity/AcrossHostsTests.cs`: `P2_2_F2_a_login_started_on_A_finishes_on_B_and_on_a_fresh_third_host` | A login started on A finishes on B, and on a third host started afterwards, on the app's shell | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-15 | P2-2 across hosts, antiforgery token, matrix row F2 (#332): `tests/Gateway.SecurityTests/Continuity/AcrossHostsTests.cs`: `P2_2_F2_an_antiforgery_token_issued_by_A_is_accepted_by_B_and_by_a_fresh_third_host` | An antiforgery token issued by A is accepted by B and by a third host started afterwards; the call reaches the upstream | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-16 | Convergence and the wait, matrix row F8 (#332): `tests/Gateway.IntegrationTests/Continuity/ConvergenceTests.cs`: `P2_5_F8_readyz_shows_the_skew_and_spactl_wait_returns_only_after_B_is_polled_and_a_shorter_wait_times_out_naming_B` | Onboard app 3 and poll A only: A's `/readyz` says 2 and B's 1; `spactl wait --instances host-a,host-b --revision 2 --timeout 3` exits 1 naming B (and not A) at revision 1; the same wait with time to spare does not return while B is behind and exits 0 once B is polled, naming both at 2 | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-17 | Cross-host login after convergence, with D3's control, matrix row F8 (#332): `tests/Gateway.IntegrationTests/Continuity/ConvergenceTests.cs`: `P2_5_F8_a_login_to_the_onboarded_app_started_on_A_ends_404_on_a_lagging_B_and_finishes_on_B_after_convergence` | A login to app 3 started on A and called back on B still at 1 ends 404 `not_found`; after B is polled the same journey lands on app 3's shell | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-18 | F17 composed, matrix row F17 (#332): `tests/Gateway.IntegrationTests/Continuity/StoreOutageTests.cs`: `P2_5_F17_with_the_session_store_failing_a_session_request_ends_500_no_cookie_is_cleared_and_health_still_answers` | With the session store failing every call, the shell and `/bff/session` end 500 on both hosts with no session cookie cleared, `/healthz` and `/readyz` still answer 200 on both, and the session is back once the store is | H | None | #538 (moved from change request P2-5, continuity rows); #345 |
| F-19 | `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row3_a_session_created_on_one_task_is_decrypted_and_used_on_the_other` | A session created on one task is decrypted and used on the other: two session requests of one attempt, each placed by its client trace id, answer 200 on different task ids, within 10 attempts and 15 minutes | D | None | change request #539 (deployed continuity rows); #358 |
| F-20 | `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row4_after_stop_task_on_the_other_task_the_session_continues_on_the_survivor_and_the_replacement` | After `aws ecs stop-task` on the task the tunnel does not use, the session answers 200 on the survivor and then on the replacement task; a connection error in the gap is missing coverage | D | None | change request #539 (deployed continuity rows); #358 |
| F-21 | `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row5_login_state_and_the_antiforgery_token_cross_tasks` | A login's start and callback, and an antiforgery token's issue and the POST carrying it, each land on different task ids and succeed | D | None | change request #539 (deployed continuity rows); #358 |
| F-22 | `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row6_a_rolling_deployment_refreshes_once_inside_the_interval_with_no_401_and_no_5xx` | Through `deploy.sh` on the same commit: no observed call gets a 401 or a 5xx, one session's single Refreshed line falls inside the replacement interval, and a login started on an old task completes on a new one; both image digests recorded | D | None | change request #539 (deployed continuity rows); #358 |
| F-23 | `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row8_fifty_calls_on_a_due_token_across_two_tasks_refresh_once` | Fifty parallel calls on a due token answer 200 from both task streams, with exactly one Refreshed line for the session after the 90 s wait; no grant count is claimed | D | None | change request #539 (deployed continuity rows); #358 |

## Negative security (N)

| ID | Attempt | Expect | Tier | Recorded by |
| --- | --- | --- | --- | --- |
| N-28 | P2-2 across hosts, the control, matrix row F2 (#332): `tests/Gateway.SecurityTests/Continuity/AcrossHostsTests.cs`: `P2_2_F2_control_over_two_parameter_store_stubs_B_refuses_all_three` | Over one Parameter Store stub per host, B refuses all three: the session 401 `unauthenticated`, the token pair 403 `csrf_failed` under `csrf.token` with nothing upstream, the callback the 400 failure page under `auth.callback` with no session cookie | H | #538 (moved from change request P2-5, continuity rows); #345 |
| N-29 | Rotation, the purge reaching the shared store, matrix row F7 (#332): `tests/Gateway.IntegrationTests/Continuity/RotationTests.cs`: `P2_5_F7_a_rotation_polled_on_A_only_refuses_the_old_session_on_A_and_through_the_shared_store_on_B` | An app rotated onto a new client with A polled and B not: the old session is refused (401 `unauthenticated`) on A and on B, which is still at the old revision, because A's purge emptied the store both read | H | #538 (moved from change request P2-5, continuity rows); #345 |
| N-30 | Rotation, the purge failing, matrix row F7 (#332): `tests/Gateway.IntegrationTests/Continuity/RotationTests.cs`: `P2_5_F7_with_the_purge_failing_A_refuses_the_old_session_at_once_and_B_serves_it_until_polled` | With the purge failing, A refuses the old session at once (the ticket store's client binding) and B serves it (200) until it is polled, then refuses it | H | #538 (moved from change request P2-5, continuity rows); #345 |
| N-31 | Rotation with a login in flight, matrix row F9 (#332): `tests/Gateway.IntegrationTests/Continuity/RotationTests.cs`: `P2_5_F9_a_login_started_under_the_old_client_and_called_back_after_convergence_is_refused_and_a_fresh_login_succeeds` | A login started under the old client and called back after convergence ends on the 400 failure page with no session cookie; a fresh login succeeds under the new client | H | #538 (moved from change request P2-5, continuity rows); #345 |
| N-32 | Replay the pre-logout cookie on the other task; `tests/e2e/Deployed/ContinuityTests.cs`: `ContinuityTests.Row9_a_session_ended_on_one_task_is_refused_by_the_other` | The pre-logout cookie, replayed after a logout on one task, is refused 401 `unauthenticated` by the other, with the same replay just before logout answering 200 as the control | D | change request #539 (deployed continuity rows); #358 |

## Infrastructure and architecture properties (P)

| ID | Check | Expect | Tier | Recorded by |
| --- | --- | --- | --- | --- |
| P-07 | Identity plan allowlist | One user pool, one domain, the resource servers and test users `fixtures/cognito/pool.json` lists with a generated password and a parameter per user, and no app client | H | ADR-0016 decision 7; Q45, 2026-10-15 |

P-07, like P-01 to P-03, is a mock-provider assertion in `terraform test`
(`infra/envs/poc/idp/tests/root.tftest.hcl`), run by CI's Terraform test step with no AWS
credential. This row is the one to edit from now on; ADR-0016 keeps decision 7 as written.

## Architecture reference rules (A)

| ID | Check | Expect | Tier | Recorded by |
| --- | --- | --- | --- | --- |

None yet.

## Tiers of the export's untiered rows

The export's P and A rows carry no Tier column; their tiers are in the paragraph under its
infrastructure table. This table gives them a row a reader can use. It is supplemental metadata,
not a second definition: an export id it tiers is not a duplicate, and its Check and Expect stay in
the export.

| Id | Tier | Recorded by |
| --- | --- | --- |
| P-01 to P-03 | H | `docs/plan/04-tests.md`, the paragraph under the infrastructure table |
| P-04 to P-06 | D | `docs/plan/04-tests.md`, the paragraph under the infrastructure table |
| A-01 to A-05 | H | `docs/plan/04-tests.md`, the paragraph under the infrastructure table |

## Owed

Checks a decided request names that are still owed an id. No number is reserved: the integrator
numbers, tiers and moves each into its table in the commit the request names.

| Check | Owed by | Numbered in |
| --- | --- | --- |
| Functional: a logout token ends the subject's sessions on its app | `changes/p2-6-pingfederate-profile.md`, item 7's decision | G7's integrator commit |
| Negative: each refused logout token is a 400 under `auth.backchannel`, including a token missing `iat`, one missing `jti` and one whose `iat` is in the future | `changes/p2-6-pingfederate-profile.md`, item 7's decision | G7's integrator commit |
