# End every session for an app

Sign every user of one app out at once, leaving every other app's users signed in: for a suspected
session theft, a permission change that must take effect now, or an app's users who must sign in
again for any other reason.

Phase 1 has no command for this, and needs none: the gateway already purges an app's sessions when
its registry entry goes or names a new client. So the sequence is to take the entry out, wait for
the gateway to swap to that registry, and put the entry back unchanged.

**The app is down between steps 2 and 4**: its host answers 404 for about two poll intervals. To end
every session of every app instead, any gateway deployment does it in Phase 1 ([deploy.md](deploy.md)).

Start with the operator session in [README.md](README.md): the cell's entry file sourced
(`set -a; . <entry file>; set +a`; `tools/scripts/cell-common.sh`'s header names its values), `APP`
set to the app's id and `APP_DEFINITION` to the app's definition file, then the platform
configuration (`$CONFIG`) and the tunnel as the README has them. The app's origin is its
definition's `host`:

```bash
ORIGIN="https://$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["host"])' "$APP_DEFINITION")"
```

## 1. Note the release the app serves

The entry comes back at the same release, and once it is out of the registry `spactl` can no longer
read that release from it.

```bash
BUCKET="$(python3 -I tools/scripts/cell_documents.py field "$SPA_OUTPUTS_FILE" bucket "$AWS_REGION")"
aws s3api get-object --region "$AWS_REGION" --bucket "$BUCKET" --key registry/registry.json "$RUN/registry.json" > /dev/null
RELEASE="$(python3 -I -c 'import json,sys; print(next(a["release"] for a in json.load(open(sys.argv[1]))["apps"] if a["appId"]==sys.argv[2]))' "$RUN/registry.json" "$APP")"
echo "$APP serves release $RELEASE"
```

If the app is the registry's only one, step 2 leaves an empty registry. That is a valid document and
the running gateway takes it, but a task that starts while it is live never becomes ready
(`deploy.sh`'s refusal, `contracts/README.md`), so deploy nothing until step 4 is done.

## 2. Remove the entry

```bash
spactl app remove "$APP" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
```

It prints `Applied revision N` and `Removed $APP.`

## 3. Wait until the gateway has swapped to it

```bash
spactl wait --url "$ORIGIN" --revision N
```

`/readyz` answers on any host, the removed one included (`contracts/pipeline.md`). **Do not skip
this wait.** The purge happens when the gateway swaps from a snapshot that holds the app to one that
does not. If the entry came back before the gateway polled, it would see the same entry before and
after, and purge nothing.

Then confirm the purge ran, in the gateway's log through the redaction:

```bash
tools/scripts/logs.sh --since 10m | grep "purged the sessions of app $APP (removed)"
```

That line is the gateway's event 3602, whose message reads "Evicted the cached options and purged
the sessions of app {AppId} ({Reason}) at registry revision {Revision}." The gateway writes JSON
lines (`contracts/telemetry.md`); if the `grep` finds nothing, look for event id 3602 in the last ten
minutes' lines.
If instead the log says the sessions `could not be purged` (event 3603), the records are still in
memory; end them with a gateway deployment ([deploy.md](deploy.md)).

## 4. Put the entry back

```bash
spactl app add "$APP_DEFINITION" --release "$RELEASE" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
spactl wait --url "$ORIGIN" --revision M
```

`M` is the revision `app add` printed. The entry is rebuilt from the app definition and the
infrastructure outputs, as the scripts build it, so it is the entry that went. The app serves again,
and every user signs in afresh.

## What this does not do

- **It revokes nothing at the provider.** The purge deletes the gateway's session records, and with
  them the only copy of each refresh token. A user's logout (`POST /bff/logout`) attempts a
  revocation (`contracts/http-contract.md`); the purge does not. A refresh token that had leaked out
  of the gateway would stay valid at the provider until it expires, but a refresh with this
  confidential client also needs its secret. If the secret may have leaked too, rotate it as well
  ([secret-rotation.md](secret-rotation.md)).
- **It does not end the user's session at the provider.** A logout redirects through the provider's
  logout (`contracts/http-contract.md`); the purge does not, so a user whose sign-in session at the
  provider is still live may sign in again without a password. What it ends is every session the
  gateway holds for the app, so every one of its gateway cookies, a stolen one included, now names a
  session that is gone (the cookie carries only a key into the session store).
- **It is not per user.** Ending one user's sessions needs a session index by subject, which is
  Phase 2's (ADR-0028).
