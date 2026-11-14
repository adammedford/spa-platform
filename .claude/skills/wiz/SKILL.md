---
name: wiz
description: Walk a sitting in the platform project. Reads preflight, the tracker and the cell, then runs sitting C from a standing or an absent platform root (deploy, the G3 to G5 rows and P-04 to P-06, the runbook walks of #244, rollback, teardown, from nothing) or sitting D from an absent one (the G6 continuity run of #348, a fresh Phase 1 deploy, the cutover apply to the two-task cell, rows 1 to 10 and the teardown), each command on the human's approval, posts each stage's evidence to its ticket from captured and redacted output, and stops where only the human can act. Never writes a Confirm comment, never applies or destroys without a confirm on the plan it showed.
---

# /wiz: a sitting in the platform project

Tracked in the repository since #135; #468 rewrote sitting C as the sittings of 2026-10-27 ran it,
#361 added sitting D, the G6 continuity run of #348, #547 added W, the runbook walks of #244, and
#1095 added the remote route.
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
   to open it; it goes into the record and never into a post. The code from `aws login --remote` is
   typed only in the human's ssh window, and is never asked for, printed or posted by the agent
   (#1090 Q2).
4. **Evidence goes to the ticket it serves**, as a comment headed by the stage, carrying the
   commit, the command, its exit code and its key lines. A failed or skipped stage posts "failed"
   or "not run" with the reason, never a sentence that would read the same on success. Each post
   ends with the Claude Code attribution footer.
5. **No `Confirm` comment, ever** (ADR-0021 decision 2). **No relabel, unblock or close** until a
   `Confirm` comment on the run's `Overnight <date>` issue covers the question that decided it
   (ADR-0023 decision 1, ADR-0021 decision 1). A comment that is only the word `Confirm` covers
   every pre-accepted answer of that run and none that read `Answer: open`. Where none covers a
   write, print the write you would make under "Held" and go on.
6. **The record is `.scratch/sitting/log/<pinned date>.log`** in the main checkout, named after
   the UTC date Stage 0 pins for the whole sitting (the prelude): every command, every output,
   unredacted, mode 600. A run's evidence bundle (the junit report, the
   configuration, a script's output) goes under `.scratch/sitting/log/<pinned date>/` (#63's Q75),
   checked with `git check-ignore` before the first write, and is never uploaded: a post names
   each file with its SHA-256. A plan that holds secrets (the apps root's carries client secrets)
   goes to a private directory outside the repository, `P=$(mktemp -d)`, mode 700 as
   `tests/e2e/Stack/CellOnboarding.cs` makes it, and `rm -rf "$P"` after the apply. Neither the
   record nor the bundle goes to `/tmp`.
7. **Stop and name the cause** when preflight is not ready, the account is not the pin, a plan has
   destroys or replaces beyond rule 2's one, a stage's exit code or a read's values are not what
   its ticket expects (an exit of 0 reporting an unhealthy target or another image is a failed
   stage), or AWS answers `AccessDenied` where it did not before (ask about the spend limit: AWS
   Settings → Billing, per `docs/agents/aws-rules.md`). An expired token and a lost `claude` are
   stops too, each as "The remote route" says.
8. **Run from the main checkout on `main`, up to date with `origin/main`**, so the scripts are the
   landed ones; never commit, switch or rebase there (`tools/main_checkout_guard.py`). Adam lands
   pull requests mid-sitting: fetch and fast-forward again before a deploy, and name the commit.

### The prelude: every Bash call starts with it

The Bash tool's shell is `/bin/bash` (#183) and keeps no state between calls, so each call that
touches AWS begins with this one line, and runs each read and script as `shown <command>`:

```bash
. "$(git rev-parse --path-format=absolute --git-common-dir)/../tools/wiz_prelude.sh"
```

The prelude is `tools/wiz_prelude.sh`, one file (#1010), so no call carries a hand copy that can
leave a line out: in sitting D one left out the Docker `PATH` line and `platform-tfvars.sh` failed
on `docker-credential-desktop`. Its header says what it does, and `tools/wiz_prelude_tests.py`
holds it to cases. Sourced, it moves to the main checkout's top level (rule 8), exports the profile
and Region of `tools/aws-project.env`, puts the per-user Docker Desktop on `PATH`, sets the
owner-only file mask, names the record `R` and the bundle `E` (rule 6) and makes the bundle's
directory once git confirms it is ignored, and defines `shown`, `captured`, `spactl`, a function so
`SPACTL` stays unset (Q80), `sitting_browser`, which D0 and D2 drive, `wiz_password`, and
`guarded_init`, which N's step 2 runs. Never copy its commands into a call or into this file.

The record and bundle are named after the sitting's pinned date, never the clock's: a sitting that
crossed midnight UTC once moved both to the next day mid-sitting, and a test run did nothing.
Stage 0 pins it once, with `--pin` (below), in `.scratch/sitting/log/date`, which `log/` keeps
ignored; a pin from an earlier sitting is replaced there. Without a pin, or with one that is not a
single date, the prelude prints a line naming Stage 0 and exits non-zero, so the rest of the call
does not run: run Stage 0's pin, then the call again.

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

### The remote route

The session runs on the mini in VS Code with `/remote-control` on, and the phone app drives it, so
its prompts and confirms are answered there. Ssh over Tailscale is a separate shell for what must
not enter the chat: it cannot attach to this session or answer its prompts (#1090 Q3, Q4). #1090's
Q3, Q6, Q7, Q11 and Q14 read here as its "Decided in a session" comment corrects them.

- **Renewal (#1090 Q2, Q13).** Stage 0 asks for a fresh `aws login --remote --profile spa-platform`
  in the human's own ssh window before its preflight, and waits for the report. When AWS later
  reports an expired token, stop, name that command, and read again on the report. The stage has
  not failed until that read comes back. The token's lifetime is unknown, so this can come at any
  step.
- **Plans (#1090 Q7).** Rule 2's confirm in the app is led by a short list of the plan's resource
  addresses and actions, which summarises the plan and does not replace it:

  ```bash
  filter='import json,sys; p=json.load(sys.stdin); p["planned_values"]; p.get("errored") and sys.exit(2); [print(",".join(c["change"]["actions"]), c["address"]) for c in p.get("resource_changes", []) if c["change"]["actions"] != ["no-op"]]'; echo "\$ terraform -chdir=<root> show -json <plan> | python3 -I -c '$filter'" >> "$R"; terraform -chdir=<root> show -json <plan> | python3 -I -c "$filter" | tee -a "$R"; echo "plan list show exit=${PIPESTATUS[0]} filter exit=${PIPESTATUS[1]}" | tee -a "$R"   # both 0, or stop: no whole plan was read
  ```

  A wrong path, a terraform error, a state file where the plan should be (`show -json` reads either,
  and only a plan has `planned_values`) or the file a failed `plan` saved (terraform sets its `errored`,
  and the filter exits 2) prints no rows, as a plan with no changes does. So no rows are an empty list
  only under `show exit=0 filter exit=0`, and any other verdict is a rule 7 stop (#1137).
  `show -json` writes sensitive values in the clear, so its output goes into this filter and nowhere
  else, and the line runs bare, never under `shown`, with its command echoed into `"$R"` (#1139).
  Where rule 2 stops, or a plan holds secrets, the human reads it with
  `terraform -chdir=<root> show <plan>` in an ssh login, and it never enters the chat.
  "Holds secrets" means in its text: the apps root's saved file carries client secrets
  (rule 6), and Terraform prints them as `(sensitive value)`, so C3's and N's `shown` plans stay as
  they are. How the app renders a long plan is unproved (#1094): one hard to read there goes to ssh
  too. A plan a confirm rests on is saved, so this line has its `<plan>`: C3's, N's saved plan, secret
  rotation's refresh-only plan and D2's (#1136). `deploy.sh`, `rollback.sh` and `teardown.sh` apply or
  destroy inside themselves, and C1's preview reads the drift and not what the script will apply, so
  theirs is led by the shape or the case list rule 2 names, with no list.
- **A lost `claude` (#1090 Q14).** A rule 7 stop whose way out is offered from a new session. The
  human starts it over ssh inside `screen` with `/remote-control` on, after typing `security
  unlock-keychain ~/Library/Keychains/login.keychain-db` in that shell, without which its `gh` calls
  answer 401; the unlock does not carry to a new login, and its password stays in that shell as the
  code does (#1094). The new session's first check is `gh api user`. It asks the human which sitting
  and stage were lost, and runs no `--pin`: on a later UTC date a new pin moves `E` off the lost
  sitting's bundle, which holds D2's copy. This route has not been run (#1094), nor is it known what
  a killed `claude` does to the tunnel and the sitting browser, its background calls, so read both
  before relying on either. **Sitting D offers D10**, which restores D2's copy and posts on #348.
  **Sitting C never offers D10 (#1138).** C makes no copy for it to restore and #348 is not its
  ticket. The new session runs Stage 0's reads from the renewal on, to see what stands, posts the
  stage that was cut short as failed, with the reason, on that stage's ticket (rule 4; C4 and C5 have
  none, so the end of the sitting names theirs), and asks C5's question. Before a destroy it reads
  whether a `deploy.sh`, `rollback.sh`, `teardown.sh`, `terraform` or suite the lost session started,
  in the background or not, is still running, since a teardown must not start over one, and what a
  kill does to any of them is unknown. A destroy runs as T does, on T's case list and a confirm on
  launch, and is posted as T's is, on #242 with P-05 on #63, with a line saying it followed a lost
  session. Nothing is restored, and the sitting ends as C5 says.
- **AWS Settings (#1090 Q9).** H2's close path and the spend-limit checks may use the phone's
  browser; wiz names the step and waits for the report. That page has not been tried on a phone
  (#1094).
- **What a remote sitting cannot do (#1094).** The human cannot see the page, and that is accepted:
  the observation lines stand in for it, a page that came back as `other` is read under
  `browser-pages/` over ssh, and no screenshot is added. Anything only an eye would catch stays
  uncovered.
- **What needs the screen (#1090 Q1, Q5, Q6).** A reboot's FileVault unlock (`fdesetup authrestart`
  for a planned one), and the one trust step, already done on this machine. A live login session on
  the mini with VS Code open is a precondition; the screen may lock. The tunnel listens on the
  mini's loopback, where the phone cannot reach it, so a step in a human's own tab is at the screen
  too: C1's browser, and W's browser binding until #1026 moves it.

## Stage 0: where things stand

Ask first whether this is a `--dry-run` (list every command, run nothing, post nothing), so the
answer governs Stage 0 too. Then pin the sitting's date, once, in a call of its own before the
first prelude, and name the date it prints:

```bash
bash "$(git rev-parse --path-format=absolute --git-common-dir)/../tools/wiz_prelude.sh" --pin
```

Then ask for the renewal (the remote route) and wait for the report. Stage 0 does not ask where the
human is: its three checks hold wherever that is (#1090 Q11), and they live here, with
`tools/preflight.sh` unchanged (Q12). Then, each line after the prelude:

```bash
ca=.certs/spa-platform-poc-ca.pem; echo "\$ openssl x509 -in $ca -noout -fingerprint -sha256" >> "$R"; want="$(openssl x509 -in "$ca" -noout -fingerprint -sha256 2>&1 | tee -a "$R" | sed -n 's/.*Fingerprint=//p' | tr -d :)"; echo "CA file SHA-256: ${want:-none}" | tee -a "$R"; filter='/^SHA-256 hash:/ {print; found = found || $3 == want} END {exit !found}'; echo "\$ security find-certificate -a -c \"SPA Platform poc Local CA\" -Z /Library/Keychains/System.keychain | awk -v want='$want' '$filter'" >> "$R"; security find-certificate -a -c "SPA Platform poc Local CA" -Z /Library/Keychains/System.keychain | awk -v want="$want" "$filter" | tee -a "$R"; echo "ca lookup exit=${PIPESTATUS[0]} match exit=${PIPESTATUS[1]}" | tee -a "$R"   # match 0, or stop: the CA in .certs is not in the System keychain
filter='$1=="sleep" {s=$2; print} END {exit (s=="" || s!=0)}'; echo "\$ pmset -g | awk '$filter'" >> "$R"; pmset -g | awk "$filter" | tee -a "$R"; echo "sleep exit=${PIPESTATUS[1]}" | tee -a "$R"   # 0, or stop: system sleep is on
filter='{print} /Container engine/ {ok=($1=="ok")} END {exit !ok}'; echo "\$ tools/preflight.sh | awk '$filter'" >> "$R"; tools/preflight.sh | awk "$filter" | tee -a "$R"; echo "preflight exit=${PIPESTATUS[0]} engine exit=${PIPESTATUS[1]}" | tee -a "$R"   # preflight 0: it ended ready and the account is the pin; engine 0, or stop: the Container engine row is not ok
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

Each miss among the three is a rule 7 stop naming its cause. The CA line matches the System
keychain's certificates by the SHA-256 of `.certs/spa-platform-poc-ca.pem`, not by name, since a name
also finds an older authority left there by `certs.sh --new-ca`, and `security verify-cert` also
passes through the login keychain's copy (#1140). An older one beside the current one is listed and
passes. The CA's trust step is at the screen, and `tools/scripts/certs.sh trust` adds nothing while
the login keychain's copy verifies (#1094 step 1). A sleeping mini takes the session with it.
preflight reports a stopped engine as `later` and still ends ready, which is why the row is read
here; Docker Desktop's engine needs the live login session (#1090 Q6).

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
names each missing piece (a tag not merged, each open edge by number and title, a standing root,
each G6 key the file carries) and offers C instead:

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
- `infra/envs/poc/platform/terraform.tfvars` carries none of the G6 keys. The script prints the
  example's G6 keys (every key it names, set or commented out, beyond the Phase 1 eight) and the
  ones `terraform.tfvars` sets; a second list that is not empty is a G6 input, and D is not offered
  until the human removes it, since D0 needs the Phase 1 shape and D2 is what adds the block. A
  `terraform.tfvars` that is absent (it is git-ignored, and N step 3 writes it) carries no key and
  holds nothing against D. An empty first list means the block is not in the tree yet (#338, #347),
  and D is not offered.

D runs alone: a sitting that runs D runs no C stage, and D ends after D10.

## The sittings

Each sitting is a file beside this one. Once Stage 0 has settled which runs, read its file whole
before the first stage:

- **Sitting C**, the deploy and the gate runs: `c.md`. It holds C1 to C5, W, R, T and N.
- **Sitting D**, the G6 continuity run (#348): `d.md`, then `c.md`, whose N, C1, C2, C3 and T
  D's steps run. It holds D0 to D10.

A stage or step this file names by its letter is in the file that holds it.

## Outside the sittings

- **H1, the open questions on an `Overnight <date>` issue.** A `Confirm` that is the one word
  covers the pre-accepted answers and none that read `Answer: open`. On request, print a digest
  table of the open ones from the issue's comments (question, recommendation, what it changes). The
  human writes and posts any comment that names them, or has a session post it at their word.
- **H2, #90.** Print the three pre-checks and the close path (AWS Settings → View all projects →
  the sample project → Close project), which the phone's browser may take (the remote route); after the human reports it closed, `rm` the two local files
  with approval, run preflight, and draft the ADR-0017 clause into the log for the integrator.
