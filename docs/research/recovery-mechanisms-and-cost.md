# What AWS offers to restore the session table, the key ring and the bucket, and what each costs

- **Ticket:** #1256, "What AWS offers to restore the session table, the key ring and the bucket, and
  what each costs", on the map #1251 (P2-10 Assurance).
- **Date:** 2026-11-16
- **Branch:** `research/recovery-mechanisms-and-cost`, from `origin/main` at `adf0273`. Pushed, never
  merged.
- **Method:** AWS's public documentation and price pages, AWS's public price list files for
  US East (Ohio), `us-east-2`, the project's Region (`tools/aws-project.env`), Microsoft's
  data-protection documentation, the Terraform provider's documentation at the pinned v6.68.0, and
  this repository read at `adf0273`. Docs-only: no `terraform plan` or `apply`, no AWS credential
  step, no read of the deployed cell, no fault injected. The `aws-mcp` connector failed to connect,
  so nothing was read through it. Every duration below is a document's statement or is marked as
  derived or **unconfirmed**; none is a measurement. The closing section lists what was executed and
  what was only read.

This finding decides nothing. It is read by the recovery-objective and runbook decisions on the map.

## Answer in brief

1. **The cell's Terraform turns on one recovery mechanism for data: S3 versioning.** The session
   table has no point-in-time recovery, no backup and no deletion protection (`sessions.tf` sets
   none; the provider defaults both to `false`). The key ring is not in Terraform at all: the gateway
   writes it at run time, under the account's AWS managed `aws/ssm` key, and no customer managed KMS
   key exists at G6.
2. **A DynamoDB restore always makes a new table and has no documented duration.** Point-in-time
   recovery restores to any second in a 1 to 35 day window, up to about five minutes before now.
   AWS gives no restore time: "no fixed completion time is guaranteed", and it tells the operator to
   measure it. The restored table comes back without its time-to-live setting, tags, IAM policies
   and point-in-time recovery, and the cell's table name is fixed, so a restore beside a live table
   cannot simply be swapped in.
3. **The session table's cheapest recovery is an empty table.** Sessions are the only data; an
   empty table signs every user out and loses nothing else. Point-in-time recovery costs $0.20 per
   GB-month of table size and stops billing when the table is destroyed. An on-demand backup costs
   $0.10 per GB-month, outlives its table, cannot be tagged, and so keeps billing after a teardown
   until it is deleted by hand.
4. **A lost key ring cannot be restored by anything AWS offers.** Each data-protection key is its
   own parameter, written once, so parameter history (100 versions) holds no earlier copy, and no
   AWS document read describes undeleting a parameter. Microsoft: after a key is deleted "all data
   protected by the key is permanently undecipherable". The cost to users is every session, every
   cookie and every login in flight; the cell itself recovers when the gateway writes a new ring.
5. **A KMS key can be lost only if a customer managed key is introduced.** AWS managed keys cannot
   be deleted. A customer managed key has a 7 to 30 day waiting period (default 30), is unusable
   during it, can be cancelled until it ends (it returns `Disabled`), and after it "you cannot
   recover a deleted KMS key". It costs $1 per month, prorated hourly, and nothing while pending
   deletion.
6. **S3 versioning restores a deleted or overwritten object in one call**, and the bucket policy
   already refuses deletes and overwrites under `apps/`. S3 stores across at least three zones, so a
   zone loss loses no object. In an S3 outage the running tasks stay healthy to the ALB and keep the
   last registry snapshot, every asset request fails, and a task started during the outage never
   becomes ready.
7. **ECS replaces a killed task and a lost zone's task by itself, with no documented time.** The
   time is the startup benchmark's two clocks: pull-and-place, then task start to target healthy.
   The ALB takes 60 to 90 s to mark a silently dead target unhealthy at the cell's effective
   settings (derived). **A two-task cell does not survive the loss of its first zone as built:** the
   one NAT gateway is there, and the surviving task loses Parameter Store, ECR, logs and the
   provider.
8. **AWS Fault Injection Service covers three of the seven failures and costs $0.10 per
   action-minute, with no idle charge.** It can stop a task and cut a subnet off from S3, from
   DynamoDB or from the other zone, and it restores the network ACL itself. It cannot stop Fargate
   capacity in a zone, lose a table, lose a parameter or touch a KMS key; those are plain CLI steps.
9. **Every option's idle price is cents or zero at this cell's size.** The exceptions to "off when
   destroyed" are an on-demand backup, a system backup kept 35 days after a table with
   point-in-time recovery is deleted (no charge), and a customer managed key held outside the
   platform root ($1 a month).

## 1. DynamoDB: the session table

### What the cell configures

`infra/modules/cell/sessions.tf` declares `aws_dynamodb_table.sessions` only under
`session_store = "dynamodb"`: on-demand billing, key `pk`, a keys-only index on `appId`,
time to live on `expiresAt`, default encryption. It sets no `point_in_time_recovery` block and no
`deletion_protection_enabled`. The provider's documentation at v6.68.0: `deletion_protection_enabled`
"Defaults to `false`", and for point-in-time recovery "If the `point_in_time_recovery` block is not
provided, this defaults to `false`". The name is fixed in `iam.tf`:
`session_table_name = "${local.name_prefix}-sessions"`. **Turned on today: none of the three.**

### Point-in-time recovery

- The window: "up to 35 days of recovery points at a per second granularity"; `RecoveryPeriodInDays`
  is 1 to 35, default 35. "`LatestRestorableDateTime` is typically five minutes before the current
  time." So the data-loss bound of a restore to the latest point is about five minutes of writes.
- "The point-in-time recovery process always restores to a new table."
- Turning it off and on again resets the window: "you can only immediately restore that table using
  the `LatestRestorableDateTime`."
- The provider notes "It can take 10 minutes to enable for new tables."
- Deleting a table that has it on: "DynamoDB automatically creates a backup snapshot called a
  *system backup* and retains it for 35 days (at no additional cost)", named
  `<table-name>$DeletedTableBackup`, and it restores "to a single point in time", "a snapshot of the
  table right before the table is deleted". The second-by-second history does not survive the
  delete.

### On-demand backup

- "DynamoDB backup requests are processed instantaneously and become available for restore within
  minutes." "Backup and restore actions run with zero impact on table performance or availability."
- "You can only restore the entire table data to a new table from a backup."
- A backup outlives its table: AWS's restore procedure "assumes that the `Music` table no longer
  exists before restoring it", and restores to the same name.
- "Tagging and attribute-based access control (ABAC) aren't supported for DynamoDB backups." A
  backup therefore carries no platform tag, and `teardown.sh`'s tag query cannot see one.

### Restore time

AWS documents no duration. For point-in-time recovery: "The restore duration depends on the amount
of data and the number of secondary indexes that DynamoDB rebuilds on the new table—larger tables
and more indexes take longer, and no fixed completion time is guaranteed." For backups: "Restore
times are directly related to the configuration of your tables (such as the size of your tables and
the number of underlying partitions) and other related variables. A best practice when planning for
disaster recovery is to regularly document average restore completion times and establish how these
times affect your overall Recovery Time Objective." Both pages add that a restore is faster with
secondary indexes excluded. **The restore time for the session table is unconfirmed and has to be
measured in a rehearsal.**

### What a restore does not bring back, and what that means for the Terraform

Restored: table data, global and local secondary indexes, capacity settings, encryption settings.
"You must manually set up the following on the restored table: Auto scaling policies, IAM policies,
Amazon CloudWatch metrics and alarms, Tags, Stream settings, Time to Live (TTL) settings, Point in
time recovery settings." Deletion protection is "off by default. This includes global replicas, and
tables restored from backups."

For this cell:

- **Time to live is lost.** The store's absolute expiry sweep (`expiresAt`) is off on a restored
  table until it is set again.
- **The platform tags are lost**, so the tag query in `teardown.sh` and P-05 would not find the
  restored table until it is tagged.
- **The name cannot move.** The task role's grant, the gateway's `Session__TableName` and the P-01
  tripwire all follow `local.session_table_name`. A restore needs a new name while the original
  exists, so recovering a *corrupted* table takes one of two routes: restore under a second name
  and change the cell to use it (a module change, by change request), or restore under a second
  name, delete the original, and restore again to the original name. Deleting the original first
  is not a route: that reduces the history to the one system backup taken at deletion.
- **A deleted table restores straight to its own name**, from the system backup or an on-demand
  backup. Terraform's state still names that table. Whether the next `apply` then re-applies time
  to live, tags and point-in-time recovery in place, or needs an import, is **unconfirmed**: no plan
  was run. The provider offers `restore_source_name`, `restore_date_time`, `restore_to_latest_time`
  and `restore_backup_arn` on `aws_dynamodb_table`, which create a table as a restore; whether
  adding them to the existing resource replaces it is also **unconfirmed**.
- **Deletion protection blocks the teardown.** "When deletion protection is enabled for a table, it
  cannot be deleted by anyone", which includes the platform root's destroy. It would have to be a
  gated input that the teardown switches off first, or it breaks the map's cost rule.
- **The alternative needs no restore.** Sessions are the table's only content. An `apply` that
  creates an empty table recovers the cell; the data lost is every session, so every user signs in
  again.

### Resilience without any of the above

"Amazon DynamoDB automatically replicates your data across three Availability Zones in a Region".
A lost zone does not lose the table.

## 2. The key ring, Parameter Store and KMS

### What the cell configures

The ring is written by the gateway, not by Terraform: `src/Platform.Hosting/KeyRing.cs` calls
`PersistKeysToAWSSystemsManager(prefix, …)` with `options.KMSKeyId` from
`DataProtection:KmsKeyId`. The module passes the prefix
`/spa-platform/{cell}/data-protection/` and an empty key id at G6
(`variables.tf`: "Empty at G6 (#332 D6: no customer-managed key)"), so the parameters are
SecureStrings under the account's `aws/ssm` key. The task role holds `ssm:PutParameter` and
`ssm:GetParametersByPath` on the prefix and "deliberately no `ssm:DeleteParameter`"
(`iam.tf`). `teardown.sh` deletes the ring as the operator, because "no root's state holds" it.
No `aws_kms_key` resource exists in `infra/`. **Turned on today: nothing that restores a ring; the
only protection is that the gateway cannot delete one.**

### Parameter versions and history

"Each time you edit the value of a parameter, Parameter Store … creates a new *version* of the
parameter and retains the previous versions." "You can create and maintain up to 100 versions of a
parameter. After you have created 100 versions of a parameter, each time you create a new version,
the oldest version of the parameter is removed from history". An earlier version is read as
`name:version`, and it is "a layer of protection if a parameter value is accidentally changed".

What history does and does not cover here:

- **An overwritten key parameter** can be read back from its history. The task role can overwrite
  (it holds `PutParameter`); nothing in the platform does.
- **A deleted parameter** is not covered. `DeleteParameter`'s reference says only "After deleting a
  parameter, wait for at least 30 seconds to create a parameter with the same name". No AWS page
  read describes an undelete, a recycle bin or a backup for Parameter Store. That history goes with
  the parameter is the natural reading and is **unconfirmed** by an explicit AWS statement.
- A key is a parameter of its own, written once and never edited (each ring key is put as a new
  parameter; `iam.tf`'s comment "PutParameter writes a new key"), so in normal operation every ring
  parameter is at version 1 and history holds nothing extra.

### KMS key deletion

- Scope: "You can only schedule the deletion of a customer managed key. You cannot delete AWS
  managed keys or AWS owned keys." **Under the cell's default, the ring's KMS key cannot be lost by
  deletion.** This half of the failure exists only once `data_protection_kms_key_id` names a
  customer managed key.
- Window: "AWS KMS requires you to set a waiting period of 7 – 30 days. The default waiting period
  is 30 days", and it "might be up to 24 hours longer".
- During it: "A KMS key pending deletion cannot be used in any cryptographic operations." The
  effect is "almost immediate (subject to eventual consistency)". So the next ring read or write
  fails, and a running task keeps working on the ring it already holds in memory until it restarts
  or re-reads the ring (derived, not tested).
- Cancellation: "To recover the KMS key, you can cancel key deletion before the waiting period
  ends." `CancelKeyDeletion`: "When this operation succeeds, the key state of the KMS key is
  `Disabled`. To enable the KMS key, use `EnableKey`."
- Unrecoverable: "After the waiting period ends you cannot cancel key deletion". Deletion "deletes
  the key material and all metadata associated with the KMS key and is irreversible. After a KMS key
  is deleted, you can no longer decrypt the data that was encrypted under that KMS key". For a
  symmetric key, "You cannot create a new symmetric encryption KMS key that can decrypt the
  ciphertexts of a deleted symmetric encryption KMS key, even if you have the same key material."
- The reversible stand-in: "consider disabling the KMS key instead of deleting it. You can
  re-enable a disabled KMS key".

### What is unrecoverable, and what it costs users

Unrecoverable: a deleted ring parameter (no AWS mechanism read), and every ring parameter once its
customer managed key is past the waiting period. Microsoft, on the framework's side: "At that
point, all data protected by the key is permanently undecipherable, and there's no emergency
override like there's with revoked keys. Deleting a key is truly destructive behavior."

What the ring protects in this platform, from the code:

- The session tickets in the table. `DynamoDbSessionStore.cs`: "A session ticket in the table could
  not be unprotected with this gateway's key ring; such sessions read as absent." Every stored
  session is lost, though its row stays until time to live removes it.
- The session cookie and the framework's other protected cookies, so every signed-in browser is
  signed out.
- Every login in flight: the framework's OpenID Connect handler, which `OidcOptionsSetup.cs`
  configures, protects the state it sends to the provider and its correlation and nonce cookies with
  the data-protection system, so a callback that returns after the loss fails and the user starts
  again. That is the framework's default as this finding understands it; Microsoft's page for it
  was not read, so it is **unconfirmed** against a primary source.

The cell recovers without a restore: with the prefix empty, the gateway generates a new key on the
next start or key-ring refresh ("a new key will be generated with immediate activation"), and with
the key readable again nothing else is needed. Keys otherwise last 90 days and a new key activates
two days after creation, so a ring normally holds more than one key and losing only the newest
loses only what was protected under it.

## 3. S3: assets and the registry document

### Versioning as the cell configures it

`storage.tf`: `aws_s3_bucket_versioning.platform` with `status = "Enabled"`, "part of the S3
contract and not an operational detail". No lifecycle rule expires noncurrent versions, so every
version is kept until the teardown. The bucket policy adds two refusals under `apps/`: a write
without `If-None-Match` (a release is written once) and any `DeleteObject` or `DeleteObjectVersion`
by a principal other than the teardown role. MFA delete and Object Lock are not configured.
**Turned on today: versioning, and the release prefix's write-once and no-delete policy.**

### Restoring a deleted or overwritten object

- Delete: "When versioning is enabled, a simple `DELETE` cannot permanently delete an object …
  Instead, Amazon S3 inserts a delete marker", and a `GET` then "returns a 404 error". Removing the
  delete marker makes the earlier version current again.
- Overwrite: two documented routes. "Copy a previous version of the object into the same bucket.
  The copied object becomes the current version of that object and all object versions are
  preserved", or "Permanently delete the current version", which "turn[s] the previous version into
  the current version".
- The limit: "To delete versioned objects permanently, you must use `DELETE Object versionId`", and
  "When you permanently delete an object version, the action cannot be undone." "You can undelete
  an object only if it was deleted as the latest (current) version."
- No duration is documented; each route is a single API call.

For this cell: a release object under `apps/` can be neither deleted nor overwritten by anyone but
the teardown role, so only the registry document needs restoring. `spactl registry rollback`
already does that from the version history, by republishing the earlier version under a new
revision (`docs/runbooks/registry-rollback.md`), and the gateway ignores a bad document and keeps
its last good snapshot (test F-09). The registry's version history is not protected by the
`apps/` statement: an operator with `s3:DeleteObjectVersion` can remove it for good.

### What a zone outage and an S3 outage look like

- **A zone.** S3 Standard "redundantly store[s] objects on multiple devices across a minimum of
  three Availability Zones" and is "designed to sustain data in the event of the loss of an entire
  Amazon S3 Availability Zone". No object is lost and the bucket stays reachable.
- **The load balancer in a zone loss.** It has a node in each of its two subnets. "If one
  Availability Zone becomes unavailable or has no healthy targets, the load balancer can route
  traffic to the healthy targets in another Availability Zone." Cross-zone balancing "is always
  enabled at the load balancer level" for an ALB. The DNS entry has a "time-to-live (TTL) of 60
  seconds", and in a zone power loss "some AWS services may take up to a few minutes to update DNS
  to reflect that private endpoints in the affected AZ are not available". A client holding the
  lost node's address fails until it resolves again. The workstation reaches the ALB through a
  Session Manager tunnel whose hop is a gateway task, so a tunnel through the lost zone's task
  ends with it.
- **An S3 outage.** `/readyz` answers 200 "once a snapshot is live" (`contracts/pipeline.md`), and
  the registry "keeps serving its last good snapshot" (`docs/gaps.md`, GAP-16). So the running
  tasks stay healthy to the ALB and keep routing, signing in and proxying `/api`, while "an S3
  outage stops every app's assets": there is no cache in front of S3. A task that starts during the
  outage has no snapshot, answers 503, and is stopped and replaced after the 60 s grace period, over
  and over; a deployment in that window fails and the circuit breaker rolls it back. If every
  target were unhealthy at once, "the load balancer fails open" and sends traffic to all of them.
  The status the gateway returns for an asset whose S3 read fails was not read from the code and is
  **unconfirmed** here.
- Nothing restores an S3 outage; it is waited out. The data lost is none.

## 4. ECS: a killed task and a lost zone

### The service's settings

`infra/modules/cell/ecs.tf` and `alb.tf`, under `multi_task = true`: desired count 2, Fargate, two
private subnets in two zones, `availability_zone_rebalancing = "ENABLED"`,
`deployment_minimum_healthy_percent = 100`, `deployment_maximum_percent = 200`, circuit breaker on
with rollback, `health_check_grace_period_seconds = 60`, `deregistration_delay = 30`. The target
group's effective check, from the startup-clocks finding
(`research/ecs-alb-startup-clocks`, section 3): interval 30 s, healthy threshold 3, unhealthy
threshold 3, the provider's defaults. Zonal shift is not enabled on the load balancer
(`alb.tf` sets no such attribute). On the default, `multi_task = false`, there is one task.

### A killed task

- Replacement is automatic: "If one of your tasks fails or stops, the Amazon ECS service scheduler
  launches another instance of your task definition to replace it."
- AWS documents no replacement time. It is the startup benchmark's two clocks end to end:
  pull-and-place (`createdAt` to `startedAt`) and task start to target healthy. The earlier finding
  already answers the parts: Fargate "caches nothing, so every task start pulls the whole image";
  "a new target needs one pass" to be healthy; and AWS's own illustrative example runs 20.4 s from
  PENDING to RUNNING. The measured numbers for this gateway belong to the startup benchmark map
  (#169), not to this finding.
- What users see depends on how the task died. A task stopped through the API is deregistered and
  drains for the 30 s `deregistration_delay`. A task that dies silently stays a target until the
  ALB marks it unhealthy: three consecutive failures at a 30 s interval, so 60 to 90 s after the
  death plus one check timeout (derived from the earlier finding's section 3; not measured). With
  two tasks the other serves throughout; with one task the cell is down for the whole replacement.
- Data lost: none under `session_store = "dynamodb"` with the shared ring. On the Phase 1 default,
  sessions and keys are in the task's memory and every user is signed out
  (`docs/runbooks/README.md`).

### A lost zone

- The scheduler: "if the underlying infrastructure fails, the service scheduler reschedules a
  task", and on Fargate it "will try its best to spread tasks across accessible Availability
  Zones". Rebalancing afterwards "start[s] a task in Availability Zone 2 before stopping a task in
  Availability Zone 1", once the zone is back. AWS documents no time for either.
- The ALB stops sending to the lost task as above, 60 to 90 s (derived), and the surviving zone's
  node keeps serving.
- **The one NAT gateway is a single-zone dependency.** `network.tf`: "one NAT gateway in the first
  zone's public subnet, which is the saving over one per zone (about $33 a month each) and the
  reason zone failure stays out of G6's scope (#332 F15)". S3 and DynamoDB go through gateway
  endpoints and survive; Parameter Store (client secrets and the ring), ECR (the pull for any
  replacement task), CloudWatch Logs, the tunnel's `ssmmessages` and the provider's OAuth endpoints
  go through the NAT. If the first zone is lost, the surviving task cannot sign anyone in or
  refresh a token, and no replacement task can pull its image. If the second zone is lost, the
  cell runs on one task and replaces the other in the first zone. A recovery objective for "a lost
  zone" therefore needs either a NAT per zone (or a regional NAT gateway, which the module's
  comments already name) or a stated bound that covers only the second zone.
- Data lost: none; the table and the bucket are multi-zone.

### A bad gateway deployment

Not in the ticket's bullets but one of the map's seven, and the earlier finding answers it. With
minimum healthy 100% the old tasks keep serving while the new ones fail. The circuit breaker fails
the deployment after three consecutive task failures (the default `BOUNDED_PERCENT` 50 clamped to a
minimum of 3; earlier finding, section 4) and rolls back to the previous task definition. Each
failed task costs at least its pull-and-place plus the 60 s grace period, so the automatic rollback
takes at least three such cycles (derived). A deployment that starts, passes `/readyz` and is wrong
in behaviour is not caught; that is `rollback.sh` (`docs/runbooks/rollback.md`). Data lost: none.

## 5. Injecting each fault for a rehearsal

### AWS Fault Injection Service

Price, from its price page: "The AWS FIS price is $0.10 per action-minute plus an additional $0.10
per action-minute for each additional account", "There are no upfront costs or minimum fees", and
an optional experiment report is "$5 per report". The price list file for `us-east-2` carries the
same two figures. One action held for 10 minutes in one account is $1.00.

What its action reference offers that fits this cell:

| Action | What it does | Recovery |
| --- | --- | --- |
| `aws:ecs:stop-task` | "Runs the Amazon ECS API action StopTask to stop the target task." | The scheduler replaces the task. |
| `aws:network:disrupt-connectivity`, scope `s3` | "Denies traffic to and from the Regional endpoint for Amazon S3 in the current Region" for the target subnets, 1 minute to 12 hours | "At action completion, FIS deletes the cloned network ACL and restores the original network ACL association." |
| the same, scope `dynamodb` | Denies traffic to and from DynamoDB's Regional endpoint | As above. |
| the same, scope `availability-zone` or `all` | "Denies intra-VPC traffic to and from subnets in other Availability Zones", or all traffic in and out of the subnet | As above. |
| `aws:arc:start-zonal-autoshift` | Shifts a load balancer's traffic away from a zone | Ends with the action; needs zonal shift enabled on the ALB, which the cell does not set. |

What it does not offer:

- **Fargate capacity loss in a zone.** The "AZ Availability: Power Interruption" scenario states:
  "Amazon ECS tasks running on AWS Fargate are not supported." A zone rehearsal on this cell is the
  network action on the zone's subnets plus stopping that zone's task; nothing stops ECS placing
  the replacement in the "lost" zone except its unreachable subnet.
- **Table loss, parameter loss, KMS key state.** The only DynamoDB action is
  `aws:dynamodb:global-table-pause-replication`, the only S3 action is
  `aws:s3:bucket-pause-replication`, and the API error injections support only "`ec2` and
  `kinesis`". There is no action for Parameter Store parameters or KMS keys.
- Whether a network ACL deny on S3's prefix also blocks traffic that leaves through the cell's S3
  gateway endpoint is expected, since the ACL is on the subnet, and is **unconfirmed**; no
  experiment was run.

FIS needs an experiment role and a template. Neither is in `infra/`; under the map's cost rule they
would be a gated input the platform root's destroy removes. A template has no listed price; that it
is free to keep is implied by "you pay for what you use" and is otherwise **unconfirmed**.

### Plain CLI steps, by failure

Each step below is drawn from the documentation and the repository, and none was run.

| Failure | FIS | Plain CLI | What keeps the cell recoverable |
| --- | --- | --- | --- |
| A killed task | `aws:ecs:stop-task` | `aws ecs stop-task` | The service replaces it. A graceful stop drains; neither path rehearses a silent death, which only the network action approximates. |
| A lost zone | `disrupt-connectivity` on one zone's subnets, plus stopping its task | A zonal shift on the ALB, at no charge, moves traffic only; it does not remove the task or the NAT | The ACL is restored by FIS. Rehearse the second zone first: the first holds the NAT. |
| A bad gateway deployment | none | `deploy.sh` with an image that never answers `/readyz` 200 | The circuit breaker rolls back; `rollback.sh` is the lever beside it. |
| A bad registry document | none | `spactl` checks a document before it writes one, so a bad one is put with `aws s3api put-object` | Versioning keeps the good version; `spactl registry rollback` republishes it. |
| A lost or corrupted session table | scope `dynamodb` rehearses the table being unreachable, not lost | With point-in-time recovery on: `aws dynamodb delete-table`, then restore from the system backup to the same name. Corruption: write bad rows, restore to a second name | Turn point-in-time recovery on, and wait for it, before the delete. Without it the delete is final and recovery is an empty table. |
| A lost key ring or KMS key | none | Key: `aws kms disable-key`, then `enable-key`; never `schedule-key-deletion` on a live cell's key. Ring: delete the ring's parameters, as `teardown.sh` does | Disabling is fully reversible. A ring delete is not: rehearse it on a cell whose users are test users, and expect a new ring. Copying the keys out first would put key material on a workstation, which the repository's rules do not allow. |
| An S3 outage | scope `s3` on the task subnets | A temporary deny statement on the bucket policy, which is Terraform's and shows as drift until the next apply | The ACL is restored by FIS; versioning and the data are untouched. |

With no customer managed key (the cell's default) the KMS half cannot be injected by key state at
all: "You can neither manage the lifecycle nor access permissions on AWS managed keys" (the KMS
price page), and they cannot be deleted.

## 6. Price per idle month, and what bills while the cell is destroyed

Prices are for US East (Ohio), read 2026-11-16. DynamoDB's price page renders its figures in the
browser, so its numbers here are from AWS's public price list file for `us-east-2`, the data that
page draws, and the S3 storage figure is from the S3 file likewise; the KMS, Systems Manager, FIS
and Application Recovery Controller figures are quoted from their price pages, and the KMS, Systems
Manager and FIS ones agree with their files. "Idle" is a cell standing with no traffic. The
session table's size is unknown and small; 1 GB is used as a deliberately generous unit.

| Option | Unit price | Per idle month | While the cell is destroyed |
| --- | --- | --- | --- |
| DynamoDB point-in-time recovery | $0.20 per GB-month of table and local index size; "Changing the recovery window … doesn't reduce the price" | $0.20 per GB; cents or less for a session table | Stops: "You're billed for PITR usage until you turn off PITR", and the table is gone. The system backup stays 35 days "at no additional cost". |
| DynamoDB on-demand backup | $0.10 per GB-month | $0.10 per GB per backup kept | **Keeps billing** until the backup is deleted; it is not in any root's state and cannot be tagged. |
| DynamoDB restore, either kind | $0.15 per GB restored | $0 | $0; charged per restore. |
| DynamoDB deletion protection | No SKU in the price file, and no page read states a charge | $0 (**unconfirmed** by an explicit statement) | Nothing to bill, but it refuses the destroy until switched off. |
| An empty replacement table | Nothing beyond the table's own on-demand price | $0 | $0 |
| Parameter Store history, standard tier | "Standard parameters are available at no additional charge" | $0 | $0; `teardown.sh` deletes the ring. |
| The `aws/ssm` key | "You are not charged for … Creation and storage of AWS managed or AWS owned KMS keys"; requests are $0.03 per 10,000 after 20,000 free a month | $0 at this volume | $0 |
| A customer managed KMS key | "$1/month (prorated hourly)"; requests as above | $1.00 | **$1.00 a month if the key lives outside the platform root.** Inside it, the destroy schedules deletion, and "There is no charge for customer managed KMS keys that … are scheduled for deletion". |
| S3 versioning | Noncurrent versions bill as storage: $0.023 per GB-month, S3 Standard, first 50 TB | $0.023 per GB of old versions; under a cent for registry documents | $0; `teardown.sh` empties every version and delete marker. |
| ECS replacement, rebalancing, circuit breaker | No separate price found; a replacement task bills as a Fargate task while it overlaps | $0 | $0 |
| ARC zonal shift | "There is no additional charge for using zonal shift or zonal autoshift." | $0 | $0 |
| Fault Injection Service | $0.10 per action-minute; $5 per optional report | $0: "no upfront costs or minimum fees" | $0 |
| A second NAT gateway, for zone loss | Not priced here; the module's comment says "about $33 a month each", which `docs/plan/05-infrastructure-and-cost.md` marks as "from memory" | **Unconfirmed**; the P2-7 map's pricing research owns it | $0 once destroyed |

## 7. The table: the map's seven failures

"Documented" means AWS states the figure. Where AWS states none, the cell says so and gives what
the time is made of; those are the numbers a rehearsal has to produce.

| Failure | Mechanism | In the cell's Terraform today | Time to recover, as documented | Data lost | Price per idle month |
| --- | --- | --- | --- | --- | --- |
| A killed task | The ECS service scheduler replaces it; the ALB routes to the other task | Yes (`ecs.tf`: service, desired count 2 under `multi_task`) | None documented. Pull-and-place plus task start to target healthy (startup benchmark). A silently dead target is marked unhealthy in 60 to 90 s (derived) | None with the DynamoDB store and shared ring. Every session on the one-task, in-memory default | $0 |
| A lost zone | The ALB routes to the surviving zone's node and task; ECS reschedules; rebalancing evens out afterwards; S3 and DynamoDB are multi-zone | Partly: two zones, two tasks and rebalancing, yes. One NAT gateway in the first zone, so losing that zone leaves the surviving task without Parameter Store, ECR, logs and the provider. Zonal shift not enabled | None documented. ALB DNS TTL 60 s; unhealthy target in 60 to 90 s (derived); AWS's own zone scenario assumes "a few minutes" for DNS | None | $0 as built. A second NAT gateway: unconfirmed |
| A bad gateway deployment | Minimum healthy 100% keeps the old tasks; the circuit breaker rolls back; `rollback.sh` | Yes (`ecs.tf`) | None documented. At least three failed task cycles, each pull-and-place plus the 60 s grace period (derived) | None | $0 |
| A bad registry document | The gateway ignores it and keeps the last good snapshot; S3 versioning; `spactl registry rollback` | Yes (`storage.tf`: versioning enabled) | None documented. One `spactl` call plus one registry poll interval (`registry_poll_seconds`) | None; the bad version stays in history | $0.023 per GB of old versions: under a cent |
| A lost or corrupted session table | Point-in-time recovery, or an on-demand backup, restored to a new table; or an empty table by `apply` | **No**: no point-in-time recovery, backup or deletion protection | Restore: "no fixed completion time is guaranteed". Enabling point-in-time recovery: up to 10 minutes. Empty table: one apply, unmeasured | Point-in-time: about the last 5 minutes of writes. Backup: everything since it. Empty table: every session. Any restore also loses time to live, tags and point-in-time recovery settings until set again | $0.20 per GB (point-in-time), $0.10 per GB (backup), $0 (empty table) |
| A lost key ring or KMS key | None restores a deleted ring. Parameter history for an overwrite. For a customer managed key: cancel deletion inside 7 to 30 days, then enable | **No**: the ring is outside Terraform, with no backup; no customer managed key exists, so the key cannot be deleted | Ring: not recoverable; the gateway writes a new one at once. Key: cancellation is immediate inside the window, impossible after it | Every session, cookie and login in flight. Rows stay in the table, unreadable | $0 (history, `aws/ssm` key); $1.00 for a customer managed key |
| An S3 outage | None; it is waited out. Running tasks keep the last registry snapshot and stay healthy; S3 itself spans at least three zones | Nothing to turn on; no asset cache exists (GAP-16) | AWS's to restore: none documented. Assets fail for the whole outage, and no task can start | None | $0 |

## What was executed, and what was only read

### Executed

Nothing against AWS, the cell or Terraform. On the workstation, on 2026-11-16:

- `git fetch origin` and a worktree at `origin/main` (`adf0273`); `git show` of
  `origin/research/ecs-alb-startup-clocks:docs/research/ecs-alb-startup-clocks.md`.
- `gh issue view` of the ticket and the map.
- Unauthenticated HTTPS downloads of the public pages and price list files below, with `curl` and
  the web fetch tool, and a local script that printed the price rows.

### Not done

No `terraform plan`, `apply` or `init`; no AWS CLI call; no credential step; no read of the
deployed cell; no fault injected; no restore, backup or experiment run; no measurement of any
duration. The `aws-mcp` connector failed to connect. Every sentence marked "derived" combines
statements from the sources and was not tested. Every sentence marked "unconfirmed" has no primary
source behind it.

### Read: this repository, at `adf0273`

`infra/modules/cell/` (`sessions.tf`, `storage.tf`, `ecs.tf`, `alb.tf`, `network.tf`, `iam.tf`,
`variables.tf`, `README.md`); `tools/aws-project.env`; `tools/scripts/teardown.sh` (header and the
ring section); `docs/runbooks/README.md`, `registry-rollback.md`, `teardown.md`;
`docs/plan/05-infrastructure-and-cost.md`; `docs/gaps.md` (GAP-16); `contracts/pipeline.md`
(`/readyz`); `src/Platform.Hosting/KeyRing.cs`; `src/Platform.Identity/DynamoDbSessionStore.cs`;
`docs/agents/issue-tracker.md`; and the startup-clocks finding on its branch (sections 2 to 4
and 6).

### Read: primary sources, all on 2026-11-16

DynamoDB:

- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Point-in-time-recovery.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/PointInTimeRecovery_Howitworks.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/PointInTimeRecovery.Tutorial.html
- https://docs.aws.amazon.com/amazondynamodb/latest/APIReference/API_RestoreTableToPointInTime.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/backuprestore_HowItWorks.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/CreateBackup.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Restore.Tutorial.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Backup-and-Restore.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/WorkingWithTables.Basics.html
- https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/disaster-recovery-resiliency.html
- https://aws.amazon.com/dynamodb/pricing/on-demand/ (figures rendered in the browser; the $0.15
  per GB restore charge appears in its worked example)
- https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonDynamoDB/current/us-east-2/index.json
  (published 2026-11-11)

Parameter Store and KMS:

- https://docs.aws.amazon.com/systems-manager/latest/userguide/sysman-paramstore-versions.html
- https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_DeleteParameter.html
- https://docs.aws.amazon.com/systems-manager/latest/userguide/parameter-store-advanced-parameters.html
- https://aws.amazon.com/systems-manager/pricing/
- https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AWSSystemsManager/current/us-east-2/index.json
  (published 2026-11-03)
- https://docs.aws.amazon.com/kms/latest/developerguide/deleting-keys.html
- https://docs.aws.amazon.com/kms/latest/developerguide/deleting-keys-scheduling-key-deletion.html
- https://docs.aws.amazon.com/kms/latest/developerguide/unusable-kms-keys.html
- https://docs.aws.amazon.com/kms/latest/APIReference/API_CancelKeyDeletion.html
- https://aws.amazon.com/kms/pricing/
- https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/awskms/current/us-east-2/index.json
  (published 2026-11-08)
- https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/default-settings
- https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-management

S3:

- https://docs.aws.amazon.com/AmazonS3/latest/userguide/DeletingObjectVersions.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/RestoringPreviousVersions.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/DataDurability.html
- https://docs.aws.amazon.com/AmazonS3/latest/userguide/disaster-recovery-resiliency.html
- https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonS3/current/us-east-2/index.json
  (published 2026-11-16)

ECS and the load balancer:

- https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs_services.html
- https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-rebalancing.html
- https://docs.aws.amazon.com/elasticloadbalancing/latest/userguide/how-elastic-load-balancing-works.html
- https://docs.aws.amazon.com/elasticloadbalancing/latest/application/target-group-health-checks.html
- https://docs.aws.amazon.com/r53recovery/latest/dg/arc-zonal-shift.resource-types.app-load-balancers.html
- https://aws.amazon.com/application-recovery-controller/pricing/

Fault injection:

- https://docs.aws.amazon.com/fis/latest/userguide/fis-actions-reference.html
- https://docs.aws.amazon.com/fis/latest/userguide/az-availability-scenario.html
- https://aws.amazon.com/fis/pricing/
- https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AWSFIS/current/us-east-2/index.json
  (published 2026-09-11)

Terraform provider, at the pinned version:

- https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/dynamodb_table.html.markdown

### Could not be established

- Any restore duration for the session table, and any task-replacement or zone-recovery time: AWS
  documents none.
- Whether a deleted Parameter Store parameter's history is recoverable: no AWS statement either way
  was found.
- How Terraform treats a table restored to the original name, and whether the provider's
  `restore_*` arguments replace an existing table: no plan was run.
- Whether DynamoDB deletion protection and a stored FIS experiment template are free: no price row
  and no explicit statement.
- The status the gateway returns for an asset when its S3 read fails: not read from the code.
- Whether FIS's `s3` scope blocks traffic through the cell's S3 gateway endpoint: not tested.
- The price of a second or regional NAT gateway in `us-east-2`: not read; the P2-7 map's pricing
  research owns it.
