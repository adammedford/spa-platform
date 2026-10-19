# What ECS and the ALB expose for the startup clocks, and the health-check minimums

A research finding for the wayfinder "gateway startup benchmark, floor and budget" (issue #169),
answering its research ticket of the same title as this file (issue #171). It is read by the budget
grilling; it decides nothing. Written 2026-10-19 from AWS primary documentation and the cell module
as it stands on `origin/main`; no AWS API was called and no Terraform was run. Every claim names
its source; the "Sources" list at the end holds the URLs once.

## Summary

- **Pull-and-place** is `createdAt` to `startedAt` on the ECS `Task` object: `createdAt` is "the
  time when the task entered the `PENDING` state" and `startedAt` is "the time when the task
  transitioned from the `PENDING` state to the `RUNNING` state". `pullStartedAt` to `pullStoppedAt`
  isolates the image pull inside it. The same fields arrive in EventBridge task state change events
  as ISO timestamps, so the harness need not poll for them.
- **Task start to target healthy** has no AWS timestamp. `DescribeTargetHealth` returns a state, a
  reason code and a description, and nothing else: no time, no history. Target-healthy must be
  sampled by polling, from `startedAt` to the first sample whose state is `healthy`. Per-check
  timestamps exist only in the opt-in health check logs (S3, delivered per load balancer node every
  5 minutes, or the CloudWatch Logs delivery), which the ADR would have to decide to enable.
- **The cell's effective health check is not the API default.** `alb.tf` sets path, protocol, port
  and matcher only; the Terraform provider fills interval 30, healthy threshold 3 and unhealthy
  threshold 3 from its schema defaults, where the ELB API's own defaults are 5 and 2. The timeout
  is left to AWS, and the two AWS pages disagree on it (6 s in the API reference, 5 s in the ALB
  guide for `ip` targets). The grace period is 60 s (`ecs.tf`); the API default is 0.
- **The circuit breaker counts to 3** on this service: the default `BOUNDED_PERCENT` 50% of a
  desired count of 1 is clamped to the minimum of 3, and the default `resetOnHealthyTask = true`
  counts consecutive failures only. Stage 1 counts tasks that never reach `RUNNING`; stage 2 counts
  tasks replaced for failing Elastic Load Balancing, Cloud Map or container health checks.
- **0.25 vCPU is a steady ceiling, not a burst.** The task-level `cpu` is "the hard limit of CPU
  units to present for the task"; AWS's own write-up calls it a kernel-enforced hard ceiling that
  containers cannot burst beyond. Image pull time does depend on task size in one documented way:
  layer decompression scales with the vCPUs available to the runtime. Fargate caches nothing, so
  every task start pulls the whole image.
- **The smallest ready time the current settings tolerate is about 60 s after the first health
  check**, quantized to the 30 s interval: `/readyz` must answer 200 to the third check (first check
  + 60 s) or the target is `unhealthy` exactly as the 60 s grace period ends, and the scheduler stops
  the task. Whether the first check fires before or after `startedAt` is undocumented and is the
  largest unknown; the harness must record it.

## 1. The task timestamps, and which pair gives the pull-and-place clock

### 1.1 What `describe-tasks` returns

The `Task` object in the ECS API reference carries these timestamps (quoted descriptions are the
reference's own words):

| Field | Documented meaning |
| --- | --- |
| `createdAt` | "The Unix timestamp for the time when the task was created. More specifically, it's for the time when the task entered the `PENDING` state." |
| `connectivityAt` | "The Unix timestamp for the time when the task last went into `CONNECTED` status." |
| `pullStartedAt` | "The Unix timestamp for the time when the container image pull began." |
| `pullStoppedAt` | "The Unix timestamp for the time when the container image pull completed." |
| `startedAt` | "The Unix timestamp for the time when the task started. More specifically, it's for the time when the task transitioned from the `PENDING` state to the `RUNNING` state." |
| `stoppingAt` | "the time when the task transitions from the `RUNNING` state to `STOPPING`." |
| `stoppedAt` | "the time when the task transitioned from the `RUNNING` state to the `STOPPED` state." |
| `executionStoppedAt` | "The Unix timestamp for the time when the task execution stopped." |

The `Task` object also carries `lastStatus`, `desiredStatus` and `healthStatus`; `healthStatus` is
derived from container health checks only ("determined by the health of the essential containers in
the task") and says nothing about the load balancer. The cell defines no container `healthCheck`
(`infra/modules/cell/ecs.tf`), so `healthStatus` will read `UNKNOWN` for the gateway task and is not
a clock input. Source: API_Task.

The Fargate guide confirms these are the task-lifecycle timestamps and names the use the map has
for them: "Tasks that run on Fargate publish timestamps to track the task through the states of the
task lifecycle ... you can use the timestamps to evaluate how much time the task spent downloading
the container images." Source: fargate-tasks-services, "Task lifecycle logging".

### 1.2 The lifecycle order the timestamps sit in

The developer guide's lifecycle states, in order: `PROVISIONING` ("for tasks that use the `awsvpc`
network mode, the elastic network interface needs to be provisioned"), `PENDING` ("a transition
state where Amazon ECS is waiting on the container agent to take further action"), `ACTIVATING`
("This is the state where Amazon ECS pulls the container images, creates the containers, configures
the task networking, registers load balancer target groups, and configures service discovery"),
then `RUNNING`. Source: task-lifecycle-explanation.

Two consequences for the map's definition of pull-and-place as "task PENDING to RUNNING":

- `createdAt` is PENDING entry, so the clock starts after `PROVISIONING`. Whether ENI provisioning
  time sits inside or before `createdAt` cannot be settled from the documents: `PROVISIONING` is
  drawn before `PENDING`, yet in AWS's own Fargate event example (below) `createdAt` is the earliest
  timestamp and the ENI reaches `ATTACHED` 3.3 s later (`connectivityAt`). A harness that wants the
  whole launch, provisioning included, has one earlier marker: the time of its own `UpdateService`
  or `RunTask` call, or the `SERVICE_DEPLOYMENT_IN_PROGRESS` event's `updatedAt`.
- Target group registration happens in `ACTIVATING`, before `startedAt`. Section 6 returns to this,
  because it means the ALB's first health check can precede the second clock's own zero.

### 1.3 The pair for each span

| Span | Pair | Notes |
| --- | --- | --- |
| Pull-and-place (the map's first Fargate clock) | `createdAt` -> `startedAt` | Exactly PENDING -> RUNNING by the field definitions. |
| Placement, networking and anything before the pull | `createdAt` -> `pullStartedAt` | Includes `connectivityAt`. |
| Image pull, all containers | `pullStartedAt` -> `pullStoppedAt` | The task metadata endpoint words the same two as "when the first container image pull began" and "when the last container image pull finished", so with the mock API in the task this is both images. |
| Container create and start, target registration | `pullStoppedAt` -> `startedAt` | |

AWS's own Fargate example in the task state change event page gives a feel for the shape (nginx
plus two sidecars, `cpu` 256, `memory` 512, platform 1.4.0): `createdAt` 11:02:13.877,
`connectivityAt` 11:02:17.19, `pullStartedAt` 11:02:24.162, `pullStoppedAt` 11:02:33.493,
`startedAt` 11:02:34.325. That is 20.4 s PENDING to RUNNING, of which 9.3 s is the pull and 0.8 s is
everything after it. The example is illustrative, not a benchmark. Source: ecs_task_events.

### 1.4 Where to read them without polling

- **EventBridge task state change events** carry the whole `Task` detail. The page states: "The
  values for the `createdAt`, `connectivityAt`, `pullStartedAt`, `startedAt`, `pullStoppedAt`, and
  `updatedAt` fields are ISO string timestamps", and that "The health status of the event is not
  available in the task state change event." Events may be delivered more than once; the `version`
  field in `detail` identifies duplicates. Sources: ecs_task_events, ecs_cwe_events.
- **The task metadata endpoint v4** inside the task gives `PullStartedAt`, `PullStoppedAt`, and per
  container `CreatedAt`, `StartedAt`, `FinishedAt`, plus `Snapshotter` (`overlayfs` or `soci`) and
  `ClockDrift` (`ClockErrorBound` in milliseconds against Amazon Time Sync). The container
  `StartedAt` is the zero for the map's three container clocks as seen from inside the task, and AWS
  recommends exactly this join: "track application start-up time from `StartedAt` in the container
  metadata response to the `StartedAt` time for your task or service." Sources:
  task-metadata-endpoint-v4-fargate-response, task-recommendations.
- **`describe-tasks` after the fact** works for an hour: "Stopped tasks only appear in the console
  for 1 hour", and the CLI path is `list-tasks --desired-status STOPPED` then `describe-tasks`. A
  harness that reads the task record for each run should do so promptly or keep the events.
  Source: stopped-task-errors.

## 2. Task start to target healthy: what the ALB exposes

### 2.1 No history: `DescribeTargetHealth` is a snapshot

`DescribeTargetHealth` takes a target group ARN, optional targets and an optional `Include`
(`AnomalyDetection | All`) and returns `TargetHealthDescriptions`. Each `TargetHealthDescription`
has `Target`, `HealthCheckPort`, `TargetHealth` (`State`, `Reason`, `Description`),
`AnomalyDetection` and `AdministrativeOverride`. No field is a timestamp and no call returns past
states. Sources: API_DescribeTargetHealth, API_TargetHealthDescription.

The states a sample can show, with their reason codes (ALB guide): `initial`
(`Elb.RegistrationInProgress`, `Elb.InitialHealthChecking`), `healthy`, `unhealthy`
(`Target.ResponseCodeMismatch`, `Target.Timeout`, `Target.FailedHealthChecks`,
`Elb.InternalError`), `unused`, `draining`, `unavailable`. The reason code lets a sample distinguish
"registered, not yet checked" from "checked and failing", so the harness should record `Reason` as
well as `State`. Source: target-group-health-checks, "Target health status".

Two rules of the state machine matter for the clock:

- A new target needs one pass, not `HealthyThresholdCount`: "After your target is registered, it
  must pass one health check to be considered healthy", and the ECS guide repeats it as an
  important note: "For newly registered targets, only a single successful health check is required
  to consider the target healthy, regardless of the healthy threshold count setting. The healthy
  threshold count only applies when a target is transitioning from an unhealthy state back to a
  healthy state." Sources: target-group-health-checks, load-balancer-healthcheck.
- Each node checks independently: "Each load balancer node checks the health of each target", at
  "approximately" every `HealthCheckIntervalSeconds`, and "Each health check request is independent
  and the result lasts for the entire interval." The API shows one state per target; the two nodes
  of an internal ALB in two subnets (`alb.tf`) both request `/readyz`, so the gateway's log will
  show two checkers. Source: target-group-health-checks.

### 2.2 Per-check timestamps exist only in the opt-in health check logs

"Health check logs ... Each log entry contains the details of a single health check result made to
the registered target", with fields `type`, `time` ("Timestamp of when health check is initiated on
a target, in ISO 8601 format"), `latency`, `target_addr`, `target_group_id`, `status` (`PASS` or
`FAIL`), `status_code`, `reason_code`, `elb`, `ip_address`. They are "disabled by default", go to an
S3 bucket as compressed files, and "Elastic Load Balancing publishes a log file for each load
balancer node every 5 minutes". The newer CloudWatch Logs integration lists "Health check logs" among
the types it can deliver to CloudWatch Logs, Firehose or S3. Sources: load-balancer-health-check-logs,
load-balancer-cloudwatch-logs.

These would date the exact passing check for a post-hoc read of the second clock. Enabling them is
a decision for the ADR, not this finding: `alb.tf` records that P-01 allows no second bucket and
keeps ALB logging off in Phase 1 for the same reason as access logs, and the CloudWatch Logs route
is a vended-log cost line the plan does not have.

### 2.3 Coarser ECS-side markers

- **Service events** (`DescribeServices` -> `events[]`, each with `createdAt`, `id`, `message`).
  "`service (service-name) has reached a steady state.`" is sent "when the service is healthy and at
  the desired number of tasks", and a load balancer failure appears as "`service (service-name) (task
  task-id) ... is unhealthy in (elb elb-name) due to (reason Instance has failed at least the
  UnhealthyThreshold number of health checks consecutively.)`". Duplicates are suppressed ("Duplicate
  event messages are omitted until either the cause is resolved, or six hours passes"), and the API
  returns only the scheduler's events. Sources: API_ServiceEvent, service-event-messages,
  service-event-messages-list.
- **Deployment state change events** (`SERVICE_DEPLOYMENT_IN_PROGRESS`, `SERVICE_DEPLOYMENT_COMPLETED`
  "once a service reaches a steady state after a deployment", `SERVICE_DEPLOYMENT_FAILED` with the
  circuit breaker on), each with `updatedAt`. Source: ecs_service_deployment_events.
- **`aws ecs wait services-stable`** polls `describe-services` every 15 seconds, up to 40 times (10
  minutes), until every service has one deployment and `runningCount == desiredCount`. **`aws elbv2
  wait target-in-service`** polls `describe-target-health` every 15 seconds, up to 40 times, until
  every target's state is `healthy`. Both are too coarse for a clock whose resolution should be
  under the 30 s check interval. Sources: cli ecs wait services-stable, cli elbv2 wait
  target-in-service.

### 2.4 How target-healthy must be sampled

- Poll `DescribeTargetHealth` for the target group at about 1 s from the moment the new task's
  record appears (its `createdAt`), not from `startedAt`: the first sample in which the task's IP
  appears gives T0 for registration (state `initial`, reason `Elb.RegistrationInProgress` then
  `Elb.InitialHealthChecking`), and the first `healthy` sample ends the clock. Record both.
- Report the clock as `startedAt` -> first `healthy` sample, and state the sampling period beside it:
  the true transition lies in the poll period before that sample. With a 30 s check interval the
  readiness time is in any case quantized to the check that passed; the health check log, if the
  ADR enables it, is the only way to recover the exact check.
- Keep the task's `createdAt`, `pullStartedAt`, `pullStoppedAt`, `startedAt` and the container
  `StartedAt` from the same run so the three container clocks and the two Fargate clocks share one
  zero, as section 1.4 describes.

## 3. Health check and grace-period minimums and defaults, and the cell's effective settings

### 3.1 The target group

`alb.tf` declares the `health_check` block with `path = var.health_check_path`, `protocol = "HTTP"`,
`port = "traffic-port"`, `matcher = "200"`, and nothing else. The cell module pins the AWS provider
at `~> 6.37` (`infra/modules/cell/versions.tf`); the defaults below are from the provider source at
v6.68.0, the version `alb.tf` itself cites, and the three load-bearing ones (30, 3, 3) are schema
defaults the provider sends on create. The timeout has no schema default and the provider sends it
only when set, "so AWS chooses the value". Source: terraform-provider-aws target_group.go and
lb_target_group docs.

| Setting (ELB API name) | API range | API default, HTTP target group | Provider default sent when unset | `alb.tf` | Effective |
| --- | --- | --- | --- | --- | --- |
| `HealthCheckIntervalSeconds` | 5–300 | 30 | 30 | unset | **30 s** |
| `HealthCheckTimeoutSeconds` | 2–120 | 6 s (API reference, "protocol of HTTP"); 5 s (ALB guide, "target type is `instance` or `ip`") | none sent | unset | **AWS default; the two AWS pages disagree, 5 or 6 s** |
| `HealthyThresholdCount` | 2–10 | 5 | 3 | unset | **3** |
| `UnhealthyThresholdCount` | 2–10 | 2 | 3 | unset | **3** |
| `HealthCheckPath` | — | `/` | — | `var.health_check_path`: default `/`, `/readyz` for the gateway | per image |
| `Matcher` | 200–499 | 200 | — | `200` | 200 |
| `HealthCheckPort` | — | `traffic-port` | — | `traffic-port` | 8080 (`var.gateway_port`) |
| `HealthCheckProtocol` | — | HTTP | HTTP | HTTP | HTTP |

Sources: API_CreateTargetGroup (ranges and API defaults), target-group-health-checks (the guide's
table), target_group.go (provider defaults), `infra/modules/cell/alb.tf` and `variables.tf`.

Two things to notice:

- The unhealthy threshold the deployment actually runs under, 3, is the Terraform provider's
  default and nobody in this repository chose it. Under the ELB API's own default of 2 the target
  would be `unhealthy` 30 s after its first failed check rather than 60 s. Section 6 shows that
  this one default is what lines the ALB up with the 60 s grace period.
- The fastest legal check is interval 5, timeout 2, healthy 2, unhealthy 2. The ECS guide's own
  recommendation for a service that "starts up and stabilizes in under 10 seconds" is interval 5
  and healthy threshold 2, which it says takes "10 seconds compared to the default of two minutes
  and 30 seconds" for an unhealthy-to-healthy recovery. Source: load-balancer-healthcheck.

Related attributes `alb.tf` leaves at default: deregistration delay 300 s (affects how long a
stopping task drains, not the start), slow start off, round robin. Source: edit-target-group-attributes.

### 3.2 The service

| Setting | Range | API default | `ecs.tf` |
| --- | --- | --- | --- |
| `healthCheckGracePeriodSeconds` | 0 to 2,147,483,647 | 0 | **60** |
| `deploymentCircuitBreaker.enable` / `rollback` | — | off | **true / true** |
| `deploymentCircuitBreaker.resetOnHealthyTask` | — | true | unset (true) |
| `deploymentCircuitBreaker.thresholdConfiguration` | `BOUNDED_PERCENT` 1–100, `UNBOUNDED_PERCENT`, `COUNT` | `BOUNDED_PERCENT` 50 | unset |
| `minimumHealthyPercent` / `maximumPercent` | — | 100 / 200 | unset |
| `desiredCount` | — | — | **1** |
| container `healthCheck` | — | none | none |

The grace period's definition: "The period of time, in seconds, that the Amazon ECS service
scheduler ignores unhealthy Elastic Load Balancing, VPC Lattice, and container health checks after a
task has first started. If you do not specify a health check grace period value, the default value
of 0 is used." `UpdateService` adds the ceiling and the purpose: "you can specify a health check
grace period of up to 2,147,483,647 seconds (about 69 years). During that time, the Amazon ECS
service scheduler ignores health check status. This grace period can prevent the service scheduler
from marking tasks as unhealthy and stopping them before they have time to come up", and "This
parameter doesn't trigger a new service deployment", so it can be widened without a rollout.
"First started" is not tied to a named timestamp in either page; reading it as `startedAt` (the
PENDING -> RUNNING transition) is this finding's inference. Sources: API_CreateService,
API_UpdateService.

What "healthy" means to the scheduler: "Tasks for services that use a load balancer are considered
healthy if they're in the `RUNNING` state and are reported as healthy by the load balancer", and
the ECS guide's health check page: "The Amazon ECS agent monitors, and waits for the load balancer
to report on the container health. It does this before it considers the container to be in a
healthy status." Sources: API_CreateService, load-balancer-healthcheck.

Repository context for the gateway's own readiness, recorded here because the arithmetic in
section 6 depends on it: `/readyz` answers 200 "once a snapshot is live, 503 before that"
(`contracts/pipeline.md`), and `RegistryPoller` "Runs `RegistrySource.RefreshAsync` once at
start-up and then every `Registry:PollSeconds`, jittered" (`src/Platform.Registry/RegistryPoller.cs`).
So the first 200 follows the first S3 read, not one poll interval; the comment on
`health_check_grace_period_seconds` in `ecs.tf` ("one poll interval after the task starts") is the
more conservative reading of the same code.

## 4. How the circuit breaker counts a failure

The developer guide describes two stages. "Stage 1 - The scheduler monitors the tasks to see if
they transition into the RUNNING state ... consecutive tasks that do not transition to the RUNNING
state count toward the failure threshold (`resetOnHealthyTask` is `true`)." "Stage 2 - The deployment
enters this stage when there is at least one task in the RUNNING state. The circuit breaker checks
the health checks for the tasks in the current deployment being evaluated. The validated health
checks are Elastic Load Balancing, AWS Cloud Map service health checks, and container health checks.
... Failure - The tasks that are replaced because of health check failures have reached the failure
threshold." In the second description on the same page: "When a health check fails for the task,
the deployment circuit breaker increases the failure count by one. When the failure count equals
the threshold, the deployment is marked as `FAILED`." Source: deployment-circuit-breaker.

The threshold for this service: `BOUNDED_PERCENT` with value 50 is "clamped to a minimum of 3 and a
maximum of 200", and the page's own table gives desired count 1 -> threshold 3. With
`resetOnHealthyTask` true (the default), "The failure count resets to 0 each time a task reaches a
healthy state. Only consecutive failures count toward the threshold." `ecs.tf` sets neither knob, so
the gateway service fails a deployment after three consecutive task failures, of either stage.
Source: deployment-circuit-breaker, "Failure threshold".

What one stage-2 failure is in practice: a task that reached `RUNNING`, whose target the load
balancer reports `unhealthy` once the grace period has passed, which the scheduler then stops and
replaces (section 3.2's definitions; the service event message in section 2.3). The `Task` object's
`stopCode` values include `ServiceSchedulerInitiated`, and the stopped-task view says the service
events record "Amazon ECS stopping and replaces a task because the containers in the task have
stopped running, or have failed too many health checks from Elastic Load Balancing." Sources:
API_Task, stopped-task-errors.

Rollback and its limits: "When the deployment circuit breaker determines that a deployment failed,
it looks for the most recent deployment that is in a `COMPLETED` state ... When the deployment
circuit breaker does not find a deployment that is in a `COMPLETED` state, the circuit breaker does
not launch new tasks and the deployment is stalled." "A deployment in a `FAILED` state doesn't launch
any new tasks." The breaker is "only supported for Amazon ECS services that use the rolling update
(`ECS`) deployment controller", which is what `ecs.tf` declares by omission. An EventBridge rule on
`SERVICE_DEPLOYMENT_FAILED` is AWS's recommended way to see it. Source: deployment-circuit-breaker.

Cost of detecting a bad image at the current settings, in time: at least three cycles, each of
pull-and-place plus the longer of the grace period and the unhealthy window (section 6), so at least
three times (pull-and-place + 60 s) before `FAILED`, then the rollback deployment's own start. The
stop side of each cycle adds a term this file cannot size: ECS "waits for the load balancer to
report that the keep alive connection is closed (the target is in an `UNUSED` state)", and "The
amount of time that the load balancer waits to move the target to the `UNUSED` state is the
deregistration delay", 300 s by default and unset in `alb.tf`; the ALB guide adds that a target
with no in-flight requests completes deregistration at once but "the status of the target is
displayed as `draining` until the deregistration delay timeout expires". Whether a draining task
still counts against `maximumPercent` (200% of 1 = 2 tasks) and so delays the next launch is not
stated on any page read; the service-parameters page only warns that "the load balancer might have
to wait 5 minutes for the old task to stop". In money, Fargate bills "from the time you start to
download your container image until the Amazon ECS Task ... terminates", per second with a
one-minute minimum, so each failed cycle is billed at least a minute and, while it drains, up to
five more. Sources: load-balancer-connection-draining, edit-target-group-attributes,
service-options, fargate pricing.

## 5. Fargate at 0.25 vCPU / 1 GB on ARM64: steady share, pull time, and task size

### 5.1 CPU is a hard ceiling, with no burst

The task definition reference defines the task-level `cpu` as "The hard limit of CPU units to
present for the task", and for Fargate "Task-level CPU and memory parameters are required". `256
(.25 vCPU)` is the smallest Linux value and pairs with 512 MiB, 1 GB or 2 GB, which is what `ecs.tf`
declares (`cpu = "256"`, `memory = "1024"`). Sources: task_definition_parameters,
fargate-tasks-services.

AWS's own engineering write-up, linked from that reference page, states the mechanism: CPU units
assigned to a task "are enforced by the Linux kernel as a hard CPU ceiling for the task", containers
"cannot burst capacity usage beyond this task ceiling", and container-level units are CPU shares
within the task that act as weights under contention, with spare capacity inside the task
redistributed in proportion. It is a 2019 post with a 2020 update, secondary to the reference, and
cited here only for the mechanism. Source: how-amazon-ecs-manages-cpu-and-memory-resources.

No Fargate page read for this finding describes a burst, credit or boost model for task CPU; the
documented model is a quota. So a 0.25 vCPU task gets a quarter of one vCPU over each scheduler
period, at start-up as at any other time, and the gateway's cold start (JIT, assembly loading, the
first S3 and Parameter Store calls) runs under that quarter. `ecs.tf` sets no container-level `cpu`,
so when the mock API container is in the task the 256 units are the ceiling for both containers
together and neither is guaranteed a share of it.

### 5.2 Pull time depends on task size in one documented way, and there is no cache

"Every Fargate task runs on its own single use, single tenant instance. When you run Linux
containers on Fargate, container images or container image layers are not cached on the instance.
Therefore, for each container image defined in the task, the whole container image needs to be
pulled from the container image registry for each Fargate task. The time it takes to pull the images
is directly correlated to the time taken to start a Fargate task." Source: fargate-pull-behavior.

On task size: "The compression algorithm used and the amount of vCPUs available to the runtime
impact the time it takes to decompress the container image. On Fargate, you can increase the size of
the task or leverage the more performant zstd compression algorithm to reduce the time taken for
decompression." So the decompression half of `pullStartedAt` -> `pullStoppedAt` is slower at 0.25
vCPU than at a larger size. Network bandwidth per Fargate task size is not stated on any page read
for this finding, and this file does not fill the gap. The other documented levers are proximity
("store the container image in the same Region where the task will run ... use a VPC interface
endpoint"; the cell pulls over its public address with no endpoint, per `ecs.tf`), image size, and
SOCI lazy loading, which AWS recommends only "with container images greater than 250 MiB
compressed" and which "can change how long your tasks take to start, you might need to change
various timeouts like the health check grace period". Sources: fargate-pull-behavior,
fargate-tasks-services.

For the map's definition of a cold start ("a fresh container from an already-pulled image"), this
means the Fargate pull-and-place clock always contains a full pull; an already-pulled image exists
only in the local Docker runs. Recorded, not re-decided.

### 5.3 ARM64

"For the Fargate type, the applications must use Fargate platform version `1.4.0` or later",
Linux only, and in us-east-1 the `use1-az3` Availability Zone does not support ARM64 tasks.
`ecs.tf` runs `platform_version = "LATEST"` with `cpu_architecture = "ARM64"`. No AWS page read
describes a difference in how the CPU quota is applied on Graviton; the pricing page's Graviton
example rates are $0.0000089944 per vCPU-second and $0.0000009889 per GB-second in us-east-1.
Sources: ecs-arm64, fargate pricing.

## 6. The smallest ready time the current settings tolerate

Definitions. S = `startedAt`. T0 = the time of the first health check request to the new target,
which is after registration completes. G = 60 s, the grace period. I = 30 s, the check interval.
U = 3, the unhealthy threshold. The gateway answers `/readyz` with an immediate 503 before a
snapshot is live, so a failed check is a `ResponseCodeMismatch`, not a timeout, and the timeout
setting adds no slack.

The two mechanisms:

1. The ALB marks the target `unhealthy` after U consecutive failures: checks at T0, T0 + I, T0 + 2I
   all fail, so the target is `unhealthy` at about T0 + (U − 1) × I = **T0 + 60 s**. That the count
   applies to a target still in `initial` is this finding's inference: the guide defines
   `UnhealthyThresholdCount` as the failures "required before considering a target unhealthy" and
   carves out no exception for the initial checks, but neither page says so in those words. A
   single pass before that makes it `healthy` (section 2.1). Once `unhealthy`, returning to
   `healthy` needs HealthyThresholdCount = 3 consecutive passes, 90 s.
2. The scheduler ignores an unhealthy target until S + G = **S + 60 s**, then stops it (section
   3.2).

The task survives when, at every scheduler look after S + 60, the target is `healthy` or still
`initial`. With the two windows both 60 s long, that reduces to: **`/readyz` must return 200 to the
check at T0 + 60 s**. Ready at T0 + 59 s passes that check and the target is `healthy` before the
grace ends. Ready at T0 + 61 s fails it, the target is `unhealthy` at about T0 + 60 s, it cannot
recover within the grace (90 s of passes needed), and the scheduler stops the task at
max(S + 60, T0 + 60). The tolerated time is quantized to the 30 s interval: between the checks at
T0 + 30 and T0 + 60 nothing changes.

The arithmetic as a line: tolerated ready time, measured from the first check, is
(U − 1) × I = (3 − 1) × 30 = 60 s, and the grace period is also 60 s. They coincide only because the
provider's default U = 3 is in force; at the API default U = 2 the ALB window would be 30 s while
the grace stayed 60, and the shorter window would govern.

Relative to the clock's own zero, `startedAt`, the answer depends on where T0 falls, which the
documents do not fix:

- If registration completes at about the RUNNING transition, T0 ≈ S and the gateway has about 60 s
  from `startedAt` to answer 200, less whatever the first S3 read of the registry takes.
- If registration completes during `ACTIVATING` before the containers start (the lifecycle page
  lists both in that state and does not order them), T0 < S and the pull eats the window: with a
  40 s pull after registration, failed checks at S − 40, S − 10 and S + 20 make the target
  `unhealthy` at S + 20, and the scheduler stops the task at S + 60 even if `/readyz` turned 200 at
  S + 25, because recovery needs three passes (S + 50, S + 80, S + 110).
- If registration completes after RUNNING, T0 > S and the window grows by the difference.

The first `DescribeTargetHealth` sample in which the new IP appears gives T0 to within the poll
period (section 2.4), so the harness can settle this on the first Fargate run; it is the single
most important unknown this file leaves open.

What the levers do, for the grilling to weigh (not decided here): widening the grace period widens
the window one-for-one and triggers no deployment; raising U or I widens the ALB window but slows
detection of a bad image; the ECS-recommended fast check (I = 5, healthy 2, unhealthy 2) makes the
ALB window 5 s so the grace alone governs, lets a target that failed early recover 10 s after it
becomes ready, and reads readiness to within 5 s instead of 30. Sources for the mechanisms:
target-group-health-checks, load-balancer-healthcheck, API_CreateService, API_UpdateService,
task-lifecycle-explanation.

## Sources

ECS API reference

- API_Task: <https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_Task.html>
- API_CreateService: <https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_CreateService.html>
- API_UpdateService: <https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_UpdateService.html>
- API_ServiceEvent: <https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_ServiceEvent.html>

ECS developer guide

- task-lifecycle-explanation: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-lifecycle-explanation.html>
- deployment-circuit-breaker: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/deployment-circuit-breaker.html>
- deployment-type-ecs: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/deployment-type-ecs.html>
- ecs_cwe_events: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs_cwe_events.html>
- ecs_task_events: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs_task_events.html>
- ecs_service_deployment_events: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs_service_deployment_events.html>
- service-event-messages: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-event-messages.html>
- service-event-messages-list: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-event-messages-list.html>
- stopped-task-errors: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/stopped-task-errors.html>
- healthcheck (container health checks): <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/healthcheck.html>
- load-balancer-healthcheck (best practice): <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/load-balancer-healthcheck.html>
- load-balancer-connection-draining (best practice): <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/load-balancer-connection-draining.html>
- service-options (best practice): <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-options.html>
- task-recommendations (best practice): <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-recommendations.html>
- fargate-pull-behavior (best practice): <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-pull-behavior.html>
- fargate-tasks-services: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/fargate-tasks-services.html>
- task_definition_parameters: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html>
- ecs-arm64: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-arm64.html>
- task-metadata-endpoint-v4-fargate-response: <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-metadata-endpoint-v4-fargate-response.html>

Elastic Load Balancing

- API_CreateTargetGroup: <https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_CreateTargetGroup.html>
- API_DescribeTargetHealth: <https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_DescribeTargetHealth.html>
- API_TargetHealthDescription: <https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_TargetHealthDescription.html>
- target-group-health-checks (ALB guide): <https://docs.aws.amazon.com/elasticloadbalancing/latest/application/target-group-health-checks.html>
- target-group-register-targets (ALB guide): <https://docs.aws.amazon.com/elasticloadbalancing/latest/application/target-group-register-targets.html>
- edit-target-group-attributes (ALB guide): <https://docs.aws.amazon.com/elasticloadbalancing/latest/application/edit-target-group-attributes.html>
- load-balancer-health-check-logs (ALB guide): <https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-health-check-logs.html>
- load-balancer-cloudwatch-logs (ALB guide): <https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-cloudwatch-logs.html>

AWS CLI reference

- cli ecs wait services-stable: <https://docs.aws.amazon.com/cli/latest/reference/ecs/wait/services-stable.html>
- cli elbv2 wait target-in-service: <https://docs.aws.amazon.com/cli/latest/reference/elbv2/wait/target-in-service.html>

Other AWS first-party

- fargate pricing: <https://aws.amazon.com/fargate/pricing/>
- how-amazon-ecs-manages-cpu-and-memory-resources (AWS Containers Blog, 2019, updated 2020): <https://aws.amazon.com/blogs/containers/how-amazon-ecs-manages-cpu-and-memory-resources/>

Terraform AWS provider v6.68.0

- target_group.go: <https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/internal/service/elbv2/target_group.go>
- lb_target_group docs: <https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/lb_target_group.html.markdown>

Repository

- `infra/modules/cell/alb.tf`, `infra/modules/cell/ecs.tf`, `infra/modules/cell/variables.tf`,
  `infra/modules/cell/versions.tf`, `contracts/pipeline.md`, `src/Platform.Registry/RegistryPoller.cs`,
  `src/Platform.Hosting/Health.cs`, all as of `origin/main` at ce9895e.
