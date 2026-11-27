# Onboard or remove an app

Make a new app servable in a running cell, with any identity provider, and take one out again.
`GLOSSARY.md` defines both: **Onboarding** ends when every instance reports the registry revision
that adds the app, and **Removal** is its inverse, registry first. Neither restarts the gateway or
creates a runtime resource: every step below is the identity provider's, the parameter store's, the
outputs document's, the bucket's or the registry's.

**Onboarding signs no one out.** Adding an entry leaves every other entry and every session as it
was. **Removal signs out every user of the removed app, and only that app**: its sessions go as the
revision that drops it lands.

Start with the operator session in [README.md](README.md), with `APP` set to the new app's id and
`APP_DEFINITION` to its definition file, including the platform configuration (`$CONFIG`, made with
`umask 077` in `$RUN`) and the tunnel.

## Preconditions

`spactl` checks none of these and performs none of them. Each is yours, in your estate:

- **The app sits inside the cell's bounds**: its issuer is one of the cell's `Issuers`, and each
  upstream it calls is in `Policy:AllowedUpstreams`. If it needs more, run
  [widening-the-bounds.md](widening-the-bounds.md) first, and start here once it is done.
- **The host lies inside `Policy:AllowedHostSuffixes`**, and the cell's DNS and its certificate
  cover it. A suffix the cell does not have is a change to the cell, not to this note
  ([widening-the-bounds.md](widening-the-bounds.md), "A new host suffix is not a widening").
- **The issuer meets the issuer section of the identity provider contract**,
  `contracts/identity-provider-contract.md` → "The issuer".

## The steps

### 1. Validate the team's draft

The app team hands over a draft definition ("What the app team supplies and gets back", below). From
the handoff the definition is yours: set its `host` and `oauth.issuer` to this cell's values, then
check it against the cell's bounds, offline, before any work at the provider:

```bash
spactl app validate "$APP_DEFINITION" --config "$CONFIG"
```

It reads no store and no outputs document, and it runs the check `app add` runs, so an app it
refuses here would be refused there, after the client and the secret existed. A refusal that says
the entry is outside the cell's bounds names the change that admits it: widening the bounds, or a
change to the cell.

### 2. Register the client at the provider

Register one confidential client for the app at the identity provider, and check it against the
contract's per-client section, `contracts/identity-provider-contract.md` → "Each client": its grants,
its callback `https://{host}/signin-oidc` on the host exactly as the definition has it, its logout
return under the issuer's profile, its token lifetimes, its scopes and, when the definition sets
`oauth.resource`, its audience. A provider that issues a refresh token only for `offline_access`
needs `offline_access` in the definition's `oauth.scopes`; if you add it, run step 1 again.

Keep the client id. The client secret goes straight to step 3, by file or pipe.

### 3. Put the secret in the store, at a known version

The value is the provider's client secret alone, as a `SecureString`. The gateway uses the value it
reads exactly as stored (`src/Platform.Registry/ParameterStoreClientSecretSource.cs`), so a trailing
newline or a label beside the secret is part of what it presents, and the provider refuses it.

It never passes through a terminal, an argument list or a log. Write it into an owner-only file, from
the provider's own export or a pipe, and let the AWS CLI read the file, which it does for any
parameter given as `file://<path>`
([AWS CLI: loading parameters from a file](https://docs.aws.amazon.com/cli/latest/userguide/cli-usage-parameters-file.html)):

```bash
PARAMETER="/spa-platform/$SPA_CELL/apps/$APP/client-secret"   # the recommended name; see below
SECRET_VERSION="$(aws ssm put-parameter --region "$AWS_REGION" --name "$PARAMETER" --type SecureString \
  --value "file://$RUN/secret" --query Version --output text)"
rm -f "$RUN/secret"
echo "parameter version $SECRET_VERSION"
```

Without `--overwrite`, `put-parameter` refuses a name that already exists, so a new parameter starts
at version 1. The registry pins the version (`ssm:{name}:{version}`, ADR-0003), and a rotation
raises it ([secret-rotation.md](secret-rotation.md)).

`/spa-platform/{cell}/apps/{appId}/client-secret` is a recommended convention, not a promise: any
name the gateway's task role may read serves. The reference cell module grants that role
`ssm:GetParameter` under `/spa-platform/{cell}/apps/` alone (`infra/modules/cell/iam.tf`), and with
no `kms:Decrypt`, so on a cell built from it a parameter named elsewhere, or encrypted under a
customer-managed key, cannot be read until your own estate grants the role that name or that key. An
unreadable parameter stalls the cell ("The stall", below).

### 4. Write the `clients` entry into the outputs document

`spactl app add` reads the app's client from the outputs document, `$SPA_OUTPUTS_FILE`, and from
nowhere else: it takes no client flags, so the next `app add` cannot silently undo a value passed by
hand. The entry is keyed by the app's id and names the client id, the parameter and its version,
never the secret (`contracts/infra-outputs.schema.json` → `clients`):

```json
{
  "clients": {
    "ledger": {
      "clientId": "0a1b2c3d4e5f",
      "secretParameter": "/spa-platform/prod-eu/apps/ledger/client-secret",
      "secretVersion": 1
    }
  }
}
```

Whatever registers the client at the provider and writes its secret writes this entry, as your
tooling writes the rest of the document. The scripts rewrite the whole document from the roots'
outputs after every apply (`write_outputs` in `tools/scripts/cell-common.sh`, through the field
map's `clients` field), so the entry must come from that tooling each time the document is written.
An entry typed into the file by hand is gone at the next apply, and `app add` then refuses the app,
saying the outputs name no client for it.

### 5. Grant the team's principal its release prefix

This is a requirement of your estate's IAM, which `spactl` neither checks nor grants. The team's
principal may write the new app's release namespace in the cell's bucket, and nothing else:
`apps/{appId}/releases/` and `apps/{appId}/claims/`. `spactl release publish` puts every key of a
release with `If-None-Match: *`, claims each `assets/` key under `claims/assets/` first, and reads
back a key or a claim it finds already there, so the grant is a put and a get on those two prefixes
(`contracts/s3-layout.md` → "Who reads and writes what"). It never covers `registry/`, another app's
`apps/` prefix, or this app's `fragments/`.

### 6. The first release

The team publishes the app's first release under that right. The registry write in step 7 refuses a
release whose `index.html` is not in the bucket, so this comes first. Where the team's right is not
yet in place, publish it once yourself, from the build the team handed over, `RELEASE` set to the
release's name and `RELEASE_DIRECTORY` to the directory of its built files:

```bash
spactl release publish "$APP" "$RELEASE_DIRECTORY" "$RELEASE" --outputs "$SPA_OUTPUTS_FILE"
```

Publishing serves nothing: a release is live only once the registry names it.

### 7. Add the entry, then wait

```bash
spactl app add "$APP_DEFINITION" --release "$RELEASE" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
spactl wait --url "$ORIGIN" --revision N
```

`app add` completes the definition from the outputs document's entry, with the client id and the
secret reference `ssm:{name}:{version}`, and writes the registry. It prints `Applied revision N` and
`Added <app> at release <release>`, and makes no Parameter Store call: whether the task role can read
the secret is learnt at the next poll. `--release` is required for an app the live registry does not
hold yet.

Onboarding ends at the revision every instance reports. On a cell of more than one task, wait for each
of them with `--instances` (README, "Waiting for the gateway: the tunnel"). Then hand the team what
it gets back.

## What the app team supplies and gets back

This section is written for the app team, and you can forward it as it stands. Its promises to the
team are the gateway's two contracts: `contracts/http-contract.md` for what the browser sees, and
`contracts/upstream-contract.md` for what the team's APIs receive.

**You supply** two things:

- **A draft app definition**, in the shape of `contracts/app-definition.schema.json`: its `appId`, its
  `oauth.scopes` (and `oauth.resource` if your APIs bind an audience), its `apis[]`, its `session`,
  its `csp` and any `fragments`. The operator sets `host` and `oauth.issuer` to the cell's values and
  owns the definition from the handoff; the copy the registry applies is kept where your organisation
  reviews registry changes. If the operator shares the cell's platform configuration with you, you
  can run the same offline check first: `spactl app validate <definition.json> --config
  <platform-config.json>`.
- **A build**: the directory of your built files, with `index.html` at its root
  (`contracts/s3-layout.md`).

**You get back** exactly these:

- **The host** your app is served on.
- **The client id, and the audience** when you set `oauth.resource`, for your API owners.
- **Your release prefix and the right to publish under it**: the bucket, its Region and
  `apps/{appId}/releases/`. You publish each release with `spactl release publish <appId>
  <directory> <release> --bucket <bucket> --region <region>`, and the operator makes a release live
  with a registry change.
- **The registry revision** that added your app.

**You never get** the client secret, the outputs document or any access to the registry.

**A `403` with `client_not_admitted` is expected** at first. Admission is each API owner's decision
(`contracts/upstream-contract.md`): until an owner admits your client id, their API refuses calls
that carry its token. Take the client id to each API owner; it is not a fault in the gateway.

An example definition as the operator applies it, for a provider under the `standard` profile. The
team's draft is the same without `host` and `oauth.issuer`:

```json
{
  "appId": "ledger",
  "host": "ledger.apps.example.com",
  "oauth": {
    "issuer": "corporate-sso",
    "scopes": ["openid", "profile", "offline_access", "ledger.read"],
    "resource": "https://ledger-api.example.com"
  },
  "apis": [
    { "name": "ledger", "upstream": "https://ledger-api.example.com", "timeoutSeconds": 30 }
  ],
  "csp": { "img-src": ["data:"] },
  "session": { "lifetimeMinutes": 480 }
}
```

`offline_access` is there because this example's provider issues a refresh token only for it; a
provider that does not need it leaves it out (`contracts/identity-provider-contract.md` → "Each
client").

## The stall

An app whose secret the gateway cannot read holds every later registry change on a live cell. A
running gateway rejects a whole registry document that names an unreadable secret, keeps its live
snapshot, and reads the document again at each poll (`src/Platform.Registry/RegistrySource.cs`,
`RefreshOutcome.SecretsUnreadable`; `docs/gaps.md`, GAP-30). Until it is freed, no later revision
lands: not another app's release activation, not another onboarding. `spactl wait` times out on
revision N, and the gateway logs that the revision is not applied because a client secret could not
be read; `tools/scripts/logs.sh --since 15m` shows it.

There are two recoveries, each followed by `spactl wait`:

- **Fix the parameter**, so the task role can read the name and version the entry names: write it if
  it is missing, or grant the role its name or its key. The next poll applies the held revision:

  ```bash
  spactl wait --url "$ORIGIN" --revision N
  ```

  A fix that changes the name or the version instead goes through step 4 and step 7 again, and the
  wait is for the revision that `app add` prints.

- **Remove the app** ("Removal", below), which writes a revision without the entry. Wait for the
  revision `app remove` prints:

  ```bash
  spactl app remove "$APP" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
  spactl wait --url "$ORIGIN" --revision M
  ```

At a cold start a gateway does not stall: it leaves out only the app whose secret it cannot read and
serves the rest, so that app's host answers 404 on the new task alone. If it can read no app's
secret, it applies nothing and stays not ready.

## Removal

Registry first, so that no live entry ever names a client or a parameter that is gone:

1. **Take the entry out, and wait for it.** `app remove` prints `Applied revision M`:

   ```bash
   spactl app remove "$APP" --outputs "$SPA_OUTPUTS_FILE" --config "$CONFIG"
   spactl wait --url "$ORIGIN" --revision M
   ```

   On a cell of more than one task, wait with `--instances`. Once every instance reports M, the app's
   host answers 404 and its sessions are gone.

2. **Then delete the rest**: the parameter, the app's `clients` entry from the source your tooling
   writes the outputs document from, and the client at the provider.

Your operator, or your tooling, does these deletions; `spactl` does none of them. A parameter deleted
too early, while the entry is still live, breaks nothing at once: a running gateway holds every secret
it has loaded and does not read it again. It breaks the instances that start later, at the next
deployment or scale-out, which leave the app out at their cold start and then hold every later
revision that still names it, as in "The stall".

Removal never narrows the bounds. An upstream or an issuer that no entry names any more stays in the
platform configuration until an operator narrows it on purpose
([widening-the-bounds.md](widening-the-bounds.md), "Narrowing the bounds"). The release prefix and its
claims stay in the bucket, which denies deletes under `apps/`, and the team's grant on them is yours
to revoke.

Then `rm -rf "$RUN"`.
