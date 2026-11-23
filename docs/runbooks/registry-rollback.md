# Roll the registry back

Put the registry's previous content back, under a new revision. The Registry lever: `spactl registry
rollback` republishes the previous object version under a new revision. The registry's revision
only ever rises (`contracts/README.md`), so a rollback is the old content with the next number, never
the old number. Live after one poll interval, with no deployment.

**Who is signed out.** An app whose entry is the same before and after keeps its sessions. An app the
rollback removes, or puts on another client, loses every session as the swap lands. A change that
keeps the client, a secret included, keeps them.

Start with the operator session in [README.md](README.md): the cell's entry file sourced
(`set -a; . <entry file>; set +a`; `tools/scripts/cell-common.sh`'s header names its values), `APP`
set to the id of an app the rolled-back registry still holds and `APP_DEFINITION` to that app's
definition file, then the platform configuration (`$CONFIG`) and the tunnel as the README has them.
The app's origin is its definition's `host`:

```bash
ORIGIN="https://$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["host"])' "$APP_DEFINITION")"
```

## Roll back one change

```bash
spactl registry rollback --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
```

It takes the version of `registry/registry.json` before the live one, by its place in the bucket's
version history, raises its revision to one past the live revision, checks it as `registry apply`
would (the schema, the platform configuration's bounds, every release prefix published) and writes
it only if the live object is still the one it read. It prints `Applied revision N` and `Rolled back
to the content of revision M`.

**A second plain rollback goes forward again**, because the version before a rollback is the one it
rolled back from. To go further back, name the revision whose content you want:

```bash
spactl registry rollback --to-revision 12 --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
```

That takes the newest earlier version carrying revision 12.

It refuses, writing nothing, when there is no live registry, no earlier version (or none with that
revision), when the earlier content fails today's checks (a release since made unreachable, an
issuer the gateway no longer has), or when another write landed after it read the live object. In
the last case run it again.

## Wait for it

```bash
spactl wait --url $ORIGIN --revision N
```

Use the origin of an app the rolled-back registry still holds: `/readyz` answers on any host, but
the hosts-file line must name it.

## If the gateway does not take it

`spactl` and the gateway apply the same validator, but the gateway also resolves every entry's
client secret before a snapshot goes live, which `spactl` does not (ADR-0003: "A snapshot with a
missing or unreadable secret never replaces a working one"). If `/readyz` stays on the old revision
past a few poll intervals, the gateway refused the snapshot whole and kept serving the working one
(ADR-0002). Read why in its log, through the redaction:

```bash
tools/scripts/logs.sh --since 15m
```
