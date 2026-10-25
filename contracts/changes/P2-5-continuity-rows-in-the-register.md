# Change request P2-5: the continuity rows of the two-host harness in the test id register

- **Raised by:** workstream 6, Verification and operations, during #345 (continuity 7/13, the carrier
  of plan row P2-5)
- **Tasks:** P2-5; #345; #332 (the `continuity` spec, T7); the register decided on #322
- **Date:** 2026-10-25
- **Status:** open

## What changed

#345 builds the two-host composed harness (`tests/Gateway.TestHarness`: `GatewayHost`,
`GatewayHarnessOptions.TwoHosts`, `KeyRingSharing`, `ParameterStoreStub`, `SwitchableSessionStore`,
`GatewayHarness.StartHostAsync`, `RotateClientAsync`, `CreateLoadBalancer`) and the composed rows
below. The ticket says "the new rows' ids and tiers belong in `contracts/test-ids.md` (#322), an
integrator file; this ticket writes a change request under `contracts/changes/` listing each row's
id, tier and test, and does not edit the register." Until the integrator numbers them, each test's
name carries the plan row and the matrix row it proves (#332, "Test ids").

## What is asked of the integrator

Number each row below in `contracts/test-ids.md` under the register's rules (next free ids: F-12 and
N-28 as the register stood on 2026-10-25), with Tier `H` and Recorded by "change request P2-5
(continuity rows); #345". The table's prefix is this request's recommendation: functional rows are
what the cell does for a user or an operator, negative rows are refusals an attacker or a stale
value meets. The integrator decides the prefix and may merge rows.

| Row | Prefix | Tier | Expect | Test |
| --- | --- | --- | --- | --- |
| Two-host harness | F | H | Two gateway hosts start over one bucket, one secret source, one session store, one lease table and the real `AddPlatformHosting` ring over one Parameter Store stub, which sits under the ring's own shield as a client registered first would; each `/readyz` names its own deterministic instance; the stand-in balancer alternates between them | `tests/Gateway.IntegrationTests/Continuity/TwoHostHarnessTests.cs`: `P2_5_two_hosts_start_over_the_shared_doubles_and_the_real_ring_and_each_readyz_names_its_own_instance`, `P2_5_the_stand_in_load_balancer_alternates_between_the_hosts` |
| P2-2 across hosts, session cookie (F2) | F | H | A session cookie issued by A is accepted by B and by a third host started afterwards | `tests/Gateway.SecurityTests/Continuity/AcrossHostsTests.cs`: `P2_2_F2_a_session_cookie_issued_by_A_is_accepted_by_B_and_by_a_fresh_third_host` |
| P2-2 across hosts, login state (F2) | F | H | A login started on A finishes on B, and on a third host started afterwards, on the app's shell | same file: `P2_2_F2_a_login_started_on_A_finishes_on_B_and_on_a_fresh_third_host` |
| P2-2 across hosts, antiforgery token (F2) | F | H | An antiforgery token issued by A is accepted by B and by a third host started afterwards; the call reaches the upstream | same file: `P2_2_F2_an_antiforgery_token_issued_by_A_is_accepted_by_B_and_by_a_fresh_third_host` |
| P2-2 across hosts, the control | N | H | Over one Parameter Store stub per host, B refuses all three: the session 401 `unauthenticated`, the token pair 403 `csrf_failed` under `csrf.token` with nothing upstream, the callback the 400 failure page under `auth.callback` with no session cookie | same file: `P2_2_F2_control_over_two_parameter_store_stubs_B_refuses_all_three` |
| Convergence and the wait (F8) | F | H | Onboard app 3 and poll A only: A's `/readyz` says 2 and B's 1; `spactl wait --instances host-a,host-b --revision 2 --timeout 3` exits 1 naming B (and not A) at revision 1; the same wait with time to spare does not return while B is behind and exits 0 once B is polled, naming both at 2 | `tests/Gateway.IntegrationTests/Continuity/ConvergenceTests.cs`: `P2_5_F8_readyz_shows_the_skew_and_spactl_wait_returns_only_after_B_is_polled_and_a_shorter_wait_times_out_naming_B` |
| Cross-host login after convergence, with D3's control (F8) | F | H | A login to app 3 started on A and called back on B still at 1 ends 404 `not_found`; after B is polled the same journey lands on app 3's shell | same file: `P2_5_F8_a_login_to_the_onboarded_app_started_on_A_ends_404_on_a_lagging_B_and_finishes_on_B_after_convergence` |
| Rotation, the purge reaching the shared store (F7) | N | H | An app rotated onto a new client with A polled and B not: the old session is refused (401 `unauthenticated`) on A and on B, which is still at the old revision, because A's purge emptied the store both read | `tests/Gateway.IntegrationTests/Continuity/RotationTests.cs`: `P2_5_F7_a_rotation_polled_on_A_only_refuses_the_old_session_on_A_and_through_the_shared_store_on_B` |
| Rotation, the purge failing (F7) | N | H | With the purge failing, A refuses the old session at once (the ticket store's client binding) and B serves it (200) until it is polled, then refuses it | same file: `P2_5_F7_with_the_purge_failing_A_refuses_the_old_session_at_once_and_B_serves_it_until_polled` |
| Rotation with a login in flight (F9) | N | H | A login started under the old client and called back after convergence ends on the 400 failure page with no session cookie; a fresh login succeeds under the new client | same file: `P2_5_F9_a_login_started_under_the_old_client_and_called_back_after_convergence_is_refused_and_a_fresh_login_succeeds` |
| F17 composed | F | H | With the session store failing every call, the shell and `/bff/session` end 500 on both hosts with no session cookie cleared, `/healthz` and `/readyz` still answer 200 on both, and the session is back once the store is | `tests/Gateway.IntegrationTests/Continuity/StoreOutageTests.cs`: `P2_5_F17_with_the_session_store_failing_a_session_request_ends_500_no_cookie_is_cleared_and_health_still_answers` |

## What #345 did without it

Everything else. The rows run in CI under the names above, which carry plan row and matrix row as
#332 asks until the register numbers them.

## Why this is a change request and not an edit

`contracts/` is the integrator's (`AGENTS.md`, `OWNERSHIP.md`), and the register's own rule is that
"the integrator adds it by deciding a request under `contracts/changes/`, never the workstream that
needs it" (`contracts/test-ids.md`, "How an id is added").
