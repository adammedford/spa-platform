# What a second cell costs, and which quotas two cells meet

- **Ticket:** #1238, "What a second cell costs per sitting-hour and per idle month, and which project
  quotas two cells meet" (`wayfinder:research`), on the map #1237, "Wayfinder: P2-8 security cells, a
  second cell and a compromise drill that reads nothing across".
- **Date:** 2026-11-16.
- **Branch:** `research/p2-8-second-cell-cost`, pushed and never merged, as the map rules.
- **Method:** docs-only and repository-only, as the ticket and the map require. Three kinds of source,
  all read on 2026-11-16:
  - This repository at `origin/main` `adf0273`: `infra/modules/cell`, `infra/envs/poc/{platform,apps,idp}`,
    `infra/modules/idp-cognito`, `infra/bootstrap`, `tools/scripts/teardown.sh`,
    `tools/scripts/verify-teardown.sh`, `tools/aws-project.env`, `docs/agents/aws-rules.md`,
    `docs/plan/05-infrastructure-and-cost.md`.
  - **Prices:** the AWS Price List bulk files for US East (Ohio), `us-east-2`, the project's selected
    Region (`tools/aws-project.env`). They are public JSON, downloaded with `curl` and filtered with a
    local script. No account or credential is involved.
  - **Quotas, plan and billing rules:** AWS documentation pages and four AWS pricing pages.
  - **Not done:** no `terraform` command, no AWS CLI call, no credential step, no read of the deployed
    cell or of the project's applied quotas, plan or spend limit. The `aws-mcp` connector failed to
    connect. Section 5 separates what was executed from what was read.

Two words used throughout. The **default shape** is the cell on the module's default inputs (Phase 1's:
one task with a public address, no NAT gateway, no table). The **two-task shape** is the cell under
`multi_task = true` with `session_store = "dynamodb"` (G6's: two tasks in private subnets, one NAT
gateway). The module has no other shape today.

## Answer in brief

1. **A second cell costs $0.039 an hour in the default shape and $0.096 an hour in the two-task shape**,
   at list price in `us-east-2`, before per-use charges. Left standing for a 730-hour month that is
   **$28.58** and **$69.94**. The two-task figure agrees with the module README's "about $70.50 a
   month, $0.10 an hour", which adds an allowance for logs and storage.
2. **Four lines bill per hour of existence, and they are the whole hourly figure**: the load balancer
   ($0.0225), each task ($0.011655), each public IPv4 address ($0.005) and the NAT gateway ($0.045).
   The NAT gateway and its address are 52% of the two-task cell. Everything else is per use or per GB
   stored and is cents at a sitting's volume.
3. **The gated input, left off, saves every one of those hourly lines.** The apps and idp roots add
   nothing per hour: a user pool, its clients, standard parameters and an imported certificate carry
   no standing charge.
4. **A destroyed cell costs nothing measurable per idle month.** `verify-teardown.sh` passes only when
   nothing carrying the platform tag remains. What could survive is outside the cell, if the second
   cell gets its own: state objects in the state bucket (bytes, at $0.023 per GB-month) and a
   teardown role in bootstrap (IAM carries no charge).
5. **The load balancer and the NAT gateway bill a partial hour as a full hour.** Tasks and public
   addresses bill by the second with a one-minute minimum. So a short sitting pays whole hours for
   $0.0675 of the two-task cell's $0.096, and an apply, destroy and re-apply inside one hour may pay
   them twice.
6. **No default quota is close for two cells.** The tightest are VPCs, internet gateways and Elastic
   IP addresses, five each per Region, against two used (three VPCs if the project still has a default
   VPC), and Fargate's six On-Demand vCPUs against at most two. These are documented defaults; the
   project's applied values were not read.
7. **Every service the cell uses is on the Free Tier list of the new AWS experience**, so both plans
   support it, and `servicequotas:*` is allowed on both, so an increase can be requested.
8. **The limit that can stop a second cell is the spend limit, not a quota.** Its optional "stop new
   resource creation" control applies a managed policy, about seven days before the limit would be
   reached, that denies the create calls a cell's apply makes: the NAT gateway, the load balancer, the
   ECS service, the table, the user pool, the repository and a KMS key. Reaching the limit pauses the
   project and stops its resources.
9. **Which plan the project is on, and whether it has a spend limit, is recorded neither in the
   repository nor on the tracker.** The two-task grilling's question 10 asks about "the limit set in
   AWS Settings > Billing" and was pre-accepted under a standing answer, so nobody read a limit to
   answer it. The P2-7 pricing research of 2026-11-16 did not read the plan either.
10. **The module declares no KMS key, no interface endpoint and no access logs**, so three of the
    ticket's named lines cost a second cell nothing today. A customer managed key would add $1 a month,
    prorated hourly. Section 1 prices each as "if added".

## 1. The price, resource by resource

### What one cell declares

Read from the Terraform, not from the plan's prose.

| Root | Resource | Default shape | Two-task shape |
| --- | --- | --- | --- |
| platform | VPC, internet gateway, public subnets, route table | 1, 1, 2, 1 | the same |
| platform | Private subnets and route tables | 0 | 2 and 2 |
| platform | NAT gateway and its Elastic IP | 0 | 1 and 1 |
| platform | Gateway endpoints (S3, DynamoDB) | 0 | 2 |
| platform | Security groups | 2, and the VPC's default one adopted | the same |
| platform | Internal Application Load Balancer, listener, target group | 1, 1, 1 | the same |
| platform | ECS cluster, service, task definition | 1, 1, 1 | the same |
| platform | Fargate tasks, ARM64, 0.25 vCPU and 1 GB | 1, with a public address | 2, with none |
| platform | CloudWatch log group, 14-day retention by default | 1 | 1 |
| platform | DynamoDB table, on-demand, one keys-only index | 0 | 1 |
| platform | S3 bucket, versioned, SSE-S3 | 1 | 1 |
| platform | ECR repositories (`gateway`, `mock-api`) | 2 | 2 |
| platform | IAM roles (task, task execution) | 2 | 2 |
| idp | Cognito user pool (Essentials), prefix domain, resource servers, test users | 1 pool | the same |
| idp | Standard SecureString parameter per test user | 1 each | the same |
| apps | Cognito client, branding style, standard SecureString parameter | 1 of each per app | the same |
| outside Terraform | Imported ACM certificate (`certs.sh`); key ring parameters the gateway writes | 1; a few | the same |

Three things the ticket names are **not in the module**:

- **A KMS key.** `data_protection_kms_key_id` is an input, "empty at G6", and no root creates a key.
  The parameters use the account's `aws/ssm` key, the table DynamoDB's default encryption, the bucket
  SSE-S3 and the repositories ECR's default.
- **An interface endpoint.** The two endpoints are gateway endpoints.
- **Access logs.** `alb.tf` has no `access_logs` block.

The plan's "Phase 2 cost drivers" table sizes a cell's tasks at 0.5 vCPU each. `ecs.tf` declares
`cpu = "256"` in both shapes, and this finding prices the Terraform.

### Billed per hour of existence

`us-east-2` list prices from the bulk price files. The billing increment is in the last column.

| Line | Unit price | Default shape, per hour | Two-task shape, per hour | Increment |
| --- | --- | --- | --- | --- |
| Application Load Balancer | $0.0225 per hour | $0.0225 | $0.0225 | "per Application LoadBalancer-hour (or partial hour)" (price file) |
| Fargate task, ARM, 0.25 vCPU and 1 GB | $0.03238 per vCPU-hour, $0.00356 per GB-hour | $0.011655 | $0.023310 | Per second, one-minute minimum (Fargate pricing page) |
| Public IPv4 address, in use | $0.005 per hour | $0.005, the task's | $0.005, the NAT gateway's Elastic IP | One-second increments, 60-second minimum (VPC pricing page) |
| NAT gateway | $0.045 per hour | none | $0.045 | "Each partial NAT Gateway-hour consumed is billed as a full hour" (VPC pricing page) |
| **Total** | | **$0.039155** | **$0.095810** | |
| **730 hours** | | **$28.58** | **$69.94** | |

- The load balancer is internal, so it holds no public address and adds no IPv4 charge.
- The task's 20 GB of ephemeral storage is included; the module configures no more.
- The arithmetic for the two-task month: 16.43 + 17.02 + 32.85 + 3.65 = 69.94. Question 10 of the
  two-task grilling (2026-10-24) reached the same four figures from the plan's `us-east-1` table, and
  the README's $70.50 adds "logs and storage under 1.50". The `us-east-2` rates equal the ones the
  plan quotes for `us-east-1`.
- Whether a partial hour is counted from creation or by the clock hour is **unsourced**. Read the
  increment as "a sitting of 3 hours 10 minutes pays 4 hours of load balancer and NAT gateway".

**A sitting.** Four hours of a two-task second cell is about $0.38 plus per-use charges; eight hours
about $0.77. The default shape is $0.16 and $0.31.

### Billed per use or per GB stored

Unit rates only. No volume is estimated here, because nothing in the repository measures one.

| Line | Unit price in `us-east-2` | Billed by |
| --- | --- | --- |
| Load balancer capacity units | $0.008 per LCU-hour (or partial hour) | Use |
| NAT gateway data | $0.045 per GB processed | Use. Carries what the README lists: "Parameter Store, ECR, CloudWatch Logs, `ssmmessages` for the tunnel and the provider's OAuth endpoints"; S3 and DynamoDB go by the gateway endpoints, which carry "no additional charge" |
| CloudWatch Logs | $0.50 per GB ingested, $0.03 per GB-month stored | Use, then storage until retention expires or the group is destroyed |
| DynamoDB on-demand | $0.125 per million read units, $0.625 per million write units; storage free for the first 25 GB-months, then $0.25 | Use. An idle table costs nothing per hour |
| S3 Standard | $0.023 per GB-month; $0.005 per 1,000 PUT/LIST, $0.004 per 10,000 GET | Storage and use |
| ECR | $0.10 per GB-month | Storage, while images exist |
| Cognito, Essentials | $0.015 per monthly active user above a free 10,000 | Use. The free 10,000 is "per account or per AWS organization", so two pools share it, not double it |
| Parameter Store, standard tier | "No additional charge" at standard throughput | Nothing |
| ACM, imported certificate | No line in the ACM price file except private CA and OCSP | Nothing |
| IAM roles, ECS cluster, VPC, subnets, route tables, security groups, internet gateway | No price-file line found | Nothing |

- **Cognito machine-to-machine tokens** are priced at $0.00225 each with no free tier. The apps
  root's clients allow the `code` flow alone (`allowed_oauth_flows = ["code"]`), and the only
  `client_credentials` request in the tree is a fixture test against the test identity provider, so
  the line is zero today. A drill that tried one cell's client secret against another's clients by
  that grant should be refused by the client's flow list, and Cognito charges "per successful token
  response", so a refused attempt should cost nothing. Neither half was run.
- **The key ring's parameters** are written by the gateway at run time. Which tier it writes them at
  was not found in `src/`, so "standard, no charge" is **unsourced** for them.
- **Data transfer** out to the internet and across zones (the second zone's task to the NAT gateway)
  is **unsourced**: the rates were not looked up.
- **Free allowances** beyond those in the table (CloudWatch's 5 GB, KMS's 20,000 requests) appear in
  the price files as global bundles. Whether they apply under the new AWS experience's plans is
  **unsourced**.

### If added: what the module does not declare today

| Addition | Price in `us-east-2` | Notes |
| --- | --- | --- |
| Customer managed KMS key | $1 per key per month, "prorated hourly", about $0.0014 an hour; $0.03 per 10,000 requests | Each of the first two rotations adds $1 a month. A key scheduled for deletion is not charged |
| Interface VPC endpoint | $0.01 per endpoint-hour in each zone, $0.01 per GB | "Billed for each hour that your VPC endpoint remains provisioned in each Availability Zone" (PrivateLink pricing page). The five services the NAT gateway carries, in two zones, would be 10 endpoint-hours, $0.10 an hour, against the NAT gateway's $0.05 |
| NAT gateway per zone, or a regional one | $0.045 per hour each; regional: $0.045 per hour "in each availability zone" | A second zone doubles the NAT line |
| ALB access logs | S3 storage at the rate above | Delivery charge **unsourced** |

## 2. Per hour of existence or per use: what the gated input saves

- **Saved by leaving the gate off, or by destroying:** the whole hourly figure, $0.039 or $0.096 an
  hour, because every hourly line belongs to the platform root's cell module.
- **Not saved by scaling the service to zero:** the load balancer, and in the two-task shape the NAT
  gateway and its address. Scaling to zero saves $0.0117 an hour per task and, in the default shape,
  the task's address.
- **Costing nothing while they exist, so not worth gating on cost:** the idp and apps roots' pool,
  clients, branding styles and parameters; the imported certificate; the IAM roles; the empty table;
  the VPC and its subnets.
- **Costing by what they hold:** the bucket, the repositories and the log group. Destroy removes all
  three (`force_delete` on the repositories; teardown empties the bucket first).

### The idle month

After `teardown.sh` for the second cell, by the script's own contract. The first two rows hold only
if the second cell gets its own state keys and its own teardown role, which the tickets on the
second cell's shape and on what cells share decide:

| Survivor | Where | Price |
| --- | --- | --- |
| State objects and their noncurrent versions, one per root | The state bucket, which bootstrap owns | $0.023 per GB-month on kilobytes: below a cent |
| A teardown role, named from the cell | `infra/bootstrap` | No charge |
| Anything of the cell | Nowhere: `verify-teardown.sh` fails if one tagged resource stands | $0 |

- The user pool has `deletion_protection = "INACTIVE"`, the repositories `force_delete = true`, and
  teardown deletes the certificate, the inactive task definitions and the key ring.
- A customer managed key, if one is added, cannot be deleted at once. KMS requires "a waiting period
  of 7 – 30 days", and there is "no charge for customer managed KMS keys that ... are scheduled for
  deletion". The key still exists for that time, so a reader that lists tagged resources would have
  to allow for it.
- **An idle month that is not destroyed** is the 730-hour figure: $28.58 or $69.94.

## 3. Quotas and plan limits two cells meet

### Service quotas

Documented defaults. **The project's applied values were not read**, and Fargate's page warns that
"new AWS accounts might have initial lower quotas".

| Quota | Default | Two cells use | Adjustable |
| --- | --- | --- | --- |
| VPCs per Region | 5 | 2, or 3 with a default VPC | Yes |
| Internet gateways per Region | 5 | 2, or 3 | With the VPC quota |
| Elastic IP addresses per Region | 5 | 2 in the two-task shape, 0 in the default | Yes |
| NAT gateways per Availability Zone | 5 | 2, both in the first zone by name | Yes |
| Subnets per VPC | 200 | 4 per VPC | Yes |
| Security groups per Region | 2,500 | 6 | Yes |
| Application Load Balancers per Region | 50 | 2 | Yes |
| Target groups per Region | 3,000 | 2 | Yes |
| Fargate On-Demand vCPU | 6 | 1 running; 2 while both cells deploy at once (`deployment_maximum_percent = 200`) | Yes |
| ECS clusters per account | 10,000 | 2 | Yes |
| KMS customer managed keys | 100,000 | 0 today | Yes |
| Cognito user pools per Region | 1,000 | 1 or 2 | Yes |
| Cognito app clients per pool | 1,000 | one per app | Yes |
| Cognito resource servers per pool | 100 | the fixture's | Yes |
| Cognito custom domains per Region | 4 | 0: the pool uses a prefix domain | No |
| DynamoDB tables per Region | 2,500 | 2 | Yes |
| ECR repositories per Region | 100,000 | 4 | Yes |
| Parameter Store standard parameters | 10,000 | tens | Not read |
| S3 general purpose buckets | 10,000 | 2, and the state bucket | Yes |
| ACM imported certificates | 2,500, and 5,000 imports in 365 days | 2; each `certs.sh` run is one import | Not read |
| CloudWatch log groups per Region | 1,000,000 | 2 | Yes |
| IAM roles per account | **unsourced** | 4, and bootstrap's | **unsourced** |

The nearest quotas are the five-per-Region ones. Two cells fit under all of them; a third cell in the
two-task shape still fits. The second cell's NAT gateway lands in the same zone as the first's, which
matters to nothing but the per-zone quota.

### The plan

- **Supported services.** Every service the three roots and the scripts use is on the Free Tier list
  of the new AWS experience: VPC, EC2, Elastic Load Balancing, ECS, ECR, CloudWatch Logs, DynamoDB,
  S3, Cognito, KMS, Certificate Manager, Systems Manager, IAM, STS, PrivateLink, Resource Groups and
  Service Quotas. The Free Tier policy's allow-list carries `ec2:*`, `elasticloadbalancing:*`,
  `ecs:*`, `ecr:*`, `dynamodb:*`, `kms:*`, `cognito-idp:*`, `logs:*`, `s3:*`, `ssm:*`,
  `ssmmessages:*`, `acm:*`, `iam:*`, `tag:*` and `servicequotas:*`. The Paid Plan adds services the
  cell does not use.
- **Neither plan's page states a quota of its own** for VPCs, addresses, NAT gateways, load balancers,
  keys or pools. The standard service quotas are the only numbers found.
- **The Free Tier ends.** It gives "USD $100 sign up credit" and up to $100 more, and "when you either
  run out of credits or have an account that is over 6 months, you must upgrade to a Paid Plan".
- **A spend limit needs the Paid Plan**, is per project, has a minimum of "the greater of $20 or a
  conservative estimate of your likely spend", and that estimate rises with what is running. It
  "exclude[s] credits".
- **What the spend limit does to a second cell:**
  - Optional, "about 7 days before you would reach your limit, new resources stop launching", by a
    managed policy. That policy denies, among others, `ec2:CreateNatGateway`,
    `elasticloadbalancing:CreateLoadBalancer`, `ecs:CreateService`, `dynamodb:CreateTable`,
    `kms:CreateKey`, `cognito-idp:CreateUserPool` and `ecr:CreateRepository`. An apply of a second
    cell under it fails part way, with the VPC and bucket created and the rest refused.
  - At the limit, "AWS pauses your project and stops all resources", and after 90 days paused the
    project's data is deleted.
  - The policy does not deny `ec2:AllocateAddress`, `ec2:CreateVpc`, `s3:CreateBucket`,
    `ecs:UpdateService` or `ecs:RunTask`.
  - The page lists the spend-limit policies together and does not label which one each control
    applies. Pairing the create-denying policy with "stop new resource creation" is this finding's
    reading of the two pages, not a quotation.
- **Which plan and what limit this project has: recorded neither in the repository nor on the
  tracker.** No file names the plan. Question 10 of the two-task grilling asks whether $70.50 a month
  "is within the limit set in AWS Settings > Billing"; its answer is "yes, pre-accepted under the
  standing answer granted 2026-10-24", so it records an acceptance, not a reading of a limit. The
  P2-7 map records that "the project's plan, spend limit and advanced-features state were not read".
  Whether a limit exists, its amount and its early controls are all unread.
- **The create denials would meet the first cell too**: every sitting applies the platform root from
  nothing, and that apply makes the same calls.

## 4. What this changes for the map

- **The ceiling can be set from two numbers:** $0.096 an hour standing, two-task shape, and whole
  hours for the load balancer and NAT gateway. Both cells standing through a sitting is about $0.19
  an hour.
- **The ceiling has to be read against the spend limit, if one is set**: this finding could read
  neither its existence nor its amount. Two cells left running for a month are about $140 at list
  price.
- **The P2-7 map's ceiling**, $5 added per four-hour sitting and $2 a month idle, is the borrowed
  rule's precedent. A two-task second cell is about $0.38 per four-hour sitting and below a cent idle.
- **Facts met on the way**, for the tickets that own them:
  - The three roots fix their state keys in `versions.tf` at `poc/platform`, `poc/apps` and `poc/idp`.
  - The teardown role is one per cell, in bootstrap.
  - The cost model in `docs/plan/05-infrastructure-and-cost.md` is priced in `us-east-1`, says its
    Fargate and Logs rates are third-party or from memory, and sizes Phase 2 tasks at 0.5 vCPU where
    the module declares 0.25. Its figures match `us-east-2`'s list prices; its sourcing note is now
    out of date.

## 5. What was executed, and what was only read

### Executed (2026-11-16)

- `curl` of the AWS Price List bulk index and the current `us-east-2` offer file for each of
  AmazonECS, AWSELB, AmazonVPC, AmazonEC2, AmazonDynamoDB, awskms, AmazonCloudWatch, AmazonECR,
  AmazonCognito, AWSSystemsManager, AmazonS3 and AWSCertificateManager, under
  `https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/`. The index's publication date was
  2026-11-16T17:10:36Z. A local Python script filtered each file by usage type. Every price in
  sections 1 and 2 comes from these files unless a page is named beside it.
- `grep` and `sed` over the repository at `origin/main` `adf0273`.
- `gh issue view` of the ticket, the map, the P2-7 map and the two-task grilling's question 10, and a
  search of the tracker for the plan and the spend limit.
- `curl` of the five pricing pages and the KMS deletion page named below, searched as text.
- Nothing else. No Terraform, no AWS CLI, no credential, no container.

### Read only (each on 2026-11-16)

Documentation pages, fetched as text and quoted from the page:

- [Supported AWS services for Sign up for AWS (new)](https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html)
- [Managed policies for your organization](https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html): the Free Tier, Paid Plan and spend-limit policies
- [Create a spend limit in AWS Settings](https://docs.aws.amazon.com/accounts/latest/reference/create-spend-limit.html)
- [Upgrade your account in AWS Settings](https://docs.aws.amazon.com/accounts/latest/reference/upgrade-account.html)
- [Amazon VPC quotas](https://docs.aws.amazon.com/vpc/latest/userguide/amazon-vpc-limits.html)
- [Quotas for your Application Load Balancers](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-limits.html)
- [Amazon ECS endpoints and quotas](https://docs.aws.amazon.com/general/latest/gr/ecs-service.html), with the Fargate quotas
- [AWS KMS resource quotas](https://docs.aws.amazon.com/kms/latest/developerguide/resource-limits.html)
- [Quotas in Amazon Cognito](https://docs.aws.amazon.com/cognito/latest/developerguide/quotas.html)
- [DynamoDB quotas](https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/ServiceQuotas.html)
- [Amazon ECR service quotas](https://docs.aws.amazon.com/AmazonECR/latest/userguide/service-quotas.html)
- [Systems Manager endpoints and quotas](https://docs.aws.amazon.com/general/latest/gr/ssm.html) and [Parameter tiers](https://docs.aws.amazon.com/systems-manager/latest/userguide/parameter-store-advanced-parameters.html)
- [Amazon S3 endpoints and quotas](https://docs.aws.amazon.com/general/latest/gr/s3.html)
- [ACM quotas](https://docs.aws.amazon.com/acm/latest/userguide/acm-limits.html)
- [CloudWatch Logs quotas](https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/cloudwatch_limits_cwl.html)
- [Gateway endpoints](https://docs.aws.amazon.com/vpc/latest/privatelink/gateway-endpoints.html), [Pricing for NAT gateways](https://docs.aws.amazon.com/vpc/latest/userguide/nat-gateway-pricing.html) and [IP addressing for your VPCs](https://docs.aws.amazon.com/vpc/latest/userguide/vpc-ip-addressing.html)

Pricing pages. Each was first read through the fetch tool's summarising model and then fetched again
with `curl` and searched as plain text; the quotations in this finding are from the second read:

- [Amazon VPC pricing](https://aws.amazon.com/vpc/pricing/): the NAT gateway's partial hour; the public address's one-second increment.
- [AWS Fargate pricing](https://aws.amazon.com/fargate/pricing/): per second, one-minute minimum; 20 GB of ephemeral storage.
- [AWS KMS pricing](https://aws.amazon.com/kms/pricing/): "prorated hourly"; no charge while scheduled for deletion; the rotation charge.
- [Amazon Cognito pricing](https://aws.amazon.com/cognito/pricing/): the free tier "per account or per AWS organization"; machine-to-machine tokens.
- [AWS PrivateLink pricing](https://aws.amazon.com/privatelink/pricing/): an interface endpoint's hour is per Availability Zone.
- [Deleting AWS KMS keys](https://docs.aws.amazon.com/kms/latest/developerguide/deleting-keys.html): the waiting period.

### Unsourced, named so that nobody reads them as findings

- The project's plan, whether it has a spend limit, the amount and the early controls.
- The project's applied quotas, and whether a default VPC exists.
- Data transfer rates, out and across zones.
- Whether a partial hour runs from creation or by the clock.
- The tier of the key ring's parameters.
- The IAM roles quota.
- Whether the price files' free bundles apply under this experience's plans.
- Every volume: gigabytes through the NAT gateway, log volume, image size, request counts.
