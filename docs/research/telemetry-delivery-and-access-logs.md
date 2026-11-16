# Telemetry delivery: the collector's shape and exporters, the log group subscription, and what ALB access logs record

- **Ticket:** #1225, "Telemetry delivery: the collector's shape and exporters, the log group
  subscription, and what ALB access logs record", on the map #1220 (P2-7 Corporate integration).
- **Date:** 2026-11-16
- **Branch:** `research/telemetry-delivery-and-access-logs` (pushed, never merged)
- **Method:** docs-only. Read this repository at `origin/main` `adf0273`
  (`src/Platform.Telemetry/PlatformTelemetryExtensions.cs`, `contracts/telemetry.md`,
  `contracts/changes/mr-5-dependency-down-catalogue.md`, `infra/modules/cell/{ecs,alb,iam,storage,network}.tf`,
  `infra/modules/cell/tests/`, `infra/envs/poc/platform/tests/root.tftest.hcl`, ADR-0010,
  `.scratch/phase-2/plan.md`, `Directory.Packages.props`) and the PingFederate finding on
  `origin/research/pingfederate-in-the-poc-cell`. Read the public AWS, AWS Distro for OpenTelemetry
  (ADOT) and OpenTelemetry pages and sources listed in section 7, each fetched on 2026-11-16 as page
  text, so quotations are the pages' own words. **Not done:** no `terraform` command, no AWS
  credential step, no AWS CLI or API call, no read of the deployed cell; the `aws-mcp` connector
  failed to connect. Nothing was deployed or run except one `docker manifest inspect` of the public
  collector image (section 7). **Prices are not here**: the ticket's body asks for them, the brief
  moved them to the pricing ticket (#1221), and this finding only names each cost driver with its
  source (section 5).

## Answer in brief

1. **A sidecar collector in the gateway's task is the shape that costs least and trips least; it needs
   no gateway code change and no new resource type.** Fargate runs every container of a task in one
   network namespace, so the gateway reaches the collector at `http://localhost:4317` (gRPC, the .NET
   exporter's default) or `http://localhost:4318` with `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`.
   The switch in `AddPlatformTelemetry` is "any non-blank `OTEL_EXPORTER_OTLP_ENDPOINT`": it turns on
   both `AddOtlpExporter()` calls, traces and metrics, and nothing else is read. The image is
   `public.ecr.aws/aws-observability/aws-otel-collector`, built `FROM scratch` with a `/healthcheck`
   binary, published for arm64 (about 50 MB compressed). A second service for the collector meets the
   same five refusals the PingFederate finding listed, P-01's "one ECS service" floor first.
2. **What the collector may export decides the IAM cost, and the cell's tests refuse every AWS
   exporter today.** CloudWatch EMF needs `logs:` actions on the task role, which
   `module.tftest.hcl` forbids ("The task role holds no ecr: or logs: action"); X-Ray and the
   CloudWatch OTLP endpoints need `xray:` or `cloudwatch:` actions on `"*"`, which the same suite
   allows to `SessionManagerMessages` alone. ADOT's documented configuration carrier, an SSM
   parameter read into `AOT_CONFIG_CONTENT`, needs `ssm:GetParameters` on the execution role, also
   forbidden. The combination that trips none of them: configuration as a plain `environment` value,
   and the `debug` and plain `otlphttp` exporters, which take no AWS permission. ADOT ships no S3
   exporter; only the upstream contrib distribution does.
3. **The collector-less route is neither simpler nor cheaper for this gateway.** CloudWatch's OTLP
   endpoints are per signal on different hosts (`xray.<region>` and `monitoring.<region>`), HTTP only,
   and authenticated by SigV4 or a per-service API key. The gateway's `AddOtlpExporter()` calls read
   only the shared `OTEL_EXPORTER_OTLP_*` variables, so one endpoint and one header serve both
   signals: at best one signal arrives. Reaching both means a change in `Platform.Telemetry` plus
   either request signing in the gateway or two long-lived IAM-user API keys in the task
   environment, and the traces endpoint needs Transaction Search switched on for the whole account.
   ADOT's own collector-less .NET path is the auto-instrumentation agent, traces only. It saves no
   compute, because the sidecar fits inside the task the cell already pays for.
4. **When the endpoint is down no request is affected, but the catalogue row's "and logs" is not
   observable as the gateway is wired.** Export runs off the request path from a bounded queue; a
   failed batch is dropped, with no retry by default. The exporter reports failures to a .NET
   `EventSource`, not to `ILogger`, and the gateway has no listener for it, so "the exporter's own
   Warning" reaches no log line. Two sidecar settings keep the row true: the collector `essential:
   false` and no `dependsOn` from the gateway. Read, not executed.
5. **A subscription filter delivers to Kinesis Data Streams, Firehose or Lambda, at least once,
   "usually less than three minutes" after ingestion; a throttled delivery is retried for 24 hours
   and then dropped.** The filter pattern is a JSON property match in the form
   `{ $.<field> = "<value>" }`; the field does not exist yet, because MR-6 (the telemetry event-line
   fields) is undrafted. Every destination is a new resource type for both allowlists, and Firehose
   to S3 meets P-01's one-bucket floor.
6. **ALB access logs record the callback's query string, so under Cognito the authorization code and
   state are written.** The `request_line` field is "HTTP method + protocol://host:port/uri + HTTP
   version. The load balancer preserves the URL sent by the client, as is". The page never says
   "query string"; that it is included is this finding's reading of "as is", not a quoted sentence.
   The S3 ("legacy") route has three attributes, on, bucket and prefix, and no way to drop or mask a
   field; files arrive per node every 5 minutes, best effort. The newer CloudWatch-vended route has a
   `recordFields` list, but whether the request field may be left out is answerable only by an
   account call (`DescribeConfigurationTemplates`), not made. Connection logs carry no URL at all.
7. **What refuses each shape today** is in section 5's table. The sidecar, gated off by default,
   changes no count P-01 holds; the subscription, a second service, an access-log bucket and any
   AWS exporter each need a named test change, and three of them a change request on P-01's sentence.

## 1. The collector on Fargate

### Sidecar against a second service

`ecs.tf` already runs two containers in one task and says how they meet: "Fargate requires awsvpc,
and it is why ... the two containers reach each other over loopback". ADOT's guide says the same of
its collector: "With the awsvpc network mode (always used on Fargate), containers in a task share
localhost: http://localhost:4318" (ADOT, Setting up the ADOT Collector on Amazon ECS).

| | Sidecar in the gateway task | Collector as its own service |
| --- | --- | --- |
| How the gateway reaches it | loopback, no rule, no name | a name (Cloud Map or Service Connect) and a port rule out of the task group |
| Compute | inside the task's 0.25 vCPU and 1 GB, if it fits | a second Fargate task; the smallest size is 0.25 vCPU with 512 MiB |
| IAM | shares the gateway's task role: "Each task definition may only specify one task IAM role" (ECS, application metrics page) | its own roles |
| Failure coupling | set by `essential` and `dependsOn` (section 2) | none |
| What the cell refuses | section 5: nothing P-01 counts | section 5: P-01's "one ECS service", the role, group and rule counts |

The sidecar's one real cost is the shared task role: any grant an exporter needs is a grant the
gateway process holds too. That is the reason to prefer exporters that need none (below).

Whether a collector fits beside the gateway and the mock API in 256 CPU units and 1 GB was not
measured, and no ADOT page read today gives a footprint. `module.tftest.hcl` pins the size
(`the_task_is_arm64_at_a_quarter_vcpu_and_a_gigabyte`), so a larger task is a test change and a
compute price change.

### The image

- `public.ecr.aws/aws-observability/aws-otel-collector` (ADOT ECS guide; the guide's sample uses
  `:latest`). The cell's variables refuse an image named by tag for the gateway and the mock API
  (`a_gateway_image_named_by_tag_is_refused`); a collector image input would follow the same rule.
- Multi-architecture. `docker manifest inspect` of `:latest` on 2026-11-16 returned `amd64` and
  `arm64` entries; the arm64 one is
  `sha256:53cf3e730ae8adad77760188df189a92ea7bf0353f71d4fe096df7ebebc8d14e`, seven layers,
  50,490,614 bytes compressed. This is the one thing executed.
- Built `FROM scratch`: no shell, user `aoc` (uid 4317), `ENTRYPOINT ["/awscollector"]`,
  `CMD ["--config=/etc/otel-config.yaml"]`, with `/healthcheck` copied in
  (`cmd/awscollector/Dockerfile`, commit `4efcd367333e`).
- The pull is from ECR Public, not from the cell's repositories. The execution role's `PullCellImages`
  names the cell's two repositories only. That an anonymous pull from ECR Public needs no IAM grant
  was not read today. The alternative, a third repository in the cell, moves
  `aws_ecr_repository = 2` and `aws_ecr_lifecycle_policy = 2` in the root's allowlist.
- "The task needs a route to the CloudWatch OTLP endpoints and to Amazon ECR Public, for example
  through a public subnet with a public IP or a NAT gateway" (ADOT ECS guide). The cell has the
  first on its default inputs and the second under `multi_task`; the task group's one egress rule is
  443 (`network.tf`, `task_https_out`).

### Health check

ADOT's sample: `"healthCheck": { "command": ["/healthcheck"], "interval": 5, "timeout": 3,
"retries": 2 }`, with the `health_check` extension in the configuration. ECS's API reference says
of `command`: "The string array must start with CMD to run the command arguments directly, or
CMD-SHELL to run the command with the container's default shell." The image has no shell, so
`CMD-SHELL` cannot work; the form the reference supports is `["CMD", "/healthcheck"]`. ADOT's sample
omits `CMD`. Whether ECS accepts the bare form was not tested.

### Configuration carrier

| Carrier | What it needs | What the cell refuses |
| --- | --- | --- |
| SSM parameter into `AOT_CONFIG_CONTENT` through `secrets` (ADOT's documented way: "The task execution role needs ssm:GetParameters on this parameter") | an `ssm:` action on the execution role, and the parameter | `the_execution_role_is_separate_and_holds_only_image_pull_and_log_writes`: the sid set is exactly three, and "The execution role reaches no parameter"; `aws_ssm_parameter` is in neither allowlist |
| `AOT_CONFIG_CONTENT` as a plain `environment` value ("The collector reads its configuration from the AOT_CONFIG_CONTENT environment variable") | nothing | nothing; the YAML is in the task definition, readable by anyone who can describe it, and holds no secret |
| A file bundled in the image, chosen by `command`, for example `--config=/etc/ecs/ecs-default-config.yaml` | nothing | nothing, but the bundled file fixes the exporters to `awsxray` and `awsemf` |
| The `s3` configuration provider (ADOT README lists `file`, `env`, `YAML`, `s3`, `http`, `https`) | an `s3:GetObject` grant for the collector | the task role's sid set |

### Exporters

ADOT's component list (README, commit `4efcd367333e`), exporters: `awsxrayexporter`,
`awsemfexporter`, `prometheusremotewriteexporter`, `debugexporter`, `otlpexporter`, `fileexporter`,
`otlphttpexporter`, `prometheusexporter`, `datadogexporter`, `signalfxexporter`, `logzioexporter`,
`kafka`, `loadbalancingexporter`, `awscloudwatchlogsexporter`. Extensions include
`healthcheckextension` and `sigv4authextension`.

| Destination | Exporter | Permissions on the task role | Notes |
| --- | --- | --- | --- |
| CloudWatch metrics as EMF | `awsemf` | `logs:PutLogEvents`, `logs:CreateLogGroup`, `logs:CreateLogStream`, `logs:DescribeLogStreams`, `logs:DescribeLogGroups`, `logs:PutRetentionPolicy` (ECS, trace-data page's list for the integration) | "The application metrics are exported to CloudWatch Logs with log group name /aws/ecs/application/metrics and the metrics can be viewed in the ECS/AWSOTel/Application namespace." The log group is made at run time, outside Terraform. |
| X-Ray | `awsxray` | `xray:PutTraceSegments`, `xray:PutTelemetryRecords`, `xray:GetSamplingRules`, `xray:GetSamplingTargets`, `xray:GetSamplingStatisticSummaries` | the bundled `ecs-default-config.yaml` pairs this with `awsemf` |
| CloudWatch OTLP endpoints | `otlp_http` with `sigv4auth` | `cloudwatch:PutMetricData` and the `xray:` actions, `"Resource": "*"` (ADOT ECS guide) | ADOT's current default. "Transaction Search enabled in CloudWatch. The OTLP traces endpoint requires it." Metrics are then queried with PromQL, not as `ECS/AWSOTel/Application`. |
| S3 | none in ADOT | - | `awss3exporter` is in the upstream `otelcol-contrib` manifest (`v0.165.1`, commit `c0c9b046d85f`), not in ADOT's list. An S3 export means the upstream image, and a bucket (P-01's one-bucket floor). |
| A plain OTLP endpoint | `otlp` (gRPC) or `otlphttp` | none | the endpoint must be reachable on 443, the task group's only egress, or be inside the task |
| The collector's own stdout | `debug` | none | lands in the cell's log group through the `awslogs` driver and the execution role's existing `WriteTaskLogs`. Its verbosity settings were not read today. Lines it writes do not pass the gateway's formatter, so the never-logged list is held only by what the gateway puts on a span. |

ADOT's guide config "only accepts OTLP traces and metrics", which is exactly what the gateway
exports: `AddPlatformTelemetry` adds an OTLP exporter to tracing and to metrics and none to logging.

### What the gateway's switch needs set

From `PlatformTelemetryExtensions.cs`: `string? otlp = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]`,
then `tracing.AddOtlpExporter()` and `metrics.AddOtlpExporter()` when it is not blank. The exporter
(`OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.19.1 in `Directory.Packages.props`) then reads its
own options from the same configuration:

- `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL` (`grpc` or `http/protobuf`),
  `OTEL_EXPORTER_OTLP_HEADERS`, `OTEL_EXPORTER_OTLP_TIMEOUT`, `OTEL_EXPORTER_OTLP_COMPRESSION`
  (exporter README, tag `core-1.19.1`). "The default is `OtlpExportProtocol.Grpc`"; defaults are
  `localhost:4317` for gRPC and `localhost:4318` for HTTP.
- With the endpoint from the environment and `http/protobuf`, the signal path is appended
  (`OtlpExporterOptions.cs`: `DefaultEndpointEnvVarName, appendSignalPathToEndpoint: true`).
- The per-signal variables (`OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` and the rest) are, in the README's
  table, "Supported" under `UseOtlpExporter` and "Not supported" under `AddOtlpExporter`. The source
  says why: "When using the "AddOtlpExporter" extensions configurationType never has a value other
  than "Default" because OtlpExporterOptions is shared by all signals".

So for the sidecar the task environment gains one value, `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`,
or two, `http://localhost:4318` with `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`. `ecs.tf` already
notes that `OTEL_*` belongs in the environment and outside the configuration contract.

## 2. The endpoint down

The catalogue row (`contracts/changes/mr-5-dependency-down-catalogue.md`): "Telemetry export, the OTLP
endpoint unreachable (P2-7) | The exporter drops and logs; no request is affected", and in the
operator table "No request is affected; the exporter's own Warning".

**"No request is affected" is supported by what was read.**

- Spans are queued, not sent, on the request path. The batch processor's defaults in 1.19.1
  (`BatchExportProcessor.cs`): queue 2048, delay 5,000 ms, export timeout 30,000 ms, batch 512; "The
  maximum queue size. After the size is reached data are dropped."
- The specification: "`Export` MUST NOT block indefinitely"; "`Failure` - exporting failed. The batch
  must be dropped" (trace SDK); "The SDK MUST NOT throw unhandled exceptions for errors in their own
  operations" (error handling).
- Retry is off unless asked for: `OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY`, "When set to `in_memory`, it
  enables in-memory retry for transient errors" (exporter README). The OTLP exporter specification
  says transient errors "MUST be handled with a retry strategy"; the .NET exporter at this version
  has it behind that experimental switch.

**"and logs" / "the exporter's own Warning" is not supported as the gateway is wired.** The exporter
"uses an EventSource with the name "OpenTelemetry-Exporter-OpenTelemetryProtocol" for its internal
logging" (exporter README, Troubleshooting); the SDK's is "OpenTelemetry-Sdk". Those events reach a
file only through self-diagnostics, an `OTEL_DIAGNOSTICS.json` file in the process's directory (SDK
README). `src/` holds no `EventListener` and no such file (`grep`), and `AddPlatformTelemetry` clears
the logging providers and adds the console formatter alone. So a dropped batch writes nothing to
stdout, and the operator's signal for a dead endpoint is the absence of telemetry at the far end.
This is read from source, not run; a hermetic test that points the exporter at a closed port and
asserts on the log stream would settle it, and belongs with P2-7a or the MR-5 follow-ups.

**Two sidecar settings decide whether the row stays true.**

- `essential`: "Suppose that the essential parameter of a container is marked as true, and that
  container fails or stops for any reason. Then, all other containers that are part of the task are
  stopped. ... If this parameter is omitted, a container is assumed to be essential" (ECS task
  definition parameters). ADOT's sample has `"essential": true`. With that, a collector crash stops
  the gateway and the ALB loses its target: a request is affected. The collector must be
  `essential: false`, stated, not omitted.
- `dependsOn`: ADOT suggests "To start your application after the collector, add a dependsOn entry
  with "condition": "START"". `START` "validates that a dependent container is started before
  permitting other containers to start". A collector that cannot start then keeps the gateway from
  starting. The gateway needs no such order: its exporter drops until the collector answers.
- Not read: whether a failed image pull for a non-essential container stops the task's launch. If
  it does, a collector image that cannot be pulled blocks a roll whatever `essential` says.

## 3. CloudWatch's own OTLP endpoints, without a collector

From "OTLP Endpoints" (CloudWatch user guide):

- "CloudWatch OpenTelemetry endpoints are HTTP 1.1 endpoints." "The endpoint supports only HTTP and
  doesn't support gRPC". Payload "binary, json"; compression "gzip, none"; "OTLP 1.x".
- Traces: `https://xray.<region>.amazonaws.com/v1/traces`. Metrics:
  `https://monitoring.<region>.amazonaws.com/v1/metrics`. Logs:
  `https://logs.<region>.amazonaws.com/v1/logs`, with `x-aws-log-group` and `x-aws-log-stream` headers.
- Authentication, each endpoint: "AWS Signature Version 4 (SigV4) - Recommended for workloads running
  on AWS" or "Bearer token (API key) - For workloads running outside AWS or environments that do not
  support the AWS SDK."
- Limits read: traces 5 MB and 10,000 spans a request; metrics 500 requests a second per account,
  1,000 datapoints and 5 MB a request; logs 1 MB and 10,000 events a request.
- "Make sure Transaction Search is enabled before you use the OTLP Endpoint for traces" (Getting
  started). "Transaction search is configured for the entire account and switches all spans
  ingestion through X-Ray into cost effective collection mode"; spans land in the `aws/spans` log
  group (Transaction Search page; ADOT ECS guide).

Can the gateway's exporter reach them with no collector?

| Need | What the exporter as wired gives | Result |
| --- | --- | --- |
| HTTP, not gRPC | `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf` | met by one environment value |
| A different host per signal | one `OTEL_EXPORTER_OTLP_ENDPOINT` for both `AddOtlpExporter()` calls; per-signal variables "Not supported" | not met. Pointed at `https://xray.<region>.amazonaws.com`, traces post to `/v1/traces` and metrics to `/v1/metrics` on the X-Ray host, where they fail and are dropped in silence (section 2). This is a reading of the source, not a run. |
| SigV4 | none in the upstream exporter; its extension point is `HttpClientFactory` on `OtlpExporterOptions` | a signing handler written into `Platform.Telemetry`, and `xray:` and `cloudwatch:` actions on `"*"` on the task role |
| Or a bearer token | `OTEL_EXPORTER_OTLP_HEADERS` carries one `Authorization` header for both signals | "Each service requires its own API key" (metrics bearer page), so one signal at most. An API key "is an IAM service-specific credential ... owned by an IAM user" (X-Ray bearer page): a long-lived credential in the task environment. AWS's own advice on both pages: "use it wherever you can" for SigV4, API keys "only where short-term credentials aren't practical". Whether the project's managed IAM experience permits creating that IAM user was not checked. |

AWS's documented collector-less path is the ADOT SDK, and for .NET that page says: "ADOT .NET version
1.8.0 or later is required", "Compression is currently not supported", "Logs is currently not
supported", "You MUST set OTEL_TRACES_EXPORTER to none", "You MUST set OTEL_AWS_SIG_V4_ENABLED to
true", with the profiler and startup-hook variables of the auto-instrumentation agent and
`OTEL_METRICS_EXPORTER=none` in the sample. That is a second instrumentation loaded into the gateway
process, for traces only. It also warns: "the SDK defaults to parentbased_always_on, which samples
100% of traces. This can result in up to 20x higher ingestion volume and costs compared to the
recommended 5% sampling rate."

**Plainly:** the collector-less route removes one container and adds a code change in
`Platform.Telemetry`, a wider task role or long-lived keys, and an account-wide setting. It is not
simpler. It is not cheaper either: the sidecar sits inside the task size already paid for, so there
is no compute to save, and both routes pay the same CloudWatch ingestion. It also stops being a
stand-in for corporate security tooling, which would hand the project an OTLP endpoint, not an AWS
signing scheme.

## 4. A subscription filter on the gateway's log group

The log group is `/spa-platform/{cell}/gateway`, 14 days' retention by default, with two stream
prefixes, `gateway` and `mock-api` (`ecs.tf`, `variables.tf`).

- **Destinations** (PutSubscriptionFilter): "An Amazon Kinesis data stream belonging to the same
  account", "A logical destination created with PutDestination that belongs to a different account"
  (Kinesis Data Streams and Firehose), "An Amazon Kinesis Data Firehose delivery stream that belongs
  to the same account", "An AWS Lambda function that belongs to the same account". The overview
  page adds OpenSearch Service as a streaming target.
- **Limits:** "Each log group can have up to five subscription filters associated with it."
  "Subscriptions are supported only for log groups in the Standard log class." A role is needed for
  every destination but Lambda, and the caller needs `iam:PassRole` for those.
- **Timing and guarantee:** "Log events are sent to the receiving resource soon after being ingested,
  usually less than three minutes." "Subscription filters ensure at least one time delivery of
  events, while duplicate events may occasionally occur." "Subscription filters might batch log
  events ... Batching is not guaranteed". The payload is gzip-compressed, base64 for Lambda.
- **On a throttle:** "If the destination service returns a retryable error such as a throttling
  exception or a retryable service exception (HTTP 5xx for example), CloudWatch Logs continues to
  retry delivery for up to 24 hours." "Throttled deliverables are retried for up to 24 hours. After
  24 hours, the failed deliverables are dropped." For Kinesis: "If the stream does not have enough
  shards, the log stream will be throttled", with `distribution` `Random` as the mitigation.
- **On a permission or missing-resource error:** "CloudWatch Logs doesn't try to re-deliver if the
  error is a non-retryable error, such as AccessDeniedException or ResourceNotFoundException. In
  these cases the subscription filter is disabled for up to 10 minutes, and then CloudWatch Logs
  retries sending logs to the destination. During this disabled period, logs are skipped." So a
  destination destroyed before its filter loses lines without retry.
- **What to watch:** `ForwardedLogEvents`, `ForwardedBytes`, `DeliveryErrors`, `DeliveryThrottling`
  (CloudWatch Logs metrics page).
- **The filter pattern:** a JSON pattern is `{ PropertySelector EqualityOperator String }`, the
  selector "a dollar sign followed by a period", so `{ $.<field> = "<value>" }`. `IS NULL` and
  `NOT EXISTS` are offered; "The variables IS NOT and EXISTS currently aren't supported", so "the
  line has an event field" cannot be written as existence and must match a value, a wildcard string
  or a compound of values. The gateway's lines are flat JSON (`PlatformJsonFormatter`), which this
  syntax reads. The field that marks an event line is not named: `contracts/telemetry.md` promises
  "one event line for each login, refresh, logout and registry swap" and MR-6, which would name the
  fields, is undrafted (`.scratch/phase-2/plan.md`, open decision 18). The pattern is a form until
  MR-6 lands. A line that is not JSON, or lacks the field, does not match and is not delivered; that
  includes the mock API's lines and a `debug`-exporting collector's.
- **The filter sees the log line, not the request.** It can only deliver what the formatter already
  wrote, so the never-logged list holds for it as for the stream.

Which destination is the stand-in is not this ticket's to decide; each one's footprint is in
section 5. Lambda is the only one that needs no delivery role; Firehose is the only one that lands
objects in S3 without code.

## 5. What the cell refuses today, shape by shape

The tests, as read:

- **P-01** (`docs/plan/04-tests.md`): "Exactly one ALB, one listener, one target group, one ECS
  service, one bucket. No NAT gateway, database or EC2 instance."
- **The root's count map** (`root.tftest.hcl`,
  `the_platform_plan_holds_exactly_the_allowlisted_types_and_counts`): every type and its count,
  among them `aws_ecs_task_definition = 1`, `aws_ecs_service = 1`, `aws_cloudwatch_log_group = 1`,
  `aws_iam_role = 2`, `aws_iam_role_policy = 2`, `aws_ecr_repository = 2`, `aws_s3_bucket = 1`,
  `aws_s3_bucket_policy = 1`, `aws_security_group = 2`, two ingress and two egress rules. "Adding a
  type, or another of one, means editing this map in the same change".
- **The floor** (the run after it): the five P-01 types are single declarations, and "an edit that
  adds a NAT gateway to the map above, or a second load balancer, still fails here".
- **The root's ALB assertion:** `length(aws_lb.cell.access_logs) == 0`, "Access logs stay off in
  Phase 1 (Q8 on #98): an entry records the login callback's query string, authorization code and
  state included". There is no assertion on `connection_logs`.
- **The module's type allowlist** (`module.tftest.hcl`,
  `the_module_declares_only_the_cell_s_intended_resource_types`).
- **The roles:** the task role's sid set is exactly `ReadRegistryAndReleases`, `ReadClientSecrets`,
  `SessionManagerMessages`; no wildcard action; only `SessionManagerMessages` on `"*"`; no `ecr:` or
  `logs:` action. The execution role's sid set is exactly `EcrAuthorization`, `PullCellImages`,
  `WriteTaskLogs`; no `ssm:` action.
- **The bucket policy:** "Every statement in the bucket policy is a Deny", four named sids, no merged
  document.
- **The default plan:** `under_memory_the_plan_is_phase_1_s_byte_for_byte` pins the SHA-256 of the
  container definitions and of the task role's statements on the default inputs; other runs pin the
  container-name set (`["gateway"]`, or `gateway` and `mock-api`).
- **The network:** exactly two groups and four rules; "only the task's 443 leaves the VPC".

| Shape | Refused today by | What it takes |
| --- | --- | --- |
| Sidecar collector, gated input off by default, config in `environment`, `debug` or plain `otlphttp` exporter | nothing P-01 counts: no new type, no new role, no new rule. With the input off the pinned hash and name sets hold. | new module runs for the input on (name set, hardening, `essential: false`, image by digest); `OTEL_EXPORTER_OTLP_ENDPOINT` in the gateway's environment behind the same input |
| The same, config from SSM | execution role sid set and "reaches no parameter"; `aws_ssm_parameter` in neither allowlist | a change request widening the execution role |
| The same, exporting to X-Ray, EMF or the CloudWatch OTLP endpoints | task role sid set; "no ecr: or logs: action" (EMF); "Only the ssmmessages grant is on "*"" (`xray:`, `cloudwatch:`) | a change request widening the gateway's own task role, since the sidecar shares it |
| Collector as a second service | the floor ("one ECS service"); `aws_ecs_service = 1`, `aws_ecs_task_definition = 1`, `aws_iam_role = 2`, `aws_iam_role_policy = 2`; the network suite's two groups and four rules; no service-discovery type in the module allowlist; `verify-teardown.sh`'s role suffixes | a change request on P-01's reading, as the PingFederate finding set out for its second service ("one input defaulting off ... under which the default plan is unchanged") |
| Collector-less, SigV4 | task role sid set and the `"*"` rule; a code change in `Platform.Telemetry` | change request and code |
| Collector-less, API key | `aws_iam_user` and its credential in neither allowlist; a long-lived credential in the task environment | change request; the managed IAM question |
| Subscription filter to Lambda | `aws_cloudwatch_log_subscription_filter`, `aws_lambda_function`, `aws_lambda_permission` in neither allowlist; `aws_iam_role = 2`; the function's log group (`aws_cloudwatch_log_group = 1`, or made at run time outside Terraform) | both allowlists, gated |
| Subscription filter to Firehose and S3 | `aws_cloudwatch_log_subscription_filter`, `aws_kinesis_firehose_delivery_stream`; two more roles; a bucket: a second one fails the floor ("one bucket"), a prefix in the cell's bucket adds a writer to the bucket that holds the registry and releases | both allowlists; a P-01 change request if a second bucket |
| Subscription filter to a Kinesis stream | `aws_cloudwatch_log_subscription_filter`, `aws_kinesis_stream`; one more role | both allowlists, gated |
| ALB access logs to S3 (legacy) | the root's `access_logs` assertion; then either a second bucket (the floor, `aws_s3_bucket = 1`) or an `Allow` for the log-delivery principal on the cell's bucket ("Every statement in the bucket policy is a Deny") | section 6 |
| ALB logs as vended deliveries | the delivery source, destination and delivery types in neither allowlist; a CloudWatch Logs destination is a second log group. The root's `access_logs` assertion does not see this route, because it reads a block on `aws_lb` and this route is separate resources. | an assertion that names the route, whichever way it is decided |

The P-01 change request the map expects is therefore for the subscription's destination and for
access logs, not for the sidecar.

**Cost drivers met, for the pricing ticket.** No price was read.

- Fargate: a sidecar adds no task unless the task size must grow (pinned at 256 and 1024); a second
  service adds a task, smallest 0.25 vCPU and 512 MiB (ECS, Fargate task sizes).
- CloudWatch Logs ingestion for whatever the collector writes to the cell's log group (`debug`
  output grows with traffic), and for `aws/spans` under Transaction Search.
- EMF: custom metrics in `ECS/AWSOTel/Application`, and the log group
  `/aws/ecs/application/metrics`, made at run time.
- Traces: Transaction Search is account-wide; "By default you will also index 1% of ingested spans
  for free"; the collector-less default of 100% sampling is "up to 20x higher ingestion volume and
  costs".
- Subscription: the destination's own charges (a Kinesis stream exists whether or not lines flow;
  Lambda and Firehose are per use). Their price pages were not read.
- ALB access and connection logs to S3: "You are charged storage costs for Amazon S3, but not
  charged for the bandwidth used by Elastic Load Balancing to send log files to Amazon S3."
- Vended deliveries: "Even when you publish logs directly to Amazon S3 or Firehose, CloudWatch
  delivery charges apply" (`<region>-S3-Egress-Bytes`, `<region>-FH-Egress-Bytes`).
- Container Insights stays off (`ecs.tf`); ADOT's guide suggests it for task metrics, and it is
  billed per metric.

**Against the cost rule ("the platform root's destroy removes it").** Four things in these shapes
are created outside Terraform and survive a destroy: the EMF log group, the `aws/spans` log group
and the account's Transaction Search setting, a Lambda's auto-created log group, and an IAM user
made for an API key through the console. None carries the cell's tags, so P-05's tag query would not
see them (a reading of how P-05 works, not a run). The `debug` and plain `otlphttp` exporters create
nothing.

## 6. ALB access logs and connection logs

### Every access log field

"The following table describes the fields of an access log entry, in order. All fields are
delimited by spaces. When we add a new field, we add it to the end of the log entry." The 36
fields, with the page's position numbers:

1 `type`, 2 `time`, 3 `elb`, 4 `client:port`, 5 `target:port`, 6 `request_processing_time`,
7 `target_processing_time`, 8 `response_processing_time`, 9 `elb_status_code`,
10 `target_status_code`, 11 `received_bytes`, 12 `sent_bytes`, 13 `"request_line"`,
14 `"user_agent"`, 15 `ssl_cipher`, 16 `ssl_protocol`, 17 `target_group_arn`, 18 `"trace_id"`,
19 `"domain_name"`, 20 `"chosen_cert_arn"`, 21 `matched_rule_priority`, 22 `request_creation_time`,
23 `"actions_executed"`, 24 `"redirect_url"`, 25 `"error_reason"`, 26 `"target:port_list"`,
27 `"target_status_code_list"`, 28 `"classification"`, 29 `"classification_reason"`,
30 `conn_trace_id`, 31 `"transformed_host"`, 32 `"transformed_uri"`,
33 `"request_transform_status"`, 34 `ip_address`, 35 `target_error_code`, 36 `elb_error_code`.

### What the request field records of the query string

The whole description of field 13, verbatim:

> The request line from the client, enclosed in double quotes and logged using the following
> format: HTTP method + protocol://host:port/uri + HTTP version. The load balancer preserves the URL
> sent by the client, as is, when recording the request URI. It does not set the content type for
> the access log file. When you process this field, consider how the client sent the URL.

**Exactly:** the page states that the URL is preserved "as is" and gives the format with `/uri`. It
does not use the words "query string", and none of its example entries has one. That the query
string is part of what is recorded is this finding's reading of "preserves the URL sent by the
client, as is", and it is the reading `alb.tf` and the root's assertion already record. It was not
observed: no access log was produced. Under that reading, a Cognito callback is logged as
`"GET https://<app host>:443/<callback path>?code=<authorization code>&state=<state> HTTP/2.0"`,
with `code` and `state` in clear, into an object that `contracts/telemetry.md`'s never-logged list
does not govern because the gateway does not write it. Under `form_post` the code and state are in
the request body, which no field records; `received_bytes` counts it.

Other fields that could carry a URL: 24 `"redirect_url"` is "The URL of the redirect target for the
location header" when the load balancer itself took a redirect action, and 32 `"transformed_uri"` is
set only by a rewrite transform. The cell has no listener rule (ADR-0010), so both are `-`; the
gateway's own redirect to the provider, whose `Location` carries the state, is not recorded. 14
`"user_agent"` and 18 `"trace_id"` are client-supplied header values.

### Dropping or masking a field

- **The S3 route:** the load balancer attributes are `access_logs.s3.enabled`, `access_logs.s3.bucket`
  and `access_logs.s3.prefix`, and nothing else (Application Load Balancers, load balancer
  attributes). No field can be left out and none masked. The only controls are whether logs are on,
  who can read the bucket, and how long objects live ("You can also define Amazon S3 lifecycle rules
  to archive or delete log files automatically").
- **The vended route:** "Application Load Balancer now offers enhanced logging options through
  CloudWatch Logs ... including to Amazon CloudWatch Logs, Amazon Data Firehose, and Amazon Simple
  Storage Service", for access, connection and health check logs. `CreateDelivery` takes
  `recordFields`: "The list of record fields to be delivered to the destination, in order. If the
  delivery's log source has mandatory fields, they must be included in this list."
  **Not verified:** which ALB access-log fields are allowed and which are mandatory. That list is
  returned by `DescribeConfigurationTemplates` (`allowedFields`, each with `mandatory`), an account
  call that was not made, and no page read today prints it. If the request field is not mandatory,
  this route can deliver access logs without the URL; that is the one open fact that would change
  the access-log decision.
- **Outside AWS's controls:** a destination the project owns (a Lambda behind Firehose, or the
  stand-in collector) could cut the query string before storing, but the unmasked line exists
  first.

### Delivery

"Elastic Load Balancing publishes a log file for each load balancer node every 5 minutes. Log
delivery is eventually consistent. The load balancer can deliver multiple logs for the same
period." "Elastic Load Balancing logs requests on a best-effort basis. We recommend that you use
access logs to understand the nature of the requests, not as a complete accounting of all requests."
Files are gzip, at
`bucket[/prefix]/AWSLogs/<account>/elasticloadbalancing/<region>/yyyy/mm/dd/...log.gz`. Requests that
never reach a target are logged too.

### The bucket and its policy

- "The bucket must be located in the same Region as the load balancer." "The only server-side
  encryption option that's supported is Amazon S3-managed keys (SSE-S3)."
- The policy statement: `"Effect": "Allow"`, `"Principal": { "Service":
  "logdelivery.elasticloadbalancing.amazonaws.com" }`, `"Action": "s3:PutObject"`, `"Resource":
  "arn:aws:s3:::<bucket>/<prefix>/AWSLogs/<account>/*"`. "Always include the account ID of the
  account with the load balancer in the resource path". The older per-Region ELB account principal
  (Ohio: 033677994240) "is still supported, but we recommend that you replace it".
- Against the cell: its bucket is SSE-S3 (`sse_algorithm = "AES256"`), so the encryption rule is met;
  but its policy is all Deny by test, and the logs' `Allow` is exactly what that test refuses. A
  second bucket avoids the `Allow` and fails P-01's "one bucket". Either way the decision is a
  change request, and `alb.tf`'s comment already says so: "Delivered to S3, the logs would also need
  a bucket of their own ... and P-01 allows no second."

### Connection logs

Same delivery (per node, every 5 minutes, eventually consistent, gzip, a `conn_log_` file name),
same bucket requirements and the same `logdelivery.elasticloadbalancing.amazonaws.com` statement.
"Each connection attempt has an entry"; a persistent connection is one connection entry and many
access entries. The 15 fields:

1 `timestamp`, 2 `client_ip`, 3 `client_port`, 4 `listener_port`, 5 `tls_protocol`, 6 `tls_cipher`,
7 `tls_handshake_latency`, 8 `leaf_client_cert_subject`, 9 `leaf_client_cert_validity`,
10 `leaf_client_cert_serial_number`, 11 `tls_verify_status`, 12 `conn_trace_id`,
13 `tls_keyexchange`, 14 `elb`, 15 `ip_address`.

No field holds a URL, a header or a body, so a connection log records nothing of the authorization
code. Fields 8 to 10 are `-` unless the listener has mutual TLS, which the cell's does not.
`conn_trace_id` joins a connection entry to its access entries. The root suite asserts nothing about
`connection_logs` today, so turning them on is refused only by the bucket question above.

## 7. What was executed and what was read

**Executed (2026-11-16):**

- `docker manifest inspect public.ecr.aws/aws-observability/aws-otel-collector:latest`, and of its
  arm64 digest, with Docker 29.9.1: the architectures, the digest and the compressed size in
  section 1. Nothing was pulled or run.
- `git ls-remote` for the commits the GitHub sources are pinned to.

**Read in this repository** (at `origin/main` `adf0273`, 2026-11-16):
`src/Platform.Telemetry/PlatformTelemetryExtensions.cs`, `PlatformJsonFormatter.cs`;
`contracts/telemetry.md`; `contracts/changes/mr-5-dependency-down-catalogue.md`;
`infra/modules/cell/ecs.tf`, `alb.tf`, `iam.tf`, `storage.tf`, `network.tf`, `variables.tf`;
`infra/modules/cell/tests/module.tftest.hcl`, `network.tftest.hcl`;
`infra/envs/poc/platform/tests/root.tftest.hcl`; `docs/adr/0010-wildcard-ingress-with-no-listener-rules.md`;
`docs/plan/04-tests.md` (P-01); `.scratch/phase-2/plan.md` (P2-7a, P2-7b, open decision 18);
`docs/gaps.md` (GAP-25); `Directory.Packages.props`; `docs/agents/aws-rules.md`; and
`docs/research/pingfederate-in-the-poc-cell.md` on `origin/research/pingfederate-in-the-poc-cell`.

**Read on the web, each on 2026-11-16:**

- Access logs for your Application Load Balancer:
  https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-access-logs.html
- Enable access logs:
  https://docs.aws.amazon.com/elasticloadbalancing/latest/application/enable-access-logging.html
- Connection logs, and enabling them:
  https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-connection-logs.html ,
  https://docs.aws.amazon.com/elasticloadbalancing/latest/application/enable-connection-logging.html
- CloudWatch Logs for your Application Load Balancer:
  https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-cloudwatch-logs.html
- Application Load Balancers (load balancer attributes):
  https://docs.aws.amazon.com/elasticloadbalancing/latest/application/application-load-balancers.html
- Enable logging from AWS services; CreateDelivery; DescribeConfigurationTemplates:
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/AWS-logs-and-resource-policy.html ,
  https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_CreateDelivery.html ,
  https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_DescribeConfigurationTemplates.html
- OTLP Endpoints; Getting started; collector-less with the ADOT SDK; bearer tokens for metrics and
  for traces; Transaction Search:
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-OTLPEndpoint.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-OTLPGettingStarted.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-OTLP-UsingADOT.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-OTLP-MetricsBearerTokenAuth.html ,
  https://docs.aws.amazon.com/xray/latest/devguide/xray-opentelemetry-bearer-token.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch-Transaction-Search-getting-started.html
- Subscriptions; concepts; log group-level filters; filter pattern syntax; PutSubscriptionFilter;
  CloudWatch Logs metrics:
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/Subscriptions.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/subscription-concepts.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/SubscriptionFilters.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/FilterAndPatternSyntax.html ,
  https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutSubscriptionFilter.html ,
  https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/CloudWatch-Logs-Monitoring-CloudWatch-Metrics.html
- ECS: trace data; application metrics; task definition parameters; HealthCheck; Parameter Store
  secrets; Fargate task sizes:
  https://docs.aws.amazon.com/AmazonECS/latest/developerguide/trace-data.html ,
  https://docs.aws.amazon.com/AmazonECS/latest/developerguide/application-metrics-cloudwatch.html ,
  https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html ,
  https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_HealthCheck.html ,
  https://docs.aws.amazon.com/AmazonECS/latest/developerguide/secrets-envvar-ssm-paramstore.html ,
  https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-tasks-services.html
- ADOT, Setting up the ADOT Collector on Amazon ECS: https://aws-otel.github.io/docs/setup/ecs
- `aws-observability/aws-otel-collector` at `main` `4efcd367333e`: `README.md`,
  `cmd/awscollector/Dockerfile`, `config/ecs/ecs-default-config.yaml`
  (https://github.com/aws-observability/aws-otel-collector)
- `open-telemetry/opentelemetry-collector-releases` at `main` `c0c9b046d85f`:
  `distributions/otelcol-contrib/manifest.yaml`
- `open-telemetry/opentelemetry-dotnet` at tag `core-1.19.1` (`4c97d678cf25`):
  `src/OpenTelemetry.Exporter.OpenTelemetryProtocol/README.md`, `OtlpExporterOptions.cs`;
  `src/OpenTelemetry/README.md`, `BatchExportProcessor.cs`
- `open-telemetry/opentelemetry-specification` at `main` `a4e05d9704fc`:
  `specification/protocol/exporter.md`, `specification/trace/sdk.md`,
  `specification/error-handling.md`

**Not verified, and what would verify it:**

- That the access log's request field includes the query string: read as the meaning of "as is",
  not quoted and not observed. One sitting with access logs on and one login settles it.
- Whether the vended delivery's request field is mandatory: `aws logs describe-configuration-templates`
  for the load balancer's access-log type.
- That a dropped export writes no log line, and that a request is unaffected with the endpoint
  down: a hermetic test against a closed port.
- Whether the collector fits in the 256 CPU unit, 1 GB task beside the gateway and the mock API: a
  deployed run.
- Whether ECS accepts ADOT's `["/healthcheck"]` without `CMD`, whether a failed pull of a
  non-essential container's image stops a task launch, and whether an ECR Public pull needs no
  grant: ECS and ECR Public pages not read, or a deployed run.
- The `debug` exporter's output and verbosity; the Terraform names of the vended delivery resources;
  whether the managed IAM experience permits an IAM user for an API key.
- Every price.
