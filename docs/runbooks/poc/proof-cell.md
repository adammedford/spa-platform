# The proof cell

The steps that belong to this project's own cell and to no other: its session, its first
deployment, rotation's Cognito calls and teardown's leftovers. The notes in
[`docs/runbooks/`](../README.md) are written for any cell built from the reference module; the proof
cell walks those same notes, and this note holds only what surrounds them, each part naming the
reference note it goes with. Owner: workstream 6.

## Walked

#244 and the sitting C posts are where the notes are walked on the deployed proof cell and where
each walk is recorded, not here. A note that has not been walked, or has changed since its walk, is
the scripts' sequence written down, not a sequence that has been run. Deploy, gateway rollback,
teardown and secret rotation changed their command sequence when the reference notes and this note
were split (#1356), so their walks at the commits on #244 do not stand for the notes as they are:
each is walked again at the next sitting C, secret rotation across its reference note and this
note together.

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
   health check is `/` for the sample. Before it, set `bastion = true` and `bastion_image`, the
   sample image pinned by digest as `gateway_image` is, in that root's `terraform.tfvars`
   (`terraform.tfvars.example` has both lines): the bastion is the tunnel's hop, and without
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
its step 1 is only `tools/scripts/poc/certs.sh import`, and its step 2 is already done: the teardown
deleted the certificate from ACM, and the listener finds it there by its domain name.
