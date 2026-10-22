# Operating notes

The seven operations an operator performs on a Phase 1 cell, each as the command sequence the
scripts in `tools/scripts/` and `spactl` already run (plan row 6.7; roadmap stage S7). Owner:
workstream 6.

These notes are the order and the reasons. Each script's header is its manual, printed by
`--help`, and `spactl` prints its usage when run with no arguments, so where a note and a header
disagree the header wins and the note is wrong: fix the note.

| Operation | Note | Lever (`docs/plan/05-infrastructure-and-cost.md`, Deployment and rollback) | Signs users out |
| --- | --- | --- | --- |
| Deploy the gateway | [deploy.md](deploy.md) | Gateway | Every user of every app |
| Roll the gateway back | [rollback.md](rollback.md) | Gateway | Every user of every app |
| Roll a release back | [release-rollback.md](release-rollback.md) | SPA release | No one |
| Roll the registry back | [registry-rollback.md](registry-rollback.md) | Registry | Only users of an app the rollback removes or moves to another client |
| Rotate a client secret | [secret-rotation.md](secret-rotation.md) | Registry | No one |
| End every session for an app | [end-app-sessions.md](end-app-sessions.md) | Registry | Every user of that app, and only that app |
| Tear the cell down | [teardown.md](teardown.md) | Infrastructure | Everyone; nothing is left |

A gateway deployment signs everyone out because Phase 1 keeps sessions and data-protection keys in
the one task's memory (`docs/plan/05-infrastructure-and-cost.md`, Deployment and rollback). Phase 2
removes that (P2-1, P2-2); until then, prefer a lever that needs no deployment.

**Walked:** none yet. Deploy and teardown are exercised by task 6.9's gate runs, the other five are
walked once on the deployed cell, and each walk is posted on #244. A note that has not been walked
is the scripts' sequence written down, not yet a sequence that has been run.

## Before any note: the operator session

Every note starts here, from the repository root, in one shell.

```bash
set -a; . tools/aws-project.env; set +a     # the profile and the Region (tools/aws-project.env)
tools/preflight.sh                          # the tools, and that the session is the platform account
```

The scripts need `terraform init -backend-config=backend.hcl` run once in each of
`infra/envs/poc/idp`, `infra/envs/poc/apps` and `infra/envs/poc/platform`, and they read the cell's
names from `infra/envs/poc/outputs.json`, which `seed.sh`, `deploy.sh` and `rollback.sh` write
(`contracts/infra-outputs.schema.json`). Nothing here reads Terraform state directly except
`teardown.sh`.

Never paste an AWS command's raw output into a ticket or a log: the bucket name, every ARN and every
repository URI carry the account id, which the scripts redact and a hand-run command does not. Never
print a client secret, a token, a cookie, an authorization code or a state value (AGENTS.md; test
N-16).

### `spactl` and its platform configuration

The notes that change the registry run `spactl` by hand. It is the project in `tools/spactl`:

```bash
spactl() { dotnet run --project tools/spactl -- "$@"; }
```

Every `spactl` command that writes the registry takes `--config <platform-config.json>`, whose
`Policy` and `Issuers` sections bound the document. The bounds must be the gateway's own, or
`spactl` accepts an entry the gateway then refuses. `seed.sh` builds that file from the running
gateway's task definition and deletes it on exit (`read_platform_config` in
`tools/scripts/cell-common.sh`); by hand it is the same three calls, into a directory only you can
read:

```bash
umask 077
RUN="$(mktemp -d)"
CLUSTER="$(python3 -I tools/scripts/cell_documents.py field infra/envs/poc/outputs.json ecsCluster "$AWS_REGION")"
SERVICE="$(python3 -I tools/scripts/cell_documents.py field infra/envs/poc/outputs.json ecsService "$AWS_REGION")"
TASK_DEFINITION="$(aws ecs describe-services --region "$AWS_REGION" --cluster "$CLUSTER" --services "$SERVICE" \
  --query 'services[0].taskDefinition' --output text)"
aws ecs describe-task-definition --region "$AWS_REGION" --task-definition "$TASK_DEFINITION" \
  --query "taskDefinition.containerDefinitions[?name=='gateway'] | [0].environment[].[name, value]" \
  --output text > "$RUN/environment.tsv"
python3 -I tools/scripts/cell_documents.py config "$RUN/environment.tsv" contracts/platform-config.schema.json \
  > "$RUN/platform-config.json"
CONFIG="$RUN/platform-config.json"
```

Build it again after a deployment or a rollback: a new task definition can carry a new policy.
Remove the directory when the note is done (`rm -rf "$RUN"`).

### Waiting for the gateway: the tunnel

A registry change is live once the gateway's poller has swapped to it. `spactl wait --url <origin>
--revision <N>` reads `/readyz` until it reports revision N or later (`contracts/pipeline.md`).
`/readyz` answers on any host, but the load balancer is internal, so the workstation reaches it
through the tunnel (`docs/plan/05-infrastructure-and-cost.md`, Workstation access):

```bash
tools/scripts/tunnel.sh                       # a second terminal; it stays in the foreground
tools/scripts/verify-tunnel.sh /healthz       # a third, once: TLS validates against the local authority
```

With the hosts-file line in place, the origin is `https://app1.spa-poc.test:8443`. Every `spactl`
write prints `Applied revision N`; that N is the one to wait for:

```bash
spactl wait --url https://app1.spa-poc.test:8443 --revision N
```

In Phase 1 there is one task, so one `/readyz` that reports N means the whole cell has it. Phase 2
waits for every task (P2-5).
