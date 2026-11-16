# What every P2-7 stand-in costs per sitting-hour and per idle month, and whether the project plan supports each service

- **Ticket:** #1221, "What every P2-7 stand-in costs per sitting-hour and per idle month, and whether
  the project plan supports each service" (`wayfinder:research`), on the map #1220.
- **Date:** 2026-11-16. **Branch:** `research/p2-7-stand-in-costs`, pushed and never merged, as the
  map rules.
- **Method:** docs-only. Repository reading at commit `adf0273` (origin/main on 2026-11-16):
  `docs/agents/aws-rules.md`, `tools/aws-project.env` (the Region name alone: `us-east-2`),
  `infra/modules/cell` (`ecs.tf`, `network.tf`, `alb.tf`, `storage.tf`, `variables.tf`),
  `infra/bootstrap` (`variables.tf`, `main.tf`, and the resource **types** in the local state file,
  no values), `docs/plan/05-infrastructure-and-cost.md`, ADR-0003,
  `src/Platform.Telemetry/PlatformTelemetryExtensions.cs` and `RequestTelemetry.cs`,
  `.github/workflows/*.yml` (the `runs-on` lines). The repository's visibility and the owner's GitHub
  plan were read with `gh`. **Prices:** every rate below is the US East (Ohio), `us-east-2`, rate from
  AWS's public price-list offer files
  (`https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/<offer>/current/us-east-2/index.json`),
  downloaded over plain HTTPS with no credential on 2026-11-16 and parsed with `python3 -I`. The
  marketing price pages were also fetched on 2026-11-16, but their Region tables do not render to a
  text fetch: they gave the billing **rules** (proration, what is free, what a deleted resource costs)
  and us-east-1 worked examples, not the Ohio figures. Section 9 lists each offer file with its
  version, and each page with what it gave. **Not done:** no `aws` CLI call, no `terraform plan` or
  `apply`, no credential step, no read of the cell; the `aws-mcp` connector failed to connect. So the
  project's plan type (Free or Paid) is **unverified** (`aws freetier get-account-plan-state` is a
  credentialed call), nothing here was tested against the managed policies, and every volume is an
  assumption stated where it is used. Arithmetic uses a 4-hour sitting and a 730-hour month, the
  month `docs/plan/05-infrastructure-and-cost.md` uses.

## Answer in brief

1. **The sitting ceiling holds with a wide margin.** Every stand-in the map names, run together for
   four hours at generous poc volumes and with no free-tier allowance counted, adds about **$0.20**
   (the likely set) and at most about **$0.60** (the dearest supported choice for each) against the
   ceiling of $5. Nothing supported comes near it.
2. **The idle ceiling holds only under the cost rule.** Gated off or destroyed, the stand-ins bill
   **$0** a month, with cents at most for retained log data. Left standing for a month they break
   $2: three secrets and a sidecar that forces the next task size are **$3.80**; a second Fargate
   service is **$10.86** by itself (with the public address the default shape gives a task); EMF
   metrics are **$0.30 per series-month**, about $60 at an assumed 200 series. Anything billed by
   the hour or the month has to be inside the gate and inside the destroy.
3. **Four facts contradict the map or the ticket.**
   - **The pipeline stand-in cannot be created under the plan.** The managed service control policy
     of both the Free Tier and the Paid Plan denies `iam:*Provider*` on every resource, with no
     condition. `iam:CreateOpenIDConnectProvider` matches it. GitHub's OIDC federation needs that
     provider, so it needs "Activate advanced AWS features" or another design. Read, not tested;
     the applied bootstrap root has no OIDC provider in its state, which agrees.
   - **The repository is public, not private.** Standard runners are free and required reviewers
     are available on the owner's Free plan. Were it made private, required reviewers would need
     GitHub Enterprise, and the Free plan would give $12 of included compute a month.
   - **"Managed rotation" does not apply to these secrets.** It exists for secrets owned by Aurora,
     RDS, DocumentDB, Redshift, ECS Service Connect, and a list of partners that has neither Cognito
     nor Ping. An OAuth client secret rotates by a Lambda function, which costs well under a cent
     a sitting.
   - **Another log group is not a subscription destination.** The four are Kinesis Data Streams,
     Lambda, Firehose and OpenSearch Service.
4. **Supported under the plan:** Secrets Manager, Lambda, KMS, ECS on Fargate, CloudWatch and
   CloudWatch Logs, X-Ray, S3 and Elastic Load Balancing are on the Free Tier list (and so the Paid
   Plan's). **Amazon Data Firehose is Paid Plan only. AWS Private CA is on neither list**, so the
   map's Out of scope has a second reason beside cost.
5. **No KMS key is forced by anything read.** Secrets Manager encrypts with the AWS managed key
   `aws/secretsmanager` at no charge, and ALB access logs accept SSE-S3 only, so a customer managed
   key cannot be used there at all. One chosen anyway is $1 a month, prorated hourly, and free
   while scheduled for deletion.
6. **A spend limit stops the stand-ins from being created, not from running.** The spend-limit
   policy denies `secretsmanager:CreateSecret`, `kms:CreateKey`, `lambda:CreateFunction`,
   `ecs:CreateService` and `firehose:CreateDeliveryStream` as the project nears its limit: a sitting
   started near the limit fails at apply.

## The totals table

Per four-hour sitting and per idle month, us-east-2, no free tier counted. "Idle month, standing" is
the stand-in left on for 730 hours with no user traffic; "idle month, cost rule" is the map's rule
applied (gate off, or the platform root destroyed). Volumes are assumptions, given in each section.

| Stand-in | Per 4-hour sitting | Idle month, standing | Idle month, cost rule | Bills while the cell is destroyed | Free Tier | Paid Plan |
| --- | --- | --- | --- | --- | --- | --- |
| Secrets Manager, 3 secrets, 1,000 API calls | $0.012 | $1.20 | $0 | No: a secret marked for deletion is free | Yes | Yes |
| Rotation Lambda (rotation by function, 100 runs bound) | $0.013 | $0 | $0 | No; its log group, if kept, cents | Yes | Yes |
| Collector as a sidecar, fits the 0.25 vCPU / 1 GB task | $0 | $0 | $0 | No | Yes | Yes |
| Collector as a sidecar, task grows to 0.25 vCPU / 2 GB | $0.014 | $2.60 | $0 | No | Yes | Yes |
| Collector as a second service, 0.25 vCPU / 0.5 GB ARM, public IPv4 | $0.060 | $10.86 | $0 | No | Yes | Yes |
| CloudWatch Logs, 100 MB more a sitting | $0.050 | $0.03 | under $0.01 | Only a log group outside the root | Yes | Yes |
| Subscription filter to Lambda | $0.004 | $0 | $0 | No | Yes | Yes |
| Subscription filter to Firehose, 100 MB | $0.013 | $0 | $0 | No | **No** | Yes |
| Subscription filter to another log group | not a destination | | | | | |
| X-Ray, 20,000 traces a sitting | $0.100 | $0, or $0.88 if health checks are traced | $0 | No | Yes | Yes |
| CloudWatch metrics from EMF, 200 series assumed | $0.329 | $60.00 while the gateway emits | $0 | No | Yes | Yes |
| ALB access logs to S3, 200 objects, 10 MB | $0.001 | under $0.01 | under $0.01 | Only objects left in a kept bucket | Yes | Yes |
| GitHub Actions, this repository (public) | $0 | $0 | $0 | No | n/a | n/a |
| IAM OIDC provider, role assumption | $0, but **denied by the plan policy** | $0 | $0 | No | **No** | **No** |
| KMS customer managed key, if one is chosen (none is forced) | $0.006 | $1.00 | $0 | No: free while scheduled for deletion | Yes | Yes |
| AWS Private CA, short-lived mode (out of scope) | $0.27 + $0.058 a certificate | $50.00 | $0 | No, unless restored | **No** | **No** |
| AWS Private CA, general-purpose mode (out of scope) | $2.19 + $0.75 a certificate | $400.00 | $0 | No, unless restored | **No** | **No** |

| Total against the ceiling | Per 4-hour sitting (ceiling $5) | Idle month, standing (ceiling $2) | Idle month, cost rule (ceiling $2) |
| --- | --- | --- | --- |
| **Likely set**: secrets and rotation Lambda, sidecar at the next task size, logs, subscription to Lambda, X-Ray, access logs, Actions | **$0.19: within** | **$3.83: over** ($1.23 and within, if the sidecar fits the present task; either figure +$0.88 if health checks are traced) | **under $0.02: within** |
| **Dearest supported set**: the likely set with a second service in place of the sidecar, plus EMF at 200 series, Firehose and a customer managed key | **$0.59: within** | **$73: over** | **under $0.02: within** |
| Private CA added to either (unsupported, out of scope) | +$0.33 or +$2.94 with one certificate: still within | +$50 or +$400: over | $0 |

Reading it: per sitting, nothing matters. Per month, three lines carry all the risk, and each is
closed by the cost rule: the secrets ($0.40 each a month), anything on Fargate, and EMF series.

## 1. Region, plan and the supported-services lists

The Region is `us-east-2`: `AWS_REGION` in `tools/aws-project.env`, `region` in the three roots'
`terraform.tfvars` and `backend.hcl`, and the `spa-platform` profile's `region` line. The plan's own
cost model (`docs/plan/05-infrastructure-and-cost.md` -> "Cost model") is written for us-east-1; the
ALB, Fargate ARM and public-address rates it quotes are the same in the us-east-2 offer files.

The lists are on
<https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html>
(read 2026-11-16), which says a service on neither list needs "Sign up for AWS (advanced)" or
"Activate advanced AWS features".

| Service | Free Tier list | Paid Plan list | Not-supported list |
| --- | --- | --- | --- |
| AWS Secrets Manager | Yes | (inherits) | |
| AWS Lambda | Yes, "Lambda@Edge is not supported" | (inherits) | |
| AWS Key Management Service | Yes | (inherits) | |
| Amazon Elastic Container Service (Fargate is not listed apart; the cell runs on it today) | Yes | (inherits) | |
| Amazon CloudWatch, Amazon CloudWatch Logs | Yes | (inherits) | |
| AWS X-Ray | Yes | (inherits) | |
| Amazon Simple Storage Service, Elastic Load Balancing | Yes | (inherits) | |
| AWS Identity and Access Management, AWS Security Token Service | Yes, but see below | (inherits) | |
| Amazon Data Firehose | No | Yes | |
| AWS Private Certificate Authority | No | No | Yes |

The managed policies behind the lists are on
<https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html> (read
2026-11-16). Three things in them bear on the stand-ins:

- **`DenyIAMRestrictedActions`**, present in both the Free Tier policy and the Paid Plan policy with
  `"Resource": "*"` and no condition, denies among others `"iam:*Provider*"`. That pattern matches
  `iam:CreateOpenIDConnectProvider`, `iam:GetOpenIDConnectProvider` and the SAML provider actions.
  So the list entry "AWS Identity and Access Management" does not extend to creating the OIDC
  provider the pipeline stand-in assumes. `infra/bootstrap` can create that provider
  (`create_github_oidc_provider`), and the applied root was written with it `false` and
  `github_repository` empty; its state holds no `aws_iam_openid_connect_provider`. This finding
  did not test the denial, and the trust-policy research on the map owns the conclusion.
- **The allow-lists** name `secretsmanager:*`, `xray:*`, `kms:*`, `logs:*`, `ecs:*`, `lambda:*`,
  `s3:*`, `cloudwatch:*`, `iam:*` and `sts:*` in both plans; `firehose:*` appears in the Paid Plan
  list only; `acm-pca` appears in neither.
- **"Service control policies for spend limits"**, applied "as it nears the spend limit", deny
  resource creation by family: `ecs:CreateService`, `elasticloadbalancing:CreateLoadBalancer`,
  `ec2:CreateNatGateway`, `lambda:CreateFunction`, `kms:CreateKey`, `secretsmanager:CreateSecret`,
  `acm-pca:CreateCertificateAuthority`, `firehose:CreateDeliveryStream`, `route53:CreateHostedZone`,
  and `lambda:InvokeFunction`. Existing resources are not named as deleted, so standing charges
  continue; what stops is a new sitting's apply. This is the fact the map's "CI holding AWS
  credentials" item waits for.

**Plan and credits.** On the Free plan "No charges incur during usage" and the "Account closes when
credits are depleted or when the plan duration ends" (six months, $100 to $200 of credits); on the
Paid plan the project pays "for charges that exceed the credit balance"
(<https://docs.aws.amazon.com/awsaccountbilling/latest/aboutv2/free-tier-plans.html>, read
2026-11-16). Either way a dollar of stand-in is a dollar off the credit balance or the bill, so the
table counts list price.

## 2. Secrets Manager

| Item | us-east-2 rate | Source |
| --- | --- | --- |
| A secret | $0.40 per secret per month | offer `AWSSecretsManager`, `USE2-AWSSecretsManager-Secrets` |
| API calls | $0.05 per 10,000 | offer `AWSSecretsManager`, `USE2-AWSSecretsManagerAPIRequest` |
| A secret marked for deletion | no charge | user guide, below |
| The AWS managed key `aws/secretsmanager` | free | user guide, below |

- <https://aws.amazon.com/secrets-manager/pricing/> (read 2026-11-16) gives "$0.40 / secret /
  month" and "$0.05/10,000 calls" in its examples, prices a secret that lives part of a month by the
  hour in its second example (so proration is hourly; the page does not state it as a rule), and
  says "You are not charged for creating new versions."
- <https://docs.aws.amazon.com/secretsmanager/latest/userguide/intro.html> -> "Pricing" (read
  2026-11-16): "There is no charge for secrets that are marked for deletion." "You can use the AWS
  managed key `aws/secretsmanager` that Secrets Manager creates to encrypt your secrets for free."
  "When you turn on automatic rotation (except managed rotation), Secrets Manager uses an AWS Lambda
  function to rotate the secret, and you are charged for the rotation function at the current Lambda
  rate."
- <https://docs.aws.amazon.com/secretsmanager/latest/userguide/manage_delete-secret.html> (read
  2026-11-16): deletion is scheduled "after a recovery window of a minimum of seven days", and
  `--force-delete-without-recovery` deletes at once. **Not found on the pages read:** whether a new
  secret can take the name of one still inside its recovery window. A sitting that recreates the
  same names every time should either force-delete or prove the reuse; that is a design point, not a
  cost.

**Arithmetic.** Three secrets (one per app: `fixtures/apps` holds two and the onboarding path adds a
third; ADR-0003 has one client secret per app). 3 x $0.40 x 4 / 730 = $0.0066 a sitting; 1,000 calls
(the gateway resolves each reference per snapshot, plus rotations) = $0.005. Standing: 3 x $0.40 =
**$1.20 a month**, which is 60% of the idle ceiling for this one stand-in. Each further secret is
$0.40 a month; under P2-6's one assertion key per cell per issuer the count would fall.

**Rotation.** The map says "Secrets Manager with managed rotation".
<https://docs.aws.amazon.com/secretsmanager/latest/userguide/rotate-secrets_managed.html> (read
2026-11-16) limits managed rotation to Amazon Aurora, Amazon ECS Service Connect, Amazon RDS,
Amazon DocumentDB, Amazon Redshift and "managed external secrets", and says "For all other types of
secrets, see Rotation by Lambda function". The partner list
(<https://docs.aws.amazon.com/secretsmanager/latest/userguide/mes-partners.html>, read 2026-11-16)
has Auth0, Okta (`OktaOAuthClientSecret`), Salesforce and others, and neither Amazon Cognito nor
Ping Identity. So the stand-in's rotation is a Lambda function:

| Item | us-east-2 rate | Source |
| --- | --- | --- |
| Requests | $0.20 per million | offer `AWSLambda`, `USE2-Request-ARM` |
| Duration, Arm | $0.0000133334 per GB-second | offer `AWSLambda`, `USE2-Lambda-GB-Second-ARM` |
| Duration, x86 | $0.0000166667 per GB-second | offer `AWSLambda`, `USE2-Lambda-GB-Second` |
| An idle function | nothing: billed "only for requests and duration" | <https://aws.amazon.com/lambda/pricing/> (read 2026-11-16) |

Bound: 100 invocations of 10 seconds at 1 GB = 1,000 GB-seconds = $0.013, plus $0.00002 of requests.
A real rotation is a handful of short invocations. The function's log group is created by Lambda
unless Terraform declares it; declared in the platform root, it is destroyed with it.

## 3. An OpenTelemetry collector in the cell

The gateway task today is ARM64, `cpu = "256"`, `memory = "1024"`, two containers
(`infra/modules/cell/ecs.tf`). The OTLP exporter is switched by `OTEL_EXPORTER_OTLP_ENDPOINT`
(`PlatformTelemetryExtensions.cs`).

| Item | us-east-2 rate | Source |
| --- | --- | --- |
| Fargate ARM vCPU | $0.03238 per vCPU-hour | offer `AmazonECS`, `USE2-Fargate-ARM-vCPU-Hours:perCPU` |
| Fargate ARM memory | $0.00356 per GB-hour | offer `AmazonECS`, `USE2-Fargate-ARM-GB-Hours` |
| Public IPv4 address in use | $0.005 per hour | offer `AmazonVPC`, `USE2-PublicIPv4:InUseAddress` |
| Cloud Map | $0.10 per registered resource per month | offer `AWSCloudMap`, `USE2-Cloud-Map-Resources` |

<https://aws.amazon.com/fargate/pricing/> (read 2026-11-16): "Pricing is calculated per second with
a 1-minute minimum"; the smallest task is "0.25 vCPU" with "0.5 GB, 1 GB, and 2 GB"; "20 GB of
ephemeral storage is available for all Fargate Tasks and Pods by default"; "You will also incur a
charge for the public IPv4 addresses associated with your Amazon ECS Tasks".

- **Sidecar in the gateway task.** Fargate bills the task size, not the container count, so a third
  container that fits in 0.25 vCPU and 1 GB adds **$0**. Whether it fits beside the gateway and the
  mock API is **not verifiable here**: no page read states a minimum for the collector. AWS's own
  example task definition for the ADOT collector as a Fargate sidecar
  (<https://raw.githubusercontent.com/aws-observability/aws-otel-collector/main/examples/ecs/aws-cloudwatch/ecs-fargate-sidecar.json>,
  read 2026-11-16) declares a task of `cpu` 256 and `memory` 512 for the collector with three
  sample containers; it is an example, not a stated minimum. If the task must grow, the next sizes
  are 0.25 vCPU / 2 GB (+1 GB = +$0.00356 an hour: **$0.014 a sitting, $2.60 a month**) or
  0.5 vCPU / 1 GB (+0.25 vCPU = +$0.008095 an hour: $0.032 a sitting, $5.91 a month).
- **A second Fargate service**, at the smallest size, 0.25 vCPU / 0.5 GB ARM: 0.25 x $0.03238 +
  0.5 x $0.00356 = $0.009875 an hour: $0.040 a sitting, $7.21 a month. On the default shape a task
  takes a public address (`assign_public_ip = !var.multi_task`), another $0.005 an hour: $0.020 a
  sitting, $3.65 a month. Together **$0.060 a sitting and $10.86 a month**. Under `multi_task` the
  task has no public address and its egress rides the NAT gateway that shape already pays for. The
  gateway then needs a name for the collector; Cloud Map is $0.10 per resource-month (cents). A
  Route 53 private hosted zone's price was not read.
- **What a second service is besides cost:** a second `aws_ecs_service` breaks plan policy P-01's
  count, which the map already routes through a change request. The sidecar does not.
- The collector image's storage and pull were not priced: pulled from a public registry there is no
  AWS storage charge; a copy in the cell's ECR would be.

## 4. CloudWatch Logs, a subscription filter, X-Ray and EMF metrics

| Item | us-east-2 rate | Source (offer `AmazonCloudWatch` unless named) |
| --- | --- | --- |
| Log ingestion, Standard class | $0.50 per GB | `USE2-DataProcessing-Bytes` |
| Log ingestion, Infrequent Access class | $0.25 per GB | `USE2-DataProcessingIA-Bytes` |
| Log storage | $0.03 per GB-month | `USE2-TimedStorage-ByteHrs` |
| Logs Insights query | $0.005 per GB scanned | `USE2-DataScanned-Bytes` |
| Custom metric (EMF included) | $0.30 per metric-month, first 10,000 | `USE2-CW:MetricMonitorUsage` |
| OpenTelemetry metrics to CloudWatch | $0.50 per GB | `USE2-CW:OTEL:Bytes` |
| X-Ray trace recorded | $0.000005 a trace ($5 per million) | offer `AWSXRay`, `USE2-XRay-TracesStored` |
| X-Ray trace retrieved or scanned | $0.0000005 a trace | offer `AWSXRay`, `USE2-XRay-TracesAccessed` |
| Firehose, Direct PUT | $0.029 per GB, in 5 KB increments | offer `AmazonKinesisFirehose`, `USE2-BilledBytes` |
| Firehose, vended logs as the source | $0.13 per GB | offer `AmazonKinesisFirehose`, `USE2-VendedLogsBilledBytes` |

The offer files also carry the monthly allowances (5 GB of log ingestion, 5 GB of log storage, 10
custom metrics, 100,000 traces recorded, 1,000,000 traces retrieved, 1,000,000 Lambda requests and
400,000 GB-seconds, 20,000 KMS requests). The existing cell draws on the same allowances, so the
table counts none of them.

- **Logs.** The gateway's log group is in the platform root with 14 days' retention
  (`log_retention_days`), so it goes with the destroy. An assumed 100 MB more a sitting (the audit
  lines and the collector's own output): $0.05 to ingest, $0.003 a month to keep.
- **The subscription filter.** No price was found for the filter itself: the us-east-2 offer file
  has no subscription SKU, and <https://aws.amazon.com/cloudwatch/pricing/> (read 2026-11-16, both
  halves) does not mention one. The cost is the destination's.
  <https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/SubscriptionFilters.html> (read
  2026-11-16): "You can use a subscription filter with Amazon Kinesis Data Streams, AWS Lambda,
  Amazon Data Firehose, or Amazon OpenSearch Service."
  - **Lambda:** 10,000 invocations of 0.1 second at 128 MB = 125 GB-seconds: $0.002 plus $0.002 of
    requests. Idle, nothing.
  - **Firehose:** by volume only; <https://aws.amazon.com/firehose/pricing/> (read 2026-11-16)
    names no hourly charge for a stream outside VPC delivery. Which SKU a subscription's data meets
    (Direct PUT or vended logs) was not settled by the pages read, so the table takes the dearer:
    100 MB x $0.13 = $0.013, plus S3 storage. **Paid Plan only.**
  - **Another log group:** not a destination. An audit copy in a second log group would have to be
    written by the Lambda (a second ingestion at $0.50 per GB) or by the collector.
- **X-Ray.** 20,000 traces a sitting is $0.10. <https://aws.amazon.com/xray/pricing/> returned no
  prices to a text fetch (2026-11-16); the CloudWatch pricing page's examples give the same
  $0.000005 and $0.0000005. **A standing risk:** the gateway's tracing has no filter for the health
  path that was found in `PlatformTelemetryExtensions.cs`, so a standing cell with the export on
  records a trace per health check. At an assumed two load balancer nodes and a 30-second interval
  that is about 175,000 traces a month, $0.88. The interval was not read from the provider's
  documentation.
- **EMF.**
  <https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/CloudWatch_Embedded_Metric_Format.html>
  (read 2026-11-16): "Charges are incurred for logs ingestion and archival, and custom metrics that
  are generated", and a high-cardinality dimension "will by design create a custom metric
  corresponding to each unique dimension combination". The pricing page: "All custom metrics and
  Detailed Monitoring charges are prorated by the hour". So one series is $0.30 x 4 / 730 =
  $0.00164 a sitting. The series count is not bounded by the code: the metrics side adds
  `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation` and the `Platform.*` meters, whose
  instruments carry route and status dimensions. At an assumed **200 series: $0.33 a sitting, and
  $60 a month for as long as the gateway keeps emitting**, the largest standing figure of any
  supported stand-in. The same metrics sent as OpenTelemetry metrics are billed at $0.50 per GB
  with, per the pricing page, no charge per series; the same documentation page now recommends that
  route "for most new workloads".

## 5. ALB access logs to S3

| Item | us-east-2 rate | Source |
| --- | --- | --- |
| Delivery | no charge from Elastic Load Balancing | user guide, below |
| S3 Standard storage | $0.023 per GB-month | offer `AmazonS3`, `USE2-TimedStorage-ByteHrs` |
| S3 PUT | $0.005 per 1,000 | offer `AmazonS3`, `USE2-Requests-Tier1` |
| S3 GET | $0.004 per 10,000 | offer `AmazonS3`, `USE2-Requests-Tier2` |

<https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-access-logs.html>
(read 2026-11-16): "You are charged storage costs for Amazon S3, but not charged for the bandwidth
used by Elastic Load Balancing to send log files to Amazon S3." "Elastic Load Balancing publishes a
log file for each load balancer node every 5 minutes", compressed.
<https://docs.aws.amazon.com/elasticloadbalancing/latest/application/enable-access-logging.html>
(read 2026-11-16): the bucket is in the load balancer's Region, and "The only server-side encryption
option that's supported is Amazon S3-managed keys (SSE-S3)".

Two nodes for four hours is at most 96 files; with 200 objects and 10 MB as the bound: $0.001 of
PUTs and $0.0002 a month of storage. The platform bucket already uses `sse_algorithm = "AES256"`
(`storage.tf`), so the encryption rule does not by itself force a second bucket; the bucket policy
and the write-once rule under `apps/` are the design question. Teardown empties the platform bucket,
so logs under it leave with the destroy. The same page notes that load balancers can now also send
logs through CloudWatch Logs delivery (to a log group, Firehose or S3); that route is billed as
vended logs ($0.50 per GB to a log group, $0.25 per GB to S3 or Firehose in the us-east-2 offer
file) and was not costed further.

## 6. GitHub Actions and AWS's side of OIDC

**The ticket says "this private repository". It is public** (`gh repo view`: `visibility: PUBLIC`,
owner a user account on the `free` plan, read 2026-11-16).

| Item | Rate | Source (read 2026-11-16) |
| --- | --- | --- |
| Standard runners, public repository | free | <https://docs.github.com/en/billing/concepts/product-billing/github-actions> |
| Included for private repositories, GitHub Free | $12 of compute a month, 500 MB of artifacts | same page: the included usage "is expressed as a monetary amount, not a fixed number of minutes" |
| Included, Pro / Team / Enterprise Cloud | $18 / $18 / $300 a month | same page |
| Linux 2-core x64 / arm64 | $0.006 / $0.005 a minute | <https://docs.github.com/en/billing/reference/actions-runner-pricing> |
| Linux 1-core, Windows 2-core, macOS | $0.002, $0.010, $0.062 a minute | same page |
| Rounding | "GitHub rounds the minutes and partial minutes each job uses up to the nearest whole minute." | same page |
| Past the included amount | blocked with no payment method on file; billed, and capped by a budget, with one | billing page |
| Required reviewers | "If you are on a GitHub Free, GitHub Pro, or GitHub Team plan, required reviewers are only available for public repositories." | <https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments> |
| Environment secrets | on GitHub Free, public repositories only; private needs Pro, Team or Enterprise | same page |

- **As it stands (public):** a deployment workflow costs $0 and environments with required reviewers
  are available on the Free plan. "You can list up to six users or teams as reviewers", and
  self-review can be prevented, which with one maintainer leaves nobody to approve; that is the
  trust-policy and approvals tickets' matter.
- **If the repository were private:** required reviewers need GitHub Enterprise, so the approval
  stand-in would fall to paper evidence or to a plan change. A 30-minute Linux deployment is $0.18
  against $12 included; `ci.yml`'s `macos-latest` job would bill at $0.062 a minute.
- **AWS's side.** "AWS Identity and Access Management (IAM), AWS IAM Identity Center and AWS
  Security Token Service (AWS STS) are features of your AWS account offered at no additional
  charge" (<https://docs.aws.amazon.com/IAM/latest/UserGuide/introduction.html>, read 2026-11-16).
  <https://aws.amazon.com/iam/pricing/> gave nothing to a text fetch. So an OIDC provider and
  `AssumeRoleWithWebIdentity` cost nothing; **the plan's policy forbids creating the provider**
  (section 1). What a workflow's role may then create is what costs, and the spend-limit policy is
  the only brake found.

## 7. AWS Private CA, as the figure the map's Out of scope cites

| Item | us-east-2 rate | Source: offer `AWSCertificateManager` |
| --- | --- | --- |
| A CA, general-purpose mode | $400 a month | `USE2-PaidPrivateCA` |
| A CA, short-lived certificate mode | $50 a month | `USE2-ShortLivedCertificatePrivateCA` |
| A certificate, general-purpose | $0.75 each for the first 1,000 a month | `USE2-PrivateCertificatesIssued` |
| A certificate, short-lived | $0.058 each | `USE2-ShortLivedCertificatesIssued` |

<https://aws.amazon.com/private-ca/pricing/> (read 2026-11-16): "Private CA operation is pro-rated
for partial months based on when you create and delete the CA"; "if you restore a deleted CA, you
are charged for the time between deleting it and restoring it"; "Any AWS account can try AWS Private
CA with no CA operation charge for the first 30 days", for the first CA in a Region, certificates
still billed.

Prorated to four hours: $400 x 4 / 730 = **$2.19** general-purpose, $50 x 4 / 730 = **$0.27**
short-lived, plus certificates. So a CA created and deleted inside one sitting would fit the sitting
ceiling; what rules it out is that a forgotten one is $50 or $400 a month, and that **AWS Private
Certificate Authority is on the "not supported for our new AWS experience" list**: neither plan's
allow-list has `acm-pca`. The `certs.sh` authority stays the stand-in on both counts.

## 8. KMS keys any of the above forces

| Item | us-east-2 rate | Source |
| --- | --- | --- |
| A customer managed key | $1 a month | offer `awskms`, `us-east-2-KMS-Keys` |
| Requests | $0.03 per 10,000 | offer `awskms`, `us-east-2-KMS-Requests` |

<https://aws.amazon.com/kms/pricing/> (read 2026-11-16): a key "costs $1/month (prorated hourly)";
"There is no charge for customer managed KMS keys that you manage and are scheduled for deletion";
no charge for "Creation and storage of AWS managed or AWS owned KMS keys", though "you will be
charged on any API request made to AWS-managed keys"; the first two rotations of a key each add
$1 a month.

**None is forced.** Secrets Manager's default key is free (section 2). ALB access logs cannot use
one (section 5). No page read requires one for a log group, a trace, a Lambda function or a
Firehose stream. If the spec chooses one, for the secrets or the log group: $0.006 a sitting, $1 a
month standing, nothing once scheduled for deletion, and 1,000 requests are $0.003. The task role
would need an explicit `kms:Decrypt`, as the comment in `iam.tf` already says of the key ring's.

## 9. What was executed and what was only read

**Executed (2026-11-16), none of it against AWS credentials or the cell:**

- `git fetch`, `git worktree add`, and reads of the repository at `adf0273`.
- `gh issue view` for the ticket and its map; `gh repo view --json visibility,isPrivate,owner` and
  `gh api user --jq .plan.name` (public; `free`).
- `curl` of the public price-list offer files for `us-east-2`, parsed with `python3 -I`. Offer,
  version, publication date:
  `AWSSecretsManager` 20261111195606 (2026-11-11); `awskms` 20261108172342 (2026-11-08);
  `AWSLambda` 20261111223004 (2026-11-11); `AmazonECS` 20261111234623 (2026-11-11);
  `AmazonCloudWatch` 20261116170738 (2026-11-16); `AWSXRay` 20261108172324 (2026-11-08);
  `AmazonKinesisFirehose` 20261104213442 (2026-11-04); `AmazonS3` 20261116071300 (2026-11-16);
  `AmazonVPC` 20261105181945 (2026-11-05); `AmazonEC2` 20261114012926 (2026-11-14, read for the NAT
  gateway rate only, $0.045 an hour and $0.045 per GB, which no stand-in adds);
  `AWSELB` 20261111153621 (2026-11-11); `AWSCertificateManager` 20261113195136 (2026-11-13);
  `AWSCloudMap` 20261106193843 (2026-11-06).
- `curl` of the ADOT example task definition named in section 3.
- A Python read of the resource types in `infra/bootstrap/terraform.tfstate` (a local, untracked
  file; no value from it or from the untracked `terraform.tfvars` appears here beyond the two
  inputs named in section 1).

**Only read, with a text fetch (all 2026-11-16):**

- AWS price pages, for rules and us-east-1 examples: Secrets Manager, KMS, CloudWatch (both halves),
  Fargate, Private CA, Firehose, Lambda. **Returned no price content:** the X-Ray and IAM pricing
  pages. **Not fetched:** the S3, VPC, Elastic Load Balancing and Cloud Map price pages; their rates
  are from the offer files alone.
- AWS documentation: the supported-services page; the managed SCP and RCP page; "Choosing a plan";
  the Secrets Manager user guide (introduction, managed rotation, managed external secrets and its
  partner list, deleting a secret) and FAQ; "Access logs for your Application Load Balancer" and
  "Enable access logs"; "Log group-level subscription filters"; "Embedding metrics within logs";
  "What is IAM?".
- GitHub documentation: Actions billing, the runner pricing reference, deployments and environments.

**Not verified, and why:**

- The project's plan type, its credit balance and whether a spend limit is set: a credentialed call
  or AWS Settings.
- That the plan policy in fact refuses `iam:CreateOpenIDConnectProvider`: read from the published
  policy, not attempted.
- Whether the collector fits the present 0.25 vCPU / 1 GB task, and every volume (log megabytes,
  trace counts, metric series, API calls): assumptions, each stated where used, none measured.
- Whether a subscription's data to Firehose meets the Direct PUT or the vended-logs rate; whether a
  secret's name is reusable inside its recovery window; the health check's interval; a Route 53
  private hosted zone's price.
- The GitHub Free figure of "$12" of included compute is as the billing page read on 2026-11-16; it
  does not bear on a public repository.
