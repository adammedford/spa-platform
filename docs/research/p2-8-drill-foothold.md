# The compromise drill's foothold and its record

- **Ticket:** #1241, "The drill's foothold and its record: a one-off task under the gateway's task
  role without ECS Exec, and what logs each refused call", on the map #1237, "Wayfinder: P2-8
  security cells, a second cell and a compromise drill that reads nothing across".
- **Date:** 2026-11-16, against `origin/main` at `adf0273`.
- **Branch:** `research/p2-8-drill-foothold`, pushed and never merged.
- **Method:** the repository at that commit, AWS's public documentation and AWS's public price-list
  files, all fetched on 2026-11-16. **Nothing was executed against AWS**: no apply, no credential
  step, no AWS CLI call and no read of the deployed cell, and the `aws-mcp` connector failed to
  connect. Every AWS behaviour below is read, not observed. Prices are the price list's US East
  (N. Virginia) figures, because `docs/plan/05-infrastructure-and-cost.md` prices the cell there;
  the project's selected Region was not read. Which plan the project is on (Free Tier or Paid Plan)
  was not read either, so both lists are reported. It decides nothing.

## Answer in brief

1. **The foothold is a second task definition, run once with `RunTask`.** It names cell A's
   existing task role and execution role, carries a drill image, and is launched into the gateway
   service's subnets and task security group. `RunTask` cannot change a container's image, so the
   gateway's own task definition cannot be the foothold once its image is chiseled. ECS Exec is not
   needed: the task runs to completion and writes to the cell's log group.
2. **Nothing is added to the gateway's task role.** The launcher is the operator. What the launch
   needs is `ecs:RunTask` on the drill task definition and `iam:PassRole` on cell A's two roles,
   both on the launcher. The task role holds no `ecs:` or `iam:` action, so the compromised gateway
   cannot launch anything itself.
3. **The bastion task is not the foothold.** Its ticket forbids its task definition from naming the
   gateway's task role, and a test fails if it does. It is the precedent for the shape (a gated
   input, off by default) and it could carry the network half by hand.
4. **Cells cannot be told apart in the role's trust policy.** AWS says `aws:SourceArn` cannot name a
   cluster. `iam:PassRole` on the launcher is the only thing that keeps cell B's role out of cell
   A's cluster. This belongs to the ticket on what cells share.
5. **CloudTrail is the trustworthy record of a refused API call, and it splits in two.** Refused
   Parameter Store and KMS calls are management events: free, in Event history, for 90 days.
   Refused S3 object reads and DynamoDB item reads are data events: they need a trail with data
   event selectors, at $0.000001 an event. The caller's own error is an index into that record, not
   evidence.
6. **A refused network reach mostly leaves no REJECT record.** Flow logs record what security
   groups and network ACLs decide. A packet from cell A to cell B's private address is allowed out
   by cell A's security group and then has no route, so cell A's flow log says ACCEPT and cell B's
   says nothing. The evidence is cell B's flow log showing no accepted flow from cell A's addresses,
   beside proof that the log was live.
7. **Two cells on the root's default address range would make the network half meaningless.** Both
   would be `10.20.0.0/16`, and "cell B's load balancer address" would route inside cell A. This
   belongs to the second cell's ticket.
8. **One sitting costs cents.** The drill task is about $0.02 an hour. Data events and flow logs
   for a sitting are each under one cent at the quantities assumed below. Neither a trail nor a flow
   log is in the cell module's resource-type allowlist today, and the module README says "No VPC
   endpoint, flow log or IPv6 range" in Phase 1.
9. **No analyzer gives a credential-free half.** The only credential-free check is the one the
   project already has: the mock-provider assertions of ADR-0016. Access Analyzer's custom policy
   checks and the policy simulator need credentials but no deployed cell. Reachability Analyzer
   needs the deployed cell, cannot analyse two unconnected VPCs, and appears to be blocked by the
   project's managed policy.

## 1. The foothold: a one-off task under the gateway's task role

### What `RunTask` takes

**Read.** `RunTask` "Starts a new task using the specified task definition." Its
`networkConfiguration` "is required for task definitions that use the `awsvpc` network mode to
receive their own elastic network interface", and holds `subnets`, `securityGroups` and
`assignPublicIp`. So a one-off task takes the same three values the gateway's service passes in
`infra/modules/cell/ecs.tf`: the public subnets (the private ones under `multi_task`), the task
security group, and `assign_public_ip = !var.multi_task`.
<https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_RunTask.html>

**Read.** `overrides` can change a container's `command`, `cpu`, `environment`, `environmentFiles`,
`memory`, `memoryReservation` and `resourceRequirements`. `ContainerOverride` has no `image` field.
`TaskOverride` can replace `taskRoleArn` and `executionRoleArn`.
<https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_ContainerOverride.html>
<https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_TaskOverride.html>

### Three shapes, and which one works

| Shape | Task role | Verdict |
| --- | --- | --- |
| `RunTask` on the gateway's task definition with a `command` override | cell A's | The image cannot be overridden. The P2-9 hardening spec moves the gateway to "a chiseled base", with no shell, so the override could only start a binary the gateway image ships. A drill would have to live in the gateway's image. |
| The bastion task of "The cell declares a bastion task behind an off-by-default input, and tunnel.sh forwards through it" | its own, four `ssmmessages` permissions | Not the foothold for the IAM half. That ticket says "The task definition never names the gateway's task role", and asks for a `terraform test` run that fails when it does. It is in the gateway's subnets and task security group, so a port forward through it is a reach from inside cell A's network: it can carry the network half by hand. |
| A second task definition, behind an off-by-default input, naming `aws_iam_role.task` and `aws_iam_role.task_execution`, with a drill image; run once with `RunTask` | cell A's | Works. Described below. |

### The drill task definition, from what the module already declares

Read from `infra/modules/cell/` at `adf0273`; none of this was applied.

- **The task role is the gateway's, by reference.** `task_role_arn = aws_iam_role.task.arn`. The
  drill then holds exactly the grants a compromised gateway holds, including whichever of the
  session-table and key-ring statements the cell's inputs turn on.
- **The execution role needs no new grant if two things hold.** It may pull from the cell's two ECR
  repositories only, and write streams to the cell's one log group only (`iam.tf`,
  `PullCellImages` and `WriteTaskLogs`). So the drill image is either pushed to one of those two
  repositories or is a public image, and the drill's `awslogs` configuration names the gateway's
  log group with its own stream prefix. A pull from a public registry needs no ECR permission; that
  is how the cell's sample image already runs. Whether a suitable public image exists was not
  checked.
- **No new resource type.** `aws_ecs_task_definition` is already in the allowlist of
  `the_module_declares_only_the_cell_s_intended_resource_types`. No service is declared: nothing
  keeps a drill task running.
- **Tests that must change, as they did for the bastion.** The platform root's type counts (one
  more task definition under the input) and a new assertion that exactly two task definitions name
  the gateway's task role: the gateway's and the drill's. That is a change request against the
  integrator's and workstream 1's tests, in the bastion ticket's form.
- **Teardown.** `teardown.sh` deletes inactive task definitions by exact family name. A drill
  family is one more name, as the bastion's is.

**Read.** ECS Exec is off the table for a different reason than P2-9: it is not needed. A
run-to-completion task writes its standard output to CloudWatch Logs through the `awslogs` driver,
and the sitting reads the stream. ECS Exec would also require a writable root filesystem and the
four `ssmmessages` permissions on the task role, which "A read-only root filesystem with ECS Exec
off" removes from the gateway's role.
<https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html>

### What in IAM permits the launch

**Read.** The Service Authorization Reference gives `RunTask` the resource type `task-definition*`
(`arn:${Partition}:ecs:${Region}:${Account}:task-definition/${TaskDefinitionFamilyName}:${TaskDefinitionRevisionNumber}`),
the condition keys `ecs:cluster`, `ecs:enable-execute-command`, `ecs:capacity-provider`,
`ecs:task-cpu` and `ecs:task-memory` among others, and the dependent action `iam:PassRole` with
`iam:PassedToService` values `ecs-tasks.amazonaws.com, ecs.amazonaws.com`. `ecs:subnet` and
`ecs:auto-assign-public-ip` exist as keys but are not listed on `RunTask`'s row.
<https://docs.aws.amazon.com/service-authorization/latest/reference/list_ecs.html>

**Read.** "A user can pass a role ARN as a parameter in any API operation that uses the role to
assign permissions to the service. The service then checks whether that user has the `iam:PassRole`
permission. To limit the user to passing only approved roles, you can filter the `iam:PassRole`
permission with the `Resources` element of the IAM policy statement." `iam:PassedToService`
"Specifies the service principal of the service to which a role can be passed."
<https://docs.aws.amazon.com/IAM/latest/UserGuide/id_roles_use_passrole.html>
<https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_iam-condition-keys.html>

So the narrowest launcher grant is three statements, all on the launcher and none on the gateway's
role:

- `ecs:RunTask` on the drill family's task-definition ARN, with `ecs:cluster` equal to cell A's
  cluster ARN and `ecs:enable-execute-command` held to false.
- `iam:PassRole` on cell A's task role and execution role ARNs, with `iam:PassedToService` equal to
  `ecs-tasks.amazonaws.com`.
- `ecs:DescribeTasks` and `ecs:StopTask`, and a read of the log group, to collect the result.

**Read from the repository.** No role in `infra/bootstrap/` holds `ecs:RunTask` or `iam:PassRole`:
the CI role's ECS actions are `Describe*` and `List*`, and the teardown role deletes objects. The
launcher today is the operator's own session. Under the new AWS experience human access is managed
by AWS, so whether to write a narrow launcher role at all, or to let the operator's session launch,
is a decision for the drill's own ticket.

**Read, and it matters to the map.** The gateway's task role (`iam.tf`) holds no `ecs:` action and
no `iam:PassRole`. A compromised gateway cannot start a task, in its own cell or another. The
launcher's permission is therefore not something the drill's subject could have used.

**Not verified.** Whether ECS checks `iam:PassRole` at `RunTask` when the request carries no role
override, or only at `RegisterTaskDefinition`. The reference lists it as a dependent action of
both. A grant written as above covers either reading.

### The trust policy cannot separate cells

**Read.** The cell's two roles share one trust document, whose `aws:SourceArn` condition is
`arn:aws:ecs:<region>:<account>:*` (`iam.tf`, `ecs_tasks_trust`). AWS's guidance: "Using the
`aws:SourceArn` condition key to specify a specific cluster is not currently supported, you should
use the wildcard to specify all clusters."
<https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html>

So ECS, acting for any cluster in the project, may assume either cell's task role. With two cells
in one project, the only control that keeps cell B's task role out of a task in cell A's cluster is
`iam:PassRole` on whoever launches or registers. A deployer or pipeline holding `iam:PassRole` on
both cells' roles is the "one compromise that reaches both" the map already lists under "The people
and pipelines above the cells".

The operator cannot assume the task role directly: the trust document names
`ecs-tasks.amazonaws.com` and nothing else. A task is the only way to hold cell A's credentials,
which is why the foothold is a task and not an `sts:AssumeRole` from the workstation.

## 2. What record each refused call leaves

### The caller's own error

The drill's process sees the service's error and prints it. That line reaches the cell's log group
through the `awslogs` driver. It is written by the thing under test. A compromised or simply buggy
drill can print "AccessDenied" for a call it never made, or for a call it aimed at the wrong
resource. It is useful for one thing: it carries the request ID that finds the service's own
record.

### CloudTrail

**Read: a refused call is recorded with its error.** A CloudTrail record's `errorCode` is "The AWS
service error if the request returns an error", and `errorMessage` "includes messages for
authorization failures. CloudTrail captures the message logged by the service in its exception
handling." The record also carries `requestID`, `eventID`, `sourceIPAddress`, `readOnly` and
`managementEvent`.
<https://docs.aws.amazon.com/awscloudtrail/latest/userguide/cloudtrail-event-reference-record-contents.html>

**Read: the record names the task.** A call made with task-role credentials has `userIdentity.type`
`AssumedRole` and an `arn` of the form `arn:aws:sts::<account>:assumed-role/<role>/<session>`. ECS
says: "Task credentials have a context of 'taskArn' that is attached to the session, so CloudTrail
logs show which task the role credentials were vended for." The drill task's id is known from
`RunTask`'s response, so the record can be tied to the drill task and told apart from the running
gateway, which uses the same role.
<https://docs.aws.amazon.com/awscloudtrail/latest/userguide/cloudtrail-event-reference-user-identity.html>
<https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html>

**Read: which of the drill's calls are management events and which are data events.**

| Drill attempt against cell B | Event class | Where it appears | Source |
| --- | --- | --- | --- |
| `ssm:GetParameter`, `GetParameters`, `GetParametersByPath` on the client secrets and the key ring | Management | Event history, free, 90 days | "Systems Manager logs all control plane operations to CloudTrail as management events"; the page's data-event table lists only control channels, managed nodes and execution previews. That `GetParameter` is a control plane operation in this sense is inferred from the two tables, not stated. |
| `kms:Decrypt` on a key | Management | Event history | "CloudTrail logs all AWS KMS operations, including ... cryptographic operations, such as GenerateDataKey and Decrypt"; "You cannot exclude AWS KMS ... events from Event history". |
| `s3:GetObject` on the bucket | Data | A trail or event data store with a data event selector for `AWS::S3::Object` | "By default, CloudTrail doesn't log data events. The CloudTrail Event history doesn't record data events." |
| `dynamodb:GetItem`, `Query` on the session table | Data | A trail with a selector for `AWS::DynamoDB::Table` | DynamoDB lists `GetItem` and `Query` under "DynamoDB data plane events in CloudTrail". |
| `ecs:RunTask` by the launcher | Management | Event history | It also records which role was passed: "`PassRole` is not an API call ... no CloudTrail logs are generated for IAM `PassRole`. To review what roles are passed ... review the CloudTrail log that created or modified the AWS resource receiving the role." |

<https://docs.aws.amazon.com/systems-manager/latest/userguide/monitoring-cloudtrail-logs.html>
<https://docs.aws.amazon.com/kms/latest/developerguide/logging-using-cloudtrail.html>
<https://docs.aws.amazon.com/awscloudtrail/latest/userguide/logging-management-events-with-cloudtrail.html>
<https://docs.aws.amazon.com/AmazonS3/latest/userguide/cloudtrail-logging-s3-info.html>
<https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/logging-using-cloudtrail.html>

**Read: Event history.** "The event history provides a viewable, searchable, downloadable, and
immutable record of the past 90 days of management events in an AWS Region ... There are no
CloudTrail charges for viewing the event history." It "only shows management events. It does not
show data events".
<https://docs.aws.amazon.com/awscloudtrail/latest/userguide/view-cloudtrail-events.html>

**Read: timing.** "CloudTrail typically delivers logs within an average of about 5 minutes of an API
call. This time is not guaranteed." A sitting has to wait before it reads the record.
<https://docs.aws.amazon.com/awscloudtrail/latest/userguide/how-cloudtrail-works.html>

**Not verified.** That a same-account S3 `GetObject` refused by IAM is delivered as a data event
with `errorCode` `AccessDenied`. The record reference describes `errorCode` generally, and S3's
page documents omissions only for cross-account cases ("except in some access denied cases where
log entries are redacted or omitted"). The first drill run settles it; its positive control
(below) shows whether the selector was recording at all.

### VPC flow logs

Flow logs record network reaches, not API calls: section 3. They say nothing about a refused
`GetObject`, which leaves cell A as an ordinary accepted HTTPS flow to S3.

### What the project's plan supports

Two senses of "plan".

**The new AWS experience.** Read from the supported-services page and the managed policy page.

| Service | Free Tier list | Paid Plan list | Not supported | What the managed policy's allowlist holds |
| --- | --- | --- | --- | --- |
| AWS CloudTrail | listed | (inherits) | | `cloudtrail:*`, `cloudtrail-data:*` |
| Amazon Virtual Private Cloud (flow logs are EC2 API actions) | listed | | | `ec2:*`, `logs:*` |
| Amazon Elastic Container Service | listed | | | `ecs:*` |
| AWS Identity and Access Management (the policy simulator is `iam:Simulate*`) | listed | | | `iam:*` |
| AWS Identity and Access Management Access Analyzer | | | listed, with "Policy validation is included in the Free Tier" | Only `access-analyzer:CheckAccessNotGranted`, `CheckNoNewAccess`, `CheckNoPublicAccess` and `ValidatePolicy`, in both the Free Tier and Paid Plan policies. No analyzer can be created, so no external, internal or unused access findings. |
| Reachability Analyzer | not named | not named | not named | Its API actions are `ec2:` and so allowed, but AWS says "Calls to the Tiros endpoint are required for Reachability Analyzer to function. To access Reachability Analyzer, users must also have the same API permissions", and no `tiros:` action is in either allowlist. Read, not tried: it looks blocked. |
| AWS Network Manager | listed | | | `networkmanager:*` |
| Amazon GuardDuty, AWS Security Hub, Amazon Detective | | | listed | |

The custom policy checks are allowed by the policy although the service is on the unsupported
list, whose note mentions policy validation only. The policy is the mechanism; the list is the
summary. Neither was tried.

<https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html>
<https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html>
<https://docs.aws.amazon.com/vpc/latest/reachability/security_iam_required-API-permissions.html>

One more line from the managed policy page that touches a sitting: the policy applied as a project
nears its spend limit denies `ecs:CreateService` and `dynamodb:CreateTable` among others, and does
not list `ecs:RunTask`. `docs/agents/aws-rules.md` already says to ask about the spend limit on a
sudden Access Denied; in a drill, a denial from that policy would look like a pass. The positive
control below is what tells them apart.

**The repository's plan.**

- `infra/modules/cell/README.md`: "No VPC endpoint, flow log or IPv6 range in Phase 1 (ticket 03,
  question 7)."
- The module's resource-type allowlist (`tests/module.tftest.hcl`) holds neither `aws_flow_log`
  nor `aws_cloudtrail`, and a trail needs a bucket and a bucket policy besides. Each is a change
  request, and each wants the bastion's shape: behind an input, off by default.
- `docs/plan/05-infrastructure-and-cost.md` has no line for either. Nothing in the repository
  creates a trail today, so the only CloudTrail record a cell has is Event history.
- ADR-0016 keeps P-03 as mock-provider assertions on the configuration. That is the
  credential-free half, and it already exists for one cell.

### What each costs for one sitting

Unit prices are from AWS's public price-list files for US East (N. Virginia), read 2026-11-16.
Quantities are this finding's assumptions, stated so they can be replaced.

| Item | Unit price | Assumed quantity for one sitting | Cost |
| --- | --- | --- | --- |
| The drill task: 0.25 vCPU and 1 GB, ARM, for one hour | $0.03238 per vCPU-hour, $0.00356 per GB-hour | 0.25 vCPU, 1 GB, 1 hour | $0.0117 |
| Its public IPv4 address (not under `multi_task`) | $0.005 per hour | 1 hour | $0.005 |
| CloudTrail Event history | none: "There are no CloudTrail charges for viewing the event history" | | $0 |
| A trail's first copy of management events | "one copy of your ongoing management events to your Amazon S3 bucket at no charge" | | $0, plus S3 storage |
| CloudTrail data events | $0.000001 per data event recorded | 1,000 events: about 100 drill calls, plus two gateways polling their registries every 30 s for two hours (480), if the selector is not narrowed to the drill | $0.001 |
| Flow logs to CloudWatch Logs | $0.50 per GB of vended logs ingested | 10,000 records at about 113 bytes (the length of AWS's default-format example line), about 1.1 MB | $0.0006 |
| Flow logs to S3 instead | $0.25 per GB delivered | the same | $0.0003 |
| CloudWatch Logs storage | $0.03 per GB-month | the same | negligible |
| Access Analyzer custom policy check | $0.002 per API request | 20 checks | $0.04 |
| Reachability Analyzer | $0.10 per analysis | 4 paths | $0.40, if it runs at all |
| Network Access Analyzer | $0.002 per network interface processed | not sized | |
| Access Analyzer internal access analysis | $9.00 per monitored resource | not available: no analyzer can be created | |
| IAM policy simulator | **unsourced**: the price list has no IAM offer and no page read here states a price | | |

The flow-log record size is an estimate from one example line, not a published figure. S3 storage
for a trail's files was not priced; at these volumes it rounds to nothing.

Price-list files:
`https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/<offer>/current/us-east-1/index.json` for
`AWSCloudTrail` (published 2026-11-02), `AmazonVPC` (2026-11-05), `AmazonCloudWatch` (2026-11-16),
`AWSIAMAccessAnalyzer` (2026-11-08) and `AmazonECS` (2026-11-11).

The cost that matters is not in this table: the second cell standing for the sitting. That is the
second cell's pricing research.

### Which record is trustworthy when the caller is the thing under test

| Record | Who writes it | Can the task role alter it | Verdict |
| --- | --- | --- | --- |
| The drill's printed error, in the cell's log group | The drill | It is the author | An index. Not evidence. |
| CloudTrail Event history | CloudTrail, from the service's side of the call | No: the role holds no `cloudtrail:` action, and the history is "immutable" | Evidence for the Parameter Store and KMS attempts. |
| A trail's data events in S3 | CloudTrail | No, if the trail's bucket is outside every cell's grants | Evidence for the S3 and DynamoDB attempts. |
| Flow logs | The VPC | No: the role holds no `ec2:` or `logs:` action beyond the execution role's stream writes, which are a different role | Evidence for the network half, with the limits in section 3. |
| `RunTask`'s own management event | CloudTrail | No | Evidence of what the drill was: which task definition, role, subnets and security group. |

Three rules follow, each from the reading above and none of them run.

- **Join on the request ID.** The drill prints each call's request ID; the sitting finds the
  CloudTrail record with that `requestID`, the drill task's session in `userIdentity`, cell B's
  resource in the request, and `errorCode` set. A refusal counts when the service's record says so.
- **A refusal counts only beside a control that succeeds.** In the same run, under the same
  credentials, the drill reads cell A's own registry object, client secret and session item, and
  those calls appear in the same records without an error. Without the control, "every call was
  refused" is also what a drill with no network, expired credentials, a project paused at its spend
  limit or a selector that records nothing looks like.
- **An absent record is not a refusal.** CloudTrail's delivery "is not guaranteed" in time, and
  data events are recorded only where a selector matches. The control is what shows the selector
  matched.

**Not verified, and worth one line in the drill's ticket.** The identity provider half (cell A's
client secret tried against cell B's clients) is not an IAM call. Its record is the identity
provider's, not CloudTrail's, and this finding did not look at it.

## 3. How a refused network reach is observed and recorded

### What the network is

Read from `infra/modules/cell/network.tf` and `alb.tf` at `adf0273`. Each cell is its own VPC. The
module declares no peering connection, no transit gateway and no shared endpoint, so two cells have
no private route to each other. The load balancer is `internal = true`. The task security group
allows egress on TCP 443 to anywhere and nothing else; it allows ingress on the gateway port from
the load balancer's security group only. Without `multi_task` the task has a public address.

### What a flow log records

**Read.** A flow log record's `action` is "ACCEPT — The traffic was accepted" or "REJECT — The
traffic was rejected. For example, the traffic was not allowed by the security groups or network
ACLs, or packets arrived after the connection was closed." The record is per network interface.
Flow logs report what security groups and network ACLs decided at an interface. They do not report
whether a packet was delivered.
<https://docs.aws.amazon.com/vpc/latest/userguide/flow-log-records.html>
<https://docs.aws.amazon.com/vpc/latest/userguide/flow-logs-records-examples.html>

**Read.** "The flow log service typically delivers logs to CloudWatch Logs in about 5 minutes and
to Amazon S3 in about 10 minutes. However, log delivery is on a best effort basis". The default
aggregation interval is 10 minutes; 1 minute can be chosen.

### Each attempt, and the record it should leave

This table is reasoning from the documents above and the module. None of it was observed.

| Attempt from cell A's task | What stops it | Cell A's flow log | Cell B's flow log | The caller sees |
| --- | --- | --- | --- | --- |
| Cell B's load balancer, private address, port 443 | No route between the VPCs. The packet matches the default route to the internet gateway, which translates only public addresses. | ACCEPT: the task security group allows 443 out | Nothing | A connection timeout |
| Cell B's task, private address, gateway port | Cell A's task security group: egress is 443 only | REJECT, on cell A's task interface | Nothing | A timeout |
| Cell B's task, private address, port 443 | No route | ACCEPT | Nothing | A timeout |
| Cell B's task, public address, port 443 (without `multi_task`) | Cell B's task security group: no ingress rule for 443 or for cell A | ACCEPT | REJECT, on cell B's task interface, from cell A's public address | A timeout |
| Cell B's task, public address, gateway port | Cell A's egress rule | REJECT | Nothing | A timeout |

Three things follow.

- **"Refused and recorded" cannot mean "a REJECT record" for the load balancer.** The reach that
  matters most leaves ACCEPT on the sender's side and nothing on the target's. The record is cell
  B's flow log holding no accepted flow from cell A's addresses in the drill's window.
- **That is evidence by absence, so it needs a control.** Cell B's own traffic in the same window
  (its load balancer's health checks to its task, accepted) shows the flow log was live. Cell A's
  ACCEPT record for the attempt, with its timestamp and destination, shows the attempt was made.
  The pair is the record. Neither is written by the drill.
- **Both cells need the flow log.** One on cell A alone shows a packet left. One on cell B alone
  cannot show an attempt was made.

The internet gateway's behaviour toward a private destination is read from "The internet gateway
logically provides the one-to-one NAT on behalf of your instance" and "traffic that's destined for
the public IPv4 address ... has its destination address translated"; the page does not say in one
sentence that a private destination is dropped.
<https://docs.aws.amazon.com/vpc/latest/userguide/VPC_Internet_Gateway.html>

### The address ranges must differ

**Read from the repository.** `infra/envs/poc/platform/variables.tf` defaults `vpc_cidr` to
`10.20.0.0/16`, and the module carves its subnets from fixed indices. A second cell applied from
the same root with the default would have the same range and the same subnet blocks. From inside
cell A, cell B's load balancer address would then be an address in cell A's own VPC: local route,
never leaving. The attempt would reach nothing, or reach cell A's own load balancer, and a
"connected" result would be a false alarm about a reach that never left the cell. The drill's
network half means something only when the two ranges do not overlap.

### Creating the flow log

**Read.** A flow log to CloudWatch Logs needs an IAM role that `vpc-flow-logs.amazonaws.com` can
assume, with log-stream write permissions, and the creator needs `iam:PassRole` on it. That is one
more role in the cell or beside it, and one more `iam:PassRole` for whoever applies.
<https://docs.aws.amazon.com/vpc/latest/userguide/flow-logs-iam-role.html>

"After you create a flow log, you can't change its configuration or the flow log record format",
so the format is chosen once. The default format has `srcaddr`, `dstaddr`, ports and `action`,
which is what the table above reads; `pkt-dstaddr` exists for cases where the interface address
and the packet's address differ.
<https://docs.aws.amazon.com/vpc/latest/userguide/flow-logs-limitations.html>

## 4. Prior art, and whether any of it gives a credential-free or cheaper half

### Published drills

**Read.** AWS's SaaS Lens asks for exactly this kind of test, in general terms: "Validate tenant
isolation: Simulate interactions with your system to help ensure that the tenant isolation policies
and practices are being successfully applied. Inject tenant tokens that attempt to simulate a SaaS
identity. Create tests that attempt to change the tenant context by injecting a new tenant
identifier. Verify that the injection is blocked from crossing a tenant boundary." It names no
mechanism for the record.
<https://wa.aws.amazon.com/saas.question.REL_3.en.html>

No AWS-published, step-by-step isolation drill for two workloads in one account was found in the
time spent. Third-party attack-simulation tools were not read; nothing here rests on them.

### The three analyzers

| Tool | Needs credentials | Needs the deployed cell | What it answers | What it cannot |
| --- | --- | --- | --- | --- |
| The mock-provider assertions the project has (ADR-0016, P-03) | No | No | That the configuration's task-role policy names this cell's resources only | Anything the provider computes at apply, and any policy not in the configuration |
| Access Analyzer `CheckAccessNotGranted` | Yes | No: it takes a policy document | "Checks whether the specified access isn't allowed by a policy", given actions and resources. $0.002 a request. | It reads one policy. It does not combine the bucket policy, the trust policy or the managed policies. |
| IAM `SimulateCustomPolicy` | Yes | No: policies are passed as strings | "The simulation does not perform the API operations; it only checks the authorization" | "The simulation does not automatically retrieve policies for the specified resources"; "does not support resource control policies (RCPs)" |
| IAM `SimulatePrincipalPolicy` | Yes | Yes: it reads the role's attached policies | The same, for the role as deployed | The same limits; "Simulation of resource-based policies isn't supported for IAM roles" |
| Access Analyzer internal access findings | Yes | Yes | Which principals in the account can reach a chosen resource | Not available in the project: no analyzer can be created. $9.00 a monitored resource where it is. |
| Reachability Analyzer | Yes | Yes | A hop-by-hop path, or the component that blocks it | "The source and destination resources must be in the same VPC or in VPCs that are connected through a VPC peering connection or a transit gateway." Two cells are neither, so the cross-cell path cannot be posed. It also appears blocked (section 2). |

<https://docs.aws.amazon.com/access-analyzer/latest/APIReference/API_CheckAccessNotGranted.html>
<https://docs.aws.amazon.com/IAM/latest/UserGuide/access-analyzer-custom-policy-checks.html>
<https://docs.aws.amazon.com/IAM/latest/UserGuide/access_policies_testing-policies.html>
<https://docs.aws.amazon.com/IAM/latest/APIReference/API_SimulateCustomPolicy.html>
<https://docs.aws.amazon.com/IAM/latest/APIReference/API_SimulatePrincipalPolicy.html>
<https://docs.aws.amazon.com/vpc/latest/reachability/how-reachability-analyzer-works.html>
<https://docs.aws.amazon.com/vpc/latest/reachability/what-is-reachability-analyzer.html>

### What that leaves

- **Credential-free:** only the mock-provider assertions. The map already keeps "a plan-policy
  check for two cells, run without credentials" in CI; this finding found nothing from AWS that
  replaces or extends it without credentials.
- **Cheaper than a standing second cell:** `SimulatePrincipalPolicy` against cell A's deployed role,
  with cell B's resource ARNs written out by hand, asks the IAM half's question with one cell
  standing and no second cell. It is an argument from the evaluator, not an execution, and the map
  decided the drill is executed. It could serve as a rehearsal before the sitting, or as the check
  that the drill's list of attempts is the right list.
- **Nothing for the network half.** Reachability Analyzer cannot pose the question, and the answer
  for two unconnected VPCs is structural: no route exists. The module's own tests already hold that
  no peering or gateway type is declared.

## What was executed and what was only read

**Executed.**

- `git`, `gh` and file reads in the repository at `origin/main` `adf0273`, on 2026-11-16:
  `infra/modules/cell/` (`iam.tf`, `ecs.tf`, `network.tf`, `alb.tf`, `storage.tf`, `variables.tf`,
  `README.md`, `tests/module.tftest.hcl`), `infra/bootstrap/` (`ci-role.tf`, `teardown-role.tf`),
  `infra/envs/poc/platform/variables.tf`, `docs/agents/aws-rules.md`,
  `docs/plan/05-infrastructure-and-cost.md`, and the tickets "P2-9 hardening: the image, its supply
  chain and the task's runtime" and "The cell declares a bastion task behind an off-by-default
  input, and tunnel.sh forwards through it".
- `curl` fetches of the public pages and price-list files below, and text searches over them.
- A search of the managed policy page's text for `tiros`, which found nothing.

**Not executed.** No `terraform` command, no AWS CLI or SDK call, no `RunTask`, no flow log, no
trail, no simulator call, no policy check. No statement here about what AWS does was observed.

**Read, each on 2026-11-16.**

- ECS API: `API_RunTask.html`, `API_ContainerOverride.html`, `API_TaskOverride.html` under
  <https://docs.aws.amazon.com/AmazonECS/latest/APIReference/>
- ECS developer guide: `task-iam-roles.html`, `ecs-exec.html` under
  <https://docs.aws.amazon.com/AmazonECS/latest/developerguide/>
- Service Authorization Reference, ECS:
  <https://docs.aws.amazon.com/service-authorization/latest/reference/list_ecs.html> (the older
  `list_amazonelasticcontainerservice.html` now redirects to the guide's index)
- IAM: `id_roles_use_passrole.html`, `reference_policies_iam-condition-keys.html`,
  `access_policies_testing-policies.html`, `what-is-access-analyzer.html`,
  `access-analyzer-custom-policy-checks.html` under
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/>; `API_SimulateCustomPolicy.html` and
  `API_SimulatePrincipalPolicy.html` under <https://docs.aws.amazon.com/IAM/latest/APIReference/>;
  <https://docs.aws.amazon.com/access-analyzer/latest/APIReference/API_CheckAccessNotGranted.html>
- CloudTrail: `logging-management-events-with-cloudtrail.html`,
  `logging-data-events-with-cloudtrail.html`, `view-cloudtrail-events.html`,
  `cloudtrail-event-reference-record-contents.html`, `cloudtrail-event-reference-user-identity.html`,
  `how-cloudtrail-works.html`, `cloudtrail-log-file-examples.html` under
  <https://docs.aws.amazon.com/awscloudtrail/latest/userguide/>
- Per-service CloudTrail pages:
  <https://docs.aws.amazon.com/systems-manager/latest/userguide/monitoring-cloudtrail-logs.html>,
  <https://docs.aws.amazon.com/kms/latest/developerguide/logging-using-cloudtrail.html>,
  <https://docs.aws.amazon.com/AmazonS3/latest/userguide/cloudtrail-logging-s3-info.html>,
  <https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/logging-using-cloudtrail.html>
- VPC: `flow-logs.html`, `flow-log-records.html`, `flow-logs-records-examples.html`,
  `flow-logs-limitations.html`, `flow-logs-iam-role.html`, `VPC_Internet_Gateway.html` under
  <https://docs.aws.amazon.com/vpc/latest/userguide/>; `what-is-reachability-analyzer.html`,
  `how-reachability-analyzer-works.html`, `security_iam_required-API-permissions.html` under
  <https://docs.aws.amazon.com/vpc/latest/reachability/>
- The new AWS experience:
  <https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html>,
  <https://docs.aws.amazon.com/accounts/latest/reference/scps-and-rcps-for-projects.html>
- SaaS Lens: <https://wa.aws.amazon.com/saas.question.REL_3.en.html>
- Price lists, US East (N. Virginia): the five offers named under "What each costs for one
  sitting".

**Open after this finding.**

- Whether a refused same-account `GetObject` is delivered as a data event with `AccessDenied`.
- Whether `iam:PassRole` is checked at `RunTask` with no override.
- Whether Reachability Analyzer and the custom policy checks run in the project at all.
- The project's selected Region and plan, and so the prices that apply.
- Which image the drill runs, and whether it is public or pushed to a cell repository.
- The identity provider half's record.
