# Load generators and what a load-test sitting costs per hour and per idle month

- **Ticket:** #1254, "Load generators and what a load-test sitting costs per hour and per idle
  month" (`wayfinder:research`), on the map #1251.
- **Date:** 2026-11-16. **Branch:** `research/load-generators-and-sitting-cost`, pushed and never
  merged, as the map rules.
- **Method:** docs-only. Repository reading at commit `adf0273` (origin/main on 2026-11-16):
  `tools/bench/startup.py`, `tools/bench/results/README.md`, `docs/dependencies.md`,
  `docs/plan/05-infrastructure-and-cost.md`, `infra/modules/cell` (`network.tf`, `ecs.tf`,
  `sessions.tf`, `storage.tf`, `variables.tf`), `tools/aws-project.env` (the Region: `us-east-2`),
  `contracts/http-contract.md`, `contracts/upstream-contract.md`, `src/Platform.Identity`
  (`DynamoDbSessionStore.cs`, `DynamoDbSessionTable.cs`, `CookieOptionsSetup.cs`),
  `src/Platform.Hosting/Assets.cs`, `src/Platform.Registry/S3ObjectStore.cs`,
  `src/Platform.Telemetry/RequestTelemetry.cs`, and two earlier findings on their branches
  (`research/p2-7-stand-in-costs`, `research/local-aws-stand-in`). **Tools:** each tool's licence
  file and README were downloaded from its own repository with `curl` and searched with `grep`;
  documentation pages were read by web fetch. **Prices:** every rate is the US East (Ohio),
  `us-east-2`, rate from AWS's public price-list offer files
  (`https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/<offer>/current/us-east-2/index.json`,
  and `index.csv` for EC2), downloaded with no credential on 2026-11-16 and parsed with
  `python3 -I`. The ticket asks for the price pages: as the stand-in cost finding records, their
  Region tables do not render to a text fetch, so the pages gave the billing **rules** and the offer
  files gave the Ohio figures. Section 6 lists each file with its version. **Not done:** no load was
  generated against anything, so nothing here is a measurement; no `terraform` run, no credential
  step, no read of the cell; the `aws-mcp` connector failed to connect. Two images were pulled with
  Docker to confirm that they exist and run their help text (section 6). Every volume is an
  assumption, stated where it is used. Arithmetic uses a 730-hour month, as
  `docs/plan/05-infrastructure-and-cost.md` does.

## Answer in brief

1. **k6 is the one candidate that meets every technical point**: an open-model constant arrival
   rate, a cookie jar per virtual user, a script that can read `csrfToken` from `GET /bff/session`
   and send `X-CSRF-TOKEN`, a `Host` header it sets itself, a DNS override, and a published
   `linux/arm64` image. Its licence is **AGPL-3.0**, which `docs/dependencies.md` has no precedent
   for: that is a decision to record, not a refusal found.
2. **The permissive alternatives replay sessions, they do not make them.** Vegeta (MIT) holds a
   constant rate and takes headers per target, so it can replay many pre-minted session cookies
   across many hosts. oha (MIT) holds a rate and corrects latency with `--latency-correction`, but
   sends one header set for the whole run. wrk2 (Apache-2.0) is the reference for coordinated
   omission and has had no push since 2024-03-03 and no release.
3. **Two are ruled out by licence or by the plan.** NBomber is "not free for organizational use".
   AWS's Distributed Load Testing solution is Apache-2.0 and its services are on the project plan's
   list, but it is a CloudFormation stack outside the platform root, with a web console, Cognito,
   API Gateway, Step Functions and a $3.00 dashboard per scenario that outlives the test: it breaks
   the map's cost rule. Locust (MIT) is a closed model: its wait time "can only constrain the
   throughput".
4. **A sitting-hour of the whole two-task cell with a generator costs $0.17 before traffic**:
   generator at 2 vCPU and 4 GB on ARM $0.079, two gateway tasks $0.023, NAT gateway $0.045, its
   address $0.005, load balancer $0.0225. The S3 and DynamoDB gateway endpoints are free.
5. **Traffic costs about $1.08 per million asset requests**, on stated assumptions: S3 GET $0.40,
   the request log line $0.25 to ingest and $0.02 through the NAT, the DynamoDB session read $0.25,
   load balancer capacity $0.16. A loaded hour is $0.39 at 100 requests a second and $3.90 at 1,000.
6. **A four-hour sitting is $1.53 at 100 requests a second and $8.54 at 1,000**, with two of the
   four hours under load. The fixed part is $0.70; the rest is request volume, which nothing
   measured yet bounds. The rate the two 0.25 vCPU tasks can sustain is the load test's own first
   result.
7. **Left standing for a month the cell is $69.95, and $127.62 if the generator is left running.
   Destroyed, it is cents**: what remains is the bootstrap state bucket and whatever the roots other
   than the platform root hold. The generator must be a task that stops, inside the gate and the
   destroy.
8. **The local tier measures the stand-ins as much as the gateway.** `startup.py` today starts the
   gateway, moto and a discovery document; a load run adds the mock API, a way to make sessions and
   the generator, all sharing one Docker VM. Its numbers are for comparison between commits; the
   map already holds the budget to the deployed numbers.

## The table

us-east-2, list price, no free tier counted. **The shape priced** is the cell under
`multi_task = true` (section 3). "Per sitting-hour" is one hour with the generator running; the
volume lines show one hour under load at 100 and at 1,000 requests a second. "Per idle month" is the
line left standing for 730 hours with no user traffic. The four-hour column assumes four hours
standing, the generator running throughout, and two hours under load.

| Line | Per sitting-hour | Per idle month | Bills while destroyed | Four-hour sitting |
| --- | --- | --- | --- | --- |
| Generator task, 2 vCPU / 4 GB, ARM | $0.0790 | $57.67 if left running; $0 as a task that stops | No | $0.32 |
| Two gateway tasks, 0.25 vCPU / 1 GB, ARM | $0.0233 | $17.02 | No | $0.09 |
| NAT gateway, hours | $0.0450 | $32.85 | No | $0.18 |
| NAT gateway's public IPv4 address | $0.0050 | $3.65 | No | $0.02 |
| S3 and DynamoDB gateway endpoints | $0 | $0 | No | $0 |
| Application Load Balancer, hours | $0.0225 | $16.43 | No | $0.09 |
| Load balancer capacity units (20 KB a request assumed) | $0.06 to $0.58 | about $0 | No | $0.12 to $1.15 |
| S3 GET requests (one per asset request) | $0.14 to $1.44 | cents (registry polls) | No | $0.29 to $2.88 |
| DynamoDB session reads (2 read units assumed) | $0.09 to $0.90 | $0 | No | $0.18 to $1.80 |
| DynamoDB session writes (10,000 logins a sitting, 8 write units each) | $0.05 once | $0 | No | $0.05 |
| CloudWatch Logs ingestion (500 bytes a request assumed) | $0.09 to $0.90 | cents | No | $0.18 to $1.80 |
| NAT gateway data processing, for those log bytes | $0.01 to $0.08 | cents | No | $0.02 to $0.16 |
| CloudWatch Logs storage of the run | under $0.01 | $0.015 per million requests kept | No: the log group is the platform root's | under $0.01 |
| The bootstrap state bucket, S3 storage | cents | cents | **Yes** | cents |
| A customer managed KMS key for the key ring, if one is set | $0.0014 | $1.00 | **Yes**, if it is held outside the platform root, as the module assumes | $0.01 |
| The identity provider under load | not established | not established | not established | not established |
| **Total, whole cell** | **$0.17 fixed, plus $0.39 to $3.90 in a loaded hour** | **$69.95 standing; $127.62 with the generator left on** | **cents** | **$1.53 to $8.54** |
| **Of which the load test adds** (generator and the volume lines) | $0.08 fixed, plus $0.39 to $3.90 | $0 | No | $1.15 to $8.16 |

Reading it: the hourly lines are small and known. The total is decided by the request count, at
about $1.08 a million, and by whether the generator and the cell are destroyed afterwards.

## 1. The candidates

What each claim rests on is in the row; "unconfirmed" means no primary source read today settled it.

| Tool, version read | Licence (its own file) | Constant arrival rate, and p99 without coordinated omission | Cookie and antiforgery token, many apps and sessions | `Host` header |
| --- | --- | --- | --- | --- |
| **k6** v2.4.0 (2026-11-12) | AGPL-3.0 (`LICENSE.md`) | Yes. `constant-arrival-rate` "is an open-model executor, meaning iterations start independently of system response". When no virtual user is free "iterations drop" and are counted in `dropped_iterations`. p99 is not in the default summary (`avg,min,med,max,p(90),p(95)`); `summaryTrendStats` adds `p(99)`. A latency measured from the intended start time is not documented: unconfirmed. | Yes. "k6 has a cookie jar for each VU"; a script reads a response body and sets a header, so it can take `csrfToken` from `GET /bff/session` and send `X-CSRF-TOKEN`. One script can address many hosts. | Yes: the source sets `Req.Host` from a `host` header (`js/modules/k6/http/request.go`, line 361). The `hosts` option overrides DNS, with ports and a leading wildcard. |
| **oha** v1.17.0 (2026-11-14) | MIT | Yes, with a flag. `-q` is a "Rate limit for all, in queries per second"; `--latency-correction` will "Correct latency to avoid coordinated omission problem" by measuring "from the time the request was scheduled". Off by default. | Partly. `-H` sets one header set for the whole run, so one session cookie per process; `--urls-from-file` varies the URL, not the cookie. No scripting, so tokens are fetched beforehand. | Yes: `--host`, and `--connect-to` to override DNS and port. |
| **wrk2** (no release; last push 2024-03-03) | Apache-2.0 | Yes: "wrk modifed to produce a constant throughput load" with `-R`, and latency measured against the intended rate; `--u_latency` prints the uncorrected figures beside it. | By LuaJIT script: headers per request can be set. No cookie jar read in the README: unconfirmed. | By script (`wrk.headers`): read, not run. |
| **Vegeta** v12.13.0 (2025-10-31) | MIT | Yes: `-rate`, and workers "increase if necessary in order to sustain the requested rate, unless it'd go beyond `-max-workers`". Latency is timed from the send, not from the schedule (`lib/attack.go`), so the rate holds only while workers are unbounded. Reports the 99th percentile. | Replay only. The targets file carries headers per target, so each line can name its own host and its own pre-minted `Cookie` and `X-CSRF-TOKEN`. No cookie jar in `lib/attack.go`. | Yes: the source copies a `Host` header to `req.Host` (`lib/targets.go`, line 49); `-connect-to` overrides the address. |
| **NBomber** v6.7.1 (2026-10-16) | No licence file in the repository; "NBomber is not free for organizational use" | Not read further: the licence rules it out. | n/a | n/a |
| **Locust** 2.47.0 (2026-10-21) | MIT | No. `constant_throughput` is per user and "Wait time can only constrain the throughput, not launch new Users to reach the target": a closed model. Whether it corrects for coordinated omission: unconfirmed. | Yes: its client "preserves cookies between requests so it can easily be used to log in to websites", and tasks are Python. | Headers are set per request; a `Host` header was not confirmed. |
| **Distributed Load Testing on AWS** v4.3.6 (2026-11-13) | Apache-2.0 | Whatever its engine does: in Native mode it runs a k6, Locust or JMeter script unchanged. | As the engine. | As the engine. |
| **Fortio** v1.76.1 (found) | Apache-2.0 | `-qps` with `-uniform` and `-nocatchup`; by default it catches up after a stall. Reports percentiles. Coordinated omission is not addressed in the README: unconfirmed. | `-H` only. | Yes: `-H` "including Host:"; `-resolve`. |
| **hey** (found) | Apache-2.0 | No: `-q` is a rate limit "per worker". | `-H` only. | Yes: `-host`. |
| **Gatling** v3.16.0, **Artillery** (found) | Apache-2.0; MPL-2.0 | Gatling's `constantUsersPerSec` "Injects users at a constant rate". Neither was read further. | Not read. | Not read. |

**The licence rule.** `docs/dependencies.md` states no single rule sentence. What it does is record
each licence from the package's own file and decide the hard cases as numbered decisions: D-1
declined a licence that charges once the user earns revenue and pinned the last MIT release; D-2
accepted Docker Desktop with its terms written down. By that practice MIT and Apache-2.0 tools pass
as they stand, NBomber is the D-1 case, and k6 is a new case: AGPL-3.0 on a tool run unmodified from
its published image, never linked into what ships. Nothing read says that use is a problem, and
nothing in the file says it is accepted. It needs a decision row.

**The deployed tier needs more than a `Host` header.** The listener is HTTPS on 443 with the
wildcard certificate `certs.sh` issues from a local authority. A generator in the cell has to send
the app's host name as the TLS server name, resolve it to the load balancer, and trust that
authority or skip verification. k6 has `hosts` and `insecureSkipTLSVerify`; oha has `--connect-to`,
`--cacert` and `--insecure`; Vegeta has `-connect-to`, `-root-certs` and `-insecure`. Whether k6
reads a CA file from the environment was not confirmed.

**The local tier has a cookie problem.** Both cookies are `__Host-` cookies with `Secure`
(`contracts/http-contract.md`), and k6 documents that a secure cookie "will only be sent over an
encrypted (SSL/TLS) connection". On a bench network that reaches the gateway over plain HTTP on
8080, a jar would hold the cookie back; a tool that sends an explicit `Cookie` header is not
affected. The harness either sends the header itself or puts TLS in front of the local gateway.

**Sessions.** Every asset needs a session, so a load run needs sessions before it needs load. They
are made either by scripted logins through an identity provider (k6 or Locust can script one against
the test identity provider) or minted beforehand and replayed (all a tool without scripting can do).
The session cookie is encrypted for the app's scheme, so a replayed one has to come from a real
login on that cell. Which of the two the harness uses is its own decision.

## 2. The local tier

**What `startup.py` gives.** A bench network of its own, moto in server mode at a fixed address
(`motoserver/moto:5.2.4`), a container that holds the network namespace and serves a discovery
document on `127.0.0.1:9000` over TLS, and the gateway joined to that namespace under
`--cpus 0.25 --memory 1g`. Probes run in a container on the same Docker VM, because a clock on the
host is not the VM's clock. It binds no host port.

**What a load run adds.** Three things the startup benchmark does not start: the mock API, which in
the deployed task is a second container inside the same 0.25 vCPU and 1 GB (`ecs.tf` sets the size
on the task and no CPU on either container), so locally it belongs in the gateway's namespace and
under the same limit; a way to make sessions, which the discovery document alone cannot do; and the
generator. A generator container on the bench network works for each tool with a published image:
`grafana/k6`, `ghcr.io/hatoo/oha`, `locustio/locust` and `fortio/fortio` all list `linux/arm64`. No
official Vegeta or wrk2 image was found.

**What the stand-ins do to the numbers.** Read, not measured:

- **moto replaces S3 and, under the DynamoDB store, the session table.** It is one Python process
  holding its state in memory. An asset request's latency is then the gateway plus moto, with no
  network distance, no S3 request latency and no throttling. The local asset p99 says nothing about
  the cost of reading S3 on every asset request, which is the thing the map wants measured; only
  the deployed tier measures it. Whether moto saturates before a 0.25 vCPU gateway does is not
  known, and a run has to show moto's own CPU to rule it out.
- **The mock API** answers from loopback in microseconds. The upstream connection bound
  (`contracts/upstream-contract.md`) can be exhausted locally only if the mock API is made slow on
  purpose.
- **The identity provider stand-in** matters at login and refresh only. A run that replays sessions
  never touches it; one that scripts logins measures the stand-in's login, not a real provider's.
- **The generator shares the VM.** Docker Desktop gives the VM a fixed number of CPUs, and the
  gateway's limit is a quota on them. k6's guidance is to keep the generator under 80% CPU: "If k6
  uses 100% of the CPU to generate load ... this may cause the result metrics to have a much larger
  response time than in reality." The earlier startup runs were on a shared machine at a load
  average of 7 to 10 (`tools/bench/results/README.md`); a load run there measures the neighbours.
- **No load balancer and no TLS** unless the harness adds them, so connection handling and the
  forwarded headers differ from the deployed path.

The local tier is therefore a relative measure: the same harness, the same machine, two commits.

## 3. The deployed tier

**The shape.** `multi_task = true` gives `desired_count` 2 over two private subnets, one NAT gateway
with an Elastic IP in the first zone's public subnet, and the S3 and DynamoDB gateway endpoints on
both private route tables; it requires `session_store = "dynamodb"`. There are no interface
endpoints in the module, so Parameter Store, ECR, CloudWatch Logs and the provider's endpoints go
through the NAT. The tasks have no public address. The load balancer is internal, HTTPS on 443. The
ticket does not list the load balancer; it is in the table because the cell cannot be reached
without it.

**Hourly rates.**

| Item | us-east-2 rate | Offer file, usage type |
| --- | --- | --- |
| Fargate ARM vCPU | $0.03238 per vCPU-hour | `AmazonECS`, `USE2-Fargate-ARM-vCPU-Hours:perCPU` |
| Fargate ARM memory | $0.00356 per GB-hour | `AmazonECS`, `USE2-Fargate-ARM-GB-Hours` |
| Fargate x86 vCPU, memory | $0.04048, $0.004445 | `AmazonECS`, `USE2-Fargate-vCPU-Hours:perCPU`, `USE2-Fargate-GB-Hours` |
| NAT gateway | $0.045 per hour, $0.045 per GB processed | `AmazonEC2`, `USE2-NatGateway-Hours`, `USE2-NatGateway-Bytes` |
| Public IPv4 address | $0.005 per hour, in use or idle | `AmazonVPC`, `USE2-PublicIPv4:InUseAddress` |
| Interface endpoint, for comparison | $0.01 per hour each, $0.01 per GB | `AmazonVPC`, `USE2-VpcEndpoint-Hours`, `-Bytes` |
| Application Load Balancer | $0.0225 per hour, $0.008 per capacity-unit hour | `AWSELB`, `USE2-LoadBalancerUsage`, `USE2-LCUUsage` |

- Gateway tasks: 0.25 x $0.03238 + 1 x $0.00356 = $0.011655 an hour each, $0.02331 for two,
  $17.02 a month.
- Billing rules (pages read 2026-11-16): Fargate "is calculated per second with a 1-minute
  minimum", from the image pull "until the task terminates". "Each partial NAT Gateway-hour consumed
  is billed as a full hour", and "Each partial Application Load Balancer hour used is billed as a
  full hour": a sitting whose apply and destroy span a fifth clock hour pays $0.07 more. "There are
  no data processing or hourly charges for using Gateway Type VPC endpoints."

**The generator's size.** "The size that does not itself bottleneck" cannot be established without
a run. What was read: k6 says "a single instance of k6 can run 30,000-40,000 simultaneous users",
that a simple test uses "~1-5MB per VU", and to leave "at least 20% idle cycles"; Locust's own
best case is "around 16000 requests per second using FastHttpUser" on one core; the AWS solution's
default task is "2 vCPUs and 4 GB". The target is two tasks of 0.25 vCPU, half a vCPU in all, so a
generator with four times that is the working assumption: **2 vCPU and 4 GB on ARM, $0.0790 an
hour**. Unconfirmed until a run shows its CPU under 80%.

| Generator size | ARM, per hour | x86, per hour |
| --- | --- | --- |
| 0.5 vCPU / 1 GB | $0.0198 | $0.0247 |
| 1 vCPU / 2 GB | $0.0395 | $0.0494 |
| 2 vCPU / 4 GB | $0.0790 | $0.0987 |

The generator has no public address and reaches the load balancer inside the VPC. A public image is
pulled through the NAT: the k6 image is about 105 MB, half a cent a pull.

**What one request costs.** From the source, not from the plan:

- **S3.** Every asset request is one `GetObject` (`S3ObjectStore.cs`), with the browser's
  `If-None-Match` passed on, so a revalidation is still a billed GET. $0.0000004 a request
  (`AmazonS3`, `USE2-Requests-Tier2`): **$0.40 per million**. It travels by the gateway endpoint,
  not the NAT. An API request reads no object.
- **DynamoDB reads.** Every request with a session is one `GetItem` with `ConsistentRead = true`,
  and the store keeps no cache (`DynamoDbSessionTable.cs`, `DynamoDbSessionStore.cs`). "Strongly
  consistent reads consume one RRU per 4 KB or part thereof", at $0.125 per million units
  (`AmazonDynamoDB`, `USE2-ReadRequestUnits`). The item holds the protected ticket with the tokens;
  its size is unmeasured. **Assumed 4 to 8 KB, 2 units: $0.25 per million**; $0.125 if it fits in
  4 KB.
- **DynamoDB writes.** `SlidingExpiration` is false and the store is replaced only on a token
  refresh, so a request writes nothing. A login writes the item once: "1 WRU per 1 KB", $0.625 per
  million units (`USE2-WriteRequestUnits`). **10,000 logins at 8 units are $0.05.** A refresh adds a
  lease put and delete and one replace.
- **Logs.** The gateway writes one `request` line per request at Information
  (`RequestTelemetry.cs`); the mock API holds `Microsoft.AspNetCore` at Warning. The line's size is
  unmeasured. **Assumed 500 bytes: 0.5 GB per million, $0.25** at $0.50 per GB
  (`AmazonCloudWatch`, `USE2-DataProcessing-Bytes`), plus **$0.0225** of NAT processing for the same
  bytes, plus $0.03 per GB-month stored until the log group is destroyed.
- **Load balancer capacity.** A capacity unit is the highest of 25 new connections a second, 3,000
  active connections a minute, 1 GB an hour and 1,000 rule evaluations a second. With kept-alive
  connections and one rule, bytes lead. **Assumed 20 KB a request and response: 20 GB per million,
  $0.16.**

Sum, per million asset requests: **$1.08**. Per million API requests: $0.68 at the same sizes.

**The two worked rates.** Nothing measured says what two 0.25 vCPU tasks sustain. 100 requests a
second is 360,000 an hour, $0.39; 1,000 a second is 3.6 million an hour, $3.90. The second is a
bound chosen to be hard to exceed, not an estimate.

**Not priced.** Data transfer between zones is $0.01 per GB (`AWSDataTransfer`,
`USE2-DataTransfer-Regional-Bytes`); whether it applies between a task and a load balancer node in
the other zone was not confirmed. If it does, in both directions, it is at most $0.40 per million
requests at 20 KB. The identity provider's charge for the logins a run makes was not read.

## 4. What bills while the cell is destroyed

"Destroyed" is the platform root's destroy. That removes the VPC, the NAT gateway and its address,
the load balancer, the service and tasks, the session table, the log group, the bucket and both
image repositories (`force_delete = true`). What remains:

- **The bootstrap root's state bucket**: S3 storage at $0.023 per GB-month, cents.
- **The identity root and the apps root**, if they are left applied: a user pool and standard
  parameters. The price list shows a charge for advanced parameters only ($0.05 each a month); the
  identity provider's standing charge was not read today.
- **A customer managed KMS key**, only if `data_protection_kms_key_id` names one: the module takes
  the key's id and does not hold the key, so the destroy does not remove it. $1 a month.
- **Whatever the load test adds outside the root.** A generator image mirrored into a repository
  that the root does not own is $0.10 per GB-month. Results committed to `tools/bench/results/`
  cost nothing. The AWS solution's stack and its dashboards would be the largest item here, which
  is the reason section 1 sets it aside.

A generator defined in the platform root, as a task run for the sitting and not as a service, leaves
nothing.

## 5. Assumptions behind every figure

| Assumption | Value | Status |
| --- | --- | --- |
| Region | `us-east-2` | `tools/aws-project.env` |
| Gateway task | 0.25 vCPU, 1 GB, ARM64, two tasks | `infra/modules/cell/ecs.tf` |
| Generator task | 2 vCPU, 4 GB, ARM64, one task, no public address | assumed; unconfirmed |
| Sitting | four hours standing, generator on for four, two under load | assumed |
| Request rate | 100 and 1,000 a second, both tasks together | assumed; unmeasured |
| Request mix | every request an asset request with a session | assumed, as the dearer case |
| Session item | 4 to 8 KB | assumed; unmeasured |
| Request log line | 500 bytes, one per request | one per request is read; the size is assumed |
| Bytes through the load balancer | 20 KB a request | assumed |
| Logins | 10,000 a sitting | assumed |
| Free tier and credits | none counted | list price |

## 6. What was executed and what was only read

**Executed, on 2026-11-16:**

- `curl` of the offer files below, parsed with `python3 -I`.
- `curl` of each tool's `LICENSE` and `README` from `raw.githubusercontent.com`, of
  `tsenart/vegeta` `lib/targets.go` and `lib/attack.go`, and of `grafana/k6`
  `js/modules/k6/http/request.go` at `v2.4.0`; `grep` over them. The GitHub API was read for each
  repository's latest release and, for wrk2, its last push.
- `docker manifest inspect` of `grafana/k6:2.4.0`, `ghcr.io/hatoo/oha:latest`,
  `locustio/locust:2.47.0` and `fortio/fortio:latest`: each lists `linux/amd64` and `linux/arm64`.
- **Two images were pulled**: `grafana/k6:2.4.0`, run as `version` (it printed `k6 v2.4.0 ...
  linux/arm64`), and `ghcr.io/hatoo/oha:latest`, run as `--help` and `--version` (`oha 1.17.0`; the
  help text shows `-q`, `--latency-correction`, `--host`, `--cacert` and `--connect-to`). Neither
  sent a request to anything.

**Only read.** Everything else: no tool generated load, no script was written or run, no flag
beyond the help text was exercised, and nothing in AWS was called.

**Offer files** (`https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/<offer>/current/us-east-2/`):

| Offer | Version | Gave |
| --- | --- | --- |
| `AmazonECS` | 20261111234623 | Fargate ARM and x86 rates |
| `AmazonEC2` (`index.csv`) | 20261114012926 | NAT gateway hours and bytes |
| `AmazonVPC` | 20261105181945 | public IPv4, endpoint rates |
| `AWSELB` | 20261111153621 | load balancer hours and capacity units |
| `AmazonS3` | 20261116071300 | GET requests, storage |
| `AmazonDynamoDB` | 20261111153612 | read and write request units, storage |
| `AmazonCloudWatch` | 20261116171036 | log ingestion and storage |
| `AmazonECR` | 20261113163947 | image storage |
| `AWSDataTransfer` | 20261111191941 | regional transfer |
| `AWSSystemsManager` | 20261103183538 | advanced parameters |
| `awskms` | 20261108172342 | key-month |

**Pages read on 2026-11-16:**

- AWS billing rules: <https://aws.amazon.com/vpc/pricing/>, <https://aws.amazon.com/fargate/pricing/>,
  <https://aws.amazon.com/elasticloadbalancing/pricing/>,
  <https://aws.amazon.com/dynamodb/pricing/on-demand/>.
- The plan's service lists:
  <https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html>. API
  Gateway, CloudFront, Cognito, DynamoDB, ECS, Lambda, Step Functions, Amplify, IoT, EventBridge and
  CloudFormation are all on the Free Tier list.
- The AWS solution:
  <https://docs.aws.amazon.com/solutions/latest/distributed-load-testing-on-aws/cost.html> ("$30.90
  per month" in N. Virginia for ten tasks over 30 hours; "$3.00 per dashboard per month", and the
  dashboards "remain in your account after tests complete"),
  <https://docs.aws.amazon.com/solutions/latest/distributed-load-testing-on-aws/architecture-details.html>,
  and <https://github.com/aws-solutions/distributed-load-testing-on-aws> (`LICENSE.txt`, `README.md`).
- k6: <https://github.com/grafana/k6> (`LICENSE.md`, the source file above),
  <https://grafana.com/docs/k6/latest/using-k6/scenarios/executors/constant-arrival-rate/>,
  <https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/open-vs-closed/>,
  <https://grafana.com/docs/k6/latest/using-k6/scenarios/concepts/dropped-iterations/>,
  <https://grafana.com/docs/k6/latest/using-k6/cookies/>,
  <https://grafana.com/docs/k6/latest/using-k6/k6-options/reference/> (its first 100,000
  characters),
  <https://grafana.com/docs/k6/latest/javascript-api/k6-http/params/>,
  <https://grafana.com/docs/k6/latest/testing-guides/running-large-tests/>.
- oha: <https://github.com/hatoo/oha> (`LICENSE`, `README.md`).
- wrk2: <https://github.com/giltene/wrk2> (`LICENSE`, `README.md`).
- Vegeta: <https://github.com/tsenart/vegeta> (`LICENSE`, `README.md`, `lib/targets.go`,
  `lib/attack.go`).
- NBomber: <https://github.com/PragmaticFlow/NBomber> (`README.md`; no licence file at the root),
  <https://nbomber.com/docs/getting-started/license/>.
- Locust: <https://github.com/locustio/locust> (`LICENSE`),
  <https://docs.locust.io/en/stable/writing-a-locustfile.html>,
  <https://docs.locust.io/en/stable/increase-performance.html>.
- Fortio, hey, Gatling, Artillery: <https://github.com/fortio/fortio> (`LICENSE`, `README.md`),
  <https://github.com/rakyll/hey> (`LICENSE`, `README.md`), <https://github.com/gatling/gatling>
  (`LICENSE.txt`), <https://docs.gatling.io/concepts/injection/>,
  <https://github.com/artilleryio/artillery> (`LICENSE.txt`).

**Not established:**

- The request rate two 0.25 vCPU tasks sustain, the session item's size, the request line's size
  and the bytes per request: all unmeasured, and they decide the total.
- The generator size that does not bottleneck.
- Whether k6 reports a latency corrected to the intended start time, and whether it reads a CA
  file from the environment.
- Whether Locust corrects for coordinated omission; whether wrk2 builds for ARM64 and carries a
  cookie jar; an official image for Vegeta or wrk2.
- Whether cross-zone data transfer applies between the tasks and the load balancer.
- The identity provider's charge for a run's logins, and its standing charge while the platform
  root is destroyed.
- Whether AGPL-3.0 on an unmodified tool is acceptable under `docs/dependencies.md`: a decision,
  not a fact to look up.
