# Which local stand-in feeds the gateway container S3 and Parameter Store

- **Ticket:** "Which local stand-in feeds the gateway container S3 and Parameter Store" (`wayfinder:research`),
  on the map "Wayfinder: gateway startup benchmark, floor and budget".
- **Date:** 2026-10-19. **Branch:** `research/local-aws-stand-in`, pushed and never merged, as the map rules.
- **Method:** primary sources only: the AWS SDK for .NET v4 developer guide and the `aws-sdk-net` source
  (pinned to commit `057579f`), the AWS SDKs and Tools Reference Guide, the ECS developer guide, and the
  stand-ins' own docs and source (`moto` at `dd8bf40`, `localstack` at its archived HEAD `8b9a79f`). The
  session had no Docker daemon, so nothing below was executed: every command and the path-style
  mechanism are read out of source and docs. Section 7 names what the first run must confirm.

## Answer in brief

1. **Yes.** The SDK for .NET v4 reads `AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL_SSM` and `AWS_ENDPOINT_URL`
   on a parameterless client: the `ClientConfig.ServiceURL` getter looks them up when no URL was set in
   code. It still needs `AWS_REGION` (not `AWS_DEFAULT_REGION`: the .NET region chain reads only
   `AWS_REGION`) and a credential pair, `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY`, with any values.
2. **Path style has no environment switch in the SDK.** `AmazonS3Config.ForcePathStyle` is code-only, and
   with a hostname endpoint the S3 endpoint rules put the bucket in the host:
   `http://<bucket>.<host>:<port>/...`, which no compose network resolves. A dotted bucket name does
   not escape it over `http`. An **IP-literal endpoint** does: the rules' `isIp` branch produces path
   style. So the stand-in gets a fixed address on a compose network and the endpoint URL names it.
3. **Recommended stand-in: moto in server mode** (`motoserver/moto`, Apache-2.0, amd64 and arm64, one
   process on one port for S3 and SSM). Its S3 answers `If-None-Match` with 304 and conditional PUTs
   with 412, keeps versions, and its SSM resolves `name:version` and decrypts a SecureString, which is
   exactly the surface `S3ObjectStore` and `ParameterStoreClientSecretSource` use. **LocalStack** no
   longer ships a token-free image: from 23 March 2026 it is a single image that needs an auth token,
   its free plan is for non-commercial use, and the last token-free release is `4.14.0` from an archived
   repository. **MinIO** has no Parameter Store and its community repository is unmaintained.
4. Seeding is a bucket with versioning, one SecureString parameter, a release prefix and the registry
   object. `spactl` does the last two against the same endpoint, because it builds its S3 client from the
   same default chain. `/readyz` answering `{"revision":42}` is the done signal.
5. The stand-in is unrepresentative of Fargate in ways that move a cold-start clock: static keys instead
   of the container credential provider's fetch from `169.254.170.2`, plaintext `http` to one local
   process instead of TLS and DNS to two regional endpoints, path style instead of virtual host, no IAM
   or bucket-policy evaluation, no KMS behind the SecureString, in-memory state.

## 1. What a parameterless client resolves from the environment

The gateway registers `new AmazonSimpleSystemsManagementClient()` and `new AmazonS3Client()`
(`src/Platform.Registry/PlatformRegistryExtensions.cs`), built on first use. Three things resolve from
the environment: the endpoint, the region and the credentials.

### Endpoint

`ClientConfig.ServiceURL`'s getter runs a lookup once, guarded by
`if (!didProcessServiceURL && this.serviceURL == null && IgnoreConfiguredEndpointUrls == false && ServiceId != null)`,
and in this order: the service-specific environment variable, then
`EnvironmentVariables.GLOBAL_ENDPOINT_ENVIRONMENT_VARIABLE`, then the shared `config` profile's
`services` section, then the profile's own `endpoint_url`. Each value passes through
`NormalizeServiceURL`, which appends `/` to a host-only URL.
([ClientConfig.cs][sdk-clientconfig])

The variable names are built by `TransformServiceId.TransformServiceIdToEnvVariable(ServiceId)`:
`serviceId.ToUpper()`, spaces to underscores, prefixed with
`SERVICE_SPECIFIC_ENDPOINT_ENVIRONMENT_VARIABLE_PREFIX = "AWS_ENDPOINT_URL_"`; the global one is
`GLOBAL_ENDPOINT_ENVIRONMENT_VARIABLE = "AWS_ENDPOINT_URL"`.
([TransformServiceId.cs][sdk-transform], [EnvironmentVariables.cs][sdk-envvars])
`AmazonSimpleSystemsManagementConfig` sets `ServiceId = "SSM"`
([AmazonSimpleSystemsManagementConfig.cs][sdk-ssmconfig]), and the reference guide's identifier table
lists `S3 -> AWS_ENDPOINT_URL_S3` and `SSM -> AWS_ENDPOINT_URL_SSM` ([identifiers table][sdkref-ids]).

Precedence, from the reference guide: code, then `AWS_ENDPOINT_URL_<SERVICE>`, then `AWS_ENDPOINT_URL`,
then the config file's service section, then the profile's `endpoint_url`, then the service default.
`AWS_IGNORE_CONFIGURED_ENDPOINT_URLS=true` switches the environment and file lookups off. The support
table marks "SDK for .NET 4.x: Yes". ([Service-specific endpoints][sdkref-ss])

The S3 resolver hands the result to the endpoint rules as the custom endpoint: `result.Endpoint =
config.ServiceURL;`, beside `result.Region`, `result.ForcePathStyle = config.ForcePathStyle;` and
`result.Bucket = request.BucketName;`. ([AmazonS3EndpointResolver.cs][sdk-s3resolver])

### Region

`ClientConfig.RegionEndpoint`'s getter calls `GetDefaultRegionEndpoint()`, which is
`FallbackRegionFactory.GetRegionEndpoint()` ([_netstandard/ClientConfig.cs][sdk-clientconfig-ns]). The
factory tries, in order, `AppConfigAWSRegion`, `EnvironmentVariableAWSRegion`,
`ProfileAWSRegion` and, last, `InstanceProfileAWSRegion`, which reads EC2 instance metadata.
`EnvironmentVariableAWSRegion` declares both `"AWS_REGION"` and `"AWS_DEFAULT_REGION"` but its
constructor reads only `AWS_REGION`, failing with "The environment variable {0} was not set with AWS
region data." ([AWSRegion.cs][sdk-awsregion]) The reference guide's region page lists only `AWS_REGION`
for the SDK for .NET; the `AWS_DEFAULT_REGION` notes are for the CLI and Boto3 ([AWS Region][sdkref-region]).

So `AWS_REGION` is required even with an endpoint URL set. The S3 resolver derives a region from
`ServiceURL` only when `RegionEndpoint` is null ("The region needs to be determined from the ServiceURL
if not set"), and an IP literal carries none; the request must still be SigV4-signed for a region. With
neither, `ClientConfig.Validate()` throws `AmazonClientException("No RegionEndpoint or ServiceURL configured")`,
which is the constructor failure the registration comment in `PlatformRegistryExtensions.cs` describes.
Without `AWS_REGION` the chain also reaches the instance-metadata step, which is a timeout on a
workstation.

### Credentials

The developer guide's search order: credentials set on the client; `SessionAWSCredentials` from
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` and `AWS_SESSION_TOKEN` when all three are set;
`BasicAWSCredentials` from the first two; web identity; the profile named by `AWSConfigs.AWSProfileName`;
the profile named by `AWS_PROFILE`; the `[default]` profile; the container credential provider; EC2
instance metadata. ([Credential and profile resolution][sdk-creds]) Two variables with any value stop the
chain at step three, before any profile file or network step.

### What Fargate supplies instead

ECS sets, among others, `AWS_CONTAINER_CREDENTIALS_RELATIVE_URI` ("the full HTTP URL endpoint for the SDK
to use when making a request for credentials"), `AWS_DEFAULT_REGION` and `AWS_REGION` ("the Region where
the task runs. If defined, this value overrides the `AWS_DEFAULT_REGION`"), and `AWS_EXECUTION_ENV`
(`AWS_ECS_FARGATE`). ([Amazon ECS environment variables][ecs-env]) The relative URI is appended to
`169.254.170.2`, and the provider is supported by the SDK for .NET 4.x ([Container credential
provider][sdkref-container]). The cell module sets none of these itself; it sets the platform settings
and `ASPNETCORE_HTTP_PORTS` (`infra/modules/cell/ecs.tf`, `platform_configuration` and
`gateway_runtime_environment`).

## 2. Path style versus virtual host

`AmazonS3Config.ForcePathStyle` is "When true, requests will always use path style addressing.", a
plain property initialised to `false`; nothing in the config reads it from an environment variable or
the shared config file, unlike `UseArnRegion`, `USEast1RegionalEndpointValue` and
`DisableMultiregionAccessPoints`, which do have variables. ([AmazonS3Config.cs][sdk-s3config]) The
gateway sets nothing on the config, so `ForcePathStyle` is false.

The generated endpoint rules decide the URL for a custom endpoint with a bucket:

- `if (Equals(refs["ForcePathStyle"], false) && IsVirtualHostableS3Bucket((string)refs["Bucket"], false))`:
  when the endpoint host is not an IP (`Equals(GetAttr(refs["url"], "isIp"), false)`) the template is
  `{url#scheme}://{Bucket}.{url#authority}{url#path}`; when it is an IP
  (`Equals(GetAttr(refs["url"], "isIp"), true)`) the template is
  `{url#scheme}://{url#authority}{url#normalizedPath}{Bucket}`, which is path style.
- A later rule for `Equals(GetAttr(refs["url"], "scheme"), "http") && IsVirtualHostableS3Bucket((string)refs["Bucket"], true) && Equals(refs["ForcePathStyle"], false)`
  also produces `{url#scheme}://{Bucket}.{url#authority}{url#path}`, and does not test `isIp`.
- The path-style rules that follow produce `{url#scheme}://{url#authority}{url#normalizedPath}{uri_encoded_bucket}`.

([AmazonS3EndpointProvider.cs][sdk-s3provider])

`IsVirtualHostableS3Bucket(hostLabel, allowSubDomains)` returns false for an IPv4 literal, treats the
whole name as one label when `allowSubDomains` is false (so a period fails it) and splits on periods
when true; each label must be 3 to 63 characters, lowercase, start and end alphanumeric. `ParseURL` sets
`isIp` from `Uri.CheckHostName(uri.Host)` being `IPv4` or `IPv6`. ([Fn.cs][sdk-fn])

Consequences for the fixture bucket `spa-platform-poc-111122223333-us-east-1-an` (no periods, so
virtual-hostable):

- `AWS_ENDPOINT_URL_S3=http://moto:5000` gives `http://spa-platform-poc-111122223333-us-east-1-an.moto:5000/registry/registry.json`.
  Compose resolves the service name `moto`, not a subdomain of it.
- A bucket name with a period does not help over `http`: the `http`-scheme rule allows subdomains, so
  `spa-platform.local` would still be hosted as `http://spa-platform.local.moto:5000/...`.
- `AWS_ENDPOINT_URL_S3=http://10.90.0.10:5000` gives `http://10.90.0.10:5000/spa-platform-poc-111122223333-us-east-1-an/registry/registry.json`.
- The IP route needs a bucket name without periods. The `http`-scheme rule above does not test `isIp`,
  so a dotted name that fails the first rule's `allowSubDomains: false` check falls into it and comes out
  as `http://spa-platform.local.10.90.0.10:5000/...`. The fixture name is safe; a local name must stay so.

So the stand-in gets a fixed address on a user-defined compose network and the endpoint variables name
that address. LocalStack's own networking page uses the same device for a different reason, giving the
container `ipv4_address: 10.0.2.20` on a `10.0.2.0/24` network ([Accessing LocalStack from another
container][ls-net]). The alternative, a compose network alias per bucket
(`<bucket>.moto`), was not read through to moto's `bucket_name_from_url` and is not recommended here.

The SSM client has no bucket and no such rule: `AWS_ENDPOINT_URL_SSM=http://10.90.0.10:5000` is simply
where its JSON requests go.

## 3. The candidates

### What the gateway needs of them

- S3 `GetObject` with `If-None-Match` (`EtagToNotMatch`), where the SDK surfaces 304 as an
  `AmazonS3Exception` with `StatusCode == NotModified`, and `NoSuchKey` for a missing key
  (`src/Platform.Registry/S3ObjectStore.cs`). The registry source sends the settled ETag on every poll
  (`RegistrySource.cs`).
- SSM `GetParameter` with `Name = "{name}:{version}"` and `WithDecryption = true`, and the response's
  `Parameter.Version` must equal the requested version or the reference is refused
  (`src/Platform.Registry/ParameterStoreClientSecretSource.cs`).
- For seeding through `spactl`: S3 `PutObject` with `If-None-Match: *` for release keys and `If-Match`
  for the registry replace, with 412 read as a refusal (`S3ObjectStore.PutAsync`), and `ListObjectVersions`
  for rollback (`tools/spactl/S3RegistryHistory.cs`). `contracts/s3-layout.md` makes the registry object's
  versioning part of the contract.

### moto in server mode (recommended)

- **Run:** `pip install moto[server]` and `moto_server`, or `docker run --rm -p 5000:5000 --name moto motoserver/moto:latest`;
  "By default, this server will start on port 5000". Newer SDKs can be pointed at it with
  `AWS_ENDPOINT_URL`. The page also warns "127.0.0.1 is not an adequate replacement, and may not work";
  section 7 says how that is checked. ([moto server mode][moto-server]) The port is `MOTO_PORT`,
  default `"5000"` ([settings.py][moto-settings]).
- **Image and licence:** `motoserver/moto:5.2.4` (2026-10-11), `amd64` and `arm64`
  ([Docker Hub tags][moto-hub]); release `5.2.4` of 2026-10-11; licence Apache-2.0 ([GitHub][moto-gh]).
- **Dispatch:** `DomainDispatcherApplication.get_backend_for_host` matches the host against each
  service's URL patterns; when none matches, `infer_service_region_host` reads the SigV4 `Authorization`
  header's credential scope, `_, _, region, service, _ = credential_scope.split("/")`, and falls back to
  `DEFAULT_SERVICE_REGION = ("s3", "us-east-1")`. ([werkzeug_app.py][moto-werkzeug]) Every SDK request
  is signed, so an IP host dispatches to S3 or SSM by the signature's service.
- **S3 addressing:** `subdomain_based_buckets` returns path-based for hosts that start with `localhost`,
  `localstack` or `host.docker.internal`, contain no dot, are an IPv4 or bracketed IPv6 literal, or appear
  in `MOTO_S3_CUSTOM_ENDPOINTS` (comma-separated); `S3_IGNORE_SUBDOMAIN_BUCKETNAME=1` forces path-based
  for every host. ([responses.py][moto-s3resp], [settings.py][moto-settings], [S3 docs][moto-s3docs])
  An IP endpoint is therefore path style on both sides.
- **S3 conditional GET:** in `_get_key`, `if if_none_match and key.etag in [if_none_match, f'"{if_none_match}"']: not_modified = True`,
  and `get_object` then does `return 304, response_headers, "Not Modified"`; `If-Match` mismatch raises
  `PreconditionFailed("If-Match")`. The ETag is the quoted MD5 of the value
  (`return f'"{self._etag}"'`). ([responses.py][moto-s3resp], [models.py][moto-s3models])
- **S3 conditional PUT:** `if if_none_match == "*"` and the object exists, `raise PreconditionFailed("If-None-Match")`;
  `elif if_match.replace('"', "") != obj.etag.replace('"', ""): raise PreconditionFailed("If-Match")`.
  `ConditionalRequestConflict` was not seen in the parts read. ([responses.py][moto-s3resp])
- **S3 versioning:** `version_id=str(random.uuid4()) if bucket.is_versioned else "null"`, versions
  appended on a versioned bucket. ([models.py][moto-s3models])
- **SSM `name:version`:** `get_parameter` strips an ARN prefix, splits the name on `:` (more than two
  parts returns None), and for a numeric suffix filters the stored versions, raising
  `ParameterVersionNotFound` ("Systems Manager could not find version {version_or_label} of
  {name_prefix}. ") when none matches; a non-numeric suffix is a label. `put_parameter` sets
  `version = 1` on the first put and `previous_parameter.version + 1` after. ([ssm/models.py][moto-ssm])
- **SecureString:** a SecureString with no `KeyId` gets `self.keyid = "alias/aws/ssm"` and
  `self.value = self.encrypt(value)`, where `encrypt` is `f"kms:{self.keyid}:" + value`; `decrypt`
  strips that prefix. `response_object` returns `"Value": self.decrypt(self.value) if decrypt else self.value`
  and `"Version": self.version`, and the response layer passes the request's `WithDecryption`:
  `response = {"Parameter": result.response_object(with_decryption, self.region)}`. No KMS is involved.
  ([ssm/models.py][moto-ssm], [ssm/responses.py][moto-ssmresp])
- **State:** in memory; an internal endpoint resets it ([moto server mode][moto-server]). Seed after
  each `up` of the stand-in; gateway restarts do not touch it.

### LocalStack

- **Licence and image, as of this research:** "Starting March 23, 2026, LocalStack for AWS will ship as a
  single, unified container image." and "Accessing the latest releases will require authenticated
  access using a LocalStack for AWS account and an auth token." A free plan continues for cases including
  "You're using LocalStack for non-commercial purposes." ([Moving to a single image][ls-blog]) The docs
  call the token "a mandatory credential required to start the LocalStack container and activate
  licensed features" ([Auth token][ls-token]); the plans page lists Hobby "for non-commercial use" and
  Base, Ultimate and Enterprise "for commercial use" ([Licensing][ls-licensing]). Calendar versioning
  (`YYYY.MM.patch`) starts with the end-of-March 2026 release; "Releases up to and including `v4.14.0`
  use semantic versioning" ([Docker images][ls-images]). The `localstack/localstack` GitHub repository is
  archived (`archived: true`, last push 2026-03-23); its README says "this repository is now archived and
  read-only" and points at "a free Hobby plan for non-commercial use" ([README][ls-readme]). The last
  token-free release is `v4.14.0` (2026-02-26), still on Docker Hub as `localstack/localstack:4.14.0`,
  `amd64` and `arm64` ([Docker Hub][ls-hub-4140]), and unmaintained.
- **Capabilities, for the record:** S3 `GetObject` preconditions are checked by
  `validate_failed_precondition`: `if ((if_none_match := request.get("IfNoneMatch")) and etag == if_none_match.strip('"'))`
  raises `CommonServiceException(message="Not Modified", code="NotModified", status_code=304)`;
  `If-Match` mismatch raises `PreconditionFailed` with `Condition="If-Match"`
  ([s3/utils.py][ls-s3utils]). `put_object` enforces `If-None-Match` and `If-Match`, raising
  `PreconditionFailed` with `Condition="If-None-Match"` or via `verify_object_equality_precondition_write`
  ([s3/provider.py][ls-s3prov]). Versioning is implemented (`generate_version_id`, `list_object_versions`).
  Virtual-hosted style is "the default and recommended approach" with the `s3.` prefix on the host; path
  style is for cases such as Docker Compose, where "Wildcard subdomains like `<bucket-name>.localstack`
  are not resolvable"; "Signature validation is disabled by default, so that S3 accepts requests signed
  with any credentials." ([S3][ls-s3]) SSM lists `GetParameter`, `PutParameter`, `LabelParameterVersion`
  and the other parameter operations among "49 of 152 operations implemented" ([SSM][ls-ssm]); the
  provider delegates `get_parameter` to moto through `call_moto_with_request` with the normalised name and
  `WithDecryption` ([ssm/provider.py][ls-ssmprov]), so the version-selector and SecureString behaviour is
  moto's, above. Any access key works; `test` maps to account `000000000000` ([Credentials][ls-creds]).
  Init hooks in `/etc/localstack/init/ready.d` seed on start, and
  `curl -s localhost:4566/_localstack/init/ready | jq .completed` says when they are done
  ([Initialization hooks][ls-init]). The image is multi-arch since 0.13 ([ARM64][ls-arm]).
- **Verdict:** functionally sufficient, but either a licence for commercial use or a frozen, unmaintained
  4.14.0. This repository is an enterprise platform, so the token-free route is not available to it.
  Nothing in the rest of this file changes for a LocalStack user: the same IP-endpoint wiring applies on
  port 4566.

### MinIO

MinIO AIStor's compatibility page lists `If-None-Match` for `GetObject`, `HeadObject` and `PutObject`
([S3 API compatibility][minio-compat]), so the S3 half would hold. It has no Parameter Store, so the SSM
half would be moto anyway, which makes a second process pointless. The community repository's README
says "THIS REPOSITORY IS NO LONGER MAINTAINED." and "We will no longer provide pre-compiled binary
releases for the community version."; the licence is "GNU AGPLv3" ([README][minio-readme]). Not a
candidate.

## 4. The exact container environment

A compose file with the stand-in on a fixed address. Addresses and names are illustrative; the rules
are: the endpoint host is an IP literal (section 2), `AWS_REGION` is set, both credential variables are
set, and the platform settings are the ones `infra/modules/cell/ecs.tf` builds, with the fixture
values from `contracts/platform-config.example.json`.

```yaml
networks:
  stand-in:
    ipam:
      config:
        - subnet: 10.90.0.0/24

services:
  moto:
    image: motoserver/moto:5.2.4
    ports:
      - "127.0.0.1:5000:5000"       # for seeding from the host
    networks:
      stand-in:
        ipv4_address: 10.90.0.10

  gateway:
    image: gateway                  # docker build -f src/Gateway/Dockerfile -t gateway .
    cpus: 0.25                      # the map's constraint; --cpus 0.25 --memory 1g on docker run
    mem_limit: 1g
    ports:
      - "127.0.0.1:8080:8080"
    networks:
      - stand-in
    environment:
      # The SDK default chain (section 1)
      AWS_ENDPOINT_URL: http://10.90.0.10:5000      # or AWS_ENDPOINT_URL_S3 and AWS_ENDPOINT_URL_SSM, same value
      AWS_REGION: us-east-1
      AWS_ACCESS_KEY_ID: test
      AWS_SECRET_ACCESS_KEY: test
      # The task environment ecs.tf builds, with the example's values
      Platform__Cell: poc
      Registry__Bucket: spa-platform-poc-111122223333-us-east-1-an
      Registry__Key: registry/registry.json
      Registry__PollSeconds: "15"
      Assets__Bucket: spa-platform-poc-111122223333-us-east-1-an
      Policy__AllowedHostSuffixes__0: .spa-poc.test:8443
      Policy__AllowedUpstreams__0: http://127.0.0.1:5080
      Policy__AllowedUpstreams__1: http://127.0.0.1:5081
      ForwardedHeaders__TrustedNetworks__0: 10.20.0.0/16
      Issuers__cognito-poc__Authority: https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE
      Issuers__cognito-poc__Profile: cognito
      Issuers__cognito-poc__BrowserOrigin: https://spa-platform-poc-111122223333.auth.us-east-1.amazoncognito.com
      Session__Store: memory
      Session__MaxSessions: "10000"
      # ASPNETCORE_HTTP_PORTS=8080 is already set by src/Gateway/Dockerfile
```

Notes on the list:

- Every platform section is `required` by `contracts/platform-config.schema.json`, and
  `PlatformPolicyConfiguration.Read` and the `Registry` options binder read them at start, so the
  `Issuers`, `Policy`, `ForwardedHeaders` and `Session` settings are needed even though no ALB and no
  provider exist locally. The issuer authority is unreachable from a workstation; the map already settles
  that the first-app-host clock stops at the 401 locally.
- The registry entry names issuer `cognito-poc`, host `app1.spa-poc.test:8443` and upstream
  `http://127.0.0.1:5080`, and the validator checks each against `Issuers`, `AllowedHostSuffixes` and
  `AllowedUpstreams` (`RegistryValidator`). The values above are the example's, so
  `contracts/registry.example.json` validates against them unchanged.
- The bucket name is free as long as it matches the schema's `bucketName` pattern and has no period
  (section 2); the example's is kept so `contracts/platform-config.example.json` serves as
  `spactl --config` as it is. (`spactl` reads only
  `Policy` and `Issuers` from that file; the bucket comes from `--bucket`.)
- Hyphens in `Issuers__cognito-poc__*` are what the cell module sends ECS too; compose's `environment`
  map passes them.
- `AWS_ENDPOINT_URL` alone covers both clients. Set the two service-specific variables instead if the
  S3 and SSM stand-ins are ever different processes; they override the global one.

## 5. Seeding

From the host, with the AWS CLI v2 and `spactl`. Both are pointed at the published port; `127.0.0.1` is
an IP literal, so path style holds there too. The CLI reads `--endpoint-url`; `spactl` builds
`new AmazonS3Client()` when `--bucket` is given without `--region` (`tools/spactl/SpactlEnvironment.cs`),
so it reads the same four variables as the container.

```bash
export AWS_ACCESS_KEY_ID=test AWS_SECRET_ACCESS_KEY=test AWS_REGION=us-east-1
export AWS_ENDPOINT_URL=http://127.0.0.1:5000
BUCKET=spa-platform-poc-111122223333-us-east-1-an

# 1. The bucket, versioned: contracts/s3-layout.md makes the registry object's versioning part of the
#    contract, and spactl registry rollback reads it.
aws s3api create-bucket --bucket "$BUCKET"
aws s3api put-bucket-versioning --bucket "$BUCKET" --versioning-configuration Status=Enabled

# 2. The client secret the example entry references: ssm:/spa-platform/poc/apps/app1/client-secret:1.
#    A first put is version 1 (moto: version = 1). Any value; nothing checks it locally.
aws ssm put-parameter --name /spa-platform/poc/apps/app1/client-secret \
  --type SecureString --value local-dev-secret
aws ssm get-parameter --name /spa-platform/poc/apps/app1/client-secret:1 --with-decryption \
  --query Parameter.Version        # expect 1

# 3. The release prefix. spactl writes every object once (If-None-Match: *) and index.html last.
dotnet run --project tools/spactl -- release publish app1 fixtures/spa/app1 2026-10-10.1 --bucket "$BUCKET"

# 4. The registry object. registry.example.json is a complete one-app document at revision 42 whose
#    release is the prefix just published; apply checks that prefix's index.html exists and writes
#    with If-None-Match: * because there is no live object yet.
cp contracts/registry.example.json /tmp/registry.json
dotnet run --project tools/spactl -- registry apply /tmp/registry.json \
  --config contracts/platform-config.example.json --bucket "$BUCKET"

# 5. Start the gateway, then confirm the snapshot is live. The poller runs once at start-up
#    (RegistryPoller), so this is immediate, not one PollSeconds later.
docker compose up -d gateway
curl -s http://127.0.0.1:8080/readyz          # {"revision":42}; 503 until the snapshot is live
dotnet run --project tools/spactl -- wait --url http://127.0.0.1:8080 --revision 42
```

Without `spactl`, steps 3 and 4 are `aws s3 cp --recursive fixtures/spa/app1 "s3://$BUCKET/apps/app1/releases/2026-10-10.1/"`
and `aws s3 cp /tmp/registry.json "s3://$BUCKET/registry/registry.json"`; the gateway checks neither
the prefix nor the write condition.

What `/readyz` needs, read from the code: `Health.ReadyAsync` answers 503 while
`snapshot.Revision < 1` and 200 with `{"revision":N}` after (`src/Platform.Hosting/Health.cs`). The
snapshot goes live when the document validates against the policy and every referenced secret reads
(`RegistrySource.RefreshOnceAsync`); at a cold start an app whose secret cannot be read is left out, and
with no app readable nothing is served. So a wrong parameter name or version keeps `/readyz` at 503 with
a warning naming the reference and the SDK error code (`ParameterNotFound`, `ParameterVersionNotFound`).

For the benchmark's N=10 cold starts: seed once, then restart only the gateway container; moto's state
survives gateway restarts and is lost only when the moto container stops.

## 6. What makes the stand-in unrepresentative of Fargate

Ordered by how directly each moves a start-up clock.

1. **Credentials.** Locally the chain stops at the environment variables with no I/O. On Fargate the
   first seven steps find nothing and the container credential provider fetches from `169.254.170.2`
   over HTTP before the first signed request ([creds][sdk-creds], [ECS env][ecs-env],
   [container provider][sdkref-container]). That fetch is inside the `/readyz` clock on Fargate and
   absent locally.
2. **Transport.** Plaintext `http` to one process on the same bridge replaces DNS resolution and a TLS
   handshake to `s3.us-east-1.amazonaws.com` and another to `ssm.us-east-1.amazonaws.com`, from a task
   with a public address and no NAT (`infra/modules/cell/network.tf`).
3. **Addressing.** Path style locally, virtual host on S3. Same SDK code path up to the endpoint rules,
   a different URL on the wire. Nothing in the gateway depends on the form.
4. **Authorization.** Neither moto nor LocalStack evaluates IAM or the bucket policy. The task role's
   read-only grants, the `If-None-Match` requirement under `apps/`, the delete denial and the 403-for-missing-key
   case `S3ObjectStore` documents (P-03, P-04) are untested locally; a misconfigured role shows up only
   on Fargate.
5. **Parameter Store.** moto's SecureString is a string prefix, not KMS; there is no decrypt latency, no
   `AccessDeniedException` for a missing `kms:Decrypt`, and no 40 TPS throttle. The account id in ARNs is
   moto's default, not the cell's.
6. **S3 semantics at the edges.** ETags are MD5 of the bytes in both, matching single-part uploads;
   `ConditionalRequestConflict` (a concurrent conditional write) was not seen in moto, so that refusal
   path in `S3ObjectStore.IsRefusal` is unexercised; moto's `304` body is `"Not Modified"` where S3's is
   empty, which the SDK hides behind the exception either way.
7. **State and durability.** In-memory; the seed is repeated on each stand-in start. No bucket
   lifecycle, no replication, no object lock.
8. **Dispatch.** moto routes an IP-host request by the signature's service and region; a request that
   arrives unsigned or with another region would land somewhere else. The SDK signs everything, so this is
   latent, not active.
9. **Image.** `motoserver/moto` is a Python process; its own cold start and first-request latency are
   not S3's. Start it before the gateway and let the seeding steps warm it, so its first request is not
   inside a gateway clock.

## 7. What the first run must confirm

Nothing here was executed. Three checks settle the parts that rest on reading alone:

1. **Path style on the wire.** moto logs each request line. The registry read must appear as
   `GET /spa-platform-poc-111122223333-us-east-1-an/registry/registry.json`. A line without the bucket
   in the path, or a gateway warning "The registry object ... could not be read (HttpRequestException ...)"
   naming a host of the form `<bucket>.10.90.0.10`, means the `isIp` reading of the rules is wrong for
   the pinned package, and `MOTO_S3_CUSTOM_ENDPOINTS`/`S3_IGNORE_SUBDOMAIN_BUCKETNAME` cannot fix the
   SDK side; the fallback is a network alias per bucket or a one-line `ForcePathStyle` the integrator
   would have to accept.
2. **moto's `127.0.0.1` caveat.** The server-mode page says `127.0.0.1` "may not work". The source read
   says an IPv4 host is path-based and dispatch comes from the signature, which covers it; the proof is
   step 2 and step 3 of the seeding succeeding from the host.
3. **Version pin.** The `ServiceURL` lookup and the rules were read at `aws-sdk-net` commit `057579f`;
   the solution pins `AWSSDK.S3 4.0.101.6` and `AWSSDK.SimpleSystemsManagement 4.0.104.2`
   (`Directory.Packages.props`). The reference guide marks the environment-variable feature supported for
   .NET 3.x and 4.x, so the pins are inside the supported range; the run confirms it.

## Sources

AWS SDK for .NET and the SDKs and Tools Reference Guide

- `sdk-creds`: [Credential and profile resolution](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html)
- `sdkref-ss`: [Service-specific endpoints](https://docs.aws.amazon.com/sdkref/latest/guide/feature-ss-endpoints.html)
- `sdkref-ids`: [Identifiers for service-specific endpoints](https://docs.aws.amazon.com/sdkref/latest/guide/ss-endpoints-table.html)
- `sdkref-region`: [AWS Region](https://docs.aws.amazon.com/sdkref/latest/guide/feature-region.html)
- `sdkref-container`: [Container credential provider](https://docs.aws.amazon.com/sdkref/latest/guide/feature-container-credentials.html)
- `sdk-clientconfig`: [sdk/src/Core/Amazon.Runtime/ClientConfig.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/ClientConfig.cs)
- `sdk-clientconfig-ns`: [sdk/src/Core/Amazon.Runtime/_netstandard/ClientConfig.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/_netstandard/ClientConfig.cs)
- `sdk-transform`: [sdk/src/Core/Amazon.Runtime/Internal/Util/TransformServiceId.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/Internal/Util/TransformServiceId.cs)
- `sdk-envvars`: [sdk/src/Core/Amazon.Util/EnvironmentVariables.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Util/EnvironmentVariables.cs)
- `sdk-awsregion`: [sdk/src/Core/Amazon.Runtime/AWSRegion.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/AWSRegion.cs)
- `sdk-ssmconfig`: [sdk/src/Services/SimpleSystemsManagement/Generated/AmazonSimpleSystemsManagementConfig.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/SimpleSystemsManagement/Generated/AmazonSimpleSystemsManagementConfig.cs)
- `sdk-s3config`: [sdk/src/Services/S3/Custom/AmazonS3Config.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/S3/Custom/AmazonS3Config.cs)
- `sdk-s3resolver`: [sdk/src/Services/S3/Generated/Internal/AmazonS3EndpointResolver.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/S3/Generated/Internal/AmazonS3EndpointResolver.cs)
- `sdk-s3provider`: [sdk/src/Services/S3/Generated/Internal/AmazonS3EndpointProvider.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/S3/Generated/Internal/AmazonS3EndpointProvider.cs)
- `sdk-fn`: [sdk/src/Core/Amazon.Runtime/Internal/Endpoints/StandardLibrary/Fn.cs](https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/Internal/Endpoints/StandardLibrary/Fn.cs)

Amazon ECS

- `ecs-env`: [Amazon ECS environment variables](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-environment-variables.html)

moto

- `moto-server`: [Server mode](https://docs.getmoto.org/en/latest/docs/server_mode.html)
- `moto-s3docs`: [S3](https://docs.getmoto.org/en/latest/docs/services/s3.html)
- `moto-gh`: [github.com/getmoto/moto](https://github.com/getmoto/moto) (release 5.2.4, 2026-10-11; licence Apache-2.0, read from the GitHub API)
- `moto-hub`: [hub.docker.com/v2/repositories/motoserver/moto/tags](https://hub.docker.com/v2/repositories/motoserver/moto/tags) (tags `latest`, `5.2.4`, `5.2.3`: amd64 and arm64)
- `moto-werkzeug`: [moto/moto_server/werkzeug_app.py](https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/moto_server/werkzeug_app.py)
- `moto-s3resp`: [moto/s3/responses.py](https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/s3/responses.py)
- `moto-s3models`: [moto/s3/models.py](https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/s3/models.py)
- `moto-settings`: [moto/settings.py](https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/settings.py)
- `moto-ssm`: [moto/ssm/models.py](https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/ssm/models.py)
- `moto-ssmresp`: [moto/ssm/responses.py](https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/ssm/responses.py)

LocalStack

- `ls-blog`: [LocalStack for AWS Is Moving to a Single Image](https://blog.localstack.cloud/localstack-single-image-next-steps/)
- `ls-token`: [Auth token](https://docs.localstack.cloud/aws/getting-started/auth-token/)
- `ls-licensing`: [Licensing and tiers](https://docs.localstack.cloud/aws/licensing/)
- `ls-images`: [Docker images](https://docs.localstack.cloud/references/docker-images)
- `ls-readme`: [README.md](https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/README.md) (repository archived; `archived: true` from the GitHub API)
- `ls-hub-4140`: [hub.docker.com/v2/repositories/localstack/localstack/tags/4.14.0](https://hub.docker.com/v2/repositories/localstack/localstack/tags/4.14.0) (2026-02-26; amd64 and arm64)
- `ls-s3`: [S3](https://docs.localstack.cloud/aws/services/s3/)
- `ls-ssm`: [Systems Manager](https://docs.localstack.cloud/aws/services/ssm/)
- `ls-net`: [Accessing LocalStack from another container](https://docs.localstack.cloud/aws/capabilities/networking/accessing-endpoint-url/)
- `ls-creds`: [Credentials](https://docs.localstack.cloud/aws/capabilities/config/credentials/)
- `ls-init`: [Initialization hooks](https://docs.localstack.cloud/aws/capabilities/config/initialization-hooks/)
- `ls-arm`: [ARM64 support](https://docs.localstack.cloud/aws/capabilities/config/arm64-support/)
- `ls-s3utils`: [localstack-core/localstack/services/s3/utils.py](https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/localstack-core/localstack/services/s3/utils.py)
- `ls-s3prov`: [localstack-core/localstack/services/s3/provider.py](https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/localstack-core/localstack/services/s3/provider.py)
- `ls-ssmprov`: [localstack-core/localstack/services/ssm/provider.py](https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/localstack-core/localstack/services/ssm/provider.py)

MinIO

- `minio-compat`: [S3 API compatibility (AIStor)](https://docs.min.io/aistor/developers/s3-api-compatibility/)
- `minio-readme`: [github.com/minio/minio/blob/master/README.md](https://github.com/minio/minio/blob/master/README.md)

Repository files read: `src/Platform.Registry/PlatformRegistryExtensions.cs`, `S3ObjectStore.cs`,
`ParameterStoreClientSecretSource.cs`, `RegistrySource.cs`, `RegistryPoller.cs`;
`src/Platform.Hosting/Health.cs`; `src/Gateway/Dockerfile`; `infra/modules/cell/ecs.tf`;
`tools/spactl/SpactlApp.cs`, `SpactlEnvironment.cs`, `Commands.cs`, `AppEntry.cs`;
`contracts/platform-config.schema.json`, `platform-config.example.json`, `registry.schema.json`,
`registry.example.json`, `s3-layout.md`, `infra-outputs.example.json`; `fixtures/apps/`, `fixtures/spa/`;
`Directory.Packages.props`.

[sdk-creds]: https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html
[sdkref-ss]: https://docs.aws.amazon.com/sdkref/latest/guide/feature-ss-endpoints.html
[sdkref-ids]: https://docs.aws.amazon.com/sdkref/latest/guide/ss-endpoints-table.html
[sdkref-region]: https://docs.aws.amazon.com/sdkref/latest/guide/feature-region.html
[sdkref-container]: https://docs.aws.amazon.com/sdkref/latest/guide/feature-container-credentials.html
[sdk-clientconfig]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/ClientConfig.cs
[sdk-clientconfig-ns]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/_netstandard/ClientConfig.cs
[sdk-transform]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/Internal/Util/TransformServiceId.cs
[sdk-envvars]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Util/EnvironmentVariables.cs
[sdk-awsregion]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/AWSRegion.cs
[sdk-ssmconfig]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/SimpleSystemsManagement/Generated/AmazonSimpleSystemsManagementConfig.cs
[sdk-s3config]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/S3/Custom/AmazonS3Config.cs
[sdk-s3resolver]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/S3/Generated/Internal/AmazonS3EndpointResolver.cs
[sdk-s3provider]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Services/S3/Generated/Internal/AmazonS3EndpointProvider.cs
[sdk-fn]: https://github.com/aws/aws-sdk-net/blob/057579fbad991760d11f8f27b051e053e83cea47/sdk/src/Core/Amazon.Runtime/Internal/Endpoints/StandardLibrary/Fn.cs
[ecs-env]: https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-environment-variables.html
[moto-server]: https://docs.getmoto.org/en/latest/docs/server_mode.html
[moto-s3docs]: https://docs.getmoto.org/en/latest/docs/services/s3.html
[moto-gh]: https://github.com/getmoto/moto
[moto-hub]: https://hub.docker.com/v2/repositories/motoserver/moto/tags
[moto-werkzeug]: https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/moto_server/werkzeug_app.py
[moto-s3resp]: https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/s3/responses.py
[moto-s3models]: https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/s3/models.py
[moto-settings]: https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/settings.py
[moto-ssm]: https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/ssm/models.py
[moto-ssmresp]: https://github.com/getmoto/moto/blob/dd8bf40ded62c303019c5d5e9027ffd3ed6f1628/moto/ssm/responses.py
[ls-blog]: https://blog.localstack.cloud/localstack-single-image-next-steps/
[ls-token]: https://docs.localstack.cloud/aws/getting-started/auth-token/
[ls-licensing]: https://docs.localstack.cloud/aws/licensing/
[ls-images]: https://docs.localstack.cloud/references/docker-images
[ls-readme]: https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/README.md
[ls-hub-4140]: https://hub.docker.com/v2/repositories/localstack/localstack/tags/4.14.0
[ls-s3]: https://docs.localstack.cloud/aws/services/s3/
[ls-ssm]: https://docs.localstack.cloud/aws/services/ssm/
[ls-net]: https://docs.localstack.cloud/aws/capabilities/networking/accessing-endpoint-url/
[ls-creds]: https://docs.localstack.cloud/aws/capabilities/config/credentials/
[ls-init]: https://docs.localstack.cloud/aws/capabilities/config/initialization-hooks/
[ls-arm]: https://docs.localstack.cloud/aws/capabilities/config/arm64-support/
[ls-s3utils]: https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/localstack-core/localstack/services/s3/utils.py
[ls-s3prov]: https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/localstack-core/localstack/services/s3/provider.py
[ls-ssmprov]: https://github.com/localstack/localstack/blob/8b9a79f05846835cf4dff63ab7eefdde9df83783/localstack-core/localstack/services/ssm/provider.py
[minio-compat]: https://docs.min.io/aistor/developers/s3-api-compatibility/
[minio-readme]: https://github.com/minio/minio/blob/master/README.md
