---
name: wiz
description: Walk a sitting in the platform project. Reads preflight, the tracker and the cell, then runs sitting C from a standing or an absent platform root (deploy, the G3 to G5 rows and P-04 to P-06, the runbook walks of #244, rollback, teardown, from nothing) or sitting D from an absent one (the G6 continuity run of #348, a fresh Phase 1 deploy, the cutover apply to the two-task cell, rows 1 to 10 and the teardown), each command on the human's approval, posts each stage's evidence to its ticket from captured and redacted output, and stops where only the human can act. Never writes a Confirm comment, never applies or destroys without a confirm on the plan it showed.
---

# /wiz: a sitting in the platform project

Tracked in the repository since #135; #468 rewrote sitting C as the sittings of 2026-10-27 ran it,
#361 added sitting D, the G6 continuity run of #348, and #547 added W, the runbook walks of #244.
Sittings A and B are done; they, and the H items that no longer fire, are in `a-and-b.md` beside
this file, for a cell that needs the read pass again.

Read first: `docs/agents/aws-rules.md` (one Region, managed IAM, the spend limit, help level
HIGH: explain each step before running it), `tools/aws-project.env`, `GLOSSARY.md` → *Sitting*,
*Unattended run*, *Read-back*, *Sample image*, *Platform project*, and the tickets this sitting
serves: #242 (the four scripts), #63 (the deployed gate rows), #236 (seed with the profile alone),
#244 (the runbook walks, their observables and their end state), and for D #348 (the G6 run, its
rows and their questions) and #358 (the deployed continuity class).

## Rules that hold in every stage

1. **Every command is shown, explained in one line, and run only on the human's approval.** The
   human may skip a stage; a skipped stage is recorded as skipped.
2. **An apply, a destroy, a deletion or a script that deletes runs only on a confirm given after
   the plan or the case list was shown.** An apply whose plan destroys or replaces anything stops
   the sitting; the human reads the plan and decides outside the wizard. One replacement is
   expected: `deploy.sh` and `rollback.sh` replace the task definition with its new revision, and
   plan and apply with no pause, so their confirm is given on launch, after the preview plan C1
   shows, and names the expected shape. A plan inside them of any other shape is a stop.
3. **Nothing secret is printed or posted:** no authorization code, no client secret, no session
   name, no client id, no ARN, no account id beyond its last four digits. Every AWS read goes to
   the terminal through the stream mode below; every post through the post mode. If a line cannot
   be redacted with confidence, leave it out and say so. The one exception is `a-and-b.md`'s A2,
   which prints the login URL, carrying the client id, to the terminal only, because the human has
   to open it; it goes into the record and never into a post.
4. **Evidence goes to the ticket it serves**, as a comment headed by the stage, carrying the
   commit, the command, its exit code and its key lines. A failed or skipped stage posts "failed"
   or "not run" with the reason, never a sentence that would read the same on success. Each post
   ends with the Claude Code attribution footer.
5. **No `Confirm` comment, ever** (ADR-0021 decision 2). **No relabel, unblock or close** until a
   `Confirm` comment on the run's `Overnight <date>` issue covers the question that decided it
   (ADR-0023 decision 1, ADR-0021 decision 1). A comment that is only the word `Confirm` covers
   every pre-accepted answer of that run and none that read `Answer: open`. Where none covers a
   write, print the write you would make under "Held" and go on.
6. **The record is `.scratch/sitting/log/<UTC date>.log`** in the main checkout: every command,
   every output, unredacted, mode 600. A run's evidence bundle (the junit report, the
   configuration, a script's output) goes under `.scratch/sitting/log/<UTC date>/` (#63's Q75),
   checked with `git check-ignore` before the first write, and is never uploaded: a post names
   each file with its SHA-256. A plan that holds secrets (the apps root's carries client secrets)
   goes to a private directory outside the repository, `P=$(mktemp -d)`, mode 700 as
   `tests/e2e/Stack/CellOnboarding.cs` makes it, and `rm -rf "$P"` after the apply. Neither the
   record nor the bundle goes to `/tmp`.
7. **Stop and name the cause** when preflight is not ready, the account is not the pin, a plan has
   destroys or replaces beyond rule 2's one, a stage's exit code or a read's values are not what
   its ticket expects (an exit of 0 reporting an unhealthy target or another image is a failed
   stage), or AWS answers `AccessDenied` where it did not before (ask about the spend limit: AWS
   Settings → Billing, per `docs/agents/aws-rules.md`).
8. **Run from the main checkout on `main`, up to date with `origin/main`**, so the scripts are the
   landed ones; never commit, switch or rebase there (`tools/main_checkout_guard.py`). Adam lands
   pull requests mid-sitting: fetch and fast-forward again before a deploy, and name the commit.

### The prelude: every Bash call starts with it

The Bash tool's shell is `/bin/bash` (#183) and keeps no state between calls, so each call that
touches AWS begins with this, and runs each read and script as `shown <command>`:

```bash
cd "$(git rev-parse --path-format=absolute --git-common-dir)/.." && set -a && . tools/aws-project.env && set +a
export PATH="$HOME/.docker/bin:$PATH"   # a per-user Docker Desktop; this shell is not a login shell
umask 077; R=".scratch/sitting/log/$(date -u +%F).log"; E="${R%.log}"; git check-ignore -q "$E/x" && mkdir -p "$E"
shown() { echo "\$ $*" >> "$R"; "$@" 2>&1 | tee -a "$R" | python3 -I tools/wiz_redact.py --stream; echo "exit=${PIPESTATUS[0]}" | tee -a "$R"; }
captured() { echo "\$ $*" >> "$R"; "$@" 2> >(tee -a "$R" | python3 -I tools/wiz_redact.py --stream >&2) | tee -a "$R"; local rc=${PIPESTATUS[0]}; echo "exit=$rc" | tee -a "$R" >&2; return "$rc"; }
spactl() { dotnet run --project tools/spactl -- "$@"; }   # a function, so SPACTL stays unset (Q80)
```

Shell variables do not survive the call either: read `TD`, `TASK` and the like again in the call
that uses them. A read whose value a later command needs runs as `X=$(captured <command>)`: its
output goes to the variable and the record, its errors and its exit to the record and, masked, to
the terminal, and nothing of it is printed unmasked. `shown` writes the command and its whole output
to the record, unredacted, and prints it through `tools/wiz_redact.py --stream`, which masks account
ids, ARNs and the rest of the script's rules line by line and passes `AccessDenied` through for rule
7. The exit printed is the command's, not the redactor's. Mask nothing by hand with `sed`: macOS
`sed` reads no `\t` inside a bracket, and a task-definition ARN was printed unmasked that way. Name
a read value with `${x##*/}` when only its last field is shown.

### The post mode

Every post goes through `tools/wiz_redact.py` without `--stream`: write the draft under `"$E"`,
pipe it through, and post the output with `--body-file`, never the draft.

```bash
python3 -I tools/wiz_redact.py < "$E/post.md" > "$E/post.redacted.md"; echo "exit=$?"
gh issue comment <n> --body-file "$E/post.redacted.md"     # only on exit=0
```

Its rules are in its header, and `tools/wiz_redact_tests.py` holds each to a case (#259). Add
`--keep-last-four` only where the ticket's grill asks for an account id's last four digits. P-04's
passing case lines, `pass  <case>: 403 AccessDenied (<the kind of policy>)`, go through as they
are; post them verbatim. On exit 3 the draft carries any other `AccessDenied` or `Access Denied`
and the script wrote nothing: post nothing, stop and name the cause (rule 7). On any other non-zero
exit post nothing and say the redaction failed. On exit 0, read the result once as a reviewer
would; the script catches shapes, not meaning.

## Stage 0: where things stand

Ask first whether this is a `--dry-run` (list every command, run nothing, post nothing), so the
answer governs Stage 0 too. Then, each line after the prelude:

```bash
tools/preflight.sh                       # must end ready; it compares the account with the pin; read its Container engine row
git fetch origin --tags && git status --short --branch   # on main, no tracked change
git merge --ff-only --quiet origin/main  # a call of its own: the guard admits exactly this form, --quiet allowed
for n in 63 242 236 244 348; do
  gh issue view "$n" --json number,state,title,body \
    --jq '"#\(.number) \(.state) \(.title)", (.body | split("\n")[] | select(test("^- \\[ \\]")))'
  gh api "repos/{owner}/{repo}/issues/$n/dependencies/blocked_by" --jq '.[] | select(.state == "open") | "  blocked by #\(.number) \(.state) \(.title)"'
done
shown aws ecs describe-services --cluster spa-poc --services gateway \
  --query 'services[0].{status:status,desired:desiredCount,running:runningCount,taskDefinition:taskDefinition}'
shown aws ecr describe-images --repository-name spa-poc/gateway --query 'length(imageDetails)'
```

The open boxes are what this sitting can still tick; a box already ticked is not a blocker. An open
`blocked_by` edge names a ticket whose work gates a row (#63 waits on #242 for P-05 alone, Q78); say
which rows it holds and offer the rest. Then say which root is standing:

- **Standing**, a service with a running task: C1, C2, C3, W, R, T, N, C4, C5.
- **Absent**, no service: N first, then C2 onwards; R needs two gateway builds, so after one
  deploy from nothing it is "not run: one gateway build" (#242's Q70) unless the human deploys again
  at a later commit that changes `src/`. W's four walks need one build only and run either way.

Offer C2 and C3 only when `tests/e2e/Stack/DeployedStack.cs` (#209) is on `main`. List the stages
with their tickets, and ask the human which to run.

**Offering D.** Sitting D (#348) is offered only when every line below holds, and otherwise Stage 0
names each missing piece (a tag not merged, each open edge by number and title, a standing root, a
`Container engine` row not ok, each G6 key the file carries) and offers C instead:

```bash
git tag --merged origin/main | grep -x -E 'g3-deployed-slice|g4-negative-green|g5-phase-1-accepted'   # all three lines, or D is not offered
gh api "repos/{owner}/{repo}/issues/348/dependencies/blocked_by" --jq '[.[] | select(.state == "open")] | length'   # 0, or D is not offered
python3 -I - <<'EOF'
import os, re
root = "infra/envs/poc/platform/"
def keys(path, commented): return {m.group(1) for m in re.finditer(r"^(?:#\s*)?([a-z_]+)\s*=" if commented else r"^([a-z_]+)\s*=", open(root + path).read(), re.M)}
phase1 = {"region", "cell", "vpc_cidr", "certificate_domain", "health_check_path", "additional_teardown_principal_arns", "gateway_image", "issuers"}
g6 = keys("terraform.tfvars.example", True) - phase1
present = os.path.exists(root + "terraform.tfvars")   # git-ignored: absent on a fresh checkout, and N step 3 writes it
print("G6 keys of the example:", sorted(g6)); print("G6 keys set in terraform.tfvars:", sorted(g6 & keys("terraform.tfvars", False)) if present else "none (the file is absent)")
EOF
```

- The three gate tags, `g3-deployed-slice`, `g4-negative-green` and `g5-phase-1-accepted`, are in
  `git tag --merged origin/main`: G6 opens after G5 (#348's Parent).
- Every blocked-by edge of #348 is closed. The loop above lists the open ones; among them are the
  tickets whose parts D reads from the tree (#338 and #347 for the G6 block of
  `terraform.tfvars.example`, #358 for the continuity class), so D is never offered against a tree
  that lacks them.
- The platform root is absent: Stage 0's `describe-services` answers no service, or one with no
  running task and no deployment. D0 deploys from nothing, and a part-run D never resumes.
- preflight's `Container engine` row is ok, as C needs it: D0 and D6 build images.
- `infra/envs/poc/platform/terraform.tfvars` carries none of the G6 keys. The script prints the
  example's G6 keys (every key it names, set or commented out, beyond the Phase 1 eight) and the
  ones `terraform.tfvars` sets; a second list that is not empty is a G6 input, and D is not offered
  until the human removes it, since D0 needs the Phase 1 shape and D2 is what adds the block. A
  `terraform.tfvars` that is absent (it is git-ignored, and N step 3 writes it) carries no key and
  holds nothing against D. An empty first list means the block is not in the tree yet (#338, #347),
  and D is not offered.

D runs alone: a sitting that runs D runs no C stage, and D ends after D10.

## Sitting C: the deploy and the gate runs

**C1, the deploy (#242).** From a standing root, preview first:

```bash
shown terraform -chdir=infra/envs/poc/platform plan -input=false -lock=false -detailed-exitcode   # 0: no drift with the current images
```

Then ask for the confirm on launch of `deploy.sh`, naming the shape it will apply: `1 to add, 1 to
change, 1 to destroy` from a standing gateway, `1 to add, 2 to change, 1 to destroy` from the
sample image, the one replacement being the task definition as its new revision. Run it in the
background (it builds both images for ARM64, pushes by digest, applies, waits for steady state and
asserts the running digest), and watch its output for `waiting through`: `deploy.sh` then waits for
each new task to report the registry revision on `/readyz`, through the tunnel. Start `tunnel.sh`
in the background at that line, so its hop goes through the new task, and then:

```bash
shown tools/scripts/deploy.sh                    # in the background; start the tunnel at "waiting through"
tools/scripts/tunnel.sh                          # in the background, at that line
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
`Gateway.E2ETests.Negative.`, the id read from the method's prefix (`F01_` is F-01). The
`Deployed/*` classes are meta-tests whose data carries ids of its own (`N-28` in
`DeployedSubsetTests`); report their count beside the rows and credit them to no id. Post one
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
git fetch origin --tags && git merge --ff-only --quiet origin/main   # a call of its own, as Stage 0 runs it
git rev-parse HEAD; cat docs/runbooks/<the walk's file>
```

Then run its steps in order, each command as the runbook writes it, under rules 1 to 8. Four
bindings make a runbook written for one shell run through this tool's, and none of them edits a
line of it:

- **The shell.** The notes assume one shell (`README.md` → "Before any note: the operator session")
  and this tool's keeps none (the prelude), so each call sets again the variables the step reads
  (`APP`, `BUCKET`, `RELEASE`, the revision `N` or `M` a step printed, and the like), from the
  record's values. A read whose value a later step needs runs as `X=$(captured <the runbook's
  command>)`, every other command as `shown <the runbook's command>`.
- **`RUN` and `CONFIG`.** The notes' `$RUN` is a private directory of rule 6's kind: `RUN=$(mktemp
  -d)` under `umask 077`, printed once, set by hand in each later call and removed with `rm -rf
  "$RUN"` at the walk's end; secret rotation keeps the new secret there between its steps 2 and 3.
  `$CONFIG` is the platform configuration C2 rebuilt, `"$E/platform-config.json"`: the README says
  to rebuild it after a deployment or a rollback, and C1 was the last of either.
- **Rule 2.** Secret rotation's `terraform apply -refresh-only` in the apps root is an apply: its plan
  is shown, must move the parameter's `version` and nothing else (the runbook's own test), and runs
  on confirm. Its step 7 deletes the old secret at the provider, and step 2 of ending an app's
  sessions removes app 1's entry: each runs on a confirm that names what goes (the secret's count
  before, the app and the release it comes back at). Every other step writes the registry through
  `spactl` or the provider through the CLI and runs on rule 1's approval.
- **Rule 3 and the browser.** No post carries a secret: the second secret's value is written to
  `"$RUN/secret"` and to the parameter and never read back, and the post names the parameter's
  version before and after and the count the runbook's last line prints, never a value, a secret's
  id or a client id. A step that signs in, or reads what a signed-in page shows, is the human's:
  wiz asks, and records the UTC time and what the page showed (signed in, sent to sign in again, a
  404, a 401), never a cookie or a session's name.

**The setup before walk 1.** A cell seeded once holds one release of app 1, so release rollback's
step 1 finds no earlier registry version whose release for app 1 differs, and the runbook has
nothing to roll back to; Q99's "app 1 on its first release" presumes a second. On approval, publish
a second release of app 1 from `fixtures/spa/app1` and activate it (the two `spactl release`
commands C3's step 2 and the runbook's step 2 show, the release named `walk-` and twelve hex
digits), wait for its revision, and post both lines under "## Release rollback" as setup, apart from
the runbook's steps. The walk then rolls app 1 back to the release it served before the setup and
leaves it there: the roll-forward the runbook's last line offers is not run, so that the cell ends
where Q99 asks. Walk 2 reads the revisions walk 1 made (Q95): its plain rollback restores the
content before walk 1's activation, under a new revision, and the runbook's second plain rollback,
which it says goes forward again, restores the content walk 1 left. Both are the walk, each waited
for, and the two revisions restored differ in app 1's entry alone.

**The observables (Q96)**, read before and after each walk as `shown`, through the tunnel or the
CLI with what the operator session offers, each read named in the post, and none needing a session
key, a cookie or a secret:

- release rollback: the served `index.html`'s release marker and `/readyz`'s revision, before and
  after each activation, the setup's included;
- registry rollback: the two revisions restored, with only app 1's entry differing between them;
- secret rotation: a session signed in before step 1 still calling app 1's API after the old secret
  is deleted; a fresh login succeeding once the entry points at the new version and again after the
  deletion; the parameter's version moved; and the apps root's `plan -detailed-exitcode` exiting 0
  at the end, with the file's "Checks for the first walk (#244)" settled (#244 names these steps by
  an older numbering of the runbook; the post cites the file's section headings as they read at the
  commit walked);
- ending an app's sessions: `/bff/user` answering 200 before, the host 404 between the removal and
  the re-add, 401 after the re-add, the purge line step 3 reads through `logs.sh`, and app 2's
  session answering 200 throughout;
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
stops the same way when the CLI or the SCP refuses Cognito's second-secret call (an `AccessDenied` at
step 2 or 7) or the apps root's plan is not empty after step 7, and the follow-up named is ws1's
(Q98). That refusal is the walk's stop, not rule 7's: it is the first such call, not one that worked
before, so the sitting goes on to the next walk. Before the next walk, put back what the stopped
walk's finished steps changed, on approval and with the runbook's own means where it offers one
(release rollback's roll-forward line, a second registry rollback, step 4 of ending an app's
sessions); where it offers none, record what now differs, so that R's end-state line is true.

W ends after walk 4's post. R follows, and the end state Q99 names goes in its post.

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
was not passed, not run or put back by hand, say what differs. When R is not run, that end-state
paragraph goes in its "not run" post, with the gateway on C1's build. The cell is then back to what
it served before W, before T and C5.

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
deployment order from the first root that is absent:

1. `shown tools/scripts/certs.sh status`, then `shown tools/scripts/certs.sh import` when ACM holds
   no `*.spa-poc.test`.
2. The identity root, then the apps root, each command as `shown terraform -chdir=<root> ...`:
   `init -backend-config=backend.hcl`, `plan`, `apply` on confirm. Post both apply summaries on
   #242: this is a new user pool, so new client ids and test user passwords. The apply's Outputs
   block prints each `clientId`; rule 8 masks it in the post, and the post is read once for any
   client id in another shape before it goes.
3. `infra/envs/poc/platform/terraform.tfvars`: back it up first (`cp -p` to `terraform.tfvars.bak`),
   then rewrite `issuers` from the identity root's outputs (`authority` is its `issuer`,
   `browser_origin` is `https://` plus its `cognito_domain`; the map's key and `profile` stay as
   the backed-up file has them), and `gateway_image` to the sample
   image by digest with `health_check_path = "/"`. Read the digest with
   `docker buildx imagetools inspect public.ecr.aws/nginx/nginx-unprivileged:stable-alpine` and write
   it only when it matches `sha256:[0-9a-f]{64}`: ECR Public can answer 429, and an empty digest was
   written once. `images.auto.tfvars` must be absent (`teardown.sh` removes it; a plain `terraform
   destroy` leaves it, and it overrides the sample image).
4. The platform root on the sample image, as `shown`: `plan`, read for the certificate found, no
   per-app resource, and P-01's counts (one load balancer, one listener, one target group, one
   service, one bucket; no NAT gateway, database or instance); `apply` on confirm, then `aws ecs
   wait services-stable`. `infra/envs/poc/outputs.json` is now stale, or absent: a `tunnel.sh` run
   before step 5 answers "no such host", because it reads the cell's names from that file, and
   `seed.sh` is what rewrites it.
5. `shown tools/scripts/seed.sh`, with #236's Q107 capture in its order: the commit; the names only
   of the `AWS_*` variables set; the count of `[spa-platform]` sections in `~/.aws/credentials`;
   seed's whole output to its pass line; `exit=`. Post it on #236 and cite it from #242 (Q103).
6. `deploy.sh` as C1 runs it, from the sample image, then `verify-tunnel.sh /healthz`. Post both on
   #242: this is the second deployment #63's Q79 asks for, and the suite is not run again.

**C4, the gate tags.** For each gate, list the range's merges and their `Reviewed:` lines as
`docs/agents/landing-work.md` → Gates shows, name any merge no record clears, and draft the tag
message with the CI run URL and the #63 comment URLs that hold its deployed rows (#63's Q76). The
human cuts the tag.

**C5, keep the stack or destroy it.** Ask whether the stack stays up (about $30 a month) or is
destroyed until Phase 2's deployed tier; `teardown.sh --yes` on confirm, never a plain `terraform
destroy` of the platform root, which leaves `images.auto.tfvars` to override the next sample-image
apply and a stale `infra/envs/poc/outputs.json` on which `tunnel.sh` answers "no such host".

End the sitting by printing what was posted where, what is held, and what the human still owes.

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
about $0.10 an hour (#347 Q10), and teardown the same day is the practice. A declined D10 is
recorded as "not run: declined", keeps D2's copy of `terraform.tfvars`, and the sitting ends naming
where it is.

**D0, the fresh deploy and the live login (#348 Q22).** The root is absent (Stage 0). Fetch and
fast-forward first (rule 8) and name the commit: D0 deploys the commit `main` is on, which is the
one #348 Q34 tags, and its CI run must be green. Then N's steps 1 to 6 from the first root that is
absent, with `terraform.tfvars` in its Phase 1 shape: N step 3's rewrite, which
`tools/scripts/platform-tfvars.sh` (#470) does, and no G6 key. N's posts for the identity and apps
roots, seed and the deploy go where N sends them; D0's own post is the one below. With the tunnel up
and `verify-tunnel.sh /healthz` passing, the human opens `https://app1.spa-poc.test:8443/` in a
browser, logs in as the test user, sees the app, and keeps that tab: D2 reloads it. Then:

```bash
git rev-parse HEAD
gh run list --workflow CI --commit "$(git rev-parse HEAD)" --json status,conclusion,url --jq '.[] | "\(.status) \(.conclusion) \(.url)"'
TASK=$(captured aws ecs list-tasks --cluster spa-poc --service-name gateway --desired-status RUNNING --query 'taskArns[0]' --output text); echo "task ${TASK##*/}"
shown aws ecs describe-tasks --cluster spa-poc --tasks "$TASK" \
  --query 'tasks[0].{revision:taskDefinitionArn,zone:availabilityZone,containers:containers[].{name:name,digest:imageDigest}}'
```

Post `## D0` on #348 (#348 Q34, #361 Q9): the deployed commit, its CI run's URL and conclusion, both
image digests (the same ones `deploy.sh`'s pass line names), the task id, the task definition
revision, and that the live login reached the app, said as what the page showed and never as the
session's name. A CI run that is not green, or a digest that differs from `deploy.sh`'s pass line,
is a failed D0, and D does not go on.

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
and otherwise creates and in-place updates. Name the counts and the one replacement when asking for
the confirm, and apply the saved plan:

```bash
shown terraform -chdir=infra/envs/poc/platform apply -input=false "$PWD/$E/cutover.tfplan"     # on the confirm, D's rule-2 clause
```

Through the roll, the count is the browser's and nothing else's: the human reloads the D0 tab
every 10 seconds; wiz records the UTC time of each reload that answers with a login prompt, the human
logs in again each time, and the count of those prompts is the row's sign-outs. A reload that fails
while the tunnel is down is missing coverage, never a sign-out. The gateway's log lines go into the
bundle uncounted (`tools/scripts/logs.sh` into `"$E/row2.log"`), because a navigation the shell gate
challenges is a redirect and not a 401 (`GLOSSARY.md` → Shell gate), so the log could count what
the browser never saw. The tunnel's hop is an old task, which the roll drains (#348 Q20): once
`describe-tasks` lists a new task RUNNING, stop `tunnel.sh`, note the UTC time, start it again in
the background (it picks a running task and prints `through task <id>`: keep that id, D4 needs it),
run `verify-tunnel.sh /healthz`, and note the time it passed; the interval between the two times is
the gap, posted as missing coverage. Then convergence:

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
which the cutover does not change. A `services-stable` that times out, or a wait that returns
naming a task that never answered, is a failed row. After convergence the human reloads the tab once
more and logs in: that login is #348 Q32's post-convergence login, and "carries into row 3" means
row 3 runs on this converged cell with no further apply; row 3's own login is #358's.

Post `## D2, row 2` on #348: the plan's counts and its one replacement address, the apply's exit,
the sign-outs observed with their times, the tunnel gap as missing coverage, the two task ids with
their zones, the readiness wait's lines, the post-convergence login, and the bundle files
(`terraform.tfvars.phase1`, `cutover.tfplan`, `row2.log`) by SHA-256. The row passes when a
sign-out occurred and the post-convergence login succeeded.

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
the environment, and row 7's onboarding reads it. Then the switches, in every call that runs a row:

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
  row, the service replaces the task; read the two tasks again before D5 and name the new id.
- **D6 (row 6, F1; #348 Q29).** The class drives for up to 40 minutes while the operator deploys:
  start the row's `dotnet test` in the background first, then `deploy.sh` on the same commit as C1
  runs it (confirm on launch, C1's shape: the one replacement is the task definition), and restart
  `tunnel.sh` at `waiting through` through a new task, its gap recorded as missing coverage and the
  new hop's id kept. The class reads the old and new task ids and both image digests from ECS and
  posts nothing itself; `deploy.sh`'s wait names both new task ids, and those go into the post.
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
G6 waits for #364 (G6's row in `contracts/gates.md`) and #328, and that the tag is Adam's, on D0's
commit, after that report (#348 Q34). D has no gate-evidence or tag step: C4 does not run for G6.

## Outside the sittings

- **H1, the open questions on an `Overnight <date>` issue.** A `Confirm` that is the one word
  covers the pre-accepted answers and none that read `Answer: open`. On request, print a digest
  table of the open ones from the issue's comments (question, recommendation, what it changes). The
  human writes and posts any comment that names them, or has a session post it at their word.
- **H2, #90.** Print the three pre-checks and the close path (AWS Settings → View all projects →
  the sample project → Close project); after the human reports it closed, `rm` the two local files
  with approval, run preflight, and draft the ADR-0017 clause into the log for the integrator.
