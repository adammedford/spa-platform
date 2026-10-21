# Session storage for Phase 2: Valkey against DynamoDB, and the field ranked

- **Raised by:** #249, "Whether Valkey should displace DynamoDB as the Phase 2 session store", under
  the #215 map, for Phase 2 package P2-1 (`docs/plan/06-phase-2-and-risks.md:11`). Drafted on
  2026-10-20; revised on 2026-10-21 against the review on PR #189, with every price re-read from the
  AWS Price List.
- **Status:** a finding with a recommendation, not a decision; the integrator decides. The plan names
  DynamoDB (ADR-0004, plan row P2-1, roadmap S8, `contracts/platform-config.schema.json` ->
  `Session:Store`), and change request `contracts/changes/p2-1-dynamodb-session-store.md` is written
  for it. This note says whether Valkey should displace it, and where every other option in the space
  lands.
- **Short answer:** keep DynamoDB. Two Valkey deployments meet P2-1's acceptance. MemoryDB for Valkey
  does at about $49 a month per cell, five to six times the table's $9 at the model's traffic; durable
  ElastiCache for Valkey does at about $205 to $218, more than twenty times. Neither can be proved
  hermetically under this repository's test rules, and both bring a connection-lifetime and memory
  surface the table does not have. The one thing Valkey buys, sub-millisecond reads, is not something
  any gate measures.

## What the store has to be

The acceptance text, and the rules around it, read as requirements on the engine. Where the third
column goes beyond what the cited text says, it is marked as this note's inference.

| Requirement | Where it is written | What it demands of the engine |
| --- | --- | --- |
| Killing a task loses no session | P2-1, "Accepted when" | The record lives outside every task. *Inferred:* a store failover loses no acknowledged write either, because a lost removal after logout (`src/Platform.Identity/SessionEnd.cs`) brings back a session whose refresh token the provider has already revoked, which would break P2-3's "revocation holds on every task at once". |
| Replace is versioned | P2-1; `ISessionStore.ReplaceAsync` (`src/Platform.Abstractions/ISessionStore.cs:36-41`) | A compare-and-set on one record, atomic at the server |
| One refresh per session across tasks | P2-3; decided on #218 under #215 | A contract type `IRefreshLeases`, independent of the session record, with a time to live of `ProviderDeadline + LeaseMargin` (15 s) and a fenced release. The engine needs a conditional write with an expiry; where the DynamoDB store keeps the lease is #221's. |
| A session ended through one task is refused by the other on its next request | P2-3 | Every read sees the last write: a strongly consistent read, and no in-process cache (change request P2-1, section 6, the paragraph that opens "Two consequences of the layout") |
| Expiry uses the engine | P2-1 | Per-record time to live, or a read rule that treats an expired record as absent |
| `RemoveAppAsync` touches only one app's sessions | `ISessionStore.cs:46-50` | A way to find an app's records without scanning everything |
| Proved first against doubles, in the owner's project, with no container | AGENTS.md -> "Rules for every task"; D-2 in `docs/dependencies.md`, applied to the store in change request P2-1, section 5, option (b) | A protocol the test project can fake over HTTP, or a client the store is written against |
| One store per cell, and more cells in P2-8 | ADR-0012; `docs/plan/01-plan.md` -> "Isolation model" | The store's floor price is paid once per cell. *This note's judgement:* at this traffic the floor matters more than the per-request price. |
| No per-SPA runtime resources (P-02) | Plan policy | Both engines pass: one table or one cache per cell, nothing per app |
| One Region, managed IAM, a spend limit that pauses the project | `docs/agents/aws-rules.md` | No cross-Region feature is needed. A higher floor sits closer to the limit. *Inferred from the file's troubleshooting steps:* the service has to be on the account plan's supported list. |
| Package pins and new services are the integrator's | AGENTS.md; `Directory.Packages.props` | Either engine needs a pin. Valkey also needs a schema enum value and an ADR, because the plan names DynamoDB in six places (the schema, ADR-0004, `02-contracts.md`, `05-`, `06-`, the roadmap). |

## The two engines, side by side

"Valkey" below means Amazon ElastiCache for Valkey unless a row says otherwise. Where a row depends
on which Valkey deployment is chosen, it says so; the deployments themselves, MemoryDB among them,
are ranked in the section after next.

| | DynamoDB, on-demand table | Valkey on ElastiCache |
| --- | --- | --- |
| Durability of an acknowledged write | Written to several zones before the acknowledgement. No failover loses a write. | Depends on the deployment. Plain replication is asynchronous, so a failover loses the writes the replica had not received. The durability mode announced on 2 June 2026 (Valkey 9.0, cluster mode only) has two settings: synchronous, where the primary acknowledges only after a Multi-AZ log holds the write, and asynchronous, which can lose up to ten seconds. Serverless caches have no durability setting. |
| Strongly consistent read | A flag on `GetItem`, at twice the read units | Reads from the primary are strongly consistent. The client must be configured never to read from a replica. |
| Versioned replace | Native: `PutItem` under `attribute_exists(pk) AND version = :expected` | No native compare-and-set on a value. Either `WATCH`/`MULTI`/`EXEC` with a retry loop, or a Lua script or Valkey function that compares and writes in one step. The script is the condition's only home. |
| The refresh lease (#218) | The DynamoDB store implements `IRefreshLeases` with one conditional write, on a lease item of its own or on the session item, as #221 settles | `SET key token NX PX ttl` is the acquire, the idiom the engine was built for. The fenced release, compare the token then delete, is a second script. |
| Expiry | The table's time to live deletes lazily, typically within a few days of expiry, so the store reads `expiresAt <= now` as absent, as #215's decisions keep it. | `EXPIREAT` is exact. An expired record is gone, and no read rule is needed. |
| Find an app's sessions | A global secondary index on `appId`, eventually consistent, which the purge tolerates (`AppSessions.OwnedBy` treats a foreign record as absent, `src/Platform.Identity/AppSessions.cs:194`) | A set per app of session keys, kept in the same transaction as the create and the remove, or `SCAN` over a prefix. In cluster mode the set and its members need a hash tag to share a slot. |
| Record size | A 400 KB item limit. A ticket with three Cognito tokens under data protection is a few kilobytes. | No practical limit at this size |
| Reach from the task | Today over the task's public address; in P2-4's private subnets over a gateway endpoint, which has no charge | Inside the cell's VPC: a subnet group and a security-group rule from the task's group. No endpoint. |
| Authentication | SigV4 from the task role, in the SDK | IAM authentication: a token good for fifteen minutes, TLS required, and a connection the service drops after twelve hours unless re-authenticated. Valkey GLIDE generates and rotates the token itself; StackExchange.Redis does not (its issue 2891), so the rotation would be hand-written. |
| Client package | `AWSSDK.DynamoDBv2` 4.0.108, Apache-2.0, already requested in change request P2-1 item 1 | `Valkey.Glide` 1.1.0 on nuget.org, 13 May 2026, Apache-2.0, a Rust core with a native library per runtime, so the ARM64 image needs its linux-arm64 binary; or `StackExchange.Redis`, MIT, managed, without IAM rotation |
| Hermetic proof | A fake JSON 1.0 server in `tests/Platform.Identity.Tests`, on the `FakeS3Server` precedent (change request P2-1, section 5, option (a)). It evaluates the conditions the store sends: two among section 5's five operations, and the two lease conditions #218 adds. #218 keeps the fake in that project. | No precedent. A RESP fake would have to run the Lua scripts to prove the replace and the release, which is writing an interpreter. A stub of the client interface proves the store called the client, not that the scripts are right. A container is ruled out for the suite by D-2. The scripts would be proved for the first time at the G6 deployed run. |
| Capacity and eviction | On-demand scales per request. Nothing to size. | Memory is a fixed quantity on a node, or metered on serverless. The engine's default eviction policy evicts live keys under memory pressure, which for sessions is a silent sign-out; `maxmemory-policy` must be `noeviction`, with an alarm, or the cache must be serverless. |
| Operations | No versions, no maintenance window, no patching. Point-in-time recovery is an option. | Engine versions, a maintenance window, parameter groups, node sizes, snapshots. Serverless removes the sizing and the nodes, not the versions. |
| Terraform | `aws_dynamodb_table` for years; P-01's test already names it as a Phase 1 tripwire (`infra/envs/poc/platform/tests/root.tftest.hcl:807`) | `aws_elasticache_replication_group` and `aws_elasticache_serverless_cache` for years. The `durability` argument shipped in provider 6.51.0 on 17 June 2026; the cell constrains `~> 6.37` and `docs/dependencies.md` pins 6.68.0, so it is reachable. |
| On the new AWS experience's supported lists | On the Free Tier list, with global tables excluded, which the one-Region rule excludes anyway | ElastiCache is on the Free Tier list too. MemoryDB is on the Paid Plan list only. `aws freetier get-account-plan-state` and the lists in `docs/agents/aws-rules.md` decide which plan applies before any option is chosen. |
| Latency per read | Single-digit milliseconds, paid twice per proxied API call (the ticket store's read and the token path's read, `src/Platform.Identity/AppSessions.cs:69,86`), with no cache to absorb it | Sub-millisecond on a node, about a millisecond on serverless |
| Extra uses the engine would offer | None beyond the store | Pub/sub for P2-5's convergence signal, a data-protection key ring for P2-2 through `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`, and a shared cache for hosting's asset reads (risk 10). None is asked for: P2-5 is designed as every task reporting the revision, and P2-2 as Parameter Store under KMS. |

## What it costs per cell

A model, not a measurement: 2,000 active users, 300 gateway requests each per working day, 22 days,
two session reads per proxied API call, a 6 KB item, one login and eight refreshes per user per day
(396,000 writes a month), 730 hours a month. Prices are US East (N. Virginia) on-demand list prices
from the AWS Price List offer files, read on 2026-10-21 (see Sources); rows that rest on a file this
session did not download say so.

| Option | Floor, idle | At the model's traffic | How the figure arises |
| --- | --- | --- | --- |
| DynamoDB on-demand | $0 | About $8 to $9 | 26.4 M strongly consistent reads of 6 KB, 2 read units each, at $0.125 per million read units ($6.60); 396,000 writes of 6 KB, 6 write units each, so 2.4 M write units at $0.625 per million ($1.50); storage under the 25 free GB-months. |
| ElastiCache Serverless for Valkey | About $6 | About $7 | The 100 MB minimum at $0.084 per GB-hour, plus about 160.8 M ECPUs at $0.0023 per million. No durability setting exists. |
| ElastiCache for Valkey, one `cache.t4g.micro` | About $9 | About $9 | $0.0128 per hour. One node, one zone: a node loss loses every session. |
| ElastiCache for Valkey, `cache.t4g.micro` with a replica, Multi-AZ | About $19 | About $19 | Two nodes. A failover loses the writes in flight. |
| ElastiCache for Valkey, durable, synchronous | About $205 to $218 | The same | Cluster mode, one shard, one replica, two nodes. The offer file prices durability as a surcharge per node-hour on the node rate: `cache.m6g.large` $0.1192 plus $0.0215, `cache.m7g.large` $0.1264 plus $0.0228. The file has durability rows for the `m6g`, `m7g`, `m8g`, `r6g`, `r7g`, `r8g`, `c7gn` and `c8gn` families and none for `t4g`. |
| MemoryDB for Valkey | About $49 | About $49 | Two `db.t4g.small` nodes at $0.0336 per hour; data written is $0 up to 10 TB a month on Valkey. Durable by design. |
| S3, one object per session | $0 | About $13 | 26.4 M GETs at $0.40 per million and 0.4 M PUTs at $5 per million; the bucket exists already. Rates as checked by the PR #189 review, not downloaded here. |
| Aurora Serverless v2 PostgreSQL | $0 scaled to zero, about $44 at a half capacity unit | About $44 and up | Half an ACU at $0.12 per ACU-hour; a cold start of seconds after idle. Rate as checked by the PR #189 review, not downloaded here. |

The ALB is the Phase 1 floor at $16.43 and the whole Phase 1 cell is about $30
(`docs/plan/05-infrastructure-and-cost.md` -> "Cost"). MemoryDB, the cheaper of the two Valkey
deployments that meet P2-1's acceptance, is three times the ALB floor and one and a half times the
cell; durable ElastiCache is about seven times the cell. P2-8 multiplies the floor by the number of
cells, which is where a $0 floor and a $49 or $205 floor diverge fastest.

## Where Valkey wins, and where it loses

**Wins.** Reads in under a millisecond against a few milliseconds. Exact expiry with no read rule.
The lease acquire in one native command. Pub/sub, should P2-5's design ever change from reporting
to signalling. Precedent: Valkey, and Redis before it, is the usual answer to "where do ASP.NET Core
sessions go", and `ITicketStore` over a distributed cache is a documented pattern.

**Loses.** The durability P2-1 and P2-3 need comes either from MemoryDB, a Paid Plan service, or
from a four-month-old ElastiCache mode on the larger node families; the cheaper deployments lose
writes on a failover, and the write they can lose includes a logout. The versioned replace and the
fenced release live in scripts that this repository's test rules cannot prove before the deployed
gate, where the DynamoDB conditions are proved against a fake in the owner's project. IAM
authentication adds a fifteen-minute token and a twelve-hour connection to the store's lifecycle.
Memory is a bound to size and an eviction policy to set, where the table has neither. And the plan
has already spent the design: the item layout, the condition table, the fake's surface, #218's lease
decisions and the cost line all exist for DynamoDB, and a switch is an ADR superseding part of
ADR-0004, a schema enum value, a plan revision and a rewritten P2-1 request.

The one scenario that flips the comparison: a gate that measures gateway latency per call and sets a
budget the table cannot meet. No gate does, and P2-10's load test is the first place one could. Any
answer to such a budget that keeps DynamoDB, an in-process read cache among them, would reopen P2-3's
no-cache rule, which is a decision for the integrator and not one this note proposes.

## The field, ranked

Ranked for this platform's Phase 2, against the requirements table above, not in general. The last
column is this note's reading for the integrator, not a decision.

| Rank | Option | Meets P2-1 and P2-3 as written | Floor per cell | Hermetic proof | Reading |
| --- | --- | --- | --- | --- | --- |
| 1 | **DynamoDB, on-demand, one table per cell** | Yes: durable, conditional writes native, consistent reads on a flag, time to live on the table | $0 | A fake JSON 1.0 server, designed in change request P2-1 and kept in workstream 3's project by #218 | **Recommended.** The plan's choice, and the comparison supports it. |
| 2 | MemoryDB for Valkey | Yes: durable by construction across zones, strongly consistent primary | About $49 | Scripts unproved until deployment | The Valkey that is durable without a mode to switch on, on small nodes, and mature. The best Valkey, at five to six times the table's cost at traffic, and Paid Plan only. |
| 3 | ElastiCache for Valkey, durable, synchronous writes | Yes | About $205 to $218 | As rank 2 | Meets the acceptance, and is the most expensive thing in this table. The offer file has no surcharge row for the asynchronous setting, so it would cost the node rate alone, about $175 to $185, and reintroduces a ten-second loss window. |
| 4 | S3, one object per session, conditional writes | Yes, with care: an object write under `If-Match` on the ETag is the versioned replace, a read is strongly consistent, and the bucket is durable. Expiry is a lifecycle rule by day, so the read rule from the DynamoDB design carries over. The lease is an object of its own under `If-None-Match`. | $0 | `FakeS3Server` exists today, and `S3ObjectStore` is the shape | The fallback if DynamoDB were ever unavailable to the project: no new service, a proof pattern the repository already has. Tens of milliseconds per read is why it is not first. |
| 5 | ElastiCache Serverless for Valkey | No: no durability setting, so a failover can lose a login, a refresh or a logout | About $6 | As rank 2 | The cheapest Valkey and the easiest to run. Right for a cache, wrong for the record of who is signed in. |
| 6 | ElastiCache for Valkey, node-based, Multi-AZ, not durable | No, as rank 5 | About $19 | As rank 2 | Rank 5's loss semantics with nodes to size and patch |
| 7 | Aurora Serverless v2 PostgreSQL, or RDS | Yes: a row with a version column and `UPDATE ... WHERE version = ?` | $0 to about $44 | A container, which the suite cannot have; or an in-process SQLite stand-in that proves another engine | A relational store for one record type. More service than the record needs, and a cold start on the login path when scaled to zero. |
| 8 | DAX in front of DynamoDB | No on its own: an eventually consistent cache in front of the table defeats "refused by the other task on its next request" | About $29 per node | None | Not a store, and not supported on the new experience unless advanced AWS features are activated. Listed because it is the usual answer to DynamoDB latency, and it is the wrong one here. |
| - | Sticky sessions on the ALB over the memory store | No: a killed task loses its sessions | $0 | n/a | Out: P2-4's acceptance is a killed task with live sessions. |
| - | The ticket in the cookie | No: tokens in the browser | $0 | n/a | Out: ADR-0004 and Phase 1 criterion 3 already exclude it. |
| - | Valkey run by the platform on ECS | Would need the platform to build durability | Task price | n/a | Out, on this note's reading: a database to operate, for a team that chose managed services everywhere else. |

## What would change the ranking

- **A latency budget per call** that single-digit milliseconds, twice, cannot meet. Measured at P2-10, not assumed.
- **DynamoDB off the project's supported list**, or the table's pricing changing shape. Rank 4 would then come before any Valkey, because it adds no service and keeps the proof pattern.
- **The durability mode reaching the `t4g` family or serverless**, which would bring rank 3's floor down toward rank 6's. Worth a look at each Phase 2 cost review.
- **A second consumer for Valkey**: pub/sub for convergence, a key ring, an asset cache. Three uses on one cache change the cost per use. None is in the plan, and each would be its own change request.

## What was executed and what was read

- **Executed by the revising session, 2026-10-21:** downloads of the AWS Price List offer files for
  `AmazonDynamoDB`, `AmazonElastiCache` and `AmazonMemoryDB` in us-east-1, and a filter over them for
  the rows above; a read of the `Valkey.Glide` version list and registration on nuget.org.
- **Not reachable from either session's proxy:** `aws.amazon.com`, `docs.aws.amazon.com`, the
  GitHub API and web pages outside this repository, `glide.valkey.io`. Statements resting on those
  pages (the durability mode's announcement date, Valkey 9.0 and cluster-mode requirement, the
  asynchronous loss window, IAM authentication's token and connection lifetimes, the TTL deletion
  delay, the supported-services lists, the Terraform 6.51.0 release, StackExchange.Redis issue 2891)
  were checked against those primary sources by the PR #189 review on 2026-10-21 and are cited to
  it, not to a read by this note's authors.
- **Not downloaded by this session:** the S3 and Aurora offer files; their rates are the review's. Before any figure here enters `docs/plan/05-infrastructure-and-cost.md`,
  that document's own "Cost" re-read applies.

## Sources

Read by the revising session on 2026-10-21:

- AWS Price List, DynamoDB, us-east-1, published 2026-09-11: https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonDynamoDB/current/us-east-1/index.json
- AWS Price List, ElastiCache, us-east-1, published 2026-10-14: https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonElastiCache/current/us-east-1/index.json
- AWS Price List, MemoryDB, us-east-1, published 2026-09-11: https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonMemoryDB/current/us-east-1/index.json
- `Valkey.Glide` on nuget.org: https://www.nuget.org/packages/Valkey.Glide

Checked against by the PR #189 review on 2026-10-21, and not reachable from this note's sessions:

- The review itself: https://github.com/adammedford/spa-platform/pull/189#issuecomment-6315399971
- AWS, "Amazon ElastiCache for Valkey now supports durability", 2 June 2026: https://aws.amazon.com/about-aws/whats-new/2026/06/durability-amazon-elasticache/
- ElastiCache developer guide, automatic failover and replication: https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/AutoFailover.html, https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/Replication.html
- ElastiCache developer guide, authenticating with IAM: https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/auth-iam.html
- DynamoDB developer guide, time to live: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/TTL.html
- AWS, supported services for the new sign-up experience: https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html
- StackExchange.Redis, IAM authentication issue: https://github.com/StackExchange/StackExchange.Redis/issues/2891
- Terraform AWS provider v6.51.0, which added `durability` to ElastiCache replication groups: https://github.com/hashicorp/terraform-provider-aws/releases/tag/v6.51.0

In this repository: `contracts/changes/p2-1-dynamodb-session-store.md`,
`contracts/changes/p2-3-session-lease-abstraction.md`, issues #215, #218 and #221.
