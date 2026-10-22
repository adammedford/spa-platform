# Rotate a client secret

Replace an app's OAuth client secret without a failed login, a failed refresh or a deployment.
`docs/plan/05-infrastructure-and-cost.md`, Rotating a client secret, and ADR-0003: the registry
names the secret by Parameter Store name and version, so a rotation is a registry change.

**No one is signed out.** The client stays the same, and a change that keeps the client keeps every
session (S-D3, R-C2; the options invalidator, plan row 3.6). Both secrets are valid from step 2 to
step 7, so a login or refresh in flight succeeds with either.

**The secret never reaches a terminal, an argument list or a log** (AGENTS.md; N-16). It is made into
an owner-only file, and the AWS CLI reads it from there: the CLI loads any parameter given as
`file://<path>` from that file
([AWS CLI: loading parameters from a file](https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-parameters-file.html)).

Start with the operator session in [README.md](README.md), including the platform configuration
(`$CONFIG`, made with `umask 077` in `$RUN`) and the tunnel. The example rotates `app1`.

## 1. Name the client and its parameter

```bash
APP=app1
CLIENT_ID="$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["clients"][sys.argv[2]]["clientId"])' infra/envs/poc/outputs.json "$APP")"
PARAMETER="$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["clients"][sys.argv[2]]["secretParameter"])' infra/envs/poc/outputs.json "$APP")"
POOL_ID="$(terraform -chdir=infra/envs/poc/idp output -raw user_pool_id)"
OLD_SECRET_ID="$(aws cognito-idp list-user-pool-client-secrets --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --query 'ClientSecrets[].ClientSecretId' --output text)"
```

These are kept in variables and not printed: `tools/scripts/logs.sh` elides client ids, and a secret
id begins with the client id (`<client-id>--<epoch-create-time>`).

The client must have exactly one secret now. Cognito allows two active secrets per client
(`aws cognito-idp add-user-pool-client-secret help`), and step 2 adds the second. If
`OLD_SECRET_ID` holds two ids, an earlier rotation stopped before its step 7: finish that one first.

```bash
printf '%s\n' "$OLD_SECRET_ID" | wc -w        # 1: go on. 2: stop
```

## 2. Add a second secret at the provider

```bash
openssl rand -hex 32 | tr -d '\n' > "$RUN/secret"      # 64 characters: Cognito takes 24 to 64 of [A-Za-z0-9_+]
NEW_SECRET_ID="$(aws cognito-idp add-user-pool-client-secret --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --client-secret "file://$RUN/secret" \
  --query 'ClientSecretDescriptor.ClientSecretId' --output text)"
```

Cognito returns the value only when it generated the secret, so a secret given this way comes back
as its id alone. Terraform's client resource may not expose a second secret yet, which is why this
and step 7 use the AWS CLI (`docs/plan/05-infrastructure-and-cost.md`; risk 14 in
`docs/plan/06-phase-2-and-risks.md`).

## 3. Write it as a new version of the app's parameter

```bash
NEW_VERSION="$(aws ssm put-parameter --region "$AWS_REGION" --name "$PARAMETER" --type SecureString \
  --value "file://$RUN/secret" --overwrite --query Version --output text)"
rm -f "$RUN/secret"
echo "parameter version $NEW_VERSION"
```

The apps root ignores later changes to the parameter's value (`lifecycle { ignore_changes = [value] }`
in `infra/envs/poc/apps/main.tf`; plan row 1.8), so the next apply does not put the old secret
back.

## 4. Bring the new version into the outputs document

`spactl app add` builds the entry's `clientSecretRef`, `ssm:{name}:{version}`, from
`infra/envs/poc/outputs.json` (`tools/spactl/AppEntry.cs`), and that document is written from the
roots' Terraform outputs, which read state. State learns the new version on a refresh:

```bash
terraform -chdir=infra/envs/poc/apps apply -refresh-only
for root in idp apps platform; do
  terraform -chdir="infra/envs/poc/$root" output -no-color -json > "$RUN/$root-outputs.json"
done
python3 -I tools/scripts/cell_documents.py outputs "$AWS_REGION" \
  "$RUN/idp-outputs.json" "$RUN/apps-outputs.json" "$RUN/platform-outputs.json" infra/envs/poc/outputs.json
python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["clients"][sys.argv[2]]["secretVersion"])' infra/envs/poc/outputs.json "$APP"
```

The refresh's plan should show the parameter's `version` changing and nothing else; answer `yes` only
then. The last line must print `$NEW_VERSION`. The three `output` calls and `cell_documents.py
outputs` are what the scripts' `write_outputs` runs (`tools/scripts/cell-common.sh`).

Why not edit `clientSecretRef` in the live registry and `registry apply` it instead: the outputs
document would still name the old version, and the next `spactl app add` for this app, by hand or by
`seed.sh`, would put the old reference back after step 7 had deleted that secret.

## 5. Point the entry at the new version

```bash
spactl app add "fixtures/apps/$APP.json" --config "$CONFIG"
```

An app the registry already holds is replaced in place, at its live release, with the new secret
reference (`AppAddAsync` in `tools/spactl/Commands.cs`). It prints `Applied revision N` and
`Replaced app1, at release ...`. The entry is rebuilt from the app definition: an onboarded app's
definition is the one in `fixtures/apps/`.

## 6. Wait for it, then sign in

```bash
spactl wait --url https://app1.spa-poc.test:8443 --revision N
```

Then sign in to the app in a browser, which exchanges a code with the new secret. If `/readyz` stays
on the old revision, the gateway could not read the new parameter version and kept the working
snapshot (decision D3, `docs/plan/01-plan.md`); the old secret is still valid, so nothing has failed
yet. Read why with `tools/scripts/logs.sh --since 15m`, fix it, and run step 5 again.

## 7. Delete the old secret at the provider

Only after step 6 reported the revision: from here the old secret stops working.

```bash
aws cognito-idp delete-user-pool-client-secret --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --client-secret-id "$OLD_SECRET_ID"
aws cognito-idp list-user-pool-client-secrets --region "$AWS_REGION" \
  --user-pool-id "$POOL_ID" --client-id "$CLIENT_ID" --query 'length(ClientSecrets)' --output text
```

The last line prints `1`. Cognito refuses to delete a client's last secret.

Then `rm -rf "$RUN"`.

## Checks for the first walk (#244)

These rest on reading, not on a run, and the first walk settles them:

- that `terraform apply -refresh-only` in the apps root moves the parameter's `version` in state
  although the value is ignored, and that its plan shows nothing else;
- that a plain `terraform plan` in the apps root after step 7 is empty, with the client's original
  secret deleted.
