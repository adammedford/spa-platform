# /wiz: sitting C

Moved out of `SKILL.md` by #1198. The rules, the prelude, the post mode, the remote route and
Stage 0 are in `SKILL.md`, read before this file; sitting D is in `d.md`, and runs this file's N,
C1, C2, C3 and T where it names them.

## Sitting C: the deploy and the gate runs

**C1, the deploy (#242).** From a standing root, preview first:

```bash
shown terraform -chdir=infra/envs/poc/platform plan -input=false -lock=false -detailed-exitcode   # 0: no drift with the current images
```

Then ask for the confirm on launch of `deploy.sh`, naming the shape it will apply: `1 to add, 1 to
change, 1 to destroy` from a standing gateway, `1 to add, 2 to change, 1 to destroy` from the
sample image, the one replacement being the task definition as its new revision. Run it in the
background (it builds both images for ARM64, pushes by digest, applies, waits for steady state and
asserts the running digest, then waits for each new task to report the registry revision on
`/readyz`), and start no tunnel while it runs: its readiness wait opens a tunnel of its own and
closes it when the wait ends (#1008). Once `deploy.sh` prints its pass line, start `tunnel.sh` in
the background, so its hop goes through a task of the new deployment, for `verify-tunnel.sh
/healthz` and, at the screen, the human's browser; a `tunnel.sh` still running from before is stopped first, since
its hop was a task the deployment replaced:

```bash
shown tools/scripts/deploy.sh                    # in the background; no tunnel while it runs
tools/scripts/tunnel.sh                          # in the background, after deploy.sh's pass line
shown tools/scripts/verify-tunnel.sh /healthz
```

If `deploy.sh` fails with `rollout is IN_PROGRESS` right after the service is stable (#467), the
deployment completes anyway: a re-run plans no changes and reaches its pass line. Post the run on
#242: the commit, the plan's counts, the pass line, the task definition revision and the gateway
digest's first eight hex digits, and one line naming the runbook followed, `docs/runbooks/deploy.md`
at that commit, with any step that differed from it: #244's first box ticks on this post and T's
(#286 Q86), and a step that differed is posted on #244 too.

**C2, the G3 and G4 rows, P-04 and P-06 (#63, one comment per test id, Q75).** The configuration
the suite's F-06 and F-07 need, rebuilt read-only from the running task definition:

```bash
TD=$(captured aws ecs describe-services --cluster spa-poc --services gateway --query 'services[0].taskDefinition' --output text)
captured aws ecs describe-task-definition --task-definition "$TD" --output text \
  --query "taskDefinition.containerDefinitions[?name=='gateway'] | [0].environment[].[name, value]" > "$E/environment.tsv"
python3 -I tools/scripts/cell_documents.py config "$E/environment.tsv" contracts/platform-config.schema.json > "$E/platform-config.json"; echo "exit=$? ${TD##*/}"
```

Then the suite, through the tunnel, in the background (about 18 minutes):

```bash
export SPA_E2E_STACK=deployed SPA_E2E_PLATFORM_CONFIG="$PWD/$E/platform-config.json"
shown dotnet test --project tests/e2e --filter-not-class Gateway.E2ETests.Functional.OnboardingTests \
  --report-xunit-junit --results-directory "$E/results-g3g4"
```

The e2e host has no `--report-trx`: the flag is `--report-xunit-junit`, and an exit of 5 means a
bad flag with zero tests run, so a 5 is a failed stage and not a result.

Count toward an id only the test cases whose class is under `Gateway.E2ETests.Functional.` or
`Gateway.E2ETests.Negative.`, the id read from the method's prefix (`F01_` is F-01), and the
registered continuity Facts: a test case of `Gateway.E2ETests.Deployed.ContinuityTests` counts toward
the id whose row in `contracts/test-ids.md` names its method. The register says which ids those are:
read them there and not from a list here. A method no row names (`Row7_`) counts toward none, and a
row naming two methods is refused. The other `Deployed/*` classes are meta-tests whose data carries
ids of its own (`N-28` in `DeployedSubsetTests`); report their count beside the rows and credit them
to no id. `tools/bench/e2e_evidence.py` applies this rule. Post one
comment per id on #63, as the F-01 comment of 2026-10-27 does: the deploy it ran on, the command,
the totals, the id's tests with their results, and the junit file's name and SHA-256.

P-04 and P-06 run on the same deploy:

```bash
shown tools/scripts/verify-release-immutability.sh --clean-up-as-teardown-role   # show the header's case list first
TASK=$(captured aws ecs list-tasks --cluster spa-poc --service-name gateway --desired-status RUNNING --query 'taskArns[0]' --output text)
ENI=$(captured aws ecs describe-tasks --cluster spa-poc --tasks "$TASK" --output text \
  --query "tasks[0].attachments[0].details[?name=='networkInterfaceId'].value | [0]")
IP=$(captured aws ec2 describe-network-interfaces --network-interface-ids "$ENI" --query 'NetworkInterfaces[0].Association.PublicIp' --output text)
for port in 8080 443; do nc -G 5 -z "$IP" "$port" > /dev/null 2>&1; echo "$port exit=$?"; done | tee -a "$R"   # nc names the address on stderr
```

P-04 passes on exit 0; post its whole output on #63. P-06 passes when 8080's probe exits non-zero
after its five seconds; 443 is the control. On macOS `-G` is the connect timeout, while `-w` is an
idle timeout that leaves the probe waiting about 75 seconds. The address is never printed or posted.

**C3, F-07 (#63, G5).** On the profile alone (#63's Q80, #236's Q105): only `AWS_PROFILE` and
`AWS_REGION` set, no `[spa-platform]` section in `~/.aws/credentials`, and `SPACTL` unset, so
spactl runs as `dotnet run --project tools/spactl`.

```bash
unset SPACTL; env | grep -o '^AWS_[A-Z_]*' | sort; grep -c '^\[spa-platform\]' ~/.aws/credentials
export SPA_E2E_STACK=deployed SPA_E2E_PLATFORM_CONFIG="$PWD/$E/platform-config.json"
shown dotnet test --project tests/e2e --filter-class Gateway.E2ETests.Functional.OnboardingTests \
  --report-xunit-junit --results-directory "$E/results-f07"
```

The report flag and its exit 5 are as in C2.

The test onboards and offboards app 3 in one run, so the platform-plan clause ("the platform plan
is empty with app 3 served") is a second onboarding by hand, in the test's own steps. #63's F-07
comment of 2026-10-27 is the worked example; post the same shape.

1. `P=$(mktemp -d)`; print it, since later calls set `P` again by hand. Then the apps root's plan
   with the onboarding directory, which must exit 2 with exactly app 3's three creates
   (`aws_cognito_managed_login_branding.app["app3"]`, `aws_cognito_user_pool_client.app["app3"]`,
   `aws_ssm_parameter.client_secret["app3"]`); show it, and apply the saved plan on confirm:

   ```bash
   shown terraform -chdir=infra/envs/poc/apps plan -input=false -detailed-exitcode -out="$P/onboard.tfplan" \
     -var 'definition_dirs=["../../../../fixtures/apps","../../../../fixtures/onboarding"]'
   shown terraform -chdir=infra/envs/poc/apps apply -input=false "$P/onboard.tfplan"
   ```

   The plan prints app 3's client id as its `id` attribute and its branding's `client_id`, the apply
   as `[id=...]` and in its Outputs block as `clientId`; rule 8 of the redactor masks each of these
   shapes, on the terminal and in a post. A client id in any other shape reaches the terminal, so
   read the output once for one, as the post mode asks.

2. spactl, as the test drives it. `outputs.json` names no client for app 3 until a script rewrites
   it, so `app add` reads a private copy with the apps root's `clients` output for app 3 merged in
   (`CellOnboarding.cs` does the same). Set `P` to the directory step 1 made, `RELEASE` to the
   test's release (`onboarding-<twelve hex>`, from the test's output; a second publish of it
   writes nothing), and `REVISION` to the one `app add` prints, before `wait`:

   ```bash
   P=<step 1's directory>; RELEASE=<the test's release>
   terraform -chdir=infra/envs/poc/apps output -json clients > "$P/clients.json"
   python3 -I -c 'import json, sys; o = json.load(open(sys.argv[1])); o["clients"]["app3"] = json.load(open(sys.argv[2]))["app3"]; json.dump(o, open(sys.argv[3], "w"))' \
     infra/envs/poc/outputs.json "$P/clients.json" "$P/outputs.json"
   shown spactl release publish app3 fixtures/spa/app3 "$RELEASE" --outputs "$P/outputs.json"
   shown spactl app add fixtures/onboarding/app3.json --release "$RELEASE" --config "$E/platform-config.json" --outputs "$P/outputs.json"
   ```

   Then, in a call of its own, `shown spactl wait --url https://app1.spa-poc.test:8443 --revision
   <REVISION>`.

3. The task's id, start time and revision, then the platform root's plan, which must exit 0, then
   the task again, which must be the same:

   ```bash
   TASK=$(captured aws ecs list-tasks --cluster spa-poc --service-name gateway --desired-status RUNNING --query 'taskArns[0]' --output text)
   echo "task ${TASK##*/}"
   shown aws ecs describe-tasks --cluster spa-poc --tasks "$TASK" --query 'tasks[0].{started:startedAt,revision:taskDefinitionArn}'
   shown terraform -chdir=infra/envs/poc/platform plan -input=false -lock=false -detailed-exitcode
   shown aws ecs describe-tasks --cluster spa-poc --tasks "$TASK" --query 'tasks[0].{started:startedAt,revision:taskDefinitionArn}'
   ```

4. Offboard, each command as `shown`: `spactl app remove app3 --config "$E/platform-config.json"
   --outputs infra/envs/poc/outputs.json` and `wait` on its revision; the apps root's plan without
   the `-var`, saved under `"$P"`, exactly the three deletes, applied on confirm; a re-plan exits 0;
   then `rm -rf "$P"`.

Post on #63: the test's run, and the clause's five steps with addresses and actions only.

**W, the runbook walks (#244 boxes 2 to 6; #286 Q86 to Q99).** After C3 and before R, where Adam
placed them once #468 had put R, T and N between C3 and C4
(https://github.com/adammedford/spa-platform/issues/286#issuecomment-6507230119). A walk runs every
command of one runbook once, as written on `main`, against the deployed cell, and passes only if the
runbook worked unedited (Q87). Five walks, in Q95's order, each from its file under `docs/runbooks/`:

1. Release rollback, `release-rollback.md`.
2. Registry rollback, `registry-rollback.md`.
3. Secret rotation, `secret-rotation.md`.
4. Ending an app's sessions, `end-app-sessions.md`.
5. Gateway rollback, `rollback.md`: this one is R itself, below, last because it signs every user
   out.

This file holds no copy of any runbook command (Q90): the runbook, read from the tree at the walk,
is the text that runs, so a fix to a runbook reaches the next sitting without a change here. Before
each walk, fast-forward (rule 8), name the commit, and read the file to the human section by
section:

```bash
git fetch origin --tags
git merge --ff-only --quiet origin/main  # a call of its own, as Stage 0 runs it: the guard admits exactly this form
git rev-parse HEAD; cat docs/runbooks/<the walk's file>
```

Then run its steps in order, each command as the runbook writes it, under rules 1 to 8. Four
bindings make a runbook written for one shell run through this tool's, and none of them edits a
line of it:

- **The shell.** The notes assume one shell (`README.md` → "Before any note: the operator session")
  and this tool's keeps none (the prelude), so each call sets again the variables the step reads
  (`APP`, `RELEASE`, `RUN`, the revision `N` or `M` a step printed, and the like), from the
  record's values. A value rule 3 keeps off the terminal (`BUCKET`, which carries the account id;
  `CLIENT_ID`, `PARAMETER`, `POOL_ID`; a secret's id) is never typed from the record: the call that
  uses it runs the runbook's own read of it again, and where no read gives it again
  (`OLD_SECRET_ID`, once the second secret exists) the call that read it writes it to a file under
  `"$RUN"` and the later call reads it back from there. A read whose value a later step needs runs
  as `X=$(captured <the runbook's command>)`, every other simple command as `shown <the runbook's
  command>`. `shown` and `captured` wrap one simple command: a runbook line that is a pipeline or
  writes to a file runs bare, as written, with its command echoed into `"$R"` and its printed
  output appended by `tee -a "$R"`, because wrapping its first command would send that command's
  output to the terminal and the record, which for secret rotation's `openssl` line is the secret
  itself and for its `printf | wc -w` line a secret's id.
- **`RUN` and `CONFIG`.** The notes' `$RUN` is a private directory of rule 6's kind: `RUN=$(mktemp
  -d)` under `umask 077`, printed once, set by hand in each later call and removed with `rm -rf
  "$RUN"` at the walk's end; secret rotation keeps the new secret there between its steps 2 and 3.
  `$CONFIG` is the platform configuration C2 rebuilt, `"$E/platform-config.json"`: the README says
  to rebuild it after a deployment or a rollback, and C1 was the last of either.
- **Rule 2.** Secret rotation's refresh-only apply (its step 4) asks for a `yes` this tool's shell
  cannot type: run without one it prints its plan and exits 1 with `error asking for approval:
  EOF`, applying nothing. So first `shown terraform -chdir=infra/envs/poc/apps plan -refresh-only
  -input=false -out="$RUN/refresh.tfplan"`, a read, whose plan must move the parameter's `version`
  and nothing else (the runbook's own test). The route's plan list on that file leads the confirm,
  with `resource_drift` in place of `resource_changes`: a refresh-only plan reports its moves there
  and has no `resource_changes`, so the line as written prints no rows, and with the swap an empty
  list is a refresh that moved nothing, which fails that test (#1136). Then, on confirm, the
  runbook's apply line as written with the approval on its standard input, `printf 'yes\n' | shown
  terraform ...`, which is rule 2's confirm given through the shell and not an edit of the line.
  The deletion of the old secret at the provider (secret rotation's
  step 7) and the removal of app 1's entry (step 2 of ending an app's sessions) each run on a
  confirm that names what goes (the secret's count before, the app and the release it comes back
  at). Every other step writes the registry through `spactl` or the provider through the CLI and
  runs on rule 1's approval.
- **Rule 3 and the browser.** No post carries a secret: the second secret's value is written to
  `"$RUN/secret"` and to the parameter and never read back, and the post names the parameter's
  version before and after and the count the runbook's last line prints, never a value, a secret's
  id or a client id. A step that signs in, or reads what a signed-in page shows, is the human's:
  wiz asks, and records the UTC time and what the page showed (signed in with its `expiresAt`, sent
  to sign in again, a 404, the `index.html` marker's text), never a cookie or a session's name. A
  session is read on the app page's session panel, which fetches `GET /bff/session`; typed as an
  address that endpoint answers 403 `origin_rejected`. When the human signs in as a test user,
  `wiz_password <user>` puts the password on the clipboard and prints only its length and the time
  the clipboard clears: a background job empties it after 45 seconds. An app host's `/` is behind
  the shell gate (`GLOSSARY.md` → Shell gate: an unauthenticated navigation is challenged), so what
  an app serves is read in the human's signed-in tab, never by a `shown` request.

**The setup before walk 1.** A cell seeded once holds one release of app 1, so release rollback's
step 1 finds no earlier registry version whose release for app 1 differs, and the runbook has
nothing to roll back to; Q99's "app 1 on its first release" presumes a second. On approval, publish
a second release of app 1 and activate it (the two `spactl release` commands C3's step 2 and the
runbook's step 2 show, the release named `walk-` and twelve hex digits, a name
`contracts/registry.schema.json`'s release pattern admits), wait for its revision, and post both
lines under "## Release rollback" as setup, apart from the runbook's steps. Publish it from a copy
of `fixtures/spa/app1` under `"$RUN"` whose `<h1 id="marker">` line names the walk release, as
`tests/e2e/Functional/ShellTests.cs` makes its second release: the fixture published unchanged
would serve the same `index.html` as the first release, and Q96's marker could not tell the two
apart. The walk then rolls app 1 back to the release it served before the setup and
leaves it there: the roll-forward the runbook's last line offers is not run, so that the cell ends
where Q99 asks. Walk 2 reads the revisions walk 1 made (Q95): its plain rollback restores the
content before walk 1's activation, under a new revision, and the runbook's second plain rollback,
which it says goes forward again, restores the content walk 1 left. Both are the walk, each waited
for, and the two revisions restored differ in app 1's entry alone.

**The observables (Q96)**, read before and after each walk as `shown`, through the tunnel or the
CLI with what the operator session offers, each read named in the post, and none needing a session
key, a cookie or a secret:

- release rollback: the served `index.html`'s release marker, read in the human's signed-in tab
  (the browser binding), and `/readyz`'s revision, before and after each activation, the setup's
  included;
- registry rollback: the two revisions restored, with only app 1's entry differing between them;
- secret rotation: a session signed in before step 1 still calling app 1's API after the old secret
  is deleted; a fresh login succeeding once the entry points at the new version and again after the
  deletion; the parameter's version moved; and the apps root's `plan -detailed-exitcode` exiting 0
  at the end, with the file's "Checks for the first walk (#244)" settled (#244 names these steps by
  the plan's four-step count of `docs/plan/05-infrastructure-and-cost.md` → Rotating a client
  secret, where step 3 points the entry and step 4 deletes the old secret, not by the runbook's
  seven headings; the post cites the file's section headings as they read at the commit walked);
- ending an app's sessions: app 1's session panel signed in before, with its `expiresAt`; the host
  404 between the removal and the re-add; a new session after the re-add, shown by a changed
  `expiresAt` with or without a login prompt, since a live Cognito session signs the browser in
  again silently; the purge line step 3 reads through `logs.sh`, read again when the first read
  comes before the line reaches the log; and app 2's session keeping its `expiresAt` throughout;
- gateway rollback: R's, below.

**The post**, one comment per walk on #244 through the post mode, headed by the walk's name as
#244's boxes read it (`## Release rollback`, `## Registry rollback`, `## Secret rotation`, `## Ending
an app's sessions`, `## Gateway rollback`; Q88), and carrying, per rule 4 and Q87: the runbook file
and the `main` commit followed; each command, its exit code and its key lines; the observables before
and after; the setup where one ran; each bundle file by name and SHA-256; and the footer. The walk
is posted as passed only when every step ran unedited and the observables read as Q96 says. A walk
the human skips is posted "not run" with the reason (rule 1). W makes no other tracker write: the
box ticks are Adam's, under rule 5.

**The stop rule (Q87, Q98).** The first step that does not work unedited ends the walk: an exit the
runbook does not expect, an output other than the one it says the step prints, a line that had to
change to run (a path, a flag, a name), or one of the file's "Checks for the first walk (#244)" that
does not hold. Post the walk as not passed at that step, with the command, its exit and its lines,
and print under "Held" the ws6 follow-up the human files (`**Raised by:** #244`) that fixes the
runbook; that runbook is walked again in a later sitting once the fix is on `main`. Secret rotation
stops the same way when the CLI or the SCP refuses Cognito's second-secret call (an `AccessDenied`
adding the second secret, step 2, or deleting the old one, step 7) or the apps root's plan is not
empty after the deletion, and the follow-up named is ws1's
(Q98). That refusal is the walk's stop, not rule 7's: it is the first such call, not one that worked
before, so the sitting goes on to the next walk. Its post names the step and says the provider
refused the call; the refusing line stays in the record and never enters the draft, since the post
mode exits 3 on that word and posts nothing. Before the next walk, put back what the stopped
walk's finished steps changed, on approval and with the runbook's own means where it offers one
(release rollback's roll-forward line, a second registry rollback, the re-add that ends an app's
sessions, its step 4); where it offers none, record what now differs, so that R's end-state line is
true.

W ends after walk 4's post. R follows, and the end state Q99 names goes in its post; when R
neither runs nor posts, because #244's box already ticks on #242's run and #242 needs no rollback,
the end-state paragraph goes in walk 4's post, with the gateway on C1's build.

**R, gateway rollback: `rollback.sh` by its runbook (#242 box 6, #244 box 6; #286 Q70, Q97),**
after W, so that the G3 to G5 rows share one deploy (#63's Q84) and because it signs every user out
(Q95). It is W's fifth walk, run from `docs/runbooks/rollback.md` under W's bindings, read and
named as W reads each file; the script is one of that runbook's commands, and this file does not
copy it. First, from Stage 0's read of the boxes: when #244's gateway-rollback box is open while
#242's box 6 is ticked, ask whether that run followed `rollback.md` on `main` unedited and its post
names the runbook and the commit. If so, #244's box ticks on that run (Q97): print the tick under
"Held", post nothing new on #244 for it, and R runs only if #242 still needs a rollback. Otherwise R
is the walk. It needs two gateway builds in ECR with different `src/` (Stage 0's `describe-images`
count, Q70); with one, post the box on #242 and the walk on #244 as "not run: one gateway build",
and never rely on a rebuild of the same commit (Q97). The script plans and applies with no pause, so
preview as C1 does and confirm on launch with the same shape (rule 2). Before and after: the
running digest, the targets healthy, and a fresh login (Q96).

Post on #242: the revision and digest it left, and its pass line. Post on #244 under "## Gateway
rollback" the walk's shape, ending with the end state Q99 names, as the last walk's post: app 1 on
its first release, the registry content as before under a later revision, the new secret live and
the old one deleted, app 1 re-added, and the gateway on the build the rollback chose; where a walk
was not passed, not run or put back by hand, say what differs. When R is not run for want of a
second build, that end-state paragraph goes in its "not run" post, with the gateway on C1's build;
when R neither runs nor posts, it went in walk 4's post. The cell is then back to what it served
before W, before T and C5.

**T, `teardown.sh --yes` and P-05 (#242 box 4, P-05 on #63).** Show the header's step list as the
case list; it destroys the platform, apps and identity roots, the imported certificate and the key
ring, and ends with `verify-teardown.sh`, whose exit is its own. Confirm on launch:

```bash
shown tools/scripts/teardown.sh --yes
```

Post the run on #242 and P-05 (the verify-teardown lines and the exit) on #63, the #242 post with
one line naming the runbook followed, `docs/runbooks/teardown.md` at the commit, and any step that
differed from it (#286 Q86, as C1's post does). The bootstrap root stays.

**N, from nothing (#242, Q61 and Q79).** `docs/plan/05-infrastructure-and-cost.md`'s First
deployment order from the first root that is absent. Steps 2 and 4 plan and apply a root by **N's
saved plan**, so the route's plan list has a file to read and the apply is the plan the confirm was
given on (#1136). The file goes in rule 6's private directory whichever the root, since nothing here
reads it after the apply:

```bash
P=$(mktemp -d); echo "$P"
shown terraform -chdir=<root> plan -input=false -out="$P/n.tfplan"
```

Later calls set `P` again by hand. Run the route's plan list on `"$P/n.tfplan"` and ask for the
confirm, then:

```bash
shown terraform -chdir=<root> apply -input=false "$P/n.tfplan"
rm -rf "$P"
```

A declined confirm removes the directory too. A plan rule 2 stops on keeps it until the human has
read the file in the ssh login the route names, and then removes it.

1. `shown tools/scripts/certs.sh status`, then `shown tools/scripts/certs.sh import` when ACM holds
   no `*.spa-poc.test`.
2. The identity root, then the apps root: `shown guarded_init <root>`, the prelude's function,
   which runs the root's `init` under a guard. It compares the bucket and key the root is
   configured with (the bucket from `backend.hcl`; the key from `backend.hcl` when it sets one,
   otherwise from the `backend "s3"` block of the root's `versions.tf`) with those of the cached
   backend in the root's `.terraform/terraform.tfstate`, and prints which branch it took and never
   the bucket's name. With no cached backend it runs `init -backend-config=backend.hcl`. On a
   match it runs `init -reconfigure -backend-config=backend.hcl` (the state is where `backend.hcl`
   says, so a cache an earlier sitting left with other settings is rewritten and nothing is
   migrated). On a difference it runs no `init` and exits non-zero: stop and name the cause, since
   a moved key would point the root at empty state, and its plan would show only creates, which
   rule 2 does not stop. Then N's saved plan. Post both apply summaries on #242: this is a new
   user pool, so new client ids and test user passwords. The apply's Outputs block prints each
   `clientId`; rule 8 masks it in the post, and the post is read once for any client id in another
   shape before it goes.
3. `infra/envs/poc/platform/terraform.tfvars` (git-ignored: on a fresh checkout copy
   `terraform.tfvars.example` to it first): back it up first (`cp -p` to `terraform.tfvars.bak`),
   then `shown tools/scripts/platform-tfvars.sh` (#470), which rewrites `issuers` from the identity
   root's outputs and pins `gateway_image` to the sample image by its current digest, with
   `health_check_path = "/"`, and leaves the file as it was unless every value is well formed. Show
   its output: it names which keys changed and prints no value but the digest. When it exits 1 on
   the digest lookup (ECR Public can answer 429, and an empty digest was written once by hand), run
   it again as `shown tools/scripts/platform-tfvars.sh --sample-digest sha256:<64 hex>` with the
   digest the human supplies. `images.auto.tfvars` must be absent (`teardown.sh` removes it; a
   plain `terraform destroy` leaves it, and it overrides the sample image).
4. The platform root on the sample image, by N's saved plan, its plan read for the certificate
   found, no per-app resource, and P-01's counts (one load balancer, one listener, one target group,
   one service, one bucket; no NAT gateway, database or instance); then `aws ecs wait
   services-stable`. `infra/envs/poc/outputs.json` is now stale, or absent: a `tunnel.sh` run
   before step 5 answers "no such host", because it reads the cell's names from that file, and
   `seed.sh` is what rewrites it.
5. `shown tools/scripts/seed.sh`, with #236's Q107 capture in its order: the commit; the names only
   of the `AWS_*` variables set; the count of `[spa-platform]` sections in `~/.aws/credentials`;
   seed's whole output to its pass line; `exit=`. Post it on #236 and cite it from #242 (Q103).
6. `deploy.sh` as C1 runs it, from the sample image, with no tunnel while it runs; after its pass
   line, `tunnel.sh` in the background, then `verify-tunnel.sh /healthz`. Post both on #242: this
   is the second deployment #63's Q79 asks for, and the suite is not run again.

**C4, the gate tags.** For each gate, list the range's merges and their `Reviewed:` lines as
`docs/agents/landing-work.md` → Gates shows, name any merge no record clears, and draft the tag
message with the CI run URL and the #63 comment URLs that hold its deployed rows (#63's Q76). The
human cuts the tag.

**C5, keep the stack or destroy it.** Ask whether the stack stays up (about $30 a month) or is
destroyed until Phase 2's deployed tier; `teardown.sh --yes` on confirm, never a plain `terraform
destroy` of the platform root, which leaves `images.auto.tfvars` to override the next sample-image
apply and a stale `infra/envs/poc/outputs.json` on which `tunnel.sh` answers "no such host".

End the sitting by printing what was posted where, what is held, and what the human still owes.
