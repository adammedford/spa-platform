# `infra/envs/poc/apps` — the apps root

Task 1.8. For each app definition (`contracts/app-definition.schema.json`) in `fixtures/apps/`,
exactly three resources:

| Resource | Settings | Why |
| --- | --- | --- |
| Cognito app client `spa-platform-<cell>-<appId>` | Confidential, code grant only, the definition's `oauth.scopes`, `COGNITO` sign-in | The gateway holds the secret and the tokens |
| | Callback `https://<host>/signin-oidc`, sign-out `https://<host>/` | `host` carries the port; Cognito's logout lands on `/` (R-C7, plan row 3.7) |
| | Access and ID tokens 5 minutes; refresh token the registry's longest session (720 minutes, read from `contracts/registry.schema.json`) plus an hour | Refresh happens during a test run; a session never outlives its refresh token |
| | Refresh rotation with a 10-second grace, token revocation on, `ALLOW_REFRESH_TOKEN_AUTH` off | Rotation rules that flow out; logout revokes |
| Managed-login branding style | Cognito's default values | Managed login serves a client only once it has a style |
| `SecureString` parameter `/spa-platform/<cell>/apps/<appId>/client-secret` | The client secret; later value changes ignored | A rotation (docs/plan/05 -> Rotating a client secret) is not undone by the next apply |

Nothing else. Onboarding an app is copying its definition into `fixtures/apps/`; the plan then
shows those three resources only (P-02). The identity and platform roots plan nothing.

Every block in this README runs from the repository root, and each one that reaches AWS begins
by sourcing `tools/aws-project.env`, which names the project's profile and its selected Region.
Without it the shell builds in whichever project its default profile points at.

## Tags

`Platform = spa-platform`, `Cell = <cell>`, `ManagedBy = terraform` through `default_tags`
(`contracts/resource-tags.md`). Only the parameters accept tags. Clients and branding styles
accept none; both are children of the tagged user pool, so destroying the identity root's pool
removes them and the teardown query stays a proof.

## Inputs

- The definitions: `definition_dirs`, default `["../../../../fixtures/apps"]`.
- The pool id and the grantable scopes: the identity root's state (`user_pool_id`,
  `resource_servers`), read with `terraform_remote_state` from the state bucket, default
  `spa-platform-<cell>-tfstate-<account id>`; override with `state_bucket`. A definition asking for
  a scope no resource server offers fails the plan, not the apply.

## Applying it

**Step 0 is `infra/bootstrap`** (`infra/bootstrap/README.md` -> Applying it), which creates the
state bucket, and the identity root comes after it (`infra/envs/poc/idp/README.md` -> Applying it):
this root reads the user pool from the identity root's state, and that pool's domain must already be
on managed login version 2.

This root's `terraform.tfvars`, which git ignores, sets `region` to the project's selected Region,
`AWS_REGION` in `tools/aws-project.env`. Every root defaults to `us-east-1`, and a parameter in the
provider block outranks the environment
(https://registry.terraform.io/providers/hashicorp/aws/latest/docs#authentication-and-configuration),
so the file, not the sourced `AWS_REGION`, is what puts the clients and parameters in the project's
Region. The recipe writes it when it is absent and leaves an existing one alone, so check that
one's `region`.

The backend block is partial, as in the identity root: the state bucket and the project's Region
are both given at init.

The plan goes to a private directory that `mktemp -d` makes outside the repository. Keep it there:
a saved plan holds, in cleartext, every value its changes carry and the whole state it was planned
from ([`plan -out`](https://developer.hashicorp.com/terraform/cli/commands/plan#out-filename),
[`prior_state`](https://developer.hashicorp.com/terraform/internals/json-format#plan-representation)).
So once this root has been applied, every plan of it holds every OAuth client secret, even a plan
with no changes, and one beside the configuration is one `git add -A` from a commit (change request
1.1).

```bash
set -a; . tools/aws-project.env; set +a   # AWS_PROFILE and the Region, or the wrong project
[ -e infra/envs/poc/apps/terraform.tfvars ] ||
  echo "region = \"$AWS_REGION\"" > infra/envs/poc/apps/terraform.tfvars
terraform -chdir=infra/envs/poc/apps init \
  -backend-config="bucket=$(terraform -chdir=infra/bootstrap output -raw state_bucket)" \
  -backend-config="region=$AWS_REGION"
plan_dir=$(mktemp -d)   # outside the repository: once the root is applied, every plan holds the client secrets
terraform -chdir=infra/envs/poc/apps plan -out="$plan_dir/apps.tfplan"
```

Read the plan. Then, in the same shell, apply exactly that plan, which Terraform does without
asking again
([saved plan mode](https://developer.hashicorp.com/terraform/cli/commands/apply#saved-plan-mode)),
and delete it. If you decide not to apply it, run the last line all the same:

```bash
set -a; . tools/aws-project.env; set +a   # AWS_PROFILE and the Region, or the wrong project
terraform -chdir=infra/envs/poc/apps apply "$plan_dir/apps.tfplan"
rm -rf "$plan_dir"
```

## Done when: apps 1 and 2 exist and a manual code-flow login works

```bash
set -a; . tools/aws-project.env; set +a   # AWS_PROFILE and the Region, or the wrong project
POOL=$(terraform -chdir=infra/envs/poc/idp output -raw user_pool_id)
DOMAIN=$(terraform -chdir=infra/envs/poc/idp output -raw cognito_domain)
CLIENT=$(terraform -chdir=infra/envs/poc/apps output -json clients | jq -r .app1.clientId)
aws cognito-idp describe-user-pool-client --user-pool-id "$POOL" --client-id "$CLIENT" \
  --query 'UserPoolClient.{flows:AllowedOAuthFlows,callbacks:CallbackURLs,rotation:RefreshTokenRotation}'
# Open in a browser, sign in as a test user, and check the redirect carries ?code=:
echo "https://$DOMAIN/oauth2/authorize?response_type=code&client_id=$CLIENT&redirect_uri=https%3A%2F%2Fapp1.spa-poc.test%3A8443%2Fsignin-oidc&scope=openid"
```

Nothing listens on the callback before the gateway runs; the browser's address bar shows the
code, which is the proof. Do not paste it anywhere: it is a credential until it expires.

## Outputs

| Output | Feeds |
| --- | --- |
| `clients` | `clients` in `contracts/infra-outputs.schema.json`, already in its shape: `clientId`, `secretParameter`, `secretVersion`, never the secret. `spactl app add` reads it; the platform root reads the client ids of apps 1 and 2 for the mock API's allowlists. |

## Tests

Plans against a mock AWS provider and an overridden identity-root state, so they need no
credentials and reach no project, which is why they do not source `tools/aws-project.env`:

```bash
terraform -chdir=infra/envs/poc/apps init -backend=false && terraform -chdir=infra/envs/poc/apps test
```

They read the definitions, `fixtures/onboarding/app3.json` (P-02), `fixtures/cognito/pool.json`
and the contracts rather than repeating them. CI's Terraform job runs them, with every other
directory under `infra/` that has tests, after `fmt` and `validate`.

The P-02 run plans the fixtures with `fixtures/onboarding/app3.json` added, as ADR-0016 decision 2
asks. It fails on a fourth resource block, a module call or a `.tf.json` file, on a definition
that shares an existing app's `appId`, and on any change the addition makes to an existing app's
client, branding style or parameter. It compares every argument `main.tf` sets on those three
blocks with `existing_app_resources` in the test file, and the run before it holds the plan
without the addition to the same map. That run also fails when `main.tf` sets an argument the map
does not name, so a new argument is compared from the change that adds it.
