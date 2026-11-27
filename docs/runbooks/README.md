# Operating notes

The ten operations an operator performs on a cell built from the reference module, each as the
command sequence the scripts in `tools/scripts/` and `spactl` already run. Owner: workstream 6.

These notes are the order and the reasons. Each script's header is its manual, printed by
`--help`, and `spactl` prints its usage when run with no arguments, so where a note and a header
disagree the header wins and the note is wrong: fix the note.

| Operation | Note | Lever | Signs users out |
| --- | --- | --- | --- |
| Deploy the gateway | [deploy.md](deploy.md) | Gateway | On one task with sessions in memory, every user of every app; on two tasks with the durable session store, no one |
| Roll the gateway back | [rollback.md](rollback.md) | Gateway | As Deploy: on one task with sessions in memory, every user of every app; on two tasks with the durable session store, no one |
| Roll a release back | [release-rollback.md](release-rollback.md) | SPA release | No one |
| Roll the registry back | [registry-rollback.md](registry-rollback.md) | Registry | Only users of an app the rollback removes or moves to another client |
| Publish, activate or roll back a fragment | [fragments.md](fragments.md) | Fragment release | No one |
| Rotate a client secret | [secret-rotation.md](secret-rotation.md) | Registry | No one |
| End every session for an app | [end-app-sessions.md](end-app-sessions.md) | Registry | Every user of that app, and only that app |
| Onboard or remove an app | [onboarding.md](onboarding.md) | Registry | Onboarding, no one; removal, every user of that app, and only that app |
| Widen or narrow the bounds | [widening-the-bounds.md](widening-the-bounds.md) | Gateway | As Deploy: on one task with sessions in memory, every user of every app; on two tasks with the durable session store, no one |
| Tear the cell down | [teardown.md](teardown.md) | Infrastructure | Everyone; nothing is left |

A fragment is added to a shell, removed from it, or moved to another client with `spactl app add`,
the registry lever, which signs no one out of the shell. A fragment added or moved reads
`loginRequired` in each open session until its user signs in to it; a removed fragment's tokens are
dropped, unrevoked (`docs/gaps.md`, GAP-33). The first fragment in a cell needs the fragments build
first ([deploy.md](deploy.md)).

Whom a gateway deployment signs out depends on the cell's shape, which the cell module's
`multi_task` and `session_store` inputs choose: everyone on one task with sessions in memory, no one
on two tasks with the durable session store. [deploy.md](deploy.md) says why. On the first shape,
prefer a lever that needs no deployment.

## Before any note: the operator session

Every note starts here, from the repository root, in one shell:

- a session with credentials for the account the cell is in, and `AWS_REGION` set to the cell's
  Region;
- the cell's entry file sourced, `set -a; . <entry file>; set +a`, which sets the values the
  scripts take with no default (`tools/scripts/cell-common.sh`'s header names each one);
- `APP` set to the id of the app the note acts on, and `APP_DEFINITION` to that app's definition
  file, the document `spactl app add` reads.

The scripts need each root on `$SPA_ROOTS` initialised with its backend, and they read the cell's
names from the outputs document, `$SPA_OUTPUTS_FILE`, which the scripts write after every apply
(`contracts/infra-outputs.schema.json`). No script reads Terraform state directly except
`teardown.sh`; of the notes, only [secret-rotation.md](secret-rotation.md) does, by hand.

The app's origin, which `spactl wait` reads through, is its definition's `host`:

```bash
ORIGIN="https://$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["host"])' "$APP_DEFINITION")"
```

Never paste an AWS command's raw output into a ticket or a log: the bucket name, every ARN and every
repository URI carry the account id, which the scripts redact and a hand-run command does not. Never
print a client secret, a token, a cookie, an authorization code or a state value.

### `spactl` and its platform configuration

The notes that change the registry run `spactl` by hand. It is the project in `tools/spactl`:

```bash
spactl() { dotnet run --project tools/spactl -- "$@"; }
```

Every `spactl` command that writes the registry takes `--config <platform-config.json>`, whose
`Policy` and `Issuers` sections bound the document. The bounds must be the gateway's own, or
`spactl` accepts an entry the gateway then refuses. The scripts build that file from the running
gateway's task definition and delete it on exit (`read_platform_config` in
`tools/scripts/cell-common.sh`); by hand it is the same three calls, into a directory only you can
read:

```bash
umask 077
RUN="$(mktemp -d)"
CLUSTER="$(python3 -I tools/scripts/cell_documents.py field "$SPA_OUTPUTS_FILE" ecsCluster "$AWS_REGION")"
SERVICE="$(python3 -I tools/scripts/cell_documents.py field "$SPA_OUTPUTS_FILE" ecsService "$AWS_REGION")"
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
The tunnel's hop is the bastion's task, which neither replaces; if the tunnel has ended all the
same, start it again. Remove the directory when the note is done (`rm -rf "$RUN"`).

### Waiting for the gateway: the tunnel

A registry change is live once the gateway's poller has swapped to it. `spactl wait --url <origin>
--revision <N>` reads `/readyz` until it reports revision N or later (`contracts/pipeline.md`).
`/readyz` answers on any host, but the load balancer is internal, so the workstation reaches it
through the tunnel. Its hop is the cell's bastion task, a service of one task that the platform root
declares only with `bastion = true` and `bastion_image` in its `terraform.tfvars`, off by default;
the bastion runs, and is billed, until an apply sets it back to false. `tools/scripts/tunnel.sh`
forwards through it and never through a gateway task; when the outputs document names no bastion it
refuses, exit 2, before it asks AWS anything:

```bash
tools/scripts/tunnel.sh                       # a second terminal; it stays in the foreground
tools/scripts/verify-tunnel.sh /healthz       # a third, once: TLS validates against the cell's authority
```

The tunnel listens on local port 8443, the port in each app's registry host, so with the app's host
resolving to this workstation `$ORIGIN` reaches the gateway through it. Every `spactl` write prints
`Applied revision N`; that N is the one to wait for:

```bash
spactl wait --url "$ORIGIN" --revision N
```

In Phase 1 there is one task, so one `/readyz` that reports N means the whole cell has it. A cell of
more than one task waits for each of them (`spactl wait --instances`).
