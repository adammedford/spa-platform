# Upstream trust and renewal: how the chiseled image and the proxy take a private CA, and how ACM reimports under a live listener

- **Ticket:** #1223, "Upstream trust and renewal: how the chiseled image and the proxy take a private CA,
  and how ACM reimports under a live listener" (`wayfinder:research`), on the map #1220 (P2-7 Corporate
  integration).
- **Date:** 2026-11-16. **Branch:** `research/upstream-trust-and-certificate-renewal`, pushed and never
  merged, as the map rules.
- **Method:** docs-only for AWS: no apply, no credential step, no AWS CLI call against the account, no
  read of the deployed cell; the `aws-mcp` connector failed to connect. Repository reading at commit
  `adf0273` (origin/main on 2026-11-16). Public Microsoft, Canonical-based image, Duende, YARP and AWS
  pages and source, each quoted with its URL; every page was read on 2026-11-16. **Executed locally with
  Docker 29.9.1 (arm64):** the gateway's pinned runtime image was pulled and listed, and a throwaway
  probe program was run on it with a read-only root filesystem in twelve trust configurations (the
  closing section lists them). The probe used a throwaway authority made with `openssl` in a scratch
  directory; `tools/scripts/certs.sh` was read and never run. Nothing was run on Fargate, so every
  statement about ECS behaviour is read, not executed.

## Answer in brief

1. **The gateway image trusts no private authority today, and that is now executed, not only read.** The
   pinned `aspnet:10.0-noble-chiseled-extra` image holds one trust file,
   `/etc/ssl/certs/ca-certificates.crt` (120 public roots), and nothing else under `/etc/ssl/certs`. A
   .NET 10 `HttpClient` in that image refuses a server certificate signed by a private authority with
   `PartialChain`. This agrees with the PingFederate finding.
2. **One environment variable and one directory are enough, and they add to the public roots instead of
   replacing them.** With `SSL_CERT_DIR` naming a directory that holds only the private authority's PEM,
   the root store went from 120 to 121 certificates, the private upstream was trusted, and a public
   site was still trusted, all with `--read-only`. `SSL_CERT_FILE` alone behaves the same way. Setting
   **both** variables to private-only locations, or mounting a volume **over** `/etc/ssl/certs`, drops
   the public roots (the root store fell to 1 and the public site failed with `UntrustedRoot`), which
   would break the gateway's calls to Cognito, DynamoDB, S3 and Parameter Store.
3. **The trust store is process-wide, so one change covers every client the gateway has.** YARP's
   per-cluster client, the OIDC handler's back channel and the token-management library all build a
   `SocketsHttpHandler` with default validation, which reads that store. The repository sets no trust
   option anywhere. YARP's cluster configuration has no setting for a custom authority (only
   `DangerousAcceptAnyServerCertificate`, which disables validation); a per-client custom trust store is
   code in two workstreams, and `CustomRootTrust` replaces the system roots for that handler (executed).
4. **Three ways to deliver the authority, all inside the cost ceiling:** bake it into the image
   (integrator's Dockerfile, $0, but the image then differs per authority); an init container that writes
   it to a shared task volume the gateway mounts read-only (workstream 1 alone, $0 with a standard
   Parameter Store parameter, $0.40 a month with a Secrets Manager secret); or per-client code that reads
   it from configuration (workstreams 3 and 4 plus a contract change, $0). ECS `secrets` and
   `environmentFiles` deliver environment variables only, never files, so a fetch at start needs a second
   container: the chiseled gateway has no shell.
5. **A reimport keeps the ARN and the listener association, and `certs.sh import` already does it.** ACM:
   reimporting "preserves the certificate's association and its Amazon Resource name (ARN)". Elastic Load
   Balancing: "Renewing or replacing a certificate does not affect in-flight requests ... After a
   certificate is renewed, new requests use the renewed certificate." How long the listener takes to
   present the new certificate is stated on no page read, and was not executed.
6. **Expiry signals exist for an imported certificate and cost at most $0.10 a month:** the `DaysToExpiry`
   metric (twice a day) and the daily "ACM Certificate Approaching Expiration" event, from 45 days before
   expiry. An imported certificate has no managed renewal, no "Expired" event, no "Renewal Action
   Required" event, no tags on reimport and no rollback to the previous import.
7. **Leaf renewal and authority rotation are different jobs.** `certs.sh issue` keeps the authority and
   makes a new RSA 2048 key and leaf, so a leaf renewal touches no trust store. `--new-ca` replaces the
   authority, and then every bundle delivered to a task must carry both authorities until the old leaf
   is gone.

## How a chiseled .NET 10 image takes an extra authority

### What the image holds (executed)

The runtime stage of `src/Gateway/Dockerfile` is
`mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:cae5b962bfb786606def03d250c6a645cc98839095c89e806e4bd8978a68115b`.
Listing its filesystem (`docker create`, `docker export`, `tar -t`) shows:

- `/etc/ssl/certs/ca-certificates.crt`, 180,744 bytes, 120 certificates, the only entry in
  `/etc/ssl/certs`. There are no hashed symbolic links and no `update-ca-certificates` (no shell either).
- `/usr/lib/ssl/cert.pem` is a symbolic link to `/etc/ssl/certs/ca-certificates.crt`, and
  `/usr/lib/ssl/certs` a symbolic link to `/etc/ssl/certs`. These are OpenSSL's default file and default
  directory, so both defaults resolve to the same bundle.
- The image runs as user 1654 (`APP_UID=1654`), .NET 10.0.15, `/tmp` present with mode 1777.

The Dockerfile has no step that adds an authority, and sets neither `SSL_CERT_FILE` nor `SSL_CERT_DIR`.

### What the runtime reads (read)

- Microsoft Learn, "Cross-platform cryptography"
  (https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography): "On Linux,
  the `LocalMachine\Root` store is an interpretation of the CA bundle in the default path for OpenSSL."
  And: "On Linux, .NET uses OpenSSL (libssl) to locate trusted root certificates. OpenSSL determines the
  certificate store location using environment variables (`SSL_CERT_FILE` and `SSL_CERT_DIR`) and
  distribution-specific default paths." The store cannot be written: the page's table marks "Open
  LocalMachine\Root (ReadWrite)" unsupported on Linux. The page also requires "PEM format with the
  `BEGIN CERTIFICATE` marker" and "appropriate read permissions".
- The runtime's source at tag `v10.0.0`,
  https://github.com/dotnet/runtime/blob/v10.0.0/src/native/libs/System.Security.Cryptography.Native/pal_x509_root.c:
  the directory is `getenv(X509_get_default_cert_dir_env())` and, when unset,
  `X509_get_default_cert_dir()`; the file is read the same way and independently. So each variable
  replaces only its own default, which is why setting one leaves the public bundle in place through the
  other.
- https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/X509Certificates/OpenSslCachedSystemStoreProvider.cs:
  the directory value is split on the path separator (`rootDirectory.Split(Path.PathSeparator, ...)`),
  so `SSL_CERT_DIR` may name several directories. The store is cached and reloaded: the code rechecks
  last-write times (`s_lastWriteRecheckInterval = TimeSpan.FromSeconds(5)`) and assumes the cache invalid
  after `s_assumeInvalidInterval = TimeSpan.FromMinutes(5)`. A changed file would therefore be picked up
  without a restart. That reload was read, not executed.

### What happens in each configuration (executed)

A probe built on the gateway's own pinned SDK and runtime images printed the size of
`LocalMachine\Root`, whether it held the probe authority and "Amazon Root CA 1", and then made two calls
with a default `HttpClient`: one to a TLS listener whose certificate the probe authority signed, one to
`https://www.microsoft.com/`. Every run used `docker run --read-only`, and every run confirmed a write
to the root filesystem was refused.

| Case | Root store | Private call | Public call |
| --- | --- | --- | --- |
| A. Nothing set (today's image) | 120, no probe authority | fails, `PartialChain` | ok |
| B. `SSL_CERT_FILE` = the authority alone | 121 | ok | ok |
| C. `SSL_CERT_DIR` = a mounted directory holding the authority alone | 121 | ok | ok |
| D. `SSL_CERT_DIR=/etc/ssl/certs:/probe-ca` | 121 | ok | ok |
| E. `SSL_CERT_FILE` = system bundle and authority concatenated | 121 | ok | ok |
| F. The authority bind-mounted as one extra file in `/etc/ssl/certs` | 121 | ok | ok |
| H. `SSL_CERT_FILE` = the authority alone, `SSL_CERT_DIR=/etc/ssl/certs` | 121 | ok | ok |
| I. **Both** variables name the authority alone | **1** | ok | **fails, `UntrustedRoot`** |
| J. A volume mounted **over** `/etc/ssl/certs` holding the authority alone | **1** | ok | **fails, `UntrustedRoot`** |
| K. The authority copied into `/etc/ssl/certs` as an image layer | 121 | ok | ok |
| L. An init container writes the authority to a shared volume; the probe mounts it read-only with `SSL_CERT_DIR` | 121 | ok | ok |
| G. No variable; the handler given `X509ChainTrustMode.CustomRootTrust` with the authority | 120 | ok | **fails, `UntrustedRoot`** |

What follows from the table:

- **A mounted file and a baked layer both work with a read-only root filesystem** (C, K, L). The trust
  store is only read; nothing needs a writable path. So the P2-9 spec ("P2-9 hardening: the image, its
  supply chain and the task's runtime", ticket 9, "A read-only root filesystem with ECS Exec off") and
  private trust do not conflict. `infra/modules/cell/ecs.tf` still sets `readonlyRootFilesystem = false`
  on origin/main, for the Session Manager route.
- **File names in the directory do not need OpenSSL's hashed form**: `probe-ca.pem` loaded as it was.
- **The safe shape is one variable.** `SSL_CERT_DIR=<mounted directory>` (C, L) or an extra file beside
  the bundle (F, K). The unsafe shapes are I and J: an ECS volume is a directory mount, so a volume
  mounted at `/etc/ssl/certs` is case J and removes the public roots.
- The file must be readable by user 1654; the probe's files were mode 644.

## Which HTTP clients need the trust, and what the repository sets today

The repository sets no trust option. A search of `src/` for `ServerCertificate`,
`RemoteCertificateValidationCallback`, `CertificateChainPolicy`, `DangerousAccept`, `SSL_CERT` and
`ConfigureHttpClient` finds nothing in the gateway's projects. `contracts/upstream-contract.md` says
nothing about TLS to an upstream; `contracts/registry.schema.json` admits `^https?://` upstream origins
and notes "Phase 2 uses https patterns only".

| Client | Where it is built | What it validates against today |
| --- | --- | --- |
| The proxy's per-cluster client | YARP's default factory. `src/Platform.Proxy/PlatformProxyExtensions.cs` calls `AddReverseProxy().AddTransforms(...)` and nothing else; `RegistryProxyConfig.cs` builds each `ClusterConfig` with `Destinations` and `HttpRequest` (the activity timeout) and no `HttpClient` member | the process's root store |
| The OIDC back channel: discovery, signing keys, code exchange | `src/Platform.Identity/IssuerDirectory.cs`, one per issuer: `new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })`, handed to the handler by `OidcOptionsSetup.cs` (`options.Backchannel = shared.Backchannel`) and to `HttpDocumentRetriever` | the process's root store |
| The token-management library: refresh and revocation | Duende.AccessTokenManagement.OpenIdConnect 4.2.0 takes the handler's back channel: `HttpClient = options.Backchannel` (https://github.com/DuendeSoftware/foss/blob/atm-4.2.0/access-token-management/src/AccessTokenManagement.OpenIdConnect/Internal/OpenIdConnectConfigurationService.cs), and its endpoint calls `oidc.HttpClient!.RequestRefreshTokenAsync` and `RevokeTokenAsync`. `SessionEnd.cs` revokes through the same `client.HttpClient` | the same client as the row above |
| The AWS SDK (DynamoDB sessions, S3, Parameter Store) and the OTLP exporter | their own clients | the process's root store; they need the public roots kept |

So the identity provider is reached through one client per issuer, and upstream APIs through one YARP
client per cluster. A process-wide trust change reaches both with no code.

**YARP's configuration cannot express a custom authority.** The settings of `HttpClientConfig` at 2.3.0
are `SslProtocols`, `DangerousAcceptAnyServerCertificate`, `MaxConnectionsPerServer`, `WebProxy`,
`EnableMultipleHttp2Connections`, `RequestHeaderEncoding` and `ResponseHeaderEncoding`
(https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Configuration/HttpClientConfig.cs). The one
that touches validation: "DangerousAcceptAnyServerCertificate - indicates whether the server's SSL
certificate validity is checked by the client. Setting it to `true` completely disables validation."
(https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/http-client-config). Anything
finer is code: "`ConfigureHttpClient` provides a callback to customize the `SocketsHttpHandler` settings
used for proxying requests. This will be called each time a cluster is added or changed." (same page).

**A per-handler custom trust store replaces the system roots.** `X509ChainTrustMode.CustomRootTrust`:
"When this value is used, the CustomTrustStore will be used instead of the default root trust."
(https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.x509certificates.x509chaintrustmode).
Case G executed it: the private call passed and the public call failed. A per-client design therefore
has to decide, per upstream and per issuer, whether that client trusts the private authority or the
public roots, or build a store holding both.

## How ECS delivers a bundle to a task

What the pages say (all read, none executed on Fargate):

- **`secrets` arrive as environment variables, not files.** "Secrets can be seamlessly injected into
  containers from AWS Secrets Manager and Amazon EC2 Systems Manager Parameter Store. These secrets can
  be referenced in your task as any of the following. They're referenced as environment variables that
  use the `secrets` container definition parameter. They're referenced as `secretOptions` if your logging
  platform requires authentication."
  (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/specifying-sensitive-data.html)
- **`environmentFiles` from S3 are also environment variables**: "You can create an environment variable
  file and store it in Amazon S3 to pass environment variables to your container."
  (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/use-environment-file.html)
- **A shared volume between containers exists on Fargate**: "Bind mounts are supported for tasks that are
  hosted on both Fargate and Amazon EC2 instances", among their uses "To provide an empty data volume to
  mount in one or more containers"
  (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/bind-mounts.html); tasks on platform
  version 1.4.0 or later "receive a minimum of 20 GiB of ephemeral storage"
  (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-task-storage.html). A mount point's
  `readOnly`: "If this value is true, the container has read-only access to the volume."
- **One container can wait for another to finish**: `dependsOn` with "SUCCESS - This condition is the
  same as COMPLETE, but it also requires that the container exits with a zero status. This condition
  can't be set on an essential container."
  (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html)
- **`readonlyRootFilesystem`**: "When this parameter is true, the container is given read-only access to
  its root file system. This parameter maps to ... the `--read-only` option to docker run." (same page).
  That is the flag the probe ran under. ECS Exec does not work with it: "making the root file system
  read-only using the `readonlyRootFilesystem` task definition parameter, or any other method, isn't
  supported." (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html)
- **A standard Parameter Store parameter holds 4 KB** ("Maximum value size: 4 KB" standard, "8 KB"
  advanced; standard "No additional charge", advanced "Charges apply":
  https://docs.aws.amazon.com/systems-manager/latest/userguide/parameter-store-advanced-parameters.html).
  An RSA 4096 authority certificate like the one `certs.sh` makes measured 1,814 bytes as PEM (executed,
  on a throwaway certificate with the same key size). One authority fits a standard parameter; two, for a
  rotation, are about 3.6 KB and fit narrowly; a corporate chain of three would not.

The authority's certificate is public material, not a secret: `certs.sh fetch-ca` already reads it back
from ACM as the imported certificate's chain. Secrets Manager is therefore not needed for
confidentiality; it would be chosen only to exercise the secret-store stand-in.

### The options

| | A. Baked into the image | B. Init container and a shared volume | C. Per-client code |
| --- | --- | --- | --- |
| Shape | A `COPY` of the PEM into `/etc/ssl/certs/` (case K), or into its own directory with `ENV SSL_CERT_DIR` | The PEM in a Parameter Store parameter (or a secret, or an S3 object). A small non-essential container receives it and writes it to a task volume, then exits; the gateway `dependsOn` it with `SUCCESS`, mounts the volume read-only and has `SSL_CERT_DIR` in its environment (case L) | A configuration key carries the PEM or a path. `Platform.Proxy` adds a `ConfigureHttpClient` callback; `Platform.Identity` builds its back channel with a chain policy |
| Who changes what | Integrator: `src/Gateway/Dockerfile`. Workstream 1: `deploy.sh` passes the PEM to the build | Workstream 1 alone: `infra/modules/cell/ecs.tf` (volume, init container, mount, one environment variable, the execution role's read of the parameter) and `certs.sh import` (writes the parameter). No image change | Workstreams 3 and 4, and the integrator for `contracts/platform-config.schema.json` |
| Covers | Every client in the process | Every client in the process; the mock API container can mount the same volume | Only the clients that are changed |
| Public roots | Kept | Kept, provided the volume is not mounted at `/etc/ssl/certs` | Dropped for a handler under `CustomRootTrust` unless the code adds them back |
| Changing the authority | Rebuild, push, deploy | Write the parameter, roll the service | Change configuration, roll the service |
| Costs to weigh | The image stops being one artefact for every cell: its digest depends on the authority, which sits badly with the Dockerfile's "two builds of one commit build on the same layers" (#781) and the digest that `deploy.sh` asserts. The authority is made on a workstation, not held in the repository | A second image to pin by digest and to pull at every task start; a shell image in the task (not in the gateway container); `secrets` delivers the PEM as an environment variable to the init container, and whether its line breaks survive that path was not executed on ECS. A variant fetches with the AWS CLI image and the task role instead | The most code, in two workstreams and a contract; the only option that can limit the private authority to named upstreams, where A and B let it vouch for any host the gateway calls |
| Read-only root | Works (case K) | Works (case L, on local Docker) | Works (case G) |

`environmentFiles`, a volume mounted over `/etc/ssl/certs`, and Amazon EFS are not options: the first
delivers no file, the second removes the public roots, and the third adds a file system and its charges
to carry a 2 KB file.

### Cost of each, for the project's Region

The Region: `tools/aws-project.env` in the main checkout sets `AWS_REGION=us-east-2`; the platform root's
`region` variable defaults to `us-east-1`. Prices below are for US East (Ohio) from the AWS Price List
file each price page draws its figures from, because the pages render the figures by script; the Secrets
Manager figures were checked equal for `us-east-1`.

| Item | Price | Per sitting-hour | Per idle month |
| --- | --- | --- | --- |
| Option A, the image layer | none | $0 | $0 |
| Option B with a standard Parameter Store parameter | "Standard parameters are available at no additional charge." (https://aws.amazon.com/systems-manager/pricing/) | $0 | $0 |
| Option B with a Secrets Manager secret | "$0.40 per Secret" a month and "$0.05 per 10000 API Requests" (https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AWSSecretsManager/current/us-east-2/index.json, published 2026-11-11; the page https://aws.amazon.com/secrets-manager/pricing/ says "the price is prorated (based on the number of hours.)") | about $0.0006 | $0.40 if left standing, $0 if the platform root's destroy removes it |
| Option B with an S3 object | "$0.023 per GB" a month and "$0.004 per 10,000 GET and all other requests" (https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonS3/current/us-east-2/index.json); a 2 KB object | under $0.0001 | under $0.0001 |
| Option B's init container | no charge of its own: Fargate "Pricing is based on requested vCPU, memory, Operating Systems, CPU Architecture, and storage resources for the Task" (https://aws.amazon.com/fargate/pricing/), and the task's size does not change | $0 | $0 |
| Option C | none | $0 | $0 |
| An alarm on `DaysToExpiry` | "$0.10 per alarm metric month (standard resolution) - US East (Ohio)" (https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonCloudWatch/current/us-east-2/index.json); the free tier covers "10 Alarm metrics" (https://aws.amazon.com/cloudwatch/pricing/) | about $0.0001 | $0.10, or $0 inside the free tier |
| An EventBridge rule on the expiry event | "AWS management events: Free" (https://aws.amazon.com/eventbridge/pricing/) | $0 | $0 |
| The imported certificate in ACM | no charge found: the price list for ACM in us-east-2 (published 2026-10-20) has entries for issued, exportable and ACME certificates and none for an import; the price page says only "By default, ACM issues certificates at no cost for use with services integrated with ACM." (https://aws.amazon.com/certificate-manager/pricing/) | $0 | $0 |

Every option is far inside the map's ceiling of $5 per four-hour sitting and $2 a month idle. The
largest figure is the Secrets Manager secret at $0.40 a month.

## ACM: reimporting under a live listener

### What `certs.sh` does today (read)

- `issue` does nothing while the leaf "is still valid in 30 days" (`-checkend 2592000`), chains to the
  authority and carries the wildcard; otherwise, or with `--force`, it makes a **new RSA 2048 key** and a
  new leaf valid for `SPA_LEAF_DAYS` (397 by default), signed by the **authority already on disk**
  ("keeping the authority that is already here, so trust on other workstations survives"). The authority
  is RSA 4096, valid 3,650 days, and is replaced only by `--new-ca`.
- `import` looks the certificate up by domain name and the `Cell` tag. When it finds one it calls
  `aws acm import-certificate --certificate-arn <existing> ...`, a reimport: "A reimport keeps the ARN,
  so the ALB listener needs no change." It refuses an expired leaf, checks that key and certificate
  match, and re-applies the tags in a separate call because a reimport takes none.
- `infra/modules/cell/alb.tf` reads the certificate with a data source (`types = ["IMPORTED"]`,
  `statuses = ["ISSUED"]`, `most_recent = true`, the `Cell` tag) and gives its ARN to the listener. An
  unchanged ARN is an unchanged plan.
- There is no renewal command, no expiry alarm and no event rule in the repository. Renewal today is
  `certs.sh issue --force` followed by `certs.sh import`, by hand.

### What ACM and the load balancer say (read)

- "To renew an imported certificate, you can obtain a new certificate from your certificate issuer and
  then manually reimport it into ACM. This action preserves the certificate's association and its Amazon
  Resource name (ARN)." (https://docs.aws.amazon.com/acm/latest/userguide/import-certificate.html)
- The conditions on a reimport
  (https://docs.aws.amazon.com/acm/latest/userguide/import-reimport.html): "You can add or remove domain
  names."; "If Key Usage extensions are present in the originally imported certificate, you can add new
  extension values, but you cannot remove existing values."; the same for Extended Key Usage; "The key
  type and size cannot be changed."; "You cannot apply resource tags when reimporting a certificate."
  `certs.sh` meets them: the same RSA 2048 key type, the same extensions file, the same wildcard name.
  A move to an elliptic-curve leaf could not be a reimport; it would be a new certificate and a new ARN.
- "Renewing or replacing a certificate does not affect in-flight requests that were received by the load
  balancer node and are pending routing to a healthy target. After a certificate is renewed, new requests
  use the renewed certificate. After a certificate is replaced, new requests use the new certificate."
  And: "If you imported a certificate into ACM, you must monitor the expiration date of the certificate
  and renew it before it expires."
  (https://docs.aws.amazon.com/elasticloadbalancing/latest/application/https-listener-certificates.html)

That is the documented basis for "no downtime": the association survives, requests in flight are not
affected, new requests get the new certificate. No page read states how long after the reimport the
listener presents the new certificate, or that no handshake fails in between. That observation needs a
sitting: a client holding requests open across `certs.sh import`, and the served certificate's serial
number before and after.

### Expiry signals (read)

- **The metric.** Namespace `AWS/CertificateManager`, metric `DaysToExpiry`, dimension `CertificateArn`:
  "Number of days until a certificate expires. ACM publishes this metric twice per day for every
  certificate until expiration, and stops publishing it after a certificate expires."
  (https://docs.aws.amazon.com/acm/latest/userguide/cloudwatch-metrics.html). Because publishing stops at
  expiry, an alarm on it should treat missing data as breaching.
- **The event.** "ACM sends daily expiration events for all active certificates (public, private and
  imported) starting 45 days prior to expiration for private/imported certificates and 30 days prior to
  expiration for public certificates. This timing can be changed using the PutAccountConfiguration action
  of the ACM API." Its detail carries `DaysToExpiry` and `CommonName`.
  (https://docs.aws.amazon.com/acm/latest/userguide/supported-events.html). The setting is
  `DaysBeforeExpiry`: "ACM sends one event per day per certificate until the certificate expires. By
  default, accounts receive events starting 45 days before certificate expiration."
  (https://docs.aws.amazon.com/acm/latest/APIReference/API_PutAccountConfiguration.html). It is an
  account-wide setting, not a per-certificate one.
- **The proof of a renewal.** The "ACM Certificate Available" event "is published on issuance, renewal,
  and import", and its `Action` field includes `"REIMPORT"` with `CertificateType` `"IMPORTED"` (same
  events page). That event, or `certs.sh status` before and after, is evidence a renewal happened.

### What an imported certificate cannot do that an issued one can (read)

- **No managed renewal**: "The certificates that you import work the same as those provided by ACM, with
  one important exception: ACM does not provide managed renewal for imported certificates." And: "You are
  responsible for monitoring the expiration date of your imported certificates and for renewing them
  before they expire." (https://docs.aws.amazon.com/acm/latest/userguide/import-certificate.html)
- **No "Expired" event**: "Certificate Expired events aren't available for imported certificates."
- **No "Renewal Action Required" event**: "Certificate Renewal Action Required events aren't available
  for imported certificates."
- **No revocation through ACM**: "Imported certificates cannot be revoked via revoke-certificate."
  (all three: https://docs.aws.amazon.com/acm/latest/userguide/supported-events.html)
- **No rollback**: "ACM does not support rollback to previously imported certificates." and no tags on
  the reimport call (https://docs.aws.amazon.com/acm/latest/userguide/import-reimport.html).

### Leaf renewal against authority rotation

A leaf renewal keeps the authority, so nothing that trusts the authority changes: the workstation
keychains, and whatever bundle the gateway task is given. Replacing the authority (`issue --new-ca`) is a
different event, and `certs.sh` already warns that "Every workstation that trusts the old one, and the
certificate in ACM, must be redone". For the task it means the delivered bundle must hold both
authorities from before the first certificate signed by the new one is served until the last signed by
the old one is gone. Under option B that is two certificates in one parameter (about 3.6 KB against the
4 KB limit) or two files in the directory.

## What the PingFederate research recorded, and whether this agrees

"PingFederate in the poc cell: a second service behind the internal ALB, the licence as a secret, sizing,
cost and teardown" (`docs/research/pingfederate-in-the-poc-cell.md` on
`origin/research/pingfederate-in-the-poc-cell`, dated 2026-11-04) says:

> **The gateway task trusts no local authority.** `src/Gateway/Dockerfile` has no CA bundle step (grep
> for `SSL_CERT`, `ca-certificates`, `trust`: nothing), and every `*.spa-poc.test` certificate chains to
> `certs.sh`'s local authority, trusted on the workstation alone. Any `https://idp.spa-poc.test...`
> authority fails the handler's TLS validation until the image trusts that authority (a `src/Gateway`
> change, the integrator's) or the task is given a certificate it already trusts

and, in its answer in brief, "Whichever shape, the gateway image must trust the local authority (an
integrator change)."

**This finding agrees on the fact and narrows the remedy.**

- The fact is confirmed and is now executed: the pinned image holds only the public bundle, and a private
  leaf is refused with `PartialChain` (case A). The Dockerfile on origin/main is unchanged in this
  respect.
- "A `src/Gateway` change, the integrator's" is one of three remedies, not the only one. The trust is the
  **task's**, not necessarily the image's: option B gives the unchanged image the authority through a
  mounted directory and one environment variable, all in workstream 1's `infra/modules/cell/ecs.tf`, with
  no integrator change. Only option A is the integrator's Dockerfile.
- The PingFederate finding's identity path needs nothing beyond what serves upstream APIs: the provider
  is reached through the one back channel in `IssuerDirectory.cs`, which reads the same process-wide
  store.

## What was executed, what was only read, what could not be verified

### Executed (2026-11-16, Docker 29.9.1, arm64, on this workstation)

- Pulled the gateway's pinned runtime image by digest, inspected its configuration and listed its
  filesystem: the trust paths, the 120-certificate bundle, user 1654, no other entry in `/etc/ssl/certs`.
- Built a probe program with the gateway's pinned SDK image
  (`mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:de4fe90a...`) on the pinned runtime image and ran it
  with `--read-only` in cases A to L of the table above (twelve runs), each printing the root store's
  size, the two TLS outcomes and the refused write.
- Case L used `busybox:1.36` as the init container and a named Docker volume, standing in for an ECS task
  volume.
- Measured an RSA 4096 authority certificate at 1,814 bytes of PEM, on a throwaway certificate.
- Read prices from the AWS Price List files for `us-east-2` (and Secrets Manager for `us-east-1`) with an
  unauthenticated download; this is a public file, not a call against the account.

### Read only (each read 2026-11-16)

- Repository at `adf0273`: `src/Gateway/Dockerfile`; `src/Platform.Proxy/PlatformProxyExtensions.cs` and
  `RegistryProxyConfig.cs`; `src/Platform.Identity/IssuerDirectory.cs`, `OidcOptionsSetup.cs`,
  `ProfileTokenEndpoints.cs`, `SessionEnd.cs`, `PlatformIdentityExtensions.cs`;
  `contracts/upstream-contract.md`, `contracts/registry.schema.json`; `tools/scripts/certs.sh`;
  `infra/modules/cell/alb.tf` and `ecs.tf`; `Directory.Packages.props` (YARP 2.3.0, Duende 4.2.0); the
  P2-9 spec's body; the PingFederate finding on its branch.
- Microsoft: the cross-platform cryptography page, the YARP HTTP client configuration page and the
  `X509ChainTrustMode` page on Learn; `dotnet/runtime` at `v10.0.0` (`pal_x509_root.c`,
  `OpenSslCachedSystemStoreProvider.cs`); `dotnet/yarp` at `v2.3.0` (`HttpClientConfig.cs`,
  `ForwarderHttpClientFactory.cs`); `dotnet/dotnet-docker`'s `documentation/ubuntu-chiseled.md` ("No
  package manager", "No shell", "Non-root user by default").
- Duende: `DuendeSoftware/foss` at tag `atm-4.2.0` (`OpenIdConnectConfigurationService.cs`,
  `OpenIdConnectUserTokenEndpoint.cs`).
- AWS documentation: the ACM pages on importing, reimporting, CloudWatch metrics, EventBridge events and
  `PutAccountConfiguration`; the Application Load Balancer page on listener certificates; the ECS pages
  on sensitive data, environment files, bind mounts, Fargate task storage, task definition parameters and
  ECS Exec; the Parameter Store page on standard and advanced parameters.
- AWS price pages: Secrets Manager, Systems Manager, Certificate Manager, CloudWatch, EventBridge, S3,
  Fargate. The figures the pages render by script were taken from the Price List files named in the cost
  table.

### Could not be verified

- **How quickly, and how cleanly, the listener presents a reimported certificate.** The pages give the
  preserved association and the unaffected in-flight requests; none gives a delay or promises no failed
  handshake. Not executed: no cell was read or changed.
- **Option B on Fargate.** The shared volume, its ownership and mode as seen by user 1654, `dependsOn`
  with `SUCCESS`, and a PEM's line breaks surviving `secrets` into an environment variable were read or
  run on local Docker only.
- **Whether the cell's network lets a task pull a second image** at start, and what that pull adds to
  task start time. The cell was not read.
- **The reload of the root store without a restart**, read in the runtime's source and not executed.
- **That ACM never returns an imported private key.** `certs.sh` says so in a comment; no page read here
  states it.
- **That importing a certificate is free.** No charge appears on the price page or in the price list; no
  sentence says so outright.
- **Which Region is the project's.** `tools/aws-project.env` (untracked, in the main checkout) says
  `us-east-2`; the platform root's variable defaults to `us-east-1`. The prices quoted are the same in
  both where both were checked.
