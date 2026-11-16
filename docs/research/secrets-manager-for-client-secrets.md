# Secrets Manager for client secrets: rotation when the provider holds the other copy, versions against the registry's reference, and the SDK package

- **Ticket:** #1224, "Secrets Manager for client secrets: rotation when the provider holds the other copy,
  versions against the registry's reference, and the SDK package" (`wayfinder:research`), on the map #1220.
- **Date:** 2026-11-16. **Branch:** `research/secrets-manager-for-client-secrets`, pushed and never merged,
  as the map rules.
- **Method:** docs-only. Repository reading at commit `adf0273` (origin/main on 2026-11-16):
  `docs/adr/0003-client-secrets-in-parameter-store.md`, `docs/runbooks/secret-rotation.md`,
  `docs/plan/05-infrastructure-and-cost.md` (Rotating a client secret, Teardown, the egress lines),
  `docs/plan/04-tests.md` (P-01 to P-03), `contracts/registry.schema.json` (`clientSecretRef`),
  `contracts/infra-outputs.schema.json`, `contracts/changes/p2-6-pingfederate-profile.md` (the `KeyRef` row
  and decision 4), `src/Platform.Registry/ParameterStoreClientSecretSource.cs`,
  `PlatformRegistryExtensions.cs` and `Platform.Registry.csproj`, `src/Platform.Identity/OidcOptionsSetup.cs`,
  `Directory.Packages.props`, `docs/dependencies.md` (the AWS rows), `infra/modules/cell/iam.tf` and
  `network.tf`, `infra/modules/cell/tests/module.tftest.hcl` (the allowlist's error messages),
  `infra/envs/poc/apps/main.tf` and its `tests/root.tftest.hcl`, `infra/bootstrap/ci-role.tf`
  (`ReadCellParameters`), `tools/scripts/verify-teardown.sh` (header and readers), and the resolution of
  "The gateway's client assertion key: generation, storage, the public key's registration and rotation"
  (#953). Primary pages were fetched on 2026-11-16 and are listed in section 8 with what each gave.
  **Executed:** `git` and `gh` reads; unauthenticated HTTPS downloads of nuget.org's flat-container index,
  nuspecs and registration pages, of AWS's public service-reference JSON, and of markdown copies of AWS and
  Ping documentation pages, parsed with `python3 -I`; a read of `obj/project.assets.json` files left by an
  earlier restore in the main checkout. **Not done:** no `aws` CLI call, no `terraform plan` or `apply`, no
  credential step, no read of the cell, no `dotnet restore` or build, nothing run against Cognito, Secrets
  Manager or PingFederate. The `aws-mcp` connector failed to connect. A claim no page read states is marked
  "not found". Prices are the pricing ticket's (#1221); section 7 names cost drivers only.

## Answer in brief

1. **Managed rotation, as AWS uses the term, does not apply to either provider.** The managed-rotation page
   lists Aurora, ECS Service Connect, RDS, DocumentDB, Redshift and "managed external secrets"; the partner
   table for the last names Auth0 and Okta client secrets among 26 secret types and names neither Amazon
   Cognito nor Ping Identity. "For all other types of secrets, see Rotation by Lambda function." So the
   stand-in is Secrets Manager with **rotation by a Lambda function the project writes**, or Secrets Manager
   as a versioned store with rotation left a runbook. Map note "Secrets Manager with managed rotation" is
   not available as written.
2. **Against Cognito a rotation function is writable on paper; against PingFederate it is not without
   reaching the admin API, and under P2-6 there is no client secret to rotate there.** Cognito holds "up to
   2 active secrets per app client", takes a caller-supplied value (24 to 64 of `[\w+]`), never returns a
   supplied value and refuses to delete the last secret, so the function's four steps map to: generate and
   store as `AWSPENDING`; `AddUserPoolClientSecret` (first deleting a stale second secret); test; move
   `AWSCURRENT`. How to test a secret non-interactively on an authorization-code client was not found.
   PingFederate keeps one current secret and retains the previous one for a period (0 to 43,800 minutes);
   changing it is an admin-API or console act, the admin port is in no target group, and P2-6 moves
   PingFederate clients to `private_key_jwt`.
3. **The real conflict is with ADR-0003's pinned reference, not with the provider.** The gateway reads a
   reference once and never invalidates it; a rotation today is a registry change. A rotation function's
   four steps have no point that knows the gateway has swapped, so either the reference names a moving label
   (`AWSCURRENT`) and the gateway re-reads, giving up "rotation is a registry change", or the reference stays
   pinned to a version id and the function only prepares the new version while the registry change stays a
   person's or a pipeline's step. This is an inference from the pages and the code, not a quoted fact.
4. **A store-neutral reference carries three things: a store scheme, a name, and a version selector whose
   form the scheme owns.** Parameter Store's selector is a positive integer; Secrets Manager's is a version
   id ("typically a UUID-type value", 32 to 64 characters) or a staging label. Secrets Manager "doesn't
   store a linear history": a version with no label is deprecated and removed once there are more than 100,
   so a pinned version id stays readable only while labelled or recent; a custom label (20 across a secret)
   can hold one.
5. **No new package: yes for the package, no for the code and the contract.** `GetParameter` and
   `GetParameters` read `/aws/reference/secretsmanager/{name}`, optionally `:{version id}` or `:{label}`,
   through the SDK package the gateway already has. But the schema pattern, `TryParse` and the
   `parameter.Version != version` guard all require an integer version (the reference returns
   `"Version": 0`), and the task role's grant is on `parameter/spa-platform/{cell}/apps/*`. The IAM this
   route needs (the `ssm` resource ARN, and whether `secretsmanager:GetSecretValue` is evaluated on the
   caller) was not found on a page read.
6. **`AWSSDK.SecretsManager`: newest 4.1.1 (2026-11-12), Apache-2.0, `AWSSDK.Core` `[4.1.0, 5.0.0)`, which
   lifts Core in every graph.** Two older releases sit on the floors the solution already resolves:
   4.0.100.16 (Core `[4.0.102.9, 5.0.0)`, the floor of the `AWSSDK.SimpleSystemsManagement` pin) and
   4.0.100.20 (Core `[4.0.102.12, 5.0.0)`, the floor of the `AWSSDK.DynamoDBv2` pin). In
   `Platform.Registry`, where the secret source lives, 4.0.100.16 moves no other version. No restore was run.
7. **IAM, audit, deletion.** The task role needs `secretsmanager:GetSecretValue` on the cell's secret ARNs
   and no `kms:Decrypt` under `aws/secretsmanager`; no resource policy is needed in one account. CloudTrail
   records every operation including `GetSecretValue`, plus rotation and deletion service events, as
   management events, without the value. `DeleteSecret` defaults to a 30-day recovery window (minimum 7);
   Terraform's `recovery_window_in_days = 0` forces deletion, which is what a destroy-then-redeploy and
   `verify-teardown.sh` need. Whether a secret in its recovery window is still returned by the tag query was
   not read.
8. **The assertion key moves by the same reference and one more grant, and loses what answer 6 of its
   ticket relied on.** `KeyRef` already takes "the `clientSecretRef` pattern", so the store-neutral reference
   covers it. Its rotation sequence relies on "SSM keeps the old version"; in Secrets Manager the old
   generation must keep a label. Its trigger is "no schedule", which a scheduled rotation contradicts.
   Managed rotation does not apply: the provider holds the public half, registered by hand or by Terraform.

## 1. Rotation of a secret whose other copy is at the identity provider

### What "managed rotation" is

"Some services offer *managed rotation*, where the service configures and manages rotation for you. With
managed rotation, you don't use an AWS Lambda function to update the secret and the credentials in the
database." The page lists Amazon Aurora, Amazon ECS Service Connect, Amazon RDS, Amazon DocumentDB, Amazon
Redshift, and "**managed external secrets** offers managed rotation for secrets held by Secrets Manager
partners", then: "For all other types of secrets, see Rotation by Lambda function."
([Managed rotation](https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotate-secrets_managed.html))

The partner table lists 26 secret types from Auth0, BigID, Cisco, Confluent Cloud, Databricks, Datadog,
GitLab, Jenkins, MongoDB Atlas, Netskope, Okta, Paddle, Palo Alto Networks, Salesforce, Snowflake,
SonarQube, Twilio and Versa Networks. `Auth0OAuthClientSecret` and `OktaOAuthClientSecret` are there; Amazon
Cognito and Ping Identity are not.
([Managed external secrets partners](https://docs.aws.amazon.com/secretsmanager/latest/userguide/mes-partners.html))
Managed external secrets also reach the partner from AWS's side: ingress is restricted by "the Secrets
Manager managed external secrets AWS-managed prefix list"
([Security and permissions](https://docs.aws.amazon.com/secretsmanager/latest/userguide/mes-security.html)),
which a PingFederate behind the cell's internal ALB would not admit even if Ping were a partner.

The Okta partner page is the closest published description of what a managed client-secret rotation does,
and is illustration only: "It creates a second client secret for the application and stores it as
AWSPENDING. It confirms that the new secret authenticates through the Okta token introspection endpoint. It
then deactivates the previous client secret, attempts to delete it, and promotes AWSPENDING to AWSCURRENT
... applications should re-read AWSCURRENT and retry after an authentication failure."
([Okta OAuth Client Secret](https://docs.aws.amazon.com/secretsmanager/latest/userguide/mes-partner-OktaOAuthClientSecret.html))
The consumer it assumes follows `AWSCURRENT`.

### Rotation by Lambda function

"To rotate a secret, Secrets Manager calls a Lambda function according to the rotation schedule you set up."
The function is invoked with `Step` (`create_secret`, `set_secret`, `test_secret`, `finish_secret`),
`SecretId` and `ClientRequestToken`, "A unique identifier for the new version of the secret."
([Rotation by Lambda function](https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotate-secrets_lambda.html))
The steps: `createSecret` stores a new value "with the staging label `AWSPENDING`"; `setSecret` "changes the
credential in the database or service to match the new secret value in the `AWSPENDING` version";
`testSecret` "tests the `AWSPENDING` version of the secret by using it to access the database or service";
`finishSecret` "moves the label `AWSCURRENT` from the previous secret version to this version. Secrets
Manager adds the `AWSPREVIOUS` staging label to the previous version". Where no template fits, "you can
create a custom rotation function", starting from the generic template; "If you use external binaries and
libraries ... you're responsible for patching and updating them."
([Lambda rotation functions](https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotate-secrets_lambda-functions.html);
[templates](https://docs.aws.amazon.com/secretsmanager/latest/userguide/reference_available-rotation-templates.html))
A schedule can be "as often as every four hours. The maximum rotation period is 999 days."
([Rotation schedules](https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotate-secrets_schedule.html))

The function's execution role, from the example policy: `secretsmanager:DescribeSecret`, `GetSecretValue`,
`PutSecretValue` and `UpdateSecretVersionStage` on the secret, `GetRandomPassword` on `*`, and the four
`ec2:*NetworkInterface*` actions only "if your database or service runs in a VPC".
([Execution role permissions](https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotating-secrets-required-permissions-function.html))
The function "must be able to access a Secrets Manager endpoint"; in a VPC without internet access that is
a Secrets Manager VPC endpoint or a NAT gateway.
([Network access](https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotation-function-network-access.html))

### Against Cognito

- "Each app client can have up to two secrets at a time, enabling secret rotation without downtime ... You
  can't change secrets after you create an app. You can add a second secret with the AddUserPoolClientSecret
  API operation to rotate secrets ... You cannot delete the only secret associated with an app client."
  ([App clients](https://docs.aws.amazon.com/cognito/latest/developerguide/user-pool-settings-client-apps.html))
- `AddUserPoolClientSecret`: "Supports up to 2 active secrets per app client for zero-downtime credential
  rotation workflows." `ClientSecret` is optional, 24 to 64 characters, pattern `[\w+]+`. "The
  ClientSecretValue is only returned when Amazon Cognito generates the secret. For custom secrets that you
  provide, the ClientSecretValue is not included in the response." A third add is `LimitExceededException`
  by the error list; the page does not tie that error to the two-secret limit in words.
  ([API](https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_AddUserPoolClientSecret.html))
- `ListUserPoolClientSecrets`: "there are only 2 secrets at any given time and we return both ... the
  response never reveals the actual secret value".
  ([API](https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_ListUserPoolClientSecrets.html))
- `DeleteUserPoolClientSecret`: "You cannot delete the last remaining secret for an app client." It takes a
  `ClientSecretId`.
  ([API](https://docs.aws.amazon.com/cognito-user-identity-pools/latest/APIReference/API_DeleteUserPoolClientSecret.html))
- The three actions authorise against the user pool ARN,
  `arn:${Partition}:cognito-idp:${Region}:${Account}:userpool/${UserPoolId}`
  ([service reference](https://servicereference.us-east-1.amazonaws.com/v1/cognito-idp/cognito-idp.json)).
- The feature was announced on 26 February 2026
  ([What's New](https://aws.amazon.com/about-aws/whats-new/2026/02/amazon-cognito-client-secret-lifecycle);
  excerpts only were read).

So `docs/runbooks/secret-rotation.md` is already the manual form of a rotation function, and what the
function must do against Cognito is (inferred from the pages above, not published by AWS as a template):

1. `create_secret`: generate the value in Secrets Manager with characters Cognito accepts and store it as
   `AWSPENDING`. Letting Cognito generate it breaks idempotency: the value is returned once.
2. `set_secret`: list the client's secrets; if two exist, delete the one that is not `AWSCURRENT`'s, which
   needs the provider's secret id stored beside the value because the list never returns values (the Okta
   type stores a `secretId` for the same reason); then `AddUserPoolClientSecret` with the pending value.
3. `test_secret`: authenticate with the pending value. **Not found:** a non-interactive check for an
   authorization-code client. "You can only activate client-credentials grants in app clients that have a
   client secret and that don't support authorization-code or implicit grants"; the same page names
   `GetClientToken` with `ALLOW_CLIENT_TOKEN_AUTH` as a second route to a machine token, and whether that
   flow can sit on an authorization-code client was not read.
4. `finish_secret`: move `AWSCURRENT`. The old Cognito secret can only be deleted once every consumer has
   moved, which the function cannot observe (section 2).

The function needs no VPC for Cognito: its API and Secrets Manager's are public endpoints. In Terraform it is
new resource types (a Lambda function, its role, its log group, the rotation configuration, the invoke
permission) in the apps root or the cell module; `tests/module.tftest.hcl` holds the cell module's type
allowlist, the platform root's test holds `aws_iam_role = 2`, `verify-teardown.sh` holds
`CELL_ROLE_SUFFIXES="task-execution task"`, and P-02 reads "A new app adds one Cognito client, one branding
style and one parameter, nothing else". The provider's `aws_cognito_user_pool_client` page at `main` shows
`generate_secret` and `client_secret` and nothing for a second secret
([provider docs](https://raw.githubusercontent.com/hashicorp/terraform-provider-aws/main/website/docs/r/cognito_user_pool_client.html.markdown)).

### Against PingFederate

- A client's secret is one field: "In the **Client Secret** field, enter a secret, or click **Generate
  Secret**." "For **Client Secret Retention Period (Minutes)**, select either **Use Global Settings** or
  enter an integer". "Click **Revoke Previous Secret(s)** to revoke any previous secrets that are no longer
  in use prior to the end of their retention period."
  ([Configuring OAuth clients, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_configuring_oauth_clients.html))
- The global setting: "The amount of time in minutes that the previous client secret is retained after
  changing the secret. If value is `0`, client secrets will not be retained. Maximum value is one month or
  `43800` minutes."
  ([Authorization server settings, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_authorizationserversettingstasklet_oauthauthorizationserversettingsstate.html))
- Ping's Terraform provider exposes the same shape: `client_secret_retention_period`, "The default value is
  `0`, which will disable secondary client secret retention", and a `client_auth.secondary_secrets` list,
  "secondary client secrets that are temporarily retained", each with an `expiry_time`.
  ([pingfederate_oauth_client](https://raw.githubusercontent.com/pingidentity/terraform-provider-pingfederate/main/docs/resources/oauth_client.md))
- The admin API has a secret endpoint: the 13.1 release notes name
  `oauth/clients/{id}/clientAuth/clientSecret` (PF-38659).
  ([13.1 release notes](https://docs.pingidentity.com/pingfederate/13.1/release_notes/pf_release_notes_131.html))
  Its request shape was not read.
- 13.1 lets PingFederate itself read a client's secret from a secret manager: "Click **Secret Manager** and
  enter your **Client Secret Reference**" (new in 13.1, PF-38494). Out of the box that is "the CyberArk
  Credential Provider and Windows gMSA"; anything else is "the SecretManager interface in the PingFederate
  SDK".
  ([Secret managers, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_secret_managers.html))
  So PingFederate reading the same Secrets Manager secret as the gateway, which would leave no second copy,
  needs a custom plugin. Not found: a Ping-published AWS Secrets Manager plugin.

So PingFederate's model is "change the secret, the old one lingers", not Cognito's "add, then delete". A
rotation function would have to call the admin API with an administrator credential (a second secret), from
inside the VPC: the finding "PingFederate in the poc cell" puts the admin port (9999) in no target group. A
Lambda in the VPC then needs a NAT gateway or a Secrets Manager interface endpoint to reach Secrets Manager
(above), and the cell has neither by default ("The task sends 443 out from a public address ... No NAT
gateway", `docs/plan/05-infrastructure-and-cost.md`). And under P2-6 the PingFederate profile authenticates
with `private_key_jwt`: "`clientSecretRef` stays required ... Under `PrivateKeyJwt` the parameter exists and
resolves; its value is never placed on the scheme's options nor sent"
(`contracts/changes/p2-6-pingfederate-profile.md`, decision 4). There is no client secret whose
other copy PingFederate holds; the credential there is the assertion key (section 6).

### Plainly

Managed rotation does not fit a client secret whose other copy is at Cognito or PingFederate. What exists is
(a) a custom Lambda rotation function, writable against Cognito with one open step (the test), or (b)
Secrets Manager as the store with versions and labels, and rotation staying `docs/runbooks/secret-rotation.md`
with its two store commands changed. Either amends ADR-0003; (a) also changes what a rotation is (section 2).

## 2. Versions and staging labels against `clientSecretRef`

### What the registry's reference is today

`contracts/registry.schema.json`: `"pattern": "^ssm:/[A-Za-z0-9][A-Za-z0-9._/-]*:[1-9][0-9]*$"`, "A
Parameter Store parameter and a version, as ssm:{name}:{version} (ADR-0003)". ADR-0003's consequences rest on
the version: "A versioned reference makes rotation a registry change rather than a deployment" and "A
snapshot with a missing or unreadable secret never replaces a working one."
`ParameterStoreClientSecretSource` is "Cached by reference, never invalidated. A reference pins a parameter
version, and a version's value never changes". `contracts/infra-outputs.schema.json` requires `secretParameter`
and `secretVersion` per client, "A rotation raises it, and the registry entry's clientSecretRef follows."

### What Secrets Manager offers instead

- "Secrets Manager doesn't store a linear history of secrets with versions. Instead, it keeps track of three
  specific versions by labeling them": `AWSCURRENT`, `AWSPREVIOUS`, `AWSPENDING`. "A secret always has a
  version labeled `AWSCURRENT`, and Secrets Manager returns that version by default".
- "You can attach up to 20 labels to versions in a secret. Two versions of a secret can't have the same
  staging label. Versions can have multiple labels."
- "Secrets Manager never removes labeled versions, but unlabeled versions are considered deprecated. Secrets
  Manager removes deprecated versions when there are more than 100. Secrets Manager doesn't remove versions
  created less than 24 hours ago."
  ([What's in a secret](https://docs.aws.amazon.com/secretsmanager/latest/userguide/whats-in-a-secret.html);
  the quotas page gives the same 20 and 100,
  [quotas](https://docs.aws.amazon.com/secretsmanager/latest/userguide/reference_limits.html))
- `GetSecretValue` takes `SecretId` ("The ARN or name"), `VersionId` ("typically a UUID-type value with 32
  hexadecimal digits", length 32 to 64) and `VersionStage`; "If you include both ... the two parameters must
  refer to the same secret version. If you don't specify either ... Secrets Manager returns the `AWSCURRENT`
  version." The response carries `VersionId` and `VersionStages`.
  ([GetSecretValue](https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_GetSecretValue.html))
- The secret's ARN ends in six random characters, and a re-created secret of the same name has a different
  ARN (same page as the first bullet), so a reference by name survives a teardown and a reference by ARN
  does not.

### What a store-neutral reference has to carry

1. **A scheme that names the store** (`ssm:` today), so the source and the validator know the selector's
   grammar and the grant to expect.
2. **A name** the store resolves: a Parameter Store path, or a Secrets Manager secret name. The characters a
   secret name admits were not read; the schema's name class has to be checked against them.
3. **A version selector whose form the scheme owns**: a positive integer for Parameter Store; for Secrets
   Manager a version id or a staging label. The colon-separated shape `scheme:name:selector` still parses at
   the last colon for both.

The choice inside item 3 is the decision, and it is not neutral (inference from the above):

- **Pinned to a version id.** Keeps every ADR-0003 consequence: the value under a reference never changes,
  the cache needs no invalidation, a rotation is a registry change, a snapshot resolves before it goes
  live, and `secretVersion` in the outputs document becomes a version id. Two things change. A version id is
  minted by the writer (a rotation's `ClientRequestToken` is "the `VersionId`") rather than counted, and a
  pinned version stays readable only while it has a label or until more than 100 versions exist: after two
  later rotations it has neither `AWSCURRENT` nor `AWSPREVIOUS`. A registry rollback to an old revision then
  depends on a deprecated version; a custom label per live reference holds it, within 20 labels.
- **Naming a label (`AWSCURRENT`).** This is what automatic rotation assumes ("rotating credentials no
  longer requires updating your applications",
  [What is Secrets Manager](https://docs.aws.amazon.com/secretsmanager/latest/userguide/intro.html)). The
  value changes under a constant reference, so "cached by reference, never invalidated" is false, the
  gateway must re-read (on a timer or on `invalid_client`), a rotation stops being a registry change, the
  outputs document has no version to carry, and D3's "resolved before a snapshot goes live" no longer
  covers the secret the provider will accept a minute later.

A rotation function fits the second shape and collides with the first: nothing in `create`, `set`, `test`,
`finish` waits for a registry revision to go live, so with a pinned reference the function can prepare the
version and add the provider's second secret, but the registry change and the deletion of the old provider
secret remain steps outside it, and the next scheduled rotation must not run before they happen (Cognito
has two slots).

## 3. Parameter Store's reference to a Secrets Manager secret

"You can only retrieve Secrets Manager secrets by using the GetParameter and GetParameters API operations.
Modification operations and advance querying API operations, such as DescribeParameters and
GetParametersByPath, aren't supported". "Secrets Manager secrets in Parameter Store must have a prefix of
`/aws/reference/secretsmanager`." "Parameter Store honors AWS Identity and Access Management (IAM) policies
attached to Secrets Manager secrets." "Parameters that reference Secrets Manager secrets can't use the
Parameter Store versioning or history features." "Parameter Store honors Secrets Manager version stages."
"Parameter Store functions as a pass-through service for references to Secrets Manager secrets ... The
reference is stateless." The examples read by name, by `name:{version id}` and by `name:AWSCURRENT`, each
with `--with-decryption`, and each returns `"Version": 0`, a `Selector`, and a `SourceResult` holding the
raw `GetSecretValue` response, `SecretString`, `VersionId` and `VersionStages` included.
([Referencing Secrets Manager secrets from Parameter Store](https://docs.aws.amazon.com/systems-manager/latest/userguide/integration-ps-secretsmanager.html);
`SourceResult` is "the raw result or response from the source",
[Parameter](https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_Parameter.html))

So the gateway could read Secrets Manager through `AWSSDK.SimpleSystemsManagement` 4.0.104.2 with **no new
package**. It is not a no-change route:

- the schema pattern admits the name `/aws/reference/secretsmanager/...` but requires `[1-9][0-9]*` after
  the last colon, so a version id or a label fails validation;
- `ParameterStoreClientSecretSource.TryParse` requires ASCII digits and a `long`;
- its guard `if (parameter.Version != version)` refuses the read, because the reference answers
  `"Version": 0`; the version actually served is in `SourceResult`, which the source would have to parse
  and which carries the secret value a second time;
- the task role's `ReadClientSecrets` statement is `ssm:GetParameter` and `ssm:GetParameters` on
  `parameter/spa-platform/${var.cell}/apps/*`, and P-03 asserts every statement.

**Not found:** the IAM the pass-through needs. No page read states which `ssm` parameter ARN a
`/aws/reference/secretsmanager/...` name authorises against, nor whether `secretsmanager:GetSecretValue` is
evaluated against the caller as well; the integration page says only that a caller without access to the
secret "can't retrieve Secret A by using Parameter Store". One call in a sitting settles it. Also not read:
what CloudTrail records for a read through the reference, and whether the Local tier's stand-in serves the
reference path.

## 4. The `AWSSDK.SecretsManager` package

From nuget.org's flat-container index, nuspecs and registration pages, read 2026-11-16
([index](https://api.nuget.org/v3-flatcontainer/awssdk.secretsmanager/index.json);
nuspecs at `https://api.nuget.org/v3-flatcontainer/awssdk.secretsmanager/{version}/awssdk.secretsmanager.nuspec`):

| Version | Published | Licence | `AWSSDK.Core` range | Against the pins |
| --- | --- | --- | --- | --- |
| 4.1.1 (newest) | 2026-11-12 | Apache-2.0 | `[4.1.0, 5.0.0)` | Lifts Core from 4.0.102.x to 4.1.x in every graph that carries it |
| 4.1.0 | 2026-11-12 | Apache-2.0 | `[4.1.0, 5.0.0)` | The same |
| 4.0.100.26 (newest 4.0) | 2026-11-10 | Apache-2.0 | `[4.0.103, 5.0.0)` | Lifts Core past both floors below |
| 4.0.100.20 | 2026-10-19 | Apache-2.0 | `[4.0.102.12, 5.0.0)` | Equals the `AWSSDK.DynamoDBv2` 4.0.108 floor: no move in `Platform.Identity` and the gateway; lifts `Platform.Registry` alone from 4.0.102.9 |
| 4.0.100.16 | 2026-10-08 | Apache-2.0 | `[4.0.102.9, 5.0.0)` | Equals the `AWSSDK.SimpleSystemsManagement` 4.0.104.2 floor: no move in any graph |

Each nuspec's licence is `<license type="expression">Apache-2.0</license>`; each has the one dependency. The
five listed versions are listed and not deprecated in the registration pages. 4.0.100.16 is from the same
8 October 2026 drop the `AWSSDK.Signin` pin comment in `Directory.Packages.props` names. `AWSSDK.Core`'s
newest is 4.1.0.1; 4.1.x is a minor line none of the solution's pins is on (`AWSSDK.SimpleSystemsManagement`
4.1.1 asks Core `[4.1.0.1, 5.0.0)`).

The assumption here is that the package would be referenced by `Platform.Registry`, where
`ParameterStoreClientSecretSource` lives and which `docs/dependencies.md` assigns `AWSSDK.SimpleSystemsManagement`
to. An earlier restore's `project.assets.json` in the main checkout shows `Platform.Registry`, `spactl` and
`Platform.Hosting` at Core 4.0.102.9 and `Platform.Identity` and the gateway at 4.0.102.12. Those files were
read, not regenerated: **no restore was run**, so "moves no other version" is arithmetic on the nuspecs, to be
confirmed by a trial restore as "Phase 2 package facts" did for DynamoDB. `Directory.Packages.props` and
`docs/dependencies.md` are the integrator's: the package is a change request.

## 5. IAM, the resource policy, CloudTrail, and deletion against teardown

**The task role, reading directly.** `GetSecretValue`: "Required permissions: `secretsmanager:GetSecretValue`.
If the secret is encrypted using a customer-managed key instead of the AWS managed key `aws/secretsmanager`,
then you also need `kms:Decrypt` permissions for that key." The service reference gives the resource as
`arn:${Partition}:secretsmanager:${Region}:${Account}:secret:${SecretId}` and, on `GetSecretValue`, the
condition keys `secretsmanager:VersionId`, `secretsmanager:VersionStage`, `secretsmanager:SecretId`,
`secretsmanager:ResourceTag/tag-key` and `secretsmanager:resource/Type` among others; `BatchGetSecretValue`
and `ListSecrets` name no resource type, so they can only be granted on `*`.
([service reference](https://servicereference.us-east-1.amazonaws.com/v1/secretsmanager/secretsmanager.json))
So the like-for-like grant is one statement, `secretsmanager:GetSecretValue` on
`secret:spa-platform/{cell}/apps/*` (the six random characters fall under the wildcard), with no batch call
and no listing; `iam.tf` and P-03's assertions change together. The AWS-managed key's policy lets principals
use it "only when the request comes from Secrets Manager on their behalf. The `kms:ViaService` condition key
enforces this"
([encryption](https://docs.aws.amazon.com/secretsmanager/latest/userguide/security-encryption.html)), the
same shape `iam.tf` records for Parameter Store's key. The CI plan role's `ReadCellParameters`
(`infra/bootstrap/ci-role.tf`) would need its counterpart for Terraform's refresh.

**The resource policy.** A resource-based policy is for granting "a single secret to multiple users and
roles" or "users or roles in other AWS accounts"; "To grant permission to retrieve secret values, you can
attach policies to secrets or identities."
([Resource-based policies](https://docs.aws.amazon.com/secretsmanager/latest/userguide/auth-and-access_resource-policies.html))
In one account the identity policy is enough, and the module's allowlist "admits no repository, key or
parameter policy" (`tests/module.tftest.hcl`), so none is the fit. A deny-all-but-the-task-role policy is
possible; no page read recommends it for this case.

**CloudTrail.** "Secrets Manager writes entries to your AWS CloudTrail log for all Secrets Manager operations
and for other events related to rotation and deletion." The list includes `GetSecretValue`, `PutSecretValue`,
`UpdateSecretVersionStage`, `RotateSecret` and `DeleteSecret`; the service events include `RotationStarted`,
`RotationSucceeded`, `RotationFailed`, `RotationAbandoned`, `StartSecretVersionDelete`, `EndSecretVersionDelete`
and `SecretVersionDeletion`.
([CloudTrail entries](https://docs.aws.amazon.com/secretsmanager/latest/userguide/cloudtrail_log_entries.html))
"Secrets Manager logs all events as management events."
([intro, Pricing](https://docs.aws.amazon.com/secretsmanager/latest/userguide/intro.html))
`SecretString` is "Sensitive: ... the service does not include it in AWS CloudTrail log entries."
(`GetSecretValue`, above.) So every read of a client secret, by whom and of which secret, is an audit record
the account already keeps: evidence for the map's "enterprise secret store" control that needs no new
resource. No comparison with Parameter Store's records is made here; that page was not read.

**Deletion against teardown.** "Deletes a secret and all of its versions ... The minimum recovery window is
7 days. The default recovery window is 30 days." "When a secret is scheduled for deletion, you cannot
retrieve the secret value." With `ForceDeleteWithoutRecovery`: "Secrets Manager performs the actual deletion
with an asynchronous background process, so there might be a short delay ... If you delete a secret and then
immediately create a secret with the same name, use appropriate back off and retry logic."
([DeleteSecret](https://docs.aws.amazon.com/secretsmanager/latest/apireference/API_DeleteSecret.html))
"There is no charge for secrets that you have marked for deletion."
([Delete a secret](https://docs.aws.amazon.com/secretsmanager/latest/userguide/manage_delete-secret.html))
Terraform: `recovery_window_in_days` "can be `0` to force deletion without recovery or range from `7` to
`30` days. The default value is `30`."
([aws_secretsmanager_secret](https://raw.githubusercontent.com/hashicorp/terraform-provider-aws/main/website/docs/r/secretsmanager_secret.html.markdown))

Against the repository:

- `verify-teardown.sh`'s fourth reader is the tag query, and "Anything else listed stands". Secrets are
  taggable resources (`AWS::SecretsManager::Secret`: Tag Editor tagging Yes, tag-based groups Yes,
  [Resource Groups supported types](https://docs.aws.amazon.com/ARG/latest/userguide/supported-resources.html)).
  **Not read:** whether a secret inside its recovery window is still returned by `GetResources`. If it is,
  a default destroy fails P-05 for up to 30 days; if it is not, the secret is still there, unbilled and
  restorable, which is not "nothing remains".
- G3 "ends with a second deployment from nothing" (`docs/plan/04-tests.md`). A secret scheduled for deletion
  still holds its name; that a create under that name is refused during the window is the expected
  behaviour and was not read on a page here.
- So the stand-in sets `recovery_window_in_days = 0`. The force delete's "short delay" sits inside the
  reader's retry (300 seconds by default) and the redeploy needs the back-off the API page asks for.
- The client secrets are in the **apps root** (`infra/envs/poc/apps/main.tf`), and the map's cost rule says
  "the platform root's destroy removes it". A gated stand-in whose resources are in the apps root is removed
  by `teardown.sh`'s destroy of all three roots, not by the platform root's alone; the rule's wording or the
  resource's home has to give.
- A rotation Lambda adds a role the by-name reader's `CELL_ROLE_SUFFIXES` does not list, and a log group.

## 6. What the assertion key would need to move too

The resolution of "The gateway's client assertion key: generation, storage, the public key's registration
and rotation" (#953) put one RSA 2048 key per cell per issuer in Parameter Store, PKCS#8 PEM, SecureString,
Standard tier, at `/spa-platform/{cell}/issuers/{issuer}/assertion-key`, generated by Terraform's
`tls_private_key` in the apps root, with `KeyRef` as `ssm:...:{version}`. Moving it means:

1. **The reference.** `KeyRef` is "the `clientSecretRef` pattern" (`p2-6-pingfederate-profile.md`), so the
   store-neutral reference of section 2 covers it and the two should change in one contract change.
2. **The grant.** Answer 1 already adds the `issuers/*` prefix to the task role; in Secrets Manager it is a
   second ARN pattern on the `GetSecretValue` statement.
3. **The resource.** `aws_ssm_parameter` becomes `aws_secretsmanager_secret` and a version, written by the
   same `tls_private_key`. A PEM fits: `SecretString` is up to 65,536 characters (`GetSecretValue`, above).
4. **The overlap.** Answer 6 relies on "SSM keeps the old version, so pinned tasks keep working". Secrets
   Manager keeps the previous generation only as `AWSPREVIOUS`, a custom label, or one of the newest 100
   (section 2). One overlap of two generations is inside `AWSCURRENT` and `AWSPREVIOUS`; anything longer
   needs a label.
5. **The trigger.** Answer 7: "Rotation happens only when a key is suspected exposed, with no schedule."
   A rotation schedule on the secret contradicts it; without a schedule Secrets Manager adds storage,
   labels and the audit trail and no rotation.
6. **Rotation itself.** The other half is a public JWKS on the provider's client record, placed by hand or
   by Ping's Terraform provider. No managed rotation applies (section 1); a function would generate the
   pair, publish the two-key JWKS through PingFederate's admin API and later drop the old key, with the
   same admin-reach and VPC questions as a client secret. AWS does this for one partner
   (`SnowflakeKeyPairAuthentication`), not for Ping.
7. **The Local tier.** Answer 5 seeds the key into the local AWS stand-in "at `KeyRef`". Whichever API the
   gateway calls, the stand-in has to serve it; that was not read here.
8. **The unused client secret.** Under `PrivateKeyJwt` each app's `clientSecretRef` "still has to exist and
   resolve, but is never sent" (answer 8). In Secrets Manager that is one billed secret per app holding a
   value nothing uses, unless the deferred item of P2-6 decision 4 is taken up.

## 7. Cost drivers met (no table: the pricing ticket's)

- **Per secret per month**, prorated: the pricing page's own table values did not load for the fetch; its
  worked examples use "$0.40 / secret / month" and "$0.05/10,000 calls", and prorate by the hour
  ([pricing](https://aws.amazon.com/secrets-manager/pricing/), read 2026-11-16 through a summarising fetch;
  figures are the examples', not confirmed for `us-east-2`). The count is one per app (two fixture apps
  today), plus one per issuer if the assertion key moves, plus an administrator secret if a function rotates
  against PingFederate. Standard parameters cost nothing (ADR-0003), so this is the whole of the idle cost
  and it scales with apps.
- **Marked for deletion: no charge** (section 5), so a force-deleted or scheduled secret stops billing at
  destroy.
- **API calls:** the gateway reads once per reference per process, so calls are negligible; a re-reading
  gateway (section 2, label) raises them by its interval.
- **The key:** "You can use the AWS managed key `aws/secretsmanager` ... for free. If you create your own
  KMS keys ... AWS charges you at the current AWS KMS rate."
- **Rotation:** "When you turn on automatic rotation (except managed rotation), Secrets Manager uses an AWS
  Lambda function ... and you are charged for the rotation function at the current Lambda rate"; its log
  group adds CloudWatch Logs.
- **Egress for a function in the VPC** (PingFederate only): a NAT gateway or a Secrets Manager interface
  endpoint, which `docs/plan/05-infrastructure-and-cost.md` puts at "about $33 each plus data" and "about
  $7 per endpoint per zone" a month. This is the one driver that could meet the map's ceiling by itself.
- **CloudTrail:** "AWS CloudTrail stores the first copy of all management events for free"; a second trail
  or S3 delivery is charged. (All quotes in the last four bullets:
  [intro, Pricing](https://docs.aws.amazon.com/secretsmanager/latest/userguide/intro.html).)
- **The Parameter Store reference route:** what a `GetParameter` on a reference is billed as was not read.

## 8. What was executed and what was only read

**Executed (2026-11-16), none against the account or the cell:**

- `git fetch`, `git worktree add`, `gh issue view` for #1224, #1220, #953 and #948.
- `curl` to `api.nuget.org`: the flat-container version lists of `AWSSDK.SecretsManager`,
  `AWSSDK.SimpleSystemsManagement`, `AWSSDK.Core` and `AWSSDK.DynamoDBv2`; 30 `AWSSDK.SecretsManager` nuspecs
  (4.0.0, 4.0.100 to 4.0.100.26, 4.1.0, 4.1.1), two of `AWSSDK.SimpleSystemsManagement` and one of
  `AWSSDK.Core`; the registration pages for publish dates and listing state. Parsed with `python3 -I`.
- `curl` to `servicereference.us-east-1.amazonaws.com` for the `secretsmanager`, `ssm` and `cognito-idp`
  service-reference JSON, parsed with `python3 -I`.
- `curl` of markdown copies of documentation pages (below) and of two Terraform provider pages on GitHub.
- A read of `obj/project.assets.json` under the main checkout for the Core each graph resolved at its last
  restore. No restore, build or test was run.

**Read only, each on 2026-11-16.** AWS Secrets Manager user guide: Managed rotation; Managed external
secrets, its partners, security and rotation pages; the Okta OAuth Client Secret partner page; Rotation by
Lambda function; Lambda rotation functions; rotation function templates; execution role permissions; network
access; rotation schedules; What's in a secret; quotas; resource-based policies; CloudTrail entries; Delete a
secret; encryption; Use Secrets Manager secrets in Parameter Store; What is Secrets Manager (Pricing). Secrets
Manager API reference: `GetSecretValue`, `DeleteSecret`. Systems Manager: Referencing Secrets Manager secrets
from Parameter Store; the `Parameter` type. Cognito: App clients (developer guide); `AddUserPoolClientSecret`,
`ListUserPoolClientSecrets`, `DeleteUserPoolClientSecret`; the 26 February 2026 What's New post (excerpts).
ECS: Pass Secrets Manager secrets through environment variables (read, not used: injection is at task start
and by the execution role, outside the registry's reference). Resource Groups: supported resource types (the
`AWS::SecretsManager::Secret` row). Secrets Manager pricing page (examples only). PingFederate 13.1:
Configuring OAuth clients; Configuring authorization server settings; Secret managers; 13.1 release notes.
GitHub: `hashicorp/terraform-provider-aws` `secretsmanager_secret` and `cognito_user_pool_client` pages at
`main`; `pingidentity/terraform-provider-pingfederate` `oauth_client` page at `main`. Each URL is at its
claim above.

**Not found, or not read, and so not claimed:**

- The IAM a `/aws/reference/secretsmanager/...` read needs, and what CloudTrail records for it.
- A non-interactive way to test a new secret on a Cognito authorization-code client.
- Whether the tag query returns a secret inside its recovery window, and the wording that a name is held
  during it.
- The characters a Secrets Manager secret name admits.
- The request shape of PingFederate's `clientAuth/clientSecret` endpoint, and any Ping-published AWS
  Secrets Manager plugin.
- Whether the Local tier's AWS stand-in serves Secrets Manager or the Parameter Store reference path.
- The Region's price table values (the pricing ticket's), and the billing of a reference read.
- The Resource Groups Tagging API's own supported-services page (it did not load); the Resource Groups
  table was read in its place.
- The service authorization reference's HTML page (it did not load); the service-reference JSON was read in
  its place.
