# ADR-0016: Assert plan policy with mock providers, and keep AWS credentials out of CI

- **Status:** Accepted; rationale corrected after the AWS profile mismatch reported in PR35
- **Date:** 2026-10-15
- **Deciders:** integrator
- **Plan ref:** `docs/plan/03-workstreams-and-tasks.md` → task 1.0 ("the read-only role CI assumes to
  run plans") and task 1.9 ("P-01 to P-03 in CI"); `docs/plan/01-plan.md` → assumption A7.
  `docs/plan/` is a frozen export; this ADR records the changed CI mechanism.

## Context

The initial rationale for this decision treated a failed bootstrap apply as proof that the intended
project prohibited GitHub's IAM OIDC provider. That inference was wrong. PR35 reports that the apply
used the default profile's different project and Region, rather than the intended `spa-platform`
profile. An SCP refusal in that other project establishes no restriction in the intended one. No
fresh AWS query or apply was made for this review, and this ADR claims no current account state.

The CI choice stands on the properties being checked, independently of that failed apply. P-01 is
an allowlist of resource types and counts in the platform configuration. P-02 is the resource delta
when a definition is added to the apps root. P-03 limits task-role statements to the cell's bucket
prefixes, client-secret parameter path and four `ssmmessages` permissions. A mock-provider plan can
exercise those configuration properties without account credentials; it cannot establish drift,
effective IAM permission or deployed service behaviour.

The existing example is `infra/modules/cell/tests/module.tftest.hcl`, with the platform root's own
tests. CI discovers `*.tftest.hcl` files and runs `terraform test` on pinned Terraform 1.11.0. Review
of task 1.4a also widened that example's mutations to cover the actual permission attachments and
trust conditions. The test files and their individual mutation records are the evidence; an old
aggregate assertion count is not a claim that all of P-01 to P-03 is complete.

## Decision

**P-01 to P-03 use mock-provider assertions in `terraform test`; CI holds no AWS credentials.**

1. **Task 1.9 writes `*.tftest.hcl` assertions rather than a credentialed plan.** It proves the
   resource allowlist, security-group rules and task-role restrictions in their owning roots or
   modules. The workflow's placeholder step is removed when those checks are implemented;
   `tools/scripts/plan-policy.sh` is not required. Until then the existing plan-generation guard
   remains in force; this decision does not bypass it or claim those outstanding tests pass.
2. **P-02 compares configuration plans.** Plan the apps root with its fixture definitions and then
   one additional definition, asserting the exact added resources and unchanged existing ones.
   The comparison must inspect all managed resources, including branding resources where configured.
3. **No AWS credential is added to the repository or CI.** `vars.AWS_PLAN_ROLE_ARN` stays unset.
   CI builds, tests and validates; Terraform tests use mocks. Downloads during init remain necessary.
4. **CI needs no GitHub OIDC provider.** For this CI setup, `create_github_oidc_provider` stays false
   and `github_repository` empty. This is a project choice, not a claim that the intended project's
   organization forbids creating a provider. A future credentialed pipeline needs a new decision.
5. **The bootstrap configuration retains the plan role and its explicit additional trust list.**
   After a successful apply, an allowlisted principal can assume it to investigate a read-only
   plan. Its configured name and policy are unchanged. This ADR does not assert that the role was
   successfully created or that its effective permissions were verified.
6. **Account-level checks stay deployed.** P-04 (release immutability), P-05 (teardown) and P-06
   (network reachability) still run at their gates with credentials. Mock-provider assertions do
   not replace those checks or establish successful provisioning.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Give GitHub Actions an OIDC role for P-01 to P-03 | Technically separate from the profile error, but adds account access for properties that mocks can assert. Revisit if a future CI requirement needs real account state. |
| CodeBuild or a self-hosted runner with an instance role | Moves the same account access into another runner without improving these configuration assertions. |
| Long-lived access keys in GitHub secrets | Adds persistent credentials for checks that need none. |
| Run the configuration checks only in an operator's local plan | Makes a repeatable change-time guard depend on someone remembering it. |
| Drop P-01 to P-03 | Removes the automated statement of what resources, rules and task-role permissions the configuration may contain. |

## Consequences

- **Task 1.9's mechanism changes; its completion bar does not.** It must still fail on an unlisted
  resource type, widened rule or new permission. Its dependency list remains unchanged, and no
  mock test is counted as complete without a failing mutation and a restored passing run.
- **Task 1.0's plan role is for an authorized person or runner, rather than CI on every push.**
  Its configured policy remains available; deployed existence and effective permission need their
  own account evidence.
- **A mock-provider assertion proves configuration, not the account.** Drift, provisioning errors,
  effective policy and real service behaviour require the deployed-tier evidence the plan assigns.
- **The profile mistake needs its own guard.** PR35 records the project/profile expectation and
  asks workstream 1 to apply it in its recipes. Choosing mock CI does not correct a local operator's
  profile or Region and must not be presented as that fix.
- **If CI later needs AWS**, revisit this decision, the plan-generation secrecy guard and the
  intended project's actual permissions. The wrong-project SCP error is not evidence for that work.

## Evidence

- The profile diagnosis and bounded follow-up: [PR35](https://github.com/adammedford/spa-platform/pull/35).
  It reports that the default and intended profiles selected different projects and Regions.
- The properties: `docs/plan/04-tests.md`, P-01, P-02 and P-03.
- Existing mock-provider configuration assertions: `infra/modules/cell/tests/module.tftest.hcl`
  and `infra/platform/tests/platform.tftest.hcl`; CI's discovery step in `.github/workflows/ci.yml`.
- [Terraform test mocking](https://developer.hashicorp.com/terraform/language/tests/mocking):
  mocks supply provider data for tests and do not contact the real provider API.
