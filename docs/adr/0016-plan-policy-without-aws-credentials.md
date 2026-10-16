# ADR-0016: Assert plan policy with mock providers, and keep AWS credentials out of CI

- **Status:** Accepted
- **Date:** 2026-10-15
- **Deciders:** integrator
- **Plan ref:** `docs/plan/03-workstreams-and-tasks.md` → task 1.0 ("the read-only role CI assumes to
  run plans") and task 1.9 ("P-01 to P-03 in CI"); `docs/plan/01-plan.md` → assumption A7.
  `docs/plan/` is a frozen export, so this ADR is where those three read differently from now on.

## Context

Task 1.0's bootstrap root was applied on 15 October 2026. The state bucket and the CI plan policy
were created; the CI plan **role** was not, because the apply failed creating the GitHub Actions
OIDC provider the role's trust needs:

```
Error: creating IAM OIDC Provider: AccessDenied: User: arn:aws:sts::160102056803:assumed-role/
AccountFullAccessRole/... is not authorized to perform: iam:CreateOpenIDConnectProvider ... with an
explicit deny in a service control policy: arn:aws:organizations::215472107744:policy/
o-ruixwwk14j/service_control_policy/p-1d03w2pr
```

The same policy denies `iam:ListOpenIDConnectProviders`, so the provider can neither be created nor
found. **This is not a permission the account can grant itself.** A service control policy overrides
every IAM policy in a member account and binds every principal in it, including an administrator who
assumes a role into it. The organization's management account is exempt from service control
policies, but an OIDC provider must be created in the account that will use it, so the exemption does
not help. Enabling it means amending `p-1d03w2pr`, detaching it, or moving the account to an
organizational unit it is not attached to — all work in account `215472107744`, by someone who is not
this project.

That turns a plan assumption into a question: the plan has CI assume a read-only role to run
`terraform plan`, and asserts P-01 to P-03 on the result. Without the provider, GitHub Actions has no
way to assume any role in this account.

**Reading the three tests settles it.** P-01 is an allowlist of resource types and counts in the
platform plan. P-02 is the resource delta when a definition is added to the apps root. P-03 is the
task role's statements: this cell's bucket prefixes and client-secret parameter path, the four
`ssmmessages` permissions, and nothing else. None of the three asserts anything about the live
account. All three are properties of the configuration, and a plan against a mock provider produces
them.

Task 1.4a demonstrated exactly that on 15 October 2026: 28 plan-time assertions in
`infra/modules/cell/tests/module.tftest.hcl` and the platform root's own test file, against
`mock_provider "aws" {}`, with no credentials and no network beyond `terraform init`. Thirty
mutations were caught, including a bucket prefix widened by one segment, a parameter path widened to
every cell, a fifth `ssmmessages` action, and a fourth statement on the task role that the assertions
did not recognise. The last of those is P-03's own failure mode.

CI already runs these: the workflow step "Test every root and module with tests under `infra/`"
discovers every `*.tftest.hcl` and runs it on the pinned Terraform 1.11.0.

## Decision

**P-01 to P-03 are asserted with mock providers in `terraform test`, and CI holds no AWS
credentials.**

1. **Task 1.9 writes `*.tftest.hcl` assertions, not a credentialed plan.** The allowlist, the two
   security groups and the task role are asserted against a mock provider, in the root or module
   whose configuration they describe. `tools/scripts/plan-policy.sh` is not written, and the
   workflow's placeholder "Plan policy (P-01 to P-03)" step is removed by the same task rather than
   filled in.
2. **P-02 is a comparison of two plans, not of two accounts:** the apps root planned with the
   fixture definitions, and again with one more, asserting the delta is one Cognito client, one
   branding style and one parameter.
3. **`vars.AWS_PLAN_ROLE_ARN` is left unset, and no AWS credential is added to the repository.** CI
   builds, tests, validates and plans against mocks. Nothing in CI reaches AWS.
4. **The GitHub Actions OIDC provider is not created, and `create_github_oidc_provider` stays
   false.** `github_repository` is empty, which drops the provider resource and the whole
   `sts:AssumeRoleWithWebIdentity` statement.
5. **The CI plan role still exists, trusted by the account** (`additional_trusted_principal_arns`),
   so a human or another runner inside the account can assume it to run a read-only plan. Its name
   and its read-only policy are unchanged. What changed is who assumes it and when: a person
   investigating, not a workflow on every push.
6. **The three checks that genuinely need AWS stay where they were.** P-04 (release immutability),
   P-05 (teardown) and P-06 (network reachability) are deployed-tier checks run at a gate by whoever
   holds credentials, exactly as the Tests tab already marks them. This decision does not touch them.

## Alternatives rejected

| Option | Why not |
| --- | --- |
| Get the service control policy amended | The right answer if CI must reach AWS, and the wrong cost for what it buys. It is a change in another account, by another team, to a guardrail that exists on purpose — and once P-01 to P-03 run against mocks, nothing in CI needs it. Revisit if a later gate needs CI to read the account. |
| An OIDC provider in a shared account, with cross-account role chaining | Works, and adds a second account's trust configuration to a proof of concept that has one. The same guardrail may well apply there. |
| CodeBuild with a service role, or a self-hosted runner with an instance role | Both avoid the provider and both move CI off GitHub Actions for this one step, which buys a credentialed plan nobody needs. |
| Long-lived IAM access keys as GitHub secrets | A static credential with read access to the account, to replace a federated one. This is what OIDC exists to avoid, and the assumption behind the project's "tokens stay on the server" posture argues against it everywhere else. |
| Assert P-01 to P-03 on a real plan run locally, recorded as evidence | A check that runs where someone remembers to run it is the thing CI exists to replace. It is also what the gate evidence already does for P-04 to P-06, which is why those three are the ones left there. |
| Drop P-01 to P-03 | They are the only automated statement of what the infrastructure may contain, and the task role assertion is the one guard on the most security-sensitive object in the cell. |

## Consequences

- **A mock-provider assertion proves the configuration, never the account.** It catches a widened
  grant, an unlisted resource type and a changed count at the moment someone writes them, which is
  what P-01 to P-03 are for. It cannot catch drift: a resource changed in the console is invisible to
  it. Nothing in Phase 1 claimed otherwise — P-05's teardown query and P-06's reachability test are
  the account-level checks, and they remain.
- **CI needs no AWS account, which makes it reproducible by anyone who clones the repository.** A
  contributor with no credentials can run everything CI runs. That is a gain this decision did not
  set out to buy.
- **Task 1.0's "the read-only role CI assumes to run plans" is now "the read-only role a person or
  another runner assumes to run plans".** The role, its name and its policy are unchanged.
- **Assumption A7 narrows.** It lists three kinds of run that touch the AWS account: every Terraform
  apply, G0, and the deployed test runs. CI is not a fourth, and was never meant to be, but the plan
  role implied it might be.
- **Task 1.9's shape changes before it starts.** Its needs (1.2 to 1.5, 1.8) are unchanged, and its
  done-when — "it fails on an unlisted resource type, a widened rule or a new permission" — is
  unchanged. Only the mechanism moves, from a credentialed plan to a mock-provider test, and task
  1.4a is the worked example to follow.
- **The bootstrap root keeps a variable nobody sets.** `create_github_oidc_provider`,
  `github_repository` and `github_subject_claims` stay, unused, for an account without this
  guardrail. They cost nothing and a second cell may need them.
- **If a later gate does need CI to read the account**, the route is the service control policy, and
  this ADR is the record of why it was not taken first.

## Evidence

- The apply that failed, and the policy that denied it: the error quoted above, account
  `160102056803`, 15 October 2026. `iam:ListOpenIDConnectProviders` is denied by the same policy,
  observed in a second account in the same organization.
- Service control policies cannot be overridden by an identity-based policy in a member account, and
  do not apply to the management account:
  [AWS Organizations User Guide, service control
  policies](https://docs.aws.amazon.com/organizations/latest/userguide/orgs_manage_policies_scps.html),
  read 15 October 2026.
- Plan-time assertions against a mock provider, with no credentials: task 1.4a's merge `04c7489`,
  `infra/modules/cell/tests/module.tftest.hcl`, 28 assertions and 30 caught mutations including the
  task-role widenings P-03 describes.
- CI already discovers and runs every `*.tftest.hcl` under `infra/`: the workflow step "Test every
  root and module with tests under `infra/`".
- The three tests' own wording, which is what makes them configuration properties:
  `docs/plan/04-tests.md`, rows P-01, P-02 and P-03.
