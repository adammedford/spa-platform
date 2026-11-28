# The proof cell

The steps that belong to this project's own cell and to no other: its session, its first
deployment, rotation's Cognito calls, onboarding through its apps root and teardown's leftovers. The
notes in [`docs/runbooks/`](../README.md) are written for any cell built from the reference module;
the proof cell walks those same notes, and this note holds only what surrounds them, each part
naming the reference note it goes with. Owner: workstream 6.

## Walked

#244 and the sitting C posts are where the notes are walked on the deployed proof cell and where
each walk is recorded, not here. A note that has not been walked, or has changed since its walk, is
the scripts' sequence written down, not a sequence that has been run. A walk stands for the note at
the commit it names: deploy, gateway rollback, teardown and secret rotation changed their command
sequence when the reference notes and this note were split (#1356), and secret rotation is walked
across its reference note and this note together.

## Session setup

Before [README.md](../README.md), "Before any note: the operator session", from the repository root,
in one shell:

```bash
set -a; . tools/aws-project.env; set +a     # the profile and the Region (tools/aws-project.env)
tools/preflight.sh                          # the tools, and that the session is the platform account
set -a; . infra/envs/poc/cell.env; set +a   # the proof cell's entry file
APP=app1
APP_DEFINITION=fixtures/apps/app1.json
```

With these, every command of the reference notes runs as written. The scripts need
`terraform init -backend-config=backend.hcl` run once in each of `infra/envs/poc/idp`,
`infra/envs/poc/apps` and `infra/envs/poc/platform`, the roots on `$SPA_ROOTS`. The hosts-file line
that resolves the app hosts to this workstation is the one `tools/scripts/poc/certs.sh` names; with
it, app 1's origin through the tunnel is `https://app1.spa-poc.test:8443`.

## The first deployment

The proof cell built from nothing, before [deploy.md](../deploy.md)'s deployment to a running cell.
The order matters once, because each step feeds the next (`docs/plan/05-infrastructure-and-cost.md`,
First deployment):

1. `tools/scripts/poc/certs.sh`: the local certificate authority and the wildcard certificate,
   imported to ACM, where the listener finds it by its domain name.
2. The bootstrap root (`infra/bootstrap/README.md`): the state bucket, the CI plan role and the
   teardown role.
3. The identity root: `terraform -chdir=infra/envs/poc/idp apply`.
4. The apps root: `terraform -chdir=infra/envs/poc/apps apply`. Cognito now has the two clients.
5. The platform root on the sample image: `terraform -chdir=infra/envs/poc/platform apply`, whose
   health check is `/` for the sample. Before it, set `health_check_path = "/"` and
   `gateway_writable_root = true` in the platform root's `terraform.tfvars`, including when
   reusing a file created before the writable-root setting was added. The sample writes under
   `/tmp` during start-up. Set `bastion = true` and pin the sample image by digest in both
   `gateway_image` and `bastion_image`, using `terraform.tfvars.example` as the reference.
   The bastion is the tunnel's hop, and without
   it `tunnel.sh` refuses and `deploy.sh` exits 2 unless `--url` names an origin this workstation
   already reaches. The bastion task runs, and is billed, until an apply sets it back to false.
6. `tools/scripts/poc/seed.sh` publishes the fixture releases and writes the first registry through
   `spactl`, and writes the outputs document, which names the bastion. `deploy.sh` refuses to run
   before it.
7. `tools/scripts/poc/mock-api.sh` builds and pushes the mock API's image and records its digest in
   the platform root's `mock-api.auto.tfvars`. It applies nothing, so it runs after `seed.sh` and
   before `deploy.sh`, and the one apply of the next step moves both images.
8. `tools/scripts/deploy.sh`, as [deploy.md](../deploy.md) has it; no tunnel need be running, since
   its readiness wait opens its own through the bastion. The target turns healthy only once
   `/readyz` reports a snapshot. While the service settles, the sample task's target shows as
   draining and fails the new `/readyz` check; `deploy.sh` tolerates that (#207).
9. After `deploy.sh` exits 0, your own check of the tunnel: `tools/scripts/tunnel.sh`, then
   `tools/scripts/verify-tunnel.sh /healthz` in a second terminal.

A later deployment that changes the mock API runs `tools/scripts/poc/mock-api.sh` before
`deploy.sh`, as step 7 does; `deploy.sh` alone leaves the mock API at what `mock-api.auto.tfvars`
holds, and so does a rollback ([rollback.md](../rollback.md)).

## Rotation's Cognito calls

[secret-rotation.md](../secret-rotation.md) holds the registry half; its steps 1, 2 and 7 say in
prose what the provider does, and on the proof cell the provider is Cognito. Terraform's client
resource may not expose a second secret yet, which is why these calls use the AWS CLI
(`docs/plan/05-infrastructure-and-cost.md`; risk 14 in `docs/plan/06-phase-2-and-risks.md`).

**After the reference step 1**, the pool and the old secret's id, kept in variables and not printed:
a secret id begins with the client id (`<client-id>--<epoch-create-time>`).

```bash
POOL_ID="$(terraform -chdir=infra/envs/poc/idp output -raw user_pool_id)"
OLD_SECRET_ID="$(aws cognito-idp list-user-pool-client-secrets --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --query 'ClientSecrets[].ClientSecretId' --output text)"
```

The client must have exactly one secret now. Cognito allows two active secrets per client
(`aws cognito-idp add-user-pool-client-secret help`), and step 2 adds the second. If
`OLD_SECRET_ID` holds two ids, an earlier rotation stopped before its step 7: finish that one first.

```bash
printf '%s\n' "$OLD_SECRET_ID" | wc -w        # 1: go on. 2: stop
```

**The reference step 2**, adding the second secret:

```bash
openssl rand -hex 32 | tr -d '\n' > "$RUN/secret"      # 64 characters: Cognito takes 24 to 64 of [A-Za-z0-9_+]
NEW_SECRET_ID="$(aws cognito-idp add-user-pool-client-secret --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --client-secret "file://$RUN/secret" \
  --query 'ClientSecretDescriptor.ClientSecretId' --output text)"
```

Cognito returns the value only when it generated the secret, so a secret given this way comes back
as its id alone.

**The reference step 3** relies on the apps root ignoring later changes to the parameter's value:
`lifecycle { ignore_changes = [value] }` in `infra/envs/poc/apps/main.tf` (plan row 1.8). Its step 4
refreshes the root the field map names for `clients`, which on the proof cell is
`infra/envs/poc/apps`.

**The reference step 7**, deleting the old secret, only after step 6 reported the revision:

```bash
aws cognito-idp delete-user-pool-client-secret --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --client-secret-id "$OLD_SECRET_ID"
aws cognito-idp list-user-pool-client-secrets --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --query 'length(ClientSecrets)' --output text
```

The last line prints `1`. Cognito refuses to delete a client's last secret.

### Checks for the first walk (#244)

These rest on reading, not on a run, and the first walk settles them:

- that `terraform apply -refresh-only` in the apps root moves the parameter's `version` in state
  although the value is ignored, and that its plan shows nothing else;
- that a plain `terraform plan` in the apps root after step 7 is empty, with the client's original
  secret deleted.

## Onboarding an app on the proof cell

[onboarding.md](../onboarding.md) holds the steps, and the proof cell runs them as written but for
three: the client, the secret and the `clients` entry come from the apps root, `infra/envs/poc/apps`,
in place of an adopter's own tooling. The proof cell's instance is app 3, whose definition is
`fixtures/onboarding/app3.json`. After "Session setup" above, in the same shell, and before the
README's operator session, whose `ORIGIN` line then reads app 3's host:

```bash
APP=app3
APP_DEFINITION=fixtures/onboarding/app3.json
RELEASE_DIRECTORY=fixtures/spa/app3
RELEASE=2026-10-10.1                        # any name app 3 has not published yet
```

1. **The client, the secret and the `clients` entry come from the apps root**, in place of the
   reference steps 2, 3 and 4. Copy the definition into `fixtures/apps/`, the directory the root
   reads, and apply the root, which registers the client and writes the parameter:

   ```bash
   cp "$APP_DEFINITION" fixtures/apps/
   terraform -chdir=infra/envs/poc/apps apply
   ```

   Answer `yes` only when the plan adds app 3's client, its branding style and its parameter, and
   nothing else (`docs/plan/05-infrastructure-and-cost.md`, Onboarding a third SPA). Then write the
   outputs document again from the roots' outputs, so that its `clients` entry carries app 3: the
   `output` calls and `cell_documents.py outputs` of [secret-rotation.md](../secret-rotation.md)
   step 4, which are what the scripts' `write_outputs` runs, without its refresh, since the apply
   has already put the new parameter in state. That block's last line, with `APP=app3`, prints the
   new parameter's version.

   The copy changes `fixtures/apps/`, which is tracked and workstream 5's: it is never committed,
   and step 3 below takes it out again.

2. **Then the reference steps from `spactl app validate` on**: the reference step 1, whose
   definition already holds the proof cell's `host` and `oauth.issuer`; the reference step 5, which
   has no team to grant, so the operator publishes as the reference step 6 allows; the reference
   step 6, from `$RELEASE_DIRECTORY`; and the reference step 7. A refusal at the reference step 1
   comes after the client and the parameter exist here: take them out as step 3 below does, from its
   apps-root apply on.

3. **Removal on the proof cell.** The reference "Removal" step 1, `app remove` and `wait`, as
   written. In place of its step 2, take the definition out of `fixtures/apps/` and apply the apps
   root, which deletes the client, its branding style and the parameter:

   ```bash
   rm fixtures/apps/app3.json
   terraform -chdir=infra/envs/poc/apps apply
   ```

   Answer `yes` only when the plan destroys those three and nothing else. Then write the outputs
   document again, as step 1 above does but without that block's last line, since the document now
   names no client for app 3, and end as the reference "Removal" ends.

Deployed F-07 (`tests/e2e/Stack/CellOnboarding.cs`) onboards app 3 by these steps with two
differences, so that its run changes no tracked file: it plans the root with `definition_dirs`
naming `fixtures/onboarding/` instead of copying the definition, and its onboarding gives `spactl` a
copy of the outputs document in its own directory. It refuses to start while the outputs document
names a client for app 3, which is why step 3 above writes the document again.

## Teardown's leftovers

After [teardown.md](../teardown.md). The teardown role's trust allows only the principals in the
bootstrap root's `teardown_trusted_principal_arns`, with MFA (`infra/bootstrap/README.md`, The
teardown role).

### What stays, on purpose

The bootstrap root's three resources, the Terraform state bucket, the CI plan role and the teardown
role, carry `Platform = spa-platform-bootstrap` and do not answer the query. They outlive every
teardown by design: the next first deployment needs them, and destroying the state bucket from inside
a teardown would destroy the state of the roots being destroyed (`contracts/resource-tags.md`, The
bootstrap exemption).

They are removed by hand, and only when the cell will never be built again in this account. The
bootstrap root keeps local state on the workstation that applied it (`infra/bootstrap/README.md`), so
that is where its removal runs from.

### Build it again

"The first deployment" above. With the bootstrap and the workstation's certificate authority kept,
its step 1 is only `tools/scripts/poc/certs.sh import`, since the teardown deleted the certificate
from ACM and the listener finds it there by its domain name, and its step 2 is already done, since
the bootstrap root was kept.
