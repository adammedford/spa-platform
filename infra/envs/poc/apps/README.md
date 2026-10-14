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

After the identity root (its domain must already be on managed login version 2):

```bash
cd infra/envs/poc/apps
terraform init -backend-config="bucket=$(terraform -chdir=../../../bootstrap output -raw state_bucket)"
terraform plan -out=apps.tfplan
terraform apply apps.tfplan
```

## Done when: apps 1 and 2 exist and a manual code-flow login works

```bash
POOL=$(terraform -chdir=../idp output -raw user_pool_id)
DOMAIN=$(terraform -chdir=../idp output -raw cognito_domain)
CLIENT=$(terraform output -json clients | jq -r .app1.clientId)
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
credentials:

```bash
terraform -chdir=infra/envs/poc/apps init -backend=false && terraform -chdir=infra/envs/poc/apps test
```

They read the definitions, `fixtures/onboarding/app3.json` (P-02), `fixtures/cognito/pool.json`
and the contracts rather than repeating them. CI's Terraform job runs `fmt` and `validate` only.
