---
name: wiz
description: Walk a sitting in the platform project. Reads preflight, the tracker and the cell, says which sitting applies (A the read pass, B the tunnel and the destroy, C the first deployment and the gate runs), runs each stage's commands with the human's approval of each one, posts each stage's evidence to its ticket from captured and redacted output, and stops where only the human can act. Never writes a Confirm comment, never applies or destroys without a confirm on the plan it showed.
---

# /wiz: a sitting in the platform project

Draft of 2026-10-19, from `.scratch/sitting/spec.md`. Install at `.claude/skills/wiz/SKILL.md`
(git-ignored until #135 decides otherwise). It replaces `aws-sitting-2.sh`.

Read first: `docs/agents/aws-rules.md` (one Region, managed IAM, the spend limit, help level
HIGH: explain each step before running it), `tools/aws-project.env`, `GLOSSARY.md` → *Sitting*,
*Read-back*, *Sample image*, *Platform project*, *Sample project* (the first three are #56's Q24,
confirmed on #129 on 2026-10-19; use them whether or not they have been committed yet), and the
tickets this sitting serves.

## Rules that hold in every stage

1. **Every command is shown, explained in one line, and run only on the human's approval.** The
   human may skip a stage; a skipped stage is recorded as skipped.
2. **An apply, a destroy, a deletion or a script that deletes runs only on a confirm given after
   the plan or the case list was shown.** An apply whose plan destroys or replaces anything stops
   the sitting; the human reads the plan and decides outside the wizard.
3. **Nothing secret is printed or posted:** no authorization code, no client secret, no session
   name, no client id, no ARN, no account id beyond its last four digits. One exception, to the
   terminal only: A2 prints the login URL, which carries the client id, because the human has to
   open it; it goes into the log and never into a post. Redact every post with the function below;
   if a line cannot be redacted with confidence, leave it out and say so.
4. **Evidence goes to the ticket it serves**, as a comment headed by the stage, carrying the
   command, its exit code and its key lines. A failed or skipped stage posts "failed" or "not run"
   with the reason, never a sentence that would read the same on success. Each post ends with the
   Claude Code attribution footer.
5. **No `Confirm` comment, ever** (ADR-0021 decision 2). **No relabel, unblock or close** until a
   `Confirm` comment on the run's `Overnight <date>` issue covers the question that decided it
   (ADR-0023 decision 1, ADR-0021 decision 1). A comment that is only the word `Confirm` covers every pre-accepted
   answer of that run and none that read `Answer: open`, which is the rule the report body states;
   the `Confirm` of 2026-10-19 on #129 is one such. Where none covers a write, print the write you
   would make under "Held" and go on.
6. **The record is `.scratch/sitting/log/<UTC date>.log`** in the main checkout: every command,
   every output, unredacted, mode 600. Nothing goes to `/tmp`.
7. **Stop and name the cause** when preflight is not ready, the account is not the pin, a plan has
   destroys or replaces, a stage's exit code or a read's values are not what its ticket expects (an
   exit of 0 reporting an unhealthy target or another image is a failed stage), or AWS answers
   `AccessDenied` where it did not before (ask about the spend limit: AWS Settings → Billing, per
   `docs/agents/aws-rules.md`).
8. **Run from the main checkout on `main`, up to date with `origin/main`**, so the scripts are the
   landed ones; never commit, switch or rebase there (`tools/main_checkout_guard.py`).

### The redaction function

The function is `tools/wiz_redact.py`; its rules are in its header and its companion,
`tools/wiz_redact_tests.py`, holds each to a case (#259). Every post goes through it: write the
draft to a file under `.scratch/sitting/log/`, beside the record (git-ignored, mode 600, never
`/tmp`), pipe it through the script, and post the script's output with `--body-file`, never the
draft.

```bash
python3 -I tools/wiz_redact.py < .scratch/sitting/log/post.md > .scratch/sitting/log/post.redacted.md; echo "exit=$?"
gh issue comment <n> --body-file .scratch/sitting/log/post.redacted.md     # only on exit=0
```

Add `--keep-last-four` only where the ticket's grill asks for an account id's last four digits, as
#125 does. On exit 3 the draft carries `AccessDenied` or `Access Denied` and the script wrote
nothing: post nothing, stop the sitting and name the cause (rule 7: ask about the spend limit, AWS
Settings → Billing, per `docs/agents/aws-rules.md`). On any other non-zero exit post nothing and
say the redaction failed. On exit 0, read the result once as a reviewer would before posting; the
script catches shapes, not meaning.

## Stage 0: where things stand

Ask first whether this is a `--dry-run` (list every command, run nothing, post nothing), so the
answer governs Stage 0 too. Then run every time, before choosing a sitting.

```bash
set -a; . tools/aws-project.env; set +a
tools/preflight.sh                              # must end ready; it compares the account with the pin; C reads its Container engine row
git fetch origin --tags && git status --short   # main checkout clean, on main, at origin/main
gh issue view 129 --json comments --jq '[.comments[] | select(.body | test("^Confirm")) | .body]'
aws ecs describe-services --cluster spa-poc --services gateway \
  --query 'services[0].{status:status,desired:desiredCount,running:runningCount}'
ls tools/scripts/                               # which of tunnel.sh, verify-tunnel.sh, seed.sh, deploy.sh, teardown.sh have landed
ls tests/e2e/Stack/DeployedStack.cs             # the browser suite's deployed mode (#209); C2 and C3 need it
```

Then say which sitting applies and list its stages with the minutes from the spec's inventory:

- **A** when the service exists and #56 has no read-back comment yet.
- **B** when #56's read-back is posted and `tunnel.sh` and `verify-tunnel.sh` are on `main`.
- **C** when `seed.sh`, `deploy.sh` and `teardown.sh` are on `main`, preflight's `Container engine`
  row reads `ok` in the shell this sitting runs in, and the platform root is destroyed or absent. A
  running engine is not enough: a per-user Docker Desktop puts `docker` in `~/.docker/bin`, which
  that shell may not have on `PATH`, and then preflight and the scripts see no engine. Offer C2
  and C3 only when `tests/e2e/Stack/DeployedStack.cs`, the browser suite's deployed mode (#209), is
  on `main`, read from the tree as the scripts are; without it the sitting stops after C1 and says
  so.
- If the service is absent in A: the sitting becomes the platform root's re-apply on the sample
  image (A-alt below) before the read pass.

Ask the human which to run.

## Sitting A: the read pass

The cell stands; nothing is applied or destroyed.

**A2, #125, the login.** Print the URL with the apps README's block
(`infra/envs/poc/apps/README.md` → "Done when"), after the `describe-user-pool-client` read:

```bash
POOL=$(terraform -chdir=infra/envs/poc/idp output -raw user_pool_id)
DOMAIN=$(terraform -chdir=infra/envs/poc/idp output -raw cognito_domain)
CLIENT=$(terraform -chdir=infra/envs/poc/apps output -json clients | jq -r .app1.clientId)
aws cognito-idp describe-user-pool-client --user-pool-id "$POOL" --client-id "$CLIENT" \
  --query 'UserPoolClient.{flows:AllowedOAuthFlows,callbacks:CallbackURLs,rotation:RefreshTokenRotation}'
echo "https://$DOMAIN/oauth2/authorize?response_type=code&client_id=$CLIENT&redirect_uri=https%3A%2F%2Fapp1.spa-poc.test%3A8443%2Fsignin-oidc&scope=openid"
```

Tell the human: open it, sign in as a test user, and report whether the address bar shows
`?code=`. Nothing listens on the callback yet, so the browser shows an error page with the code in
its address bar; that is the proof. Post on #125: the date, "app 1's client, managed login, a test
user", the `describe-user-pool-client` output with the client id elided, and "the redirect carried
`?code=`" or "did not". Never the URL with the client id, never the code.

**A3, #55, Q12's evidence.**

```bash
terraform -chdir=infra/envs/poc/platform init -backend-config=backend.hcl -input=false
terraform -chdir=infra/envs/poc/platform plan -input=false -lock=false -detailed-exitcode; echo "exit=$?"   # 0 means no changes; no -out, so no plan file
LB=$(aws elbv2 describe-load-balancers --names spa-poc --query 'LoadBalancers[0].LoadBalancerArn' --output text)
aws elbv2 describe-load-balancers --load-balancer-arns "$LB" --query 'LoadBalancers[0].{scheme:Scheme,type:Type,state:State.Code}'
aws elbv2 describe-load-balancer-attributes --load-balancer-arn "$LB" \
  --query "Attributes[?Key=='routing.http.preserve_host_header.enabled' || Key=='routing.http.drop_invalid_header_fields.enabled']"
aws elbv2 describe-listeners --load-balancer-arn "$LB" \
  --query 'Listeners[].{port:Port,protocol:Protocol,certificates:length(Certificates),actions:DefaultActions[].Type}'
aws elbv2 describe-rules --listener-arn "$(aws elbv2 describe-listeners --load-balancer-arn "$LB" --query 'Listeners[0].ListenerArn' --output text)" --query 'length(Rules)'
aws elbv2 describe-target-groups --load-balancer-arn "$LB" \
  --query 'TargetGroups[].{type:TargetType,protocol:Protocol,port:Port,path:HealthCheckPath}'
LISTENER_CERT=$(aws elbv2 describe-listeners --load-balancer-arn "$LB" --query 'Listeners[0].Certificates[0].CertificateArn' --output text)
IMPORTED_CERT=$(aws acm list-certificates --query "CertificateSummaryList[?DomainName=='*.spa-poc.test' && Type=='IMPORTED' && Status=='ISSUED'].CertificateArn | [0]" --output text)
if [ -n "$LISTENER_CERT" ] && [ "$LISTENER_CERT" != "None" ] && [ "$LISTENER_CERT" = "$IMPORTED_CERT" ]; then echo "certificate: match"; else echo "certificate: MISMATCH"; fi
```

The load balancer is named `spa-poc` (`local.lb_name` in `infra/modules/cell/alb.tf`, read back on 2026-10-20). If the name differs, find it by the `Platform` tag: the A5 `get-resources` call
with `--resource-type-filters elasticloadbalancing:loadbalancer`. Expect: `internal`, both
attributes `true`, one listener on 443 HTTPS with one certificate and one `forward`, one rule (the
default), the target group `ip`, `HTTP`, 8080, path `/`, and `certificate: match`: the listener's
certificate ARN equals the imported, issued `*.spa-poc.test` certificate's, compared in the shell so
that the post carries the one word and no ARN. A count and a separate listing cannot show the
attachment; the comparison can. Post on #55 under "Read-back, Q12", with the ARNs removed. Then,
under "Held" unless #98's Q13 is confirmed (it is): close #55 with an `## Answer` comment saying
the apply was recorded on 2026-10-18, the plan and the
read-back are above, and task 1.6b checks it end to end.

**A4, #56, Q6 to Q8's read-back.** Names from the module: cluster `spa-poc`, service `gateway`,
task definition `spa-platform-poc`, task role `spa-platform-poc-task`, log group
`/spa-platform/poc/gateway`. Read the pin from the git-ignored
`infra/envs/poc/platform/terraform.tfvars`.

```bash
aws ecs wait services-stable --cluster spa-poc --services gateway; echo "exit=$?"
aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].{desired:desiredCount,running:runningCount,pending:pendingCount,deployments:deployments[].{status:status,rollout:rolloutState},events:events[:3].message,exec:enableExecuteCommand,breaker:deploymentConfiguration.deploymentCircuitBreaker,publicIp:networkConfiguration.awsvpcConfiguration.assignPublicIp}'
TASK=$(aws ecs list-tasks --cluster spa-poc --service-name gateway --query 'taskArns[0]' --output text)
aws ecs describe-tasks --cluster spa-poc --tasks "$TASK" --query 'tasks[0].{status:lastStatus,image:containers[0].image,digest:containers[0].imageDigest,containers:containers[].name,agents:containers[0].managedAgents}'   # the nested managedAgents filter returns null; read the list and expect one ExecuteCommandAgent RUNNING
aws ecs describe-task-definition --task-definition spa-platform-poc --query 'taskDefinition.{cpu:cpu,memory:memory,arch:runtimePlatform.cpuArchitecture}'
TG=$(aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].loadBalancers[0].targetGroupArn' --output text)
aws elbv2 describe-target-health --target-group-arn "$TG" --query 'TargetHealthDescriptions[].{port:Target.Port,health:TargetHealth.State}'
aws logs describe-log-groups --log-group-name-prefix /spa-platform/poc/gateway --query 'logGroups[].{name:logGroupName,retention:retentionInDays}'
aws iam list-role-policies --role-name spa-platform-poc-task
aws iam list-attached-role-policies --role-name spa-platform-poc-task --query 'length(AttachedPolicies)'
aws iam get-role-policy --role-name spa-platform-poc-task --policy-name "$(aws iam list-role-policies --role-name spa-platform-poc-task --query 'PolicyNames[0]' --output text)" --query 'PolicyDocument.Statement[].Sid'
for r in $(aws ecs describe-clusters --clusters spa-poc --query 'clusters[0].clusterArn' --output text) \
         $(aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].serviceArn' --output text) \
         $(aws ecs describe-task-definition --task-definition spa-platform-poc --query 'taskDefinition.taskDefinitionArn' --output text); do
  aws ecs list-tags-for-resource --resource-arn "$r" --query "tags[?key=='Platform'].value"; done
aws logs list-tags-for-resource --resource-arn "$(aws logs describe-log-groups --log-group-name-prefix /spa-platform/poc/gateway --query 'logGroups[0].logGroupArn' --output text | sed 's/:\*$//')" --query 'tags.Platform'
aws iam list-role-tags --role-name spa-platform-poc-task --query "Tags[?Key=='Platform'].Value"
aws iam list-role-tags --role-name spa-platform-poc-task-execution --query "Tags[?Key=='Platform'].Value"
```

The execution role is `spa-platform-poc-task-execution` (read from the task definition's `executionRoleArn` on 2026-10-20; `spa-platform-poc-execution` does not exist).
Expect, per #56's Q6 to Q8: the waiter exits 0; desired 1, running 1, pending 0; one `PRIMARY`
deployment `COMPLETED`; a steady-state event and no rollback; the task `RUNNING` with the image
equal to the pin and the Exec agent `RUNNING`; `256`, `1024`, `ARM64`; Exec on, the breaker with
rollback, public IP `ENABLED`; one target on 8080 `healthy`; retention 14; one inline policy with
statement ids `ReadRegistryAndReleases`, `ReadClientSecrets`, `SessionManagerMessages` and no
managed policy; `Platform = spa-platform` on all six. Post on #56 under "Read-back, Q6 to Q8",
names and fields only, and add the sentence Q7 asks for: this is what IAM holds for the role, not
its effective permission under the project's managed SCPs and RCPs. If any read differs, post it
as it is and say #56 stays open.

**A5, #118, step 1.**

```bash
aws resourcegroupstaggingapi get-resources --tag-filters Key=Platform,Values=spa-platform --query 'ResourceTagMappingList[].ResourceARN' | sed -E 's/arn:aws:[^:]*:[^:]*:[0-9]{12}:/.../'
aws resourcegroupstaggingapi get-resources --region us-east-1 --tag-filters Key=Platform,Values=spa-platform --query 'ResourceTagMappingList[].ResourceARN' | sed -E 's/arn:aws:[^:]*:[^:]*:[0-9]{12}:/.../'
aws iam list-roles --query "Roles[?starts_with(RoleName, 'spa-platform-poc')].RoleName"
```

Post both on #118: the query's list, with the two roles absent from it and present by name.

**A6, #122 and #92, P-04.** Show the case list from the script's header, then:

```bash
tools/scripts/verify-release-immutability.sh; echo "exit=$?"
```

Ask whether to run it again with `--clean-up-as-teardown-role` (the recommendation: yes, so the
bucket is empty for the destroy in sitting B). Post the full output on #122 and the case lines on
#92; keep the output in the log, since `Merge task 1.5:`'s body carries it later.

**A7.** `rm infra/envs/poc/platform/terraform.tfvars.bak`, with approval, and say so on #129's
thread only if the human wants; it is Q16 there.

**A8, the tracker writes.** Each rests on two things: a `Confirm` covering the question (all four
below were pre-accepted on #129, which its `Confirm` of 2026-10-19 covers), and the evidence of the
stage it rests on having held. Print the command, then make the write only when both hold;
otherwise list it under "Held" with the reason, confirmation or evidence:

- #125: `gh issue close 125 --comment "<## Answer: the apply on 2026-10-18 at 19:17:25Z, the plan
  and outputs in the grill of 2026-10-19, the login above>"` (#125's Q7), only when A2's login
  reached `?code=`.
- #55: the close under A3 (#98's Q13, confirmed), only when every A3 read matched what Q12 expects,
  `certificate: match` included.
- #56: `gh issue edit 56 --remove-label ready-for-human --add-label ready-for-agent` (#56's Q11),
  and the blocker removals for #57 and #58 (#56's Q12: "once every read holds") through the
  dependencies API, as `docs/agents/issue-tracker.md` → Blocking shows, only when every A4 read
  matched what Q6 to Q8 expect. A differing read is posted as it is, and #56 stays as it was.
- #125 as #55's blocker (#125's Q6): on the confirmation alone, since the dependency it modelled,
  the apps root's state, has held since 2026-10-18.

End sitting A by printing: what was posted where, what is held, that the stack stays up until
sitting B (#55's Q14) at about $1 a day, and the one thing the human still owes by hand (nothing,
if the login was done).

**A-alt: the cell is gone.** Re-apply the platform root on the sample image before the read pass,
in #55's Q11 order: `certs.sh status` (import only if ACM has no `*.spa-poc.test`), the apps root's
plan must be empty, rewrite `terraform.tfvars` from the example with the current digest of
`public.ecr.aws/nginx/nginx-unprivileged:stable-alpine` (`docker manifest inspect` or
`crane digest`), `health_check_path = "/"`, the issuers block from the identity root's outputs; then
`init`, `plan` (read for the certificate found, no per-app resource, and P-01's counts), `apply` on
confirm. Then `aws ecs wait services-stable` before A3.

## Sitting B: the tunnel on the sample image, then the destroy

**B0.** `tools/preflight.sh` (the Session Manager plugin row), `tools/scripts/certs.sh status`, and
`grep spa-poc.test /etc/hosts`. Anything missing is the human's: `sudo` for the hosts file,
`certs.sh trust` for the keychain.

**B1, #57.** Run `tools/scripts/tunnel.sh` in one terminal and `tools/scripts/verify-tunnel.sh /`
in another, as #57's landed scripts document; the done-when is exit 0 on a clean workstation. Post
the exit code and the key lines on #57.

**B2, #55's Q14.** With `verify-tunnel.sh` green and #56's read-back posted:

```bash
terraform -chdir=infra/envs/poc/platform plan -destroy -input=false -out=/dev/null 2>&1 | tail -20   # read it: the platform root only
terraform -chdir=infra/envs/poc/platform destroy -input=false                                         # on confirm
aws resourcegroupstaggingapi get-resources --tag-filters Key=Platform,Values=spa-platform --query 'length(ResourceTagMappingList)'
aws iam list-roles --query "Roles[?starts_with(RoleName, 'spa-platform-poc')].RoleName"
```

The certificate, the identity root and the apps root stay. The bucket must be empty (A6's
clean-up); if the destroy refuses on the bucket, stop and say so rather than forcing it. Post the
destroy's exit code and the two reads on #118 as its second data point, and a dated note on #56
that the cell is down until `deploy.sh`.

## Sitting C: the first deployment and the gate runs

Needs #58's scripts on `main`, preflight's `Container engine` row reading `ok` in this sitting's
shell (Docker Desktop, D-2), and the platform root absent; C2 and
C3 also need `tests/e2e`'s deployed stack on `main` (#209).

**C1.** In the plan's First deployment order: the platform root on the sample image (A-alt),
`tools/scripts/seed.sh`, `tools/scripts/deploy.sh` (builds both images for ARM64, pushes by
digest, applies with `health_check_path=/readyz`, waits for steady state, asserts the running
digest), then `tunnel.sh` and `verify-tunnel.sh /healthz`. Each apply on confirm; post each
script's exit code and key lines on #58.

**C2, #63 for G3.** The browser suite through the tunnel (`SPA_E2E_STACK=deployed dotnet test
--project tests/e2e`, with what `tests/e2e/Stack/DeployedStack.cs`'s summary says a sitting has
before the run), `verify-release-immutability.sh` as P-04, P-06 (a
connection from a host outside the VPC to the task's public address on 8080 must time out: `nc -G 5
-z <address> 8080; echo "exit=$?"`, expecting non-zero; on macOS `-G` is the connect timeout,
while `-w` is an idle timeout that leaves the probe waiting about 75 seconds), then `teardown.sh` and P-05 (the tag query
empty, the roles gone by name, #118), then seed and deploy again from nothing. Post per test id on
#63.

**C3, #63 for G4 and G5.** The deployed negative subset (the rows
`tests/e2e/Deployed/DeployedSubsetTests.cs` names, run by the same `SPA_E2E_STACK=deployed`
`dotnet test --project tests/e2e`, N-26 in the browser), then F-07: the apps root's plan with `fixtures/onboarding/app3.json`
shows three resources, `spactl release publish`, `spactl app add`, `spactl wait`, the task id and
task definition revision unchanged before and after, app 3 served. Post on #63.

**C4, the gate tags.** For each gate, list the range's merges and their `Reviewed:` lines as
`docs/agents/landing-work.md` → Gates shows, name any merge no record clears (the five under
"Review debt from before the check" included), and draft the tag message with the CI run URL. The
human cuts the tag.

**C5.** Ask whether the stack stays up (about $30 a month) or is destroyed until Phase 2's
deployed tier; destroy on confirm.

## Outside the sittings

- **H1, the open questions on an `Overnight <date>` issue.** A `Confirm` that is the one word
  covers the pre-accepted answers and none that read `Answer: open`. On request, print a digest
  table of the open ones from the issue's comments (question, recommendation, what it changes), as
  `.scratch/sitting/open-questions-129.md` did for #129, whose 26 were answered on 2026-10-19. The
  human writes and posts any comment that names them, or has a session post it at his word.
- **H2, #90.** Print the three pre-checks and the close path (AWS Settings → View all projects →
  the sample project → Close project); after the human reports it closed, `rm` the two local files
  with approval, run preflight, and draft the ADR-0017 clause into the log for the integrator.
- **H3, #68.** `tools/check.sh --full --both-halves --port 5180` once from `zsh -l` and once from
  `/bin/bash`, on `main`; post both verdict lines on #68.
- **H4, #71.** `python3 -I tools/prune_branches.py` from a checkout at `origin/main`; post its
  output on #71.
