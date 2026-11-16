# GitHub's OIDC federation under the managed IAM experience: the provider, the roles, the trust conditions and environments' required reviewers

- **Ticket:** #1222, "GitHub's OIDC federation under the managed IAM experience: the provider, the
  roles, the trust conditions and environments' required reviewers" (`wayfinder:research`), on the
  map #1220 (P2-7 Corporate integration).
- **Date:** 2026-11-16. **Branch:** `research/github-oidc-under-managed-iam`, pushed and never merged,
  as the map rules.
- **Method:** docs-only. Repository reading at commit `adf0273` (origin/main on 2026-11-16):
  `docs/agents/aws-rules.md`, ADR-0016, ADR-0029 (its Context and Evidence), `infra/bootstrap`
  (`ci-role.tf`, `variables.tf`, the GitHub locals of `main.tf`, `terraform.tfvars.example`),
  `.github/workflows/ci.yml` (header, permissions, the Terraform job), `tools/aws-project.env` (for
  the Region name alone, `us-east-2`), and pull request 35's body. The brief named
  `infra/envs/poc/bootstrap`; the bootstrap root is `infra/bootstrap`. The repository's GitHub
  settings were **read** with `gh api` (section 8 lists each call); none was changed. Primary AWS and
  GitHub pages were fetched on 2026-11-16 and are listed in section 8 with what each gave. Two public
  AWS documents were downloaded over plain HTTPS with no credential and parsed with `python3 -I`: the
  IAM service reference (action names) and the CodeBuild price list for `us-east-2`. **Not done:** no
  `aws` CLI call, no `terraform plan` or `apply`, no credential step, no read of the cell, no change
  to any GitHub setting, no workflow run. The `aws-mcp` connector failed to connect. Pages were
  fetched through a tool that returns a model's extraction of the page: text in quotation marks
  below came back as a quotation; where a page came back paraphrased, or did not give a sentence
  asked for, this finding says so and does not quote from memory.

## Answer in brief

1. **The working assumption does not hold as written: the managed policies deny the provider.** The
   managed service control policy for both the Free Tier and the Paid Plan has a statement
   `DenyIAMRestrictedActions` that denies `iam:*Provider*` on every resource, with no condition.
   That pattern covers `iam:CreateOpenIDConnectProvider`, and also `iam:GetOpenIDConnectProvider`
   and `iam:ListOpenIDConnectProviders`, so Terraform can neither create nor read an
   `aws_iam_openid_connect_provider`. IAM's own page says a role for GitHub Actions starts "by
   adding the GitHub OIDC provider to IAM"; GitHub is not one of the providers "already built into
   AWS". No provider, no `sts:AssumeRoleWithWebIdentity` from GitHub (section 1).
2. **The only documented way past that deny is one-way and removes the spend limit.** Activating
   advanced features hands over the organization's policies ("You can modify those policies after
   you activate advanced features"), but "Activation cannot be reversed", and "If you have a spend
   limit for any of your AWS accounts, it is removed ... You cannot create a spend limit if you
   activate advanced features." That is against the map's cost rule, so it is a decision for Adam,
   not a route this ticket can recommend (section 1).
3. **The roles themselves are not denied.** The role statement in the managed policy protects only
   `role/managed/*`; `iam:CreateRole`, `iam:CreatePolicy` and `iam:AttachRolePolicy` elsewhere are
   in neither deny list. `infra/bootstrap` already creates the plan role with the provider switched
   off (ADR-0016 decision 4), trusting named principals. A role whose trust names a provider that
   cannot exist is useless, so this helps only a design with no GitHub federation (section 1).
4. **The repository is public, not private, on a Free personal account.** Read with `gh api` today:
   `visibility: public`, owner type `User`, plan `free`, one collaborator. The ticket's "private
   personal repository" premise is wrong today, and it matters: on Free, required reviewers and
   wait timers are "only available for public repositories", and "Users with GitHub Free plans can
   only configure environments for public repositories." Everything in sections 3 and 4 holds only
   while the repository stays public; a flip to private on Free loses environments altogether
   (section 3).
5. **The bootstrap root's `sub` condition cannot match this repository's tokens**, a second blocker
   independent of the first. GitHub: "repositories created after July 15, 2026 now use an immutable
   default subject format"; this repository was created on 2026-10-13 and the API reports
   `use_immutable_subject: true` with prefix `repo:adammedford@7797565/spa-platform@1416377593`.
   `infra/bootstrap/main.tf` builds `repo:${var.github_repository}:${claim}` under `StringEquals`,
   and `variables.tf` validates `github_repository` with a pattern that refuses `@`. A change
   request for workstream 1 if federation is ever used (section 2).
6. **The claims a trust policy could condition on are richer than the root uses.** AWS now maps
   `repository_id`, `repository_owner_id`, `actor_id`, `ref`, `environment`, `job_workflow_ref`,
   `workflow` and others to condition keys for GitHub, and advises "Use immutable identifiers, not
   names". IAM refuses a GitHub trust policy with no `sub` condition or one that is "solely a
   wildcard" (section 2).
7. **Environments' required reviewers are available today, for free, with no AWS involved, and leave
   a record an API reads back.** A job that references an environment "must follow any protection rules for
   the environment before running or accessing the environment's secrets." The record is
   `GET /repos/{owner}/{repo}/actions/runs/{run_id}/approvals`: `state`, `comment`, `user`,
   `environments`. So the approval step and its evidence are available as a stand-in on their own;
   what is lost without federation is AWS itself refusing an unapproved deployment (section 3).
8. **One account cannot be held to a second reviewer, in either place.** "Pull request authors
   cannot approve their own pull requests", so a required pull-request approval with one account can
   only be bypassed, never satisfied. An environment with self-review prevented and one account has
   nobody who may approve; with it allowed, the approver is the person who started the run. Both
   conclusions are this finding's inference from the quoted sentences. A second reviewer needs a
   second GitHub account; nothing else read supplies one (sections 3 and 4).
9. **Session length is 15 minutes to the role's maximum (1 to 12 hours), one hour by default, and
   its expiry stops nothing already created.** What bounds the bill is the role's permissions
   policy first, then the project's spend limit, which needs the Paid Plan and has a floor of "the
   greater of $20 or a conservative estimate of your likely spend": a backstop well above the map's
   $5 a sitting and $2 a month, not a substitute for it (section 5).
10. **Prices, for `us-east-2`.** The federation path would cost nothing: IAM and STS are "offered at
    no additional charge", and standard GitHub-hosted runners in public repositories are free. So
    the assumption fails on permission, not on price. The one unverified alternative that keeps a
    GitHub job inside the account, a CodeBuild-hosted runner, lists at $0.005 per build minute on
    `build.general1.small` and nothing idle; it is offered in section 6 as a candidate for its own
    ticket, with three open questions, not as a rescue.

## 1. The managed policies: the provider is denied, the roles are not

**The deny.** "Managed policies for your organization"
(<https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html>, read
2026-11-16) gives the Free Tier policy ("This policy cannot be modified. To access additional
services, upgrade your account") and the Paid Plan policy ("This policy cannot be modified. To access
additional services, activate advanced features"). Each contains, identically:

```json
{
  "Sid": "DenyIAMRestrictedActions",
  "Effect": "Deny",
  "Action": [
    "iam:*Alias*",
    "iam:*Organizations*",
    "iam:*Provider*",
    "iam:SetSecurityTokenServicePreferences",
    "iam:*LoginProfile*",
    "iam:CreateGroup",
    "iam:AttachGroupPolicy",
    "iam:AddUserToGroup",
    "iam:PutGroupPolicy",
    "iam:UpdateGroup",
    "iam:UpdateServerCertificate",
    "iam:UpdateSigningCertificate",
    "iam:UploadServerCertificate",
    "iam:UploadSigningCertificate",
    "iam:TagServerCertificate",
    "iam:UntagServerCertificate"
  ],
  "Resource": "*"
}
```

The IAM service reference (<https://servicereference.us-east-1.amazonaws.com/v1/iam/iam.json>,
downloaded 2026-11-16) lists eighteen IAM actions whose name contains `Provider`, among them
`CreateOpenIDConnectProvider`, `GetOpenIDConnectProvider`, `ListOpenIDConnectProviders`,
`TagOpenIDConnectProvider`, `UpdateOpenIDConnectProviderThumbprint`, `DeleteOpenIDConnectProvider`
and the SAML set. All match `iam:*Provider*`. The Terraform resource
`aws_iam_openid_connect_provider.github` in `infra/bootstrap/ci-role.tf` calls the create and get
actions, so an apply with `create_github_oidc_provider = true` is refused under either plan. The
statement carries no condition, so no principal in a project escapes it. SAML federation is denied
by the same pattern. Because the whole answer rests on this statement, the page was also fetched raw
with `curl` and searched: `iam:*Provider*` and `DenyIAMRestrictedActions` each occur twice, once
under `AllowFreeTierServices`'s policy and once under `AllowPaidTierServices`'s.

**The provider is required.** "Create a role for OpenID Connect federation"
(<https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_create_for-idp_oidc.html>, read
2026-11-16): "If you want to create a role for GitHub Actions, you must start by adding the GitHub
OIDC provider to IAM." The providers that need none are named: "If you are using an OIDC IdP from
Google, Facebook, or Amazon Cognito, don't create a separate IAM IdP ... These OIDC identity
providers are already built into AWS". GitHub is not among them. The page was silent on any
pre-provisioned GitHub provider in a project, and the managed-policies page names none.

**Does this apply to the intended project?** ADR-0016's Context says the earlier refused apply ran
against the wrong project and "establishes no restriction in the intended one", and pull request 35
speaks of "organisation guardrails that the *intended* project does not have". The documentation
read here says otherwise for this action: the deny is in the plan policy of every project in the new
AWS experience, and `docs/agents/aws-rules.md` records that the intended project is one. ADR-0016's
decision does not depend on the point and its text is not this ticket's to edit; the next decision
on a credentialed pipeline should not carry the "does not have" sentence forward. Two repository
records bear on whether the managed policies are in force on the intended project: ADR-0029 records
that on 2026-10-20 "the project's managed SCP refused `tag:GetResources` outright" in us-east-1,
and `tag:GetResources` is in the us-east-1 `NotAction` list AWS applies after advanced features and
absent from the list before. That is consistent with advanced features not being active on that
date. **Not verified:** the project's state today (Free or Paid, advanced features, any spend limit,
whether any provider or role exists); no call was made.

**The way out, and its price.** "Activate advanced AWS features"
(<https://docs.aws.amazon.com/accounts/latest/reference/activate-advanced-features.html>, read
2026-11-16):

- "You must upgrade your account before you activate advanced features."
- "You can now configure Resource Control Policies (RCPs) and Service Control Policies (SCPs) to
  enforce custom guardrails in your organization. Previously, AWS managed RCPs and SCPs on your
  behalf. You can modify those policies after you activate advanced features."
- "You can access all AWS services."
- "If you have a spend limit for any of your AWS accounts, it is removed. Any projects that are
  currently paused will be reactivated. You cannot create a spend limit if you activate advanced
  features."
- "Without a spend limit, you will be responsible for all costs incurred by your resources."
- "You can activate advanced features in AWS Settings. Activation cannot be reversed."

The page does not say in so many words that the plan policy holding `DenyIAMRestrictedActions` is
removed on activation. "Remove organization policies"
(<https://docs.aws.amazon.com/accounts/latest/reference/remove-org-policies.html>, read 2026-11-16)
says "Certain AWS managed organizational policies are removed when you activate advanced features"
and gives the steps to edit a policy; it names no statement. That the deny goes, or can be edited
out, after activation is therefore an inference from "You can access all AWS services" and "You can
modify those policies", not a quoted fact.

**The roles.** The policy "for all users in a project" that "cannot be changed" has
`ProtectManagedRoles`: a deny on `iam:CreateRole`, `iam:AttachRolePolicy`, `iam:PutRolePolicy`,
`iam:UpdateAssumeRolePolicy` and eight more, on `arn:*:iam::*:role/managed/*` only. Outside that
path, no statement on the page denies creating a role, a policy or an attachment, and `iam:*` is in
the allowed-service lists of both plans. The cell's two task roles and the bootstrap root's plan
and teardown roles are that case. `iam:PutRolePolicy` on a non-managed role is likewise not denied,
which is the permission "Revoke active sessions" needs (section 5).

**The resource control policy.** The same page's RCP lists `sts:*` in `DenyAnyoneOutsideMyOrgAndAWS`,
conditioned on `"Null": {"aws:PrincipalARN": "false"}`, `aws:PrincipalIsAWSService` false if it
exists, and `aws:PrincipalOrgID` unequal to the resource's organization if it exists. The STS API
page says "Calling `AssumeRoleWithWebIdentity` does not require the use of AWS security
credentials", and the global condition keys page says of `aws:PrincipalArn`: "This key is included
in the request context for all signed requests. Anonymous requests do not include this key." Read
together, a web-identity call carries no `aws:PrincipalArn` and the RCP statement would not match
it. No page read states that for `AssumeRoleWithWebIdentity` by name, so it is **open**, and moot
while the provider is denied.

**Region.** `iam:*` and `sts:*` are in the us-east-1 `NotAction` list, so the global IAM endpoint
is reachable from a `us-east-2` project. Nothing in the Region statements bears on this question.

## 2. The token's claims and the trust conditions

**Subject forms.** "OpenID Connect reference"
(<https://docs.github.com/en/actions/reference/security/oidc>, read 2026-11-16) gives:

| Trigger | `sub` |
| --- | --- |
| A job that references an environment | `repo:ORG-NAME/REPO-NAME:environment:ENVIRONMENT-NAME` |
| A pull request event, no environment | `repo:ORG-NAME/REPO-NAME:pull_request` |
| A branch | `repo:ORG-NAME/REPO-NAME:ref:refs/heads/BRANCH-NAME` |
| A tag | `repo:ORG-NAME/REPO-NAME:ref:refs/tags/TAG-NAME` |

"Any `:` within the metadata values will be replaced with `%3A` in the subject claim." The
environment form replaces the ref: a job that names an environment carries no branch in its `sub`.

**The immutable form, which is this repository's.** The same page: syntax
`repo:OWNER@OWNER-ID/REPO@REPO-ID:ref:refs/heads/BRANCH`; "repositories created after July 15, 2026
now use an immutable default subject format"; "Repositories created before July 15, 2026 keep the
previous format unless you opt in to immutable subject claims"; "Repository renames and transfers
after July 15, 2026 also move to the immutable subject format." Read from this repository today:

```text
$ gh api repos/adammedford/spa-platform --jq '{id,created_at,owner_id:.owner.id}'
{"created_at":"2026-10-13T02:58:15Z","id":1416377593,"owner_id":7797565}
$ gh api repos/adammedford/spa-platform/actions/oidc/customization/sub
{"use_default":true,"use_immutable_subject":true,"sub_claim_prefix":"repo:adammedford@7797565/spa-platform@1416377593"}
```

So a branch token from this repository would carry
`repo:adammedford@7797565/spa-platform@1416377593:ref:refs/heads/main`. The bootstrap root
(`infra/bootstrap/main.tf`) computes
`github_subjects = [for claim in var.github_subject_claims : "repo:${var.github_repository}:${claim}"]`
and compares with `StringEquals`; `variables.tf` validates `github_repository` against
`^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$`, which refuses the `@`. With `github_repository =
"adammedford/spa-platform"` the condition would never be true. No token was minted to observe this;
it follows from the API read and the documented format. The root's comment "One provider per
account is allowed" and its default `create_github_oidc_provider = true` are also now at odds with
section 1.

**`aud`.** GitHub's page: "By default, this is the URL of the repository owner". The AWS role page
has the role wizard set "For **Audience**, choose `sts.amazonaws.com`", the value the bootstrap
root's `client_id_list` and trust condition use. The audience is requested by the workflow step that
fetches the token; the action that does so (`aws-actions/configure-aws-credentials`) was not read.

**Other claims AWS can condition on.** "IAM and AWS STS condition context keys"
(<https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_iam-condition-keys.html>, read
2026-11-16; the last 3,425 characters of the page were not read) has a GitHub tab mapping these
claims to keys under `token.actions.githubusercontent.com:`: `actor`, `actor_id`,
`job_workflow_ref`, `repository`, `repository_id`, `repository_owner_id`, `workflow`, `ref`,
`environment`, `enterprise_id`; none is available in the session. `sub` and `aud` are in the
Default tab. Its guidance: "**Use immutable identifiers, not names** On GitHub, repository,
organization, and user names can change. A name that is freed by renaming or deletion can be claimed
by a different account." Its example policy conditions on `repository_owner_id`, `repository_id`,
`actor_id` and `ref` with `StringEquals`. On `job_workflow_ref`: "This key contains the reference
path to the reusable workflow for jobs using a reusable workflow. Use this to restrict access to
specific workflows and to make sure only approved workflows can assume roles." On `environment`:
"If the environment claim is included in your trust policy, an environment must be configured and
provided in the GitHub workflow."

A conflict between the two vendors: GitHub's "Secure use reference"
(<https://docs.github.com/en/actions/reference/security/secure-use>, read 2026-11-16) came back with
"Support for custom claims for OIDC is unavailable in AWS." The AWS page above lists the keys. AWS
owns the AWS behaviour; the GitHub sentence reads as stale.

**What IAM enforces.** The AWS role page: "When GitHub's OIDC IdP is the trusted Principal for your
role, IAM checks the role trust policy condition to verify that the condition key
`token.actions.githubusercontent.com:sub` is present and that its value is not solely a wildcard
character (\* and ?) or null. IAM performs this check when the trust policy is created or updated."
And: "If you do not limit the condition key `token.actions.githubusercontent.com:sub` to a specific
organization or repository, then GitHub Actions from organizations or repositories outside of your
control are able to assume roles associated with the GitHub IAM IdP in your AWS account."

**A pull request from a fork.** "Workflow syntax"
(<https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax>, read
2026-11-16, the first 200,000 of 237,191 characters): the `id-token` permission is "Fetch an OpenID
Connect (OIDC) token. This requires `id-token: write`"; for a pull request event other than
`pull_request_target` from a forked repository, "the permissions are adjusted to change any write
permissions to read only", unless the repository setting to send write tokens to fork pull requests
is on; and under `pull_request_target` the token "is granted read/write repository permission, even
when it is triggered from a public fork." The pages read do not say by name that a fork's pull
request cannot fetch an OIDC token; that it cannot is an inference from the downgrade of every write
permission. This repository reads `default_workflow_permissions: read`, fork approval policy
`first_time_contributors`, and its CI declares `permissions: contents: read` with no `id-token`
anywhere. Because the repository is public, anyone can open a fork pull request, so this inference
carries more weight here than on a private one and would want a test before a role trusted
`pull_request`.

**The known ways a loose `sub` is abused.** From the pages read, with the last two as this finding's
reasoning from the formats:

- No `sub` condition, or an owner-wide one: any repository on GitHub, or any under the owner, can
  assume the role (the AWS "Important" note above). IAM now refuses the first at policy write.
- `repo:OWNER/REPO:*`: every branch, tag, pull request and environment of the repository, including
  a branch whose workflow file the pusher wrote. AWS's own example of it is labelled as granting
  "access to any branch within the repository".
- A name-based `sub`: a renamed or deleted owner or repository name "can be claimed by a different
  account" (AWS). GitHub's immutable format and AWS's `repository_id` key are the answer.
- `pull_request` as a subject (the bootstrap root's default lists it): the token says only that the
  event was a pull request, not which branch or author, and a pull request can change the workflow
  that runs. Bounded here by one account holding push access and by the fork downgrade above.
- An `environment` subject with no deployment branch rule: the `sub` has no ref, so any branch that
  names the environment gets it. AWS: "we strongly recommend adding protection rules to the
  environment ... Use deployment branches and tags to restrict which branches and tags can deploy to
  the environment."
- `pull_request_target` or `workflow_run` running untrusted code with a privileged token. GitHub:
  "Avoid using the `pull_request_target` and `workflow_run` workflow triggers with untrusted pull
  requests or code content."

## 3. GitHub environments: reviewers, self-review, timers, branches, the plan, the record

**What the plan offers, and that it depends on the repository being public.** Read today:

```text
$ gh api repos/adammedford/spa-platform --jq '{visibility,private,owner_type:.owner.type}'
{"owner_type":"User","private":false,"visibility":"public"}
$ gh api user --jq '{login,plan:.plan.name,type}'
{"login":"adammedford","plan":"free","type":"User"}
$ gh api repos/adammedford/spa-platform/environments
{"total_count":0,"environments":[]}
```

"Managing environments for deployment"
(<https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments>,
read 2026-11-16): "Users with GitHub Free plans can only configure environments for public
repositories." "Organizations with GitHub Team and users with GitHub Pro can configure environments
for private repositories." "To configure an environment in a personal account repository, you must
be the repository owner."

"Deployments and environments"
(<https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments>,
read 2026-11-16; long sentences came back split, joined here at the split):

| Rule | What the page says | On Free, Pro and Team |
| --- | --- | --- |
| Required reviewers | "You can list up to six users or teams as reviewers." "Only one of the required reviewers needs to approve the job for it to proceed." "The reviewers must have at least read access to the repository." | "required reviewers are only available" "for public repositories" |
| Self-review | "You also have the option to prevent self-reviews for deployments to protected environments." When on, "users who initiate a deployment cannot approve the deployment job, even if they are a required reviewer." | No separate plan note came back |
| Wait timer | "The time (in minutes) must be an integer between 1 and 43,200 (30 days)." "Wait time will not count towards your billable time." | "wait timers are only available for public repositories" |
| Deployment branches and tags | "Only branches and tags that match your specified name patterns can deploy to the environment." With the protected-branches option, "If no branch protection rules are defined for any branch in the repository, then all branches can deploy." | "available for all public repositories"; Pro and Team "also" "for private repositories" |
| Administrator bypass | "By default, administrators can bypass the protection rules and force deployments to specific environments." It can be switched off: "disallow bypassing the protection rules for all deployments" | Public repositories only |

So on the repository as it stands, every rule is available at no cost. A private repository on Free
has no environments; on Pro it has environments and deployment branch rules, but by the notes above
neither required reviewers nor wait timers. The ticket's "private personal repository" would
therefore have had no approval step at all on Free or Pro.

**What the approval gates.** A job that references an environment "must follow any protection rules
for the environment before running or accessing the environment's secrets" (the managing page). With
federation, the same job is the only one that can obtain a token whose `sub` is the environment
form, which is the link between the approval and AWS. Without federation the gate still holds the
job and its environment secrets.

**May the author approve their own run?** By default, yes: the prevention is an option. "Reviewing
deployments"
(<https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments>,
read 2026-11-16; its sentences also came back split and are joined at the split): "If the targeted environment is configured to prevent self-approvals for
deployments, you will not be able to approve a deployment from a workflow run you initiated." With
one account that leaves two shapes, both inferred:

- self-review allowed: Adam starts the run and Adam approves it. The step is exercised and recorded,
  and the record shows one person in both seats;
- self-review prevented: nobody may approve a run Adam started. The job waits until it is bypassed
  ("you can bypass these rules and force all pending jobs referencing the environment to proceed";
  the bypass asks for a description) or fails. With bypass also switched off ("You cannot bypass
  deployment protection rules if the environment has been configured to prevent admins from
  bypassing configured protection rules"), no run Adam starts can deploy.

Whether a run started by a schedule, an app or another workflow counts as initiated by a different
actor, so that Adam could approve it under prevented self-review, was not found on the pages read.
How long a job waits for review before it fails was asked of three pages and none gave it; it is
**not found**, and not stated here from memory.

**The record, and the API that reads it back.** "REST API endpoints for workflow runs"
(<https://docs.github.com/en/rest/actions/workflow-runs?apiVersion=2022-11-28>, read 2026-11-16):

- `GET /repos/{owner}/{repo}/actions/runs/{run_id}/approvals`, "Get the review history for a
  workflow run": an array of entries with `environments` (`id`, `name`, `url`, `html_url`,
  `created_at`, `updated_at`), `state` (`approved`, `rejected` or `pending`), `user` and `comment`.
  "Anyone with read access to the repository can use this endpoint."
- `GET /repos/{owner}/{repo}/actions/runs/{run_id}/pending_deployments`: the environments the run is
  waiting on.
- `POST /repos/{owner}/{repo}/actions/runs/{run_id}/pending_deployments` with `environment_ids`,
  `state` and `comment`: the approval itself. "Required reviewers with read access to the repository
  contents and deployments can use this endpoint." So an approval can be given from a terminal or a
  phone, which suits a sitting run remotely.

None of these was called against a real run: the repository has no environment and no run has
waited for review. Whether a bypass appears in the approvals history, and in what shape, was not
found. On the AWS side, "Calling `AssumeRoleWithWebIdentity` can result in an entry in your AWS
CloudTrail logs. The entry includes the Subject of the provided web identity token" (the STS API
page), which would be the second half of the record if federation existed.

## 4. Branch protection and rulesets: one account and a second reviewer

Main's protection, read today (`gh api repos/adammedford/spa-platform/branches/main/protection`):
required status checks `Build and test`, `Terraform`, `Documents`; no required pull request reviews;
`enforce_admins` false; force pushes and deletions off. Rulesets: none
(`gh api repos/adammedford/spa-platform/rulesets` returns `[]`).

- "Approving a pull request with required reviews"
  (<https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/approving-a-pull-request-with-required-reviews>,
  read 2026-11-16): "Pull request authors cannot approve their own pull requests."
- "About protected branches"
  (<https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches>,
  read 2026-11-16): "You can require approving reviews from people with write permissions in the
  repository or from a designated code owner." By default "the restrictions of a branch protection
  rule don't apply to people with admin permissions to the repository"; the "Do not allow bypassing
  the above settings" option removes that. With the last-push option, "the most recent reviewable
  push must be approved by someone other than the person who pushed it." "Actors may only be added
  to bypass lists when the repository belongs to an organization."

So, by inference: one required approval on `main` with one account can never be satisfied by that
account. With administrators exempt (today's `enforce_admins: false`) the owner merges past it and
the rule holds nobody; with the exemption removed, nothing merges. The owner, who administers the
repository, can also change the rule. An owner with one account cannot be held to a second reviewer;
`ci.yml`'s header already says as much of check A-05 ("one author, one reviewer -- is a permissions
model for a team of one"). A second GitHub account with write access, held by a second person, is
the only thing read that changes this. Whether a GitHub App's or a bot account's review counts
toward a required approval was not read.

**Plan availability.** The plan callouts of "About protected branches" and "About rulesets" did not
come back from either page. The rulesets page's extraction gave a sentence about "customers on
GitHub Team and GitHub Enterprise plans" that reads as a fragment about a narrower feature, so it is
not relied on. "GitHub's plans"
(<https://docs.github.com/en/get-started/learning-about-github/githubs-plans>, read 2026-11-16)
lists, for GitHub Free for personal accounts, "Deployment protection rules for public
repositories", and for GitHub Pro, under "Advanced tools and insights in private repositories",
"Required pull request reviewers", "Multiple pull request reviewers" and "Protected branches". By
inference, a private repository on Free has neither protected branches nor required reviews; the
page does not say so in a sentence. The API shows branch protection in force on this public Free
repository. For rulesets on either plan the availability is **not found** here.

## 5. Session duration, and what stops a run from running up the bill

**Duration.** "AssumeRoleWithWebIdentity"
(<https://docs.aws.amazon.com/STS/latest/APIReference/API_AssumeRoleWithWebIdentity.html>, read
2026-11-16): "By default, the temporary security credentials created by `AssumeRoleWithWebIdentity`
last for one hour. However, you can use the optional `DurationSeconds` parameter to specify the
duration of your session. You can provide a value from 900 seconds (15 minutes) up to the maximum
session duration setting for the role. This setting can have a value from 1 hour to 12 hours." The
bootstrap plan role sets `max_session_duration = 3600`. A session policy can narrow one session
further: "The resulting session's permissions are the intersection of the role's identity-based
policy and the session policies."

**What stops a runaway, in the order it bites.** Expiry is not on the list: credentials lapsing
deletes nothing, and a service or NAT gateway created in the first minute bills until something
destroys it.

1. **The role's permissions policy.** The plan role in `infra/bootstrap/ci-role.tf` can create
   nothing: its only write is the optional `.tflock` object. A deploy role would be the first role
   in this repository that a workflow could spend with, and its policy is the real ceiling.
2. **The trust conditions and the approval** of sections 2 and 3: which run may hold the role at
   all.
3. **Revoking sessions.** "Revoke IAM role temporary security credentials"
   (<https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_use_revoke-sessions.html>, read
   2026-11-16): "AWS attaches a new inline policy to the role that denies all permissions to all
   actions. It includes a condition that applies the restrictions only if the user assumed the role
   *before* the point in time when you revoke the permissions." It needs "the `PutRolePolicy`
   permission for the role". It stops the session, not the resources.
4. **The spend limit.** "Create a spend limit in AWS Settings"
   (<https://docs.aws.amazon.com/accounts/latest/reference/create-spend-limit.html>, read
   2026-11-16): "If your usage in a project reaches its limit, AWS pauses that project which stops
   its resources so your costs stay within it." "You must be on a Paid Plan to create a spend
   limit." "When you set a spend limit, the minimum allowed value is the greater of $20 or a
   conservative estimate of your likely spend." Optional earlier controls: "About 7 days before you
   would reach your limit, new resources stop launching", through a managed policy whose text on the
   managed-policies page denies, among others, `ecs:CreateService`,
   `elasticloadbalancing:CreateLoadBalancer`, `ec2:CreateNatGateway`, `secretsmanager:CreateSecret`,
   `ecr:CreateRepository`, `cognito-idp:CreateUserPool` and `codebuild:CreateProject`. And: "If you
   take no action within 90 days of your project being paused, AWS permanently deletes your project
   data." The page gives no figure for how late billing data may be. So the limit is a monthly
   backstop with a $20 floor, four times the map's per-sitting ceiling; it cannot enforce $5 a
   sitting or $2 a month, and the same policy that protects the bill would refuse a sitting's own
   deploy near the limit.
5. **On the GitHub side**, a run can be cancelled and a job given a timeout; neither was read for
   this ticket, and neither undoes what a job already created.

**Prices, `us-east-2`.**

| Item | Price | Source, read 2026-11-16 |
| --- | --- | --- |
| IAM OIDC provider, roles, policies, STS calls | $0 per sitting-hour, $0 idle | "AWS Identity and Access Management (IAM), AWS IAM Identity Center and AWS Security Token Service (AWS STS) are features of your AWS account offered at no additional charge. You are charged only when you access other AWS services using your IAM users or AWS STS temporary security credentials." <https://docs.aws.amazon.com/IAM/latest/UserGuide/introduction.html>. The IAM pricing page (<https://aws.amazon.com/iam/pricing/>) returned no pricing text. |
| GitHub-hosted standard runners, public repository | $0 | <https://docs.github.com/en/billing/concepts/product-billing/github-actions>: "The following usage remains free independently and does not reduce the included compute usage:", followed by a list the extraction reported as standard GitHub-hosted runners in public repositories and self-hosted runners (the list items came back paraphrased). |
| GitHub environments, required reviewers | $0 on a public repository | Section 3's plan notes. |
| CodeBuild, `build.general1.small`, Linux | $0.005 per build minute ($0.30 per hour of running job), $0 idle | "AWS CodeBuild - Build minutes on build.general1.small - US East (Ohio)", `USE2-Build-Min:Linux:g1.small`, <https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/CodeBuild/current/us-east-2/index.json>, publication date 2026-09-11. The same file lists a free tier line of 100 build minutes on that size; whether it applies to a project was not checked. |

## 6. What this leaves for the pipeline stand-in

Nothing here decides; these are the shapes the evidence leaves, for the grilling ticket that owns
the decision.

- **Approval without federation.** GitHub environments' required reviewers, a deployment branch
  rule and the approvals API are available today at $0 and exercise the approval step and its
  record. The job behind the gate would hold no AWS credential, so the deployment itself stays with
  the operator's sitting, and AWS does not refuse an unapproved deployment: the control is
  procedural, with a machine-readable record. ADR-0016 stays as it is.
- **Federation by activating advanced features.** The assumption as written, at the cost in
  section 1: irreversible, and the spend limit is removed and cannot be recreated. Against the cost
  rule; Adam's call.
- **A CodeBuild-hosted GitHub Actions runner**, unverified, a candidate for its own research
  ticket. "Tutorial: Configure a CodeBuild-hosted GitHub Actions runner"
  (<https://docs.aws.amazon.com/codebuild/latest/userguide/action-runner.html>, read 2026-11-16):
  "For each job in the workflow, CodeBuild starts a build to run an ephemeral GitHub Actions
  runner", selected by `runs-on: codebuild-<project-name>-${{ github.run_id }}-${{
  github.run_attempt }}`; the project is created with a `--service-role`. `codebuild:*` and
  `codeconnections:*` are in both plans' allowed lists. The job would run inside the account under
  the project's role with no OIDC provider. Three things are open and each could sink it: whether a
  job awaiting an environment approval is dispatched to the runner only after approval (not read);
  the exposure of a self-hosted runner on a public repository, which the approval step requires on
  Free (GitHub's secure-use page warns such runners can be compromised by anyone who can "fork the
  repository and open a pull request"); and the GitHub credential CodeBuild must hold ("Connect with
  a personal access token, a Secrets Manager secret, OAuth app, or GitHub App"), itself a stored
  secret. Price in section 5.
- **Long-lived access keys in an environment secret.** ADR-0016 already rejected long-lived keys for
  CI. `iam:CreateUser` and `iam:CreateAccessKey` are not in the deny list read, but
  `docs/agents/aws-rules.md` says team members are "invited by email, not created or federated in
  IAM". Not investigated further.
- **Paper evidence**, the map's recorded fallback for a control with no honest stand-in.

In every shape, one account means the approver is the author. A second reviewer is a second GitHub
account, and nothing on the AWS side changes that.

## 7. Evidence against the assumption, and for it

Against:

- the managed policies deny the IAM identity provider GitHub federation needs, on both plans
  (section 1);
- the documented exit is irreversible and removes the spend limit (section 1);
- the bootstrap root's subject condition does not match this repository's token format (section 2);
- one account cannot supply a second reviewer on GitHub, for a deployment or a pull request
  (sections 3 and 4);
- required reviewers exist on this plan only because the repository is public (section 3).

For:

- roles and policies outside `role/managed/*` are not denied, and the root already creates them
  (section 1);
- IAM and STS cost nothing, and the runners and environments cost nothing on a public repository
  (section 5);
- the approval step, its branch rule and its API record are available today without AWS
  (section 3);
- AWS's condition keys for GitHub (`repository_id`, `ref`, `environment`, `job_workflow_ref`) would
  allow a tight trust policy if a provider could exist (section 2).

## 8. What was executed, and what was only read

**Executed (2026-11-16), all read-only:**

- `gh issue view 1222` and `gh issue view 1220` (the ticket and the map); `gh pr view 35`.
- `gh api repos/adammedford/spa-platform` (visibility, owner type, id, creation date);
  `gh api user` (plan); `.../environments` (none); `.../rulesets` (none);
  `.../branches/main/protection`; `.../actions/oidc/customization/sub`; `.../collaborators` (one,
  admin); `.../actions/permissions` (`allowed_actions: all`, `sha_pinning_required: false`);
  `.../actions/permissions/workflow` (`default_workflow_permissions: read`,
  `can_approve_pull_request_reviews: false`);
  `.../actions/permissions/fork-pr-contributor-approval` (`first_time_contributors`);
  `.../actions/permissions/access` (HTTP 422, "Access policy only applies to internal and private
  repositories").
- `.../actions/variables` (no variable: `AWS_PLAN_ROLE_ARN` is unset, as ADR-0016 decision 3 says)
  and `.../actions/runs?per_page=1` (the latest run, to see the API answer; nothing is drawn from
  it).
- `curl` of <https://servicereference.us-east-1.amazonaws.com/v1/iam/iam.json> and of the CodeBuild
  price list for `us-east-2`, each parsed with `python3 -I`; `curl` of the managed-policies page,
  searched with `grep` for the deny statement (section 1). No credential.
- Repository files read at `adf0273`, as the Method bullet lists.

**Not executed:** any `aws` call, any `terraform` command, any workflow run, any token request, any
approval, any change to a GitHub setting. Every statement about what AWS or GitHub would do is from
a page, not from an observation.

**Read only (each fetched 2026-11-16):**

| Page | What it gave |
| --- | --- |
| <https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html> | Every managed SCP and the RCP, in full; `DenyIAMRestrictedActions`, `ProtectManagedRoles`, the Region statements, the spend-limit policies |
| <https://docs.aws.amazon.com/accounts/latest/reference/activate-advanced-features.html> | Irreversibility, the spend limit's removal, control of the policies |
| <https://docs.aws.amazon.com/accounts/latest/reference/remove-org-policies.html> | How policies are edited after activation; names no statement |
| <https://docs.aws.amazon.com/accounts/latest/reference/create-spend-limit.html> | The limit, its floor, the early controls, the pause |
| <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_create_for-idp_oidc.html> | The provider requirement, the built-in providers, the `sub` check, the example policies |
| <https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_iam-condition-keys.html> | The GitHub claim-to-key table and the immutable-identifier advice (all but the last 3,425 characters) |
| <https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html> | Availability of `aws:PrincipalArn`, `aws:PrincipalOrgID`, `aws:PrincipalIsAWSService` (the first 100,000 of 166,735 characters) |
| <https://docs.aws.amazon.com/STS/latest/APIReference/API_AssumeRoleWithWebIdentity.html> | Duration, no credentials required, session policies, the CloudTrail note |
| <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_use_revoke-sessions.html> | Revoking sessions |
| <https://docs.aws.amazon.com/IAM/latest/UserGuide/introduction.html> | IAM and STS at no additional charge |
| <https://aws.amazon.com/iam/pricing/> | Nothing: no pricing text came back |
| <https://docs.aws.amazon.com/codebuild/latest/userguide/action-runner.html> | The CodeBuild-hosted runner |
| <https://docs.aws.amazon.com/service-authorization/latest/reference/list_awsidentityandaccessmanagementiam.html> | Nothing: the table did not render; the service reference JSON was used instead |
| <https://docs.github.com/en/actions/reference/security/oidc> | Claims, subject formats, the immutable format and its dates; silent on forks |
| <https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments> | Protection rules and their plan notes; no review timeout |
| <https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments> | The gate sentence, Free-plan availability, the owner rule; no review timeout |
| <https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments> | Self-approval and bypass; no review timeout; does not say which roles may approve |
| <https://docs.github.com/en/rest/actions/workflow-runs?apiVersion=2022-11-28> | The approvals and pending-deployments endpoints |
| <https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax> | `id-token`, the fork downgrade (first 200,000 of 237,191 characters); no sentence joining `id-token` and forks |
| <https://docs.github.com/en/actions/reference/security/secure-use> | Fork and `pull_request_target` warnings, self-hosted runner exposure, the stale custom-claims sentence |
| <https://docs.github.com/en/actions/tutorials/authenticate-with-github_token> | Nothing on `id-token`; points to the workflow syntax page |
| <https://docs.github.com/en/pull-requests/collaborating-with-pull-requests/reviewing-changes-in-pull-requests/approving-a-pull-request-with-required-reviews> | Authors cannot approve their own pull requests |
| <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches> | Required reviews, the administrator exemption, bypass lists; plan callout not found |
| <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets> | Bypass by role, team or app; plan callout not found |
| <https://docs.github.com/en/billing/concepts/product-billing/github-actions> | Free usage for public repositories (list items paraphrased) |
| <https://docs.github.com/en/get-started/learning-about-github/githubs-plans> | What Free and Pro personal plans list for public and private repositories; silent on rulesets |

**Could not verify:**

- the intended project's plan (Free or Paid), whether a spend limit is set, whether advanced
  features are active, and whether any provider or role exists there;
- that the plan policy's IAM deny is lifted by activating advanced features (inferred, section 1);
- whether the RCP's `sts:*` statement would match a web-identity call (section 1);
- that a fork's pull request cannot fetch an OIDC token (inferred, section 2);
- how long a job waits for review, who counts as the initiator of a scheduled or app-started run,
  and how a bypass is recorded (section 3);
- plan availability of rulesets; that a private Free repository lacks branch protection is inferred
  from the plans page (section 4);
- everything about the CodeBuild runner beyond the tutorial page (section 6);
- `aws-actions/configure-aws-credentials`, which was not read.
