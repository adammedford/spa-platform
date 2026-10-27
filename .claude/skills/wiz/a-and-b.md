# /wiz: sittings A and B, and the finished H items

Moved out of `SKILL.md` by #468: sitting A ran on 2026-10-19 and sitting B on 2026-10-24, and #68
(H3) and #71 (H4) are closed. Kept as the record of how they ran, for a cell rebuilt from nothing
that needs the read pass again. The rules, the redaction function and Stage 0 are in `SKILL.md`;
where a stage below says "A-alt", read `SKILL.md` → N, the platform root on the sample image.

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

Before comparing a read, read #55 → "Read-back expectations": it names the load balancer and says
what each read above must show, the certificate comparison's one word included. If the load
balancer's name differs, find it by the `Platform` tag: the A5 `get-resources` call with
`--resource-type-filters elasticloadbalancing:loadbalancer`. Post on #55 under "Read-back, Q12",
with the ARNs removed. Then,
under "Held" unless #98's Q13 is confirmed (it is): close #55 with an `## Answer` comment saying
the apply was recorded on 2026-10-18, the plan and the
read-back are above, and task 1.6b checks it end to end.

**A4, #56, Q6 to Q8's read-back.** Before comparing a read, read #56 → "Read-back expectations":
it names the cluster, the service, the task definition, both roles and the log group the commands
below use, and says what each read must show. Read the pin from the git-ignored
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

Compare each read with #56 → "Read-back expectations". Post on #56 under "Read-back, Q6 to Q8",
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
- #55: the close under A3 (#98's Q13, confirmed), only when every A3 read matched #55 →
  "Read-back expectations", the certificate comparison included.
- #56: `gh issue edit 56 --remove-label ready-for-human --add-label ready-for-agent` (#56's Q11),
  and the blocker removals for #57 and #58 (#56's Q12: "once every read holds") through the
  dependencies API, as `docs/agents/issue-tracker.md` → Blocking shows, only when every A4 read
  matched #56 → "Read-back expectations". A differing read is posted as it is, and #56 stays as it was.
- #125 as #55's blocker (#125's Q6): on the confirmation alone, since the dependency it modelled,
  the apps root's state, has held since 2026-10-18.

End sitting A by printing: what was posted where, what is held, that the stack stays up until
sitting B (#55's Q14) at about $1 a day, and the one thing the human still owes by hand (nothing,
if the login was done).

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

Before reading the destroy plan, read #55 → "Read-back expectations" (Q14): it says what the
destroy may touch and what stays. A6's clean-up empties the bucket; if the destroy refuses on the
bucket, stop and say so rather than forcing it. Post the
destroy's exit code and the two reads on #118 as its second data point, and a dated note on #56
that the cell is down until `deploy.sh`.

## The finished H items

- **H3, #68.** `tools/check.sh --full --both-halves --port 5180` once from `zsh -l` and once from
  `/bin/bash`, on `main`; post both verdict lines on #68.
- **H4, #71.** `python3 -I tools/prune_branches.py` from a checkout at `origin/main`; post its
  output on #71.
