# /wiz: sitting D

Moved out of `SKILL.md` by #1198. The rules, the prelude, the post mode, the remote route and
Stage 0, with "Offering D", are in `SKILL.md`, read before this file. N, T and C1 to C4, which the
steps below name, are stages of sitting C in `c.md`: read it too.

## Sitting D: the G6 continuity run (#348)

One sitting, #348's "What to run" in its Q22 order: a fresh deploy of the commit to be tagged with
every G6 input off and a live login (D0), the cutover apply (D2), then row 1's reads (D1) and rows 3
to 10 (D3 to D10). D<n> carries #348's row n, so the comment URLs #348 Q34's tag message cites map
one to one. D's units are steps, never stages: `GLOSSARY.md` keeps *stage* for the pipeline's entry
points, and "Stage 0" stays the chooser's name. D never resumes: #348 Q22 asks for one fresh deploy
and the rows in order in one sitting, so a part-run D's next attempt starts again at D0, from an
absent root.

**Every post of D goes on #348**, headed by its step and row (`## D2, row 2`; `## D0` alone),
through the post mode and nowhere else, carrying the commit, each command, its exit code and its key
lines (rule 4), each bundle file by its name and SHA-256, and each task by its id, `${TASK##*/}`,
never its ARN, with the attribution footer. `tools/wiz_redact.py` keeps an ARN's resource field, so
a task id survives it and a post may name the tasks a row crossed (#348 Q19, Q23). D makes no other
tracker write: no relabel, unblock, close or box tick (rule 5), so it needs no `Confirm` read.

**Rule 2 for D.** Rules 1 to 8 hold unchanged, with one clause of D's own: **D2's apply may replace
the gateway task definition and nothing else**, on a confirm given after `terraform show` of the
saved plan. Every environment change forces that one replacement (`aws_ecs_task_definition`: the
plan's one `must be replaced` is `module.cell.aws_ecs_task_definition.gateway`); the rest of the
cutover is adds and in-place changes (the table, the ring's grants, D6's network, the service's
count, deployment and shutdown settings). A second `must be replaced`, or any `will be destroyed`,
stops the sitting as rule 2 says, and the human reads the plan outside the wizard. The scripts that
apply inside D keep their confirm on launch: `deploy.sh` in D0 and D6 with C1's shape, `teardown.sh`
in D10 with T's case list.

**Parts read from the tree at run time.** D names two parts by ticket and spells neither: the G6
block of `infra/envs/poc/platform/terraform.tfvars.example` (#338's store and ring keys, #347's two
tasks, network, deployment and shutdown settings) and the summary of
`tests/e2e/Deployed/ContinuityTests.cs` (#358), as C2 reads `DeployedStack.cs`'s. Read both before
D0 and quote them to the human. Stage 0 offers D only once #348's edges to those tickets are
closed, so both are in the tree when D runs.

**Failure, and the way out.** A row that fails, or that the class reports `not proved:`, is posted
as such with the reason (rule 4), and wiz then asks whether to go on to the next step or straight
to D10. Every stop, rule 7's included, offers D10 before the sitting ends: the two-task cell costs
about $0.10 an hour (#347 Q10), and teardown the same day is the practice. A stop after D0's `start`
and before D2's end, and a failed D2 whichever way the human then answers, first ends any watch and
closes the sitting browser, as D2 says, and a failed D2 with no convergence read says what the cell
is before the question, as D2 says too. A declined D10 is recorded as "not run: declined", keeps
D2's copy of `terraform.tfvars`, and the sitting ends naming where it is.

**D0, the fresh deploy and the live login (#348 Q22).** The root is absent (Stage 0). Fetch and
fast-forward first (rule 8) and name the commit: D0 deploys the commit `main` is on, which is the
one #348 Q34 tags, and its CI run must be green. Then N's steps 1 to 6 from the first root that is
absent, with `terraform.tfvars` in its Phase 1 shape: N step 3's rewrite, which
`tools/scripts/platform-tfvars.sh` (#470) does, and no G6 key. N's posts for the identity and apps
roots, seed and the deploy go where N sends them; D0's own post is the one below. Once N step 6's
tunnel, started after `deploy.sh`'s pass line, is up and `verify-tunnel.sh /healthz` passes, the
sitting browser starts, and D0's live login is its own. In D the human opens no browser; each
`sitting_browser` call is shown and approved under rule 1 like any other, until #1028 says
otherwise. `start` runs in a background call of its own, and every later command finds it by the
socket in the bundle, once its ready line has printed:

```bash
export SPA_E2E_STACK=deployed; shown sitting_browser start     # in the background; its ready line says stack=deployed
```

```bash
shown sitting_browser login main --app app1                    # keep profile main: D2 reloads it
shown sitting_browser marker main
```

A ready line that does not say `stack=deployed`, or a login line that is not `signed-in`, is a
failed D0: the switch unset starts the local stack, and that login would prove nothing about the
cell. Then:

```bash
git rev-parse HEAD
gh run list --workflow CI --commit "$(git rev-parse HEAD)" --json status,conclusion,url --jq '.[] | "\(.status) \(.conclusion) \(.url)"'
TASK=$(captured aws ecs list-tasks --cluster spa-poc --service-name gateway --desired-status RUNNING --query 'taskArns[0]' --output text); echo "task ${TASK##*/}"
shown aws ecs describe-tasks --cluster spa-poc --tasks "$TASK" \
  --query 'tasks[0].{revision:taskDefinitionArn,zone:availabilityZone,containers:containers[].{name:name,digest:imageDigest}}'
```

Post `## D0` on #348 (#348 Q34, #361 Q9): the deployed commit, its CI run's URL and conclusion, both
image digests (the same ones `deploy.sh`'s pass line names), the task id, the task definition
revision, and the `signed-in` and `marker` lines as what the page showed. A CI run that is not
green, a digest that differs from `deploy.sh`'s pass line, or a `signed-in` line whose `instance=`
is not the one task's id, is a failed D0, and D does not go on.

**D2, the cutover (row 2, F14; #348 Q32, #361 Q7 and Q11).** First the copy D10 restores, in the
bundle, then the block:

```bash
cp -p infra/envs/poc/platform/terraform.tfvars "$E/terraform.tfvars.phase1" && chmod 600 "$E/terraform.tfvars.phase1" && shasum -a 256 "$E/terraform.tfvars.phase1"
cat infra/envs/poc/platform/terraform.tfvars.example      # read it to the human; point at the G6 block
```

Show the G6 block as the example carries it, each key with the value the example documents for the
G6 run, and on approval append exactly those lines, uncommented, to `terraform.tfvars` (a heredoc
with `>>`, never an edit of the Phase 1 lines); then `diff "$E/terraform.tfvars.phase1"
infra/envs/poc/platform/terraform.tfvars`, so the human sees the lines added and nothing else
changed. The plan goes into the bundle, since it holds the cell's values, and is shown before the
confirm:

```bash
shown terraform -chdir=infra/envs/poc/platform plan -input=false -detailed-exitcode -out="$PWD/$E/cutover.tfplan"
shown terraform -chdir=infra/envs/poc/platform show "$PWD/$E/cutover.tfplan"
```

Read the plan under D's rule-2 clause: exit 2, one `must be replaced`, the gateway task definition,
and otherwise creates and in-place updates. Then the baseline:

```bash
shown sitting_browser clear-provider main
shown sitting_browser reload main
```

Anything but `signed-in` stops the sitting (rule 7) before any apply: D0's session is gone, so no
sign-out could be attributed to the cutover. Name the counts and the one replacement when asking for
the confirm. On the confirm, set any earlier attempt's log aside and leave this attempt's empty
(#1135), start the watch in a background call of its own, and once its first line has printed
apply the saved plan (the apply does not wait for the roll):

```bash
[ ! -e "$E/browser.log" ] || mv "$E/browser.log" "$E/browser.log.$$"
: > "$E/browser.log"
```

```bash
shown sitting_browser watch main --every 10s --clear-provider --relogin    # in the background, until unwatch
```

```bash
shown terraform -chdir=infra/envs/poc/platform apply -input=false "$PWD/$E/cutover.tfplan"     # on the confirm, D's rule-2 clause
```

Through the roll, the count is the sitting browser's and nothing else's. Each tick clears the
provider's cookies, since a live provider session answers the gateway's challenge with no prompt
(#1012), reloads, and writes one observation line to `"$E/browser.log"`; on `login-prompt` it logs in
again. A sign-out is a `login-prompt` line with a `signed-in` line since the previous prompt, so a
prompt repeated after a failed login is one sign-out (#1025 Q3). `tunnel-down`, `timeout`, `other` and
`not-found` lines are missing coverage, never a sign-out and never a stop. A `watch` call that exits
before `unwatch` has ended, and the `failed` line it printed is in the log too, where it marks the
interval's start: run `unwatch`, run the watch line again with the log left where it is, and post
the interval as missing coverage. The gateway's log lines go into the bundle uncounted (`tools/scripts/logs.sh` into `"$E/row2.log"`), because a navigation the shell gate
challenges is a redirect and not a 401 (`GLOSSARY.md` → Shell gate), so the log could count what
the browser never saw. The tunnel's hop is an old task, which the roll drains (#348 Q20): once
`describe-tasks` lists a new task RUNNING, stop `tunnel.sh`, note the UTC time, start it again in
the background (it picks a running task and prints `through task <id>`: keep that id, D4 needs it),
run `verify-tunnel.sh /healthz`, and note the time it passed; the interval between the two times is
the gap, posted as missing coverage, and any `tunnel-down` line outside it is named as further
missing coverage. Then convergence:

```bash
shown aws ecs wait services-stable --cluster spa-poc --services gateway
TASKS=$(captured aws ecs list-tasks --cluster spa-poc --service-name gateway --desired-status RUNNING --query 'taskArns[]' --output text)
shown aws ecs describe-tasks --cluster spa-poc --tasks $TASKS \
  --query 'tasks[].{task:taskArn,zone:availabilityZone,status:lastStatus,revision:taskDefinitionArn,started:startedAt}'
shown spactl wait --url https://app1.spa-poc.test:8443 --revision <N> --instances <id1>,<id2>   # N: "the live registry is at revision N" in D0's deploy.sh output
```

Two tasks RUNNING in two zones on the new revision, and `spactl wait --instances` returning with
each task's `/readyz` answered at the registry's revision, is convergence; the `--instances` ids are
the two task ids, comma-separated, as `deploy.sh`'s own wait passes them (`cell-common.sh`
`wait_for_revision`), and the revision is the one D0's `deploy.sh` read from the live registry,
which the cutover does not change. A `services-stable` that times out, a wait that returns naming a
task that never answered, or an apply that exits non-zero is a failed row. Every way out of D2 once
the watch has started, convergence, a failed row or a stop, ends the watch and reads its count:

```bash
shown sitting_browser unwatch main
shown awk '$1=="login-prompt" && !p {n++; p=1} $1=="signed-in" {p=0} END {print n+0}' "$E/browser.log"
shown grep -E '^(login-prompt|tunnel-down|timeout|other|not-found|failed) ' "$E/browser.log"
```

The watch writes no line before its first tick, an `--every` away, so a stop in that interval reads
the confirm's empty log: a count of 0, no lines and `grep`'s `exit=1`. The apply has not run then, so
that 0 observed no roll, and the post names the empty log as missing coverage (#1135).

After convergence, and only then, the sitting browser makes the login. `/bff/login` discards the
profile's session first, and the cleared provider makes it fill the form:

```bash
shown sitting_browser clear-provider main
shown sitting_browser login main --app app1
shown sitting_browser session main
```

A `signed-in` line at the revision `spactl wait` named, and `session` answering `http=200`, is #348
Q32's post-convergence login, and "carries into row 3" means row 3 runs on this converged cell with
no further apply; row 3's own login is #358's. Rows 3 to 10 bring their own browsers, so nothing after
D2 uses the sitting browser, and every way out of D2 closes it:

```bash
shown sitting_browser stop
```

A failed row or a stop runs this fence, and once the watch has started the count's fence before it,
before its post and before wiz asks what next: a watch left running would write `browser.log` through
D3 onward, and a same-day attempt's `start` would answer `refused reason=running`, a failed D0. A stop
before the watch has started, at the G6 block, the plan's read, the baseline or the confirm, runs this fence alone:
until the confirm's `mv` sets it aside, a `browser.log` in the bundle is an earlier attempt's, and its
count is not this attempt's.

Post `## D2, row 2` on #348: the plan's counts and its one replacement address, the apply's exit,
the baseline's line, the sign-outs observed with the watch's lines that were not `signed-in` quoted
as they read, the tunnel gap as missing coverage, the two task ids with their zones, the readiness
wait's lines, the post-convergence login's lines, and the bundle files (`terraform.tfvars.phase1`,
`cutover.tfplan`, `row2.log`, `browser.log`, and each file under `browser-pages/` when an `other`
wrote one) by SHA-256. A failed row or a stop posts as much of this as it reached: once the watch has
started, the count and the watch's lines included; before that, no count and no watch's lines, among
the files no `browser.log` or `row2.log` and under `browser-pages/` only what this attempt wrote, since
an earlier attempt's stay in the bundle, and the post says the watch never started. The row passes
when a sign-out occurred and the post-convergence login succeeded.

**A failed row with no convergence read (#1134).** An apply that exits non-zero, or a
`services-stable` that times out, ends D2 on a cell nobody has read, and the apply does not wait for
the roll. The service may be rolling to the new task definition, may have finished that roll, may
have been rolled back by its circuit breaker (`infra/modules/cell/ecs.tf`), or may stand as D0 left
it, when the apply stopped before reaching it. After the fences and before the post, read which:

```bash
shown aws ecs describe-services --cluster spa-poc --services gateway \
  --query 'services[0].{desired:desiredCount,running:runningCount,deployments:deployments[].{status:status,rollout:rolloutState,desired:desiredCount,running:runningCount,revision:taskDefinition}}'
```

then the `list-tasks` and `describe-tasks` lines above. More than one deployment, or a `rollout` of
`IN_PROGRESS`, is a roll under way. One settled deployment on a new revision has finished the roll,
and no convergence has been read. One on D0's revision is rolled back when `desired` has reached the
cutover's 2, and is as D0 left it when `desired` is still 1. A new task is a RUNNING one on a revision
other than D0's. The post carries these reads and says whether the tunnel restart ran, with the id it
kept, or that it did not. Asking what next, wiz first says, from those reads: which of the four the
cell is; that "go on" takes D1 and D3 onward to that cell and not to one D2 has read as converged, so
a read or a row can fail for that reason alone, and a pass says nothing of convergence; and whether
D4 has an id, which is one `tunnel.sh` printed in D2 that still names a RUNNING task. "Go on" stays
the human's answer, and wiz holds it to no wait. On it:

- **The tunnel.** If the restart has not run, or the task its id names is no longer RUNNING, and a
  new task is RUNNING, offer it now as above (rule 1), and keep the id; its times and id go into the
  next step's post. With no new task it has nothing to go through yet: wiz says that D4 still has no
  id from D2, and that D0's tunnel is up if the restart never stopped it and down if the hop it moved
  to has gone. D0's id is not one for D4: a row 4 on D0's revision says nothing of the cutover.
  `tunnel.sh` takes the first task it can reach, so mid-roll its id can name an old task that the roll
  then drains: name that task's revision from `describe-tasks` when keeping the id.
- **Each later step, D1 and D3 to D9.** Run the `describe-services` read above first, and open the
  step's post with "D2 failed" and what that read showed. A read that has since settled makes no
  row 2 pass and is no convergence: only D2's own block is that.

**D1, the configuration as decided (row 1; #348 Q24).** Reads only, each a `describe`, `get` or
`list`, through `shown`; never `get-parameter` or `get-parameters`, which return the ring's values
(rule 3). First the handles, each into a variable:

```bash
TD=$(captured aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].taskDefinition' --output text)
TG=$(captured aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].loadBalancers[0].targetGroupArn' --output text)
SG=$(captured aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].networkConfiguration.awsvpcConfiguration.securityGroups[0]' --output text)
TABLE=$(captured aws ecs describe-task-definition --task-definition "$TD" --output text --query "taskDefinition.containerDefinitions[?name=='gateway'] | [0].environment[?name=='Session__TableName'].value | [0]")
ROLE=$(captured aws ecs describe-task-definition --task-definition "$TD" --query 'taskDefinition.taskRoleArn' --output text); ROLE=${ROLE##*/}
POOL=$(captured terraform -chdir=infra/envs/poc/idp output -raw user_pool_id)
```

Then the reads, each as `shown aws ...`, and what each must show:

1. The clients: for each client id in the apps root's `clients` output (`terraform -chdir=infra/envs/poc/apps
   output -json clients` into a variable, then each id through `python3 -I`, never printed),
   `cognito-idp describe-user-pool-client --user-pool-id "$POOL" --client-id "$ID" --query
   'UserPoolClient.RefreshTokenRotation'`: grace 60 on each (#348 row 1, #339).
2. The task definition: `ecs describe-task-definition --task-definition "$TD" --query
   "taskDefinition.containerDefinitions[?name=='gateway'] | [0].{stopTimeout:stopTimeout,
   settings:environment[?starts_with(name, 'Session__') || starts_with(name, 'DataProtection__')]}"`:
   `stopTimeout` 30, `Session__Store` dynamodb, the table name, the ring prefix.
3. The target group: `elbv2 describe-target-group-attributes --target-group-arn "$TG" --query
   "Attributes[?Key=='deregistration_delay.timeout_seconds']"`: 30.
4. The service and its tasks: `ecs describe-services --cluster spa-poc --services gateway --query
   'services[0].{desired:desiredCount,running:runningCount,deployment:deploymentConfiguration,
   publicIp:networkConfiguration.awsvpcConfiguration.assignPublicIp}'`: two running, 100% / 200%,
   the circuit breaker on, no public IP; and D2's `describe-tasks` read again: two zones.
5. The table: `dynamodb describe-table --table-name "$TABLE" --query
   'Table.{keys:KeySchema,billing:BillingModeSummary.BillingMode,indexes:GlobalSecondaryIndexes[].IndexName}'`
   and `dynamodb describe-time-to-live --table-name "$TABLE"`: hash key `pk`, on-demand, the `appId`
   index, time to live on `expiresAt`.
6. The ring: `ssm describe-parameters --parameter-filters Key=Path,Values=/spa-platform/poc/data-protection/
   --query 'Parameters[].{name:Name,type:Type}'`, then for each name `ssm list-tags-for-resource
   --resource-type Parameter --resource-id "$NAME"`: the three tags of `contracts/resource-tags.md`
   on each. `describe-parameters` returns no values.
7. The task policy: `iam list-role-policies --role-name "$ROLE"` and `iam list-attached-role-policies
   --role-name "$ROLE"`, then each inline policy with `iam get-role-policy --role-name "$ROLE"
   --policy-name "$NAME"` and each attached one's default version with `iam get-policy-version`;
   `grep -c DeleteParameter` over the record's copy of each must print 0.
8. D6's network: the subnets the service's `awsvpcConfiguration.subnets` names through `ec2
   describe-subnets --subnet-ids ... --query 'Subnets[].{zone:AvailabilityZone,public:MapPublicIpOnLaunch}'`
   (two zones, no public address on launch); `ec2 describe-vpc-endpoints --filters
   Name=vpc-id,Values=<the subnets' VpcId> --query 'VpcEndpoints[].{service:ServiceName,type:VpcEndpointType}'`
   (the S3 and DynamoDB gateway endpoints); `ec2 describe-nat-gateways --filter
   Name=vpc-id,Values=<id> --query 'NatGateways[?State==`available`] | length(@)'` (one); and `ec2
   describe-security-groups --group-ids "$SG" --query
   'SecurityGroups[0].IpPermissionsEgress[].{from:FromPort,to:ToPort,protocol:IpProtocol}'` (443 only).

A read whose value is not the one above is a failed row (rule 7), posted with the value it showed.
Post `## D1, row 1` on #348: each read's command, exit and the lines that carry the value, with the
task ids from D2.

**D3 to D9, #358's class, one row per invocation (#361 Q8, #442 Q169).** Before D3, rebuild the
configuration from the post-cutover task definition exactly as C2's first block does: D2 changed
the environment, and row 7's onboarding reads it. After a failed D2 that block reads whatever
revision the service names, which can be D0's: the post names it. Then the switches, in every call
that runs a row:

```bash
export SPA_E2E_STACK=deployed SPA_E2E_PLATFORM_CONFIG="$PWD/$E/platform-config.json" SPA_E2E_EVIDENCE="$PWD/$E/continuity"
git check-ignore -q "$E/continuity/x" && mkdir -p "$E/continuity"     # the class refuses a directory git does not ignore
shown dotnet test --project tests/e2e -- --filter-method "Gateway.E2ETests.Deployed.ContinuityTests.Row<n>_*" \
  --report-xunit-junit --results-directory "$E/results-row<n>"
```

The selector is the class summary's, read from the tree before D0; a summary that names another
form wins over the line above. The report flag and its exit 5 are as in C2. Each row test is the
driver and the observer together (#358): it sends the requests, matches each to its task by the
client trace id in the request line's stream, pulls the row's own lever, and writes one JSON
evidence file under `SPA_E2E_EVIDENCE`, holding no cookie, token or key; a rerun of a row reads
that file and spends only the attempts and wall time left (10 attempts and 15 minutes for a
two-task row, reruns included), and a spent row reports `not proved:` without pulling a lever, so a
fresh budget needs a fresh evidence directory, named in the post. The junit file and the evidence
file are the row's bundle files. Each run reads the class summary's mutations and levers for the
row before asking for the approval; the three rows that mutate get it as follows, and a lever or a
mutation the summary does not name stops the sitting:

- **D4 (row 4, F2).** The class runs `aws ecs stop-task` on the task `SPA_TUNNEL_TASK` does not
  name, with a drain, and refuses unless that variable is exactly one of the two RUNNING tasks. Set
  `SPA_TUNNEL_TASK=<the id tunnel.sh printed in D2>` in the call, say which task will be stopped,
  and the human's approval of the row's command is its rule-2 confirm, as C3 treats F-07. After the
  row, the service replaces the task; read the two tasks again before D5 and name the new id. After
  a failed D2 that kept no id, or one whose task is no longer RUNNING, run D2's restart first if a new
  task is RUNNING, and use the id it prints; with none, say so when asking: the row then refuses and
  stops nothing. Its evidence file then records not proved and names only the exception, so post it as
  not proved with the refusal's line from the test's output. A refusal spends no attempt.
- **D6 (row 6, F1; #348 Q29).** The class drives for up to 40 minutes while the operator deploys:
  start the row's `dotnet test` in the background first, then `deploy.sh` on the same commit as C1
  runs it (confirm on launch, C1's shape: the one replacement is the task definition), with no
  tunnel started while it runs. Once `deploy.sh` prints its pass line, restart `tunnel.sh` through a
  new task as D2 does: stop it, note the UTC time, start it again in the background (keep the id it
  prints after `through task`), run `verify-tunnel.sh /healthz`, and note the time it passed. The
  old hop's task drains during the roll, so requests through it fail before the stop; the gap is
  recorded as missing coverage and the new hop's id kept. The class reads the old and new task ids
  and both image digests from ECS and posts nothing itself; `deploy.sh`'s wait names both new task
  ids, and those go into the post.
- **D7 (row 7; #348 Q31).** The class onboards app 3 with #212's F-07 flow (the apps root's plan
  and apply of app 3's three creates, `spactl release publish`, `app add`, `wait` on both tasks) and
  offboards it after the row, failed or not. Show C3's step 1 addresses as the mutations, and the
  approval of the row's command covers them; a plan the class reports with any other address is a
  stop. Then `shown terraform -chdir=infra/envs/poc/apps plan -input=false -lock=false
  -detailed-exitcode` must exit 0: the registry and the apps root are as they were.

Rows 3, 5, 8 and 9 mutate nothing and run on approval of the command. Each row's post, `## D<n>,
row n` on #348, carries the command, the test's verdict line (`passed`, or the message starting
`failed:` or `not proved:`), the task ids the evidence file names, the attempts spent and the wall
time, the junit and evidence files by SHA-256, and for D4, D6 and D7 the lever's output lines. A
`not proved:` row is posted as not proved, never as passed, and the question of "Failure, and the
way out" follows.

**D10, the teardown and the restore (row 10; #361 Q12).** As T: show `teardown.sh`'s header step
list as the case list (it now deletes the ring prefix and the table too), confirm on launch, and
run `shown tools/scripts/teardown.sh --yes`, which ends with `verify-teardown.sh`. Then, on
approval, the restore and its proof:

```bash
shown cp -p "$E/terraform.tfvars.phase1" infra/envs/poc/platform/terraform.tfvars
diff "$E/terraform.tfvars.phase1" infra/envs/poc/platform/terraform.tfvars && echo "restored: no difference"
ls infra/envs/poc/platform/images.auto.tfvars 2>&1    # absent: teardown.sh removed it
```

Post `## D10, row 10` on #348: the teardown's step lines and exit, P-05's `verify-teardown.sh` lines
and its exit, and that `terraform.tfvars` was restored from D2's copy, by the copy's SHA-256. If the
restore was declined, the post says so and names the copy's path. `teardown.sh` never touches
`terraform.tfvars`, so without the restore the next C1 or N would plan the two-task shape while G3
keeps the Phase 1 one (#347 Q7). The bootstrap root stays.

**The end of D.** After D10, and after every stop that declined it, print what was posted where
(each #348 comment URL by its step), what is held, and what the human still owes. Under "Held" print
the tick of #348's first box, which D never makes (rule 5), and say that the gate-evidence route for
G6 reads its exit test from G6's row in `contracts/gates.md` (#364) and runs outside D, and that the
tag is Adam's, on D0's commit, after that report (#348 Q34). D has no gate-evidence or tag step: C4
does not run for G6.
