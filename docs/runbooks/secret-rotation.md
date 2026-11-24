# Rotate a client secret

Replace an app's OAuth client secret without a failed login, a failed refresh or a deployment.
ADR-0003: the registry names the secret by Parameter Store name and version, so a rotation is a
registry change.

**No one is signed out.** The client stays the same, and a change that keeps the client keeps every
session. Both secrets are valid from step 2 to step 7, so a login or refresh in flight succeeds with
either.

**The secret never reaches a terminal, an argument list or a log.** It is made into an owner-only
file, and the AWS CLI reads it from there: the CLI loads any parameter given as `file://<path>` from
that file
([AWS CLI: loading parameters from a file](https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-parameters-file.html)).

Start with the operator session in [README.md](README.md), with `APP` and `APP_DEFINITION` naming
the app whose secret is rotated, including the platform configuration (`$CONFIG`, made with
`umask 077` in `$RUN`) and the tunnel.

## 1. Name the client and its parameter

```bash
CLIENT_ID="$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["clients"][sys.argv[2]]["clientId"])' "$SPA_OUTPUTS_FILE" "$APP")"
PARAMETER="$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["clients"][sys.argv[2]]["secretParameter"])' "$SPA_OUTPUTS_FILE" "$APP")"
```

These are kept in variables and not printed: `tools/scripts/logs.sh` elides client ids, and a
provider's secret ids can carry the client id.

The provider must hold the client's secret alone now, and accept a second beside it, since step 2
adds one. If the client already has two, an earlier rotation stopped before its step 7: finish that
one first.

## 2. Add a second secret at the provider

Add a second secret to the client at the provider, so that the old one and the new one are both
valid. Its value goes into the owner-only file `$RUN/secret`, never onto the terminal, in the form
and length the provider accepts: step 3 reads it from there. Keep whatever the provider names the
old secret by, out of the terminal too, for step 7.

## 3. Write it as a new version of the app's parameter

```bash
NEW_VERSION="$(aws ssm put-parameter --region "$AWS_REGION" --name "$PARAMETER" --type SecureString \
  --value "file://$RUN/secret" --overwrite --query Version --output text)"
rm -f "$RUN/secret"
echo "parameter version $NEW_VERSION"
```

The root that creates the parameter must ignore later changes to its value
(`lifecycle { ignore_changes = [value] }` on the parameter), or its next apply puts the old secret
back.

## 4. Bring the new version into the outputs document

`spactl app add` builds the entry's `clientSecretRef`, `ssm:{name}:{version}`, from the outputs
document, `$SPA_OUTPUTS_FILE`, and that document is written from the roots' Terraform outputs, which
read state. State learns the new version on a refresh, in the root the field map names for the
`clients` field:

```bash
CLIENTS_ROOT="$(python3 -I -c '
import json, sys
name = next(f["root"] for f in json.load(open(sys.argv[1]))["fields"] if f["field"] == "clients")
print(next(d for d in sys.argv[2].split(":") if d.rstrip("/").rsplit("/", 1)[-1] == name))
' "$SPA_FIELD_MAP" "$SPA_ROOTS")"
terraform -chdir="$CLIENTS_ROOT" apply -refresh-only
printf '%s\n' "$SPA_ROOTS" | tr ':' '\n' > "$RUN/roots"
set --
while IFS= read -r dir; do
  terraform -chdir="$dir" output -no-color -json > "$RUN/${dir##*/}-outputs.json"
  set -- "$@" "${dir##*/}" "$RUN/${dir##*/}-outputs.json"
done < "$RUN/roots"
python3 -I tools/scripts/cell_documents.py outputs "$AWS_REGION" "$SPA_FIELD_MAP" "$SPA_OUTPUTS_FILE" "$@"
python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["clients"][sys.argv[2]]["secretVersion"])' "$SPA_OUTPUTS_FILE" "$APP"
```

The refresh's plan should show the parameter's `version` changing and nothing else; answer `yes` only
then. The last line must print `$NEW_VERSION`. The `output` calls, one per root on `$SPA_ROOTS` in
its order, and `cell_documents.py outputs` over them and the field map are what the scripts'
`write_outputs` runs (`tools/scripts/cell-common.sh`).

Why not edit `clientSecretRef` in the live registry and `registry apply` it instead: the outputs
document would still name the old version, and the next `spactl app add` for this app, by hand or by
a script, would put the old reference back after step 7 had deleted that secret.

## 5. Point the entry at the new version

```bash
spactl app add "$APP_DEFINITION" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
```

An app the registry already holds is replaced in place, at its live release, with the new secret
reference. It prints `Applied revision N` and `Replaced <app>, at release ...`. The entry is rebuilt
from the app definition, so `$APP_DEFINITION` must be the definition the app was onboarded with.

## 6. Wait for it, then sign in

```bash
spactl wait --url "$ORIGIN" --revision N
```

Then sign in to the app in a browser, which exchanges a code with the new secret. If `/readyz` stays
on the old revision, the gateway could not read the new parameter version and kept the working
snapshot (ADR-0003: "A snapshot with a missing or unreadable secret never replaces a working one");
the old secret is still valid, so nothing has failed yet. Read why with
`tools/scripts/logs.sh --since 15m`, fix it, and run step 5 again.

## 7. Delete the old secret at the provider

Only after step 6 reported the revision: from here the old secret stops working.

Delete the old secret at the provider, by the name step 2 kept, and check that the client holds the
new one alone.

Then `rm -rf "$RUN"`.
