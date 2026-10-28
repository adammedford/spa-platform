---
name: wiz
description: Walk a sitting in the platform project. Reads preflight, the tracker and the cell, then runs sitting C from a standing or an absent platform root (deploy, the G3 to G5 rows and P-04 to P-06, rollback, teardown, from nothing), each command on the human's approval, posts each stage's evidence to its ticket from captured and redacted output, and stops where only the human can act. Never writes a Confirm comment, never applies or destroys without a confirm on the plan it showed.
---

# /wiz: a sitting in the platform project

Tracked in the repository since #135; #468 rewrote sitting C as the sittings of 2026-10-27 ran it.
Sittings A and B are done; they, and the H items that no longer fire, are in `a-and-b.md` beside
this file, for a cell that needs the read pass again.

Read first: `docs/agents/aws-rules.md` (one Region, managed IAM, the spend limit, help level
HIGH: explain each step before running it), `tools/aws-project.env`, `GLOSSARY.md` → *Sitting*,
*Unattended run*, *Read-back*, *Sample image*, *Platform project*, and the tickets this sitting
serves: #242 (the four scripts), #63 (the deployed gate rows), #236 (seed with the profile alone).

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
for n in 63 242 236; do
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

- **Standing**, a service with a running task: C1, C2, C3, R, T, N, C4, C5.
- **Absent**, no service: N first, then C2 onwards; R needs two gateway builds, so after one
  deploy from nothing it is "not run: one gateway build" (#242's Q70) unless the human deploys again
  at a later commit that changes `src/`.

Offer C2 and C3 only when `tests/e2e/Stack/DeployedStack.cs` (#209) is on `main`. List the stages
with their tickets, and ask the human which to run.

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
digest's first eight hex digits.

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

**R, `rollback.sh` (#242 box 6),** after F-07, so that the G3 to G5 rows share one deploy (#63's
Q84). It needs two gateway builds in ECR (Stage 0's `describe-images` count); with one, post the box
as "not run: one gateway build" (Q70). It plans and applies with no pause, so preview as C1 does and
confirm on launch with the same shape:

```bash
shown tools/scripts/rollback.sh                  # the previous gateway image; --to-revision N for another
```

Post on #242: the revision and digest it left, and its pass line.

**T, `teardown.sh --yes` and P-05 (#242 box 4, P-05 on #63).** Show the header's step list as the
case list; it destroys the platform, apps and identity roots, the imported certificate and the key
ring, and ends with `verify-teardown.sh`, whose exit is its own. Confirm on launch:

```bash
shown tools/scripts/teardown.sh --yes
```

Post the run on #242 and P-05 (the verify-teardown lines and the exit) on #63. The bootstrap root
stays.

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

## Outside the sittings

- **H1, the open questions on an `Overnight <date>` issue.** A `Confirm` that is the one word
  covers the pre-accepted answers and none that read `Answer: open`. On request, print a digest
  table of the open ones from the issue's comments (question, recommendation, what it changes). The
  human writes and posts any comment that names them, or has a session post it at their word.
- **H2, #90.** Print the three pre-checks and the close path (AWS Settings → View all projects →
  the sample project → Close project); after the human reports it closed, `rm` the two local files
  with approval, run preflight, and draft the ADR-0017 clause into the log for the integrator.
