# Roll a release back

Make an app serve the release it served before. The SPA release lever: activate the previous
release. Activation is a registry change (`contracts/s3-layout.md`), so nothing is deployed, no task
restarts and no one is signed out. Live after one poll interval.

Start with the operator session in [README.md](README.md): the cell's entry file sourced
(`set -a; . <entry file>; set +a`; `tools/scripts/cell-common.sh`'s header names its values), `APP`
set to the app's id and `APP_DEFINITION` to the app's definition file, then the platform
configuration (`$CONFIG`) and the tunnel as the README has them. The app's origin is its
definition's `host`:

```bash
ORIGIN="https://$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["host"])' "$APP_DEFINITION")"
```

## 1. Find the release to go back to

Every release ever published stays in the bucket: a release prefix is written once and cannot be
deleted (ADR-0009, ADR-0014). List the app's releases:

```bash
BUCKET="$(python3 -I tools/scripts/cell_documents.py field $SPA_OUTPUTS_FILE bucket "$AWS_REGION")"
aws s3api list-objects-v2 --region "$AWS_REGION" --bucket "$BUCKET" --prefix apps/$APP/releases/ \
  --delimiter / --query 'CommonPrefixes[].Prefix' --output text | tr '\t' '\n'
```

The list does not say which one was live before. The registry's own history does, since every
change to it is a new object version (`contracts/s3-layout.md`): the release `$APP` had in the
version before the current one is

```bash
PREVIOUS="$(aws s3api list-object-versions --region "$AWS_REGION" --bucket "$BUCKET" --prefix registry/registry.json \
  --query 'Versions[1].VersionId' --output text)"
aws s3api get-object --region "$AWS_REGION" --bucket "$BUCKET" --key registry/registry.json \
  --version-id "$PREVIOUS" "$RUN/previous-registry.json" > /dev/null
python3 -I -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["revision"], [a["release"] for a in d["apps"] if a["appId"]==sys.argv[2]])' \
  "$RUN/previous-registry.json" $APP
```

S3 lists versions newest first, so `Versions[1]` is the one before the live one. If that version
changed something else, step back further (`Versions[2]`, and so on) until the release differs.

## 2. Activate it

```bash
spactl release activate $APP <previous release> --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
```

It refuses an app the registry does not hold, and a release whose prefix has no `index.html` (it was
never published, or its publish stopped part way). It prints `Applied revision N`, or `Nothing to
change` when the app already serves that release.

It also refuses an older release whose import map no hash in the definition's `csp.script-src`
admits any more, naming the `'sha256-…'` source to add. Add it back to the definition, run
`spactl app add` with it, then activate.

## 3. Wait for it

```bash
spactl wait --url $ORIGIN --revision N
```

`Ready: ... reports revision N` means the gateway serves the release. Load the app's host in a
browser to see it.

Rolling forward again is the same command with the newer release.
