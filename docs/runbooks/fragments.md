# Publish, activate and roll back a fragment

Make one fragment of a shell serve a new release, or the one it served before. The fragment's lever:
publish its release, then activate it. Activation is a registry change to that fragment alone
(`contracts/s3-layout.md`), so nothing is deployed, no task restarts, the shell's release stays as
it is and no one is signed out. Live after one poll interval.

**Before the first fragment reaches a cell**, the cell must run the fragments build: the deployment
prerequisite in [deploy.md](deploy.md).

Start with the operator session in [README.md](README.md): the cell's entry file sourced, `APP` set
to the shell's id and `APP_DEFINITION` to its definition file, then the platform configuration
(`$CONFIG`) and the tunnel. Set `FRAGMENT` to the fragment's id. The shell's origin is its
definition's `host`, as the README has it (`$ORIGIN`).

## 1. Publish the release

```bash
spactl fragment publish $APP $FRAGMENT <directory> <release> --outputs "$SPA_OUTPUTS_FILE"
spactl fragment publish $APP $FRAGMENT <directory> <release> --entry main.js --outputs "$SPA_OUTPUTS_FILE"
```

It uploads the directory under `apps/$APP/fragments/$FRAGMENT/releases/<release>/` and writes the
entry file last, so a release is complete only once its entry is there. It reads no registry, so a
fragment's owner can publish before the fragment is onboarded. `--entry` defaults to `entry.js`
and must equal the definition's `entry`: the publish cannot check it, and an activation then refuses
the release for its missing entry file.

It refuses a directory with no entry file, and one with a top-level `api/`, since
`/fragments/{fragmentId}/api/` is the fragment's APIs and maps to no release key; nothing is
written. A publish that stopped part way is run again with the same bytes; an object already there
with other bytes is refused, as is an `assets/` URL another release claimed with other bytes.

## 2. Activate it

```bash
spactl fragment activate $APP $FRAGMENT <release> --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
```

One conditional registry write that sets only that fragment's `release`. It refuses a fragment the
live entry lacks (add it with `spactl app add` first, `--fragment-release $FRAGMENT=<release>`), and
a release with no entry file ("no entry file: publish it first"). Every write checks every entry of
the document, so a fault elsewhere refuses it too, naming the entry. If another write landed first it
says the live registry changed and writes nothing: run it again. It prints `Applied revision N`, or
`Nothing to change`; then

```bash
spactl wait --url "$ORIGIN" --revision N
```

## 3. Roll it back

Rolling a fragment back is `spactl fragment activate` with the earlier release, as above; there is
no fragment rollback command. Never use `spactl registry rollback` for it: that returns the whole
document, every shell and every other fragment with it ([registry-rollback.md](registry-rollback.md)).
Every release stays in the bucket, under `apps/$APP/fragments/$FRAGMENT/releases/`.

## A page open across an activation

A document loaded before the activation keeps the chunks it already imported, and its next import
resolves under the new release: there is no guarantee that one document runs one release of a
fragment. A lazy chunk the new release lacks answers 404 with `Cache-Control: private, no-store`.
Reloading the document is the recovery: it then loads the new release's entry and chunks.
