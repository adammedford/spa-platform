# Session storage for Phase 2: Valkey against DynamoDB, and the field ranked

- **Raised by:** a research session on 2026-10-20, for Phase 2 package P2-1 (`docs/plan/06-phase-2-and-risks.md`)
- **Status:** a finding, not a decision. The plan names DynamoDB (ADR-0004, D4, plan row P2-1, roadmap S8,
  `contracts/platform-config.schema.json` -> `Session:Store`), and change request
  `contracts/changes/p2-1-dynamodb-session-store.md` is written for it. This note says whether Valkey
  should displace it, and where every other option in the space lands.
- **Short answer:** keep DynamoDB. A Valkey that meets P2-1's acceptance exists, but it costs
  fifteen to twenty times more per cell, cannot be proved hermetically under this repository's test
  rules, and brings a connection-lifetime and eviction-policy surface the table does not have. The
  one thing Valkey buys, sub-millisecond reads, is not something any gate measures.

## What the store has to be

The acceptance text, and the rules around it, read as requirements on the engine.

| Requirement | Where it is written | What it demands of the engine |
| --- | --- | --- |
| Killing a task loses no session | P2-1, "Accepted when" | The record lives outside every task, and a store failover loses no acknowledged write. A lost write is not only a sign-out: a lost removal after logout brings back a session whose refresh token the provider has already revoked, with an access token good until it expires, which is the negative of P2-3's "revocation holds on every task at once". |
| Replace is versioned | P2-1; `ISessionStore.ReplaceAsync` (`src/Platform.Abstractions/ISessionStore.cs:36-41`) | A compare-and-set on one record, atomic at the server |
| One refresh per session across tasks: a short lease taken with a conditional write | P2-3; change request `p2-3-session-lease-abstraction.md` | A conditional write with a time to live and a fenced release |
| A session ended through one task is refused by the other on its next request | P2-3 | Every read sees the last write: a strongly consistent read, and no in-process cache (`p2-1-dynamodb-session-store.md` -> "Two consequences of the layout") |
| Expiry uses the engine | P2-1 | Per-record time to live, or a read rule that treats an expired record as absent |
| `RemoveAppAsync` touches only one app's sessions | `ISessionStore.cs:46-50` | A way to find an app's records without scanning everything |
| Proved first against doubles, in the owner's project, with no container | AGENTS.md -> "Rules for every task"; `CODING_STANDARDS.md` -> "An assertion must be able to fail"; D-2 in `docs/dependencies.md` | A protocol the test project can fake over HTTP, or a client the store is written against |
| One store per cell, and more cells in P2-8 | ADR-0012; `docs/plan/01-plan.md` -> "Isolation model" | The store's floor price is paid once per cell, so the floor matters more than the per-request price |
| No per-SPA runtime resources (P-02) | Plan policy | Both engines pass: one table or one cache per cell, nothing per app |
| One Region, managed IAM, a spend limit that pauses the project | `docs/agents/aws-rules.md` | No cross-Region feature is needed. A higher floor sits closer to the limit. The service must be on the plan's supported list. |
| Package pins and new services are the integrator's | AGENTS.md; `Directory.Packages.props` | Either engine needs a pin. Valkey also needs a schema enum value and an ADR, because the plan names DynamoDB in six places. |

## The two engines, side by side

"Valkey" below means Amazon ElastiCache for Valkey unless a row says otherwise. Where a row depends
on which Valkey deployment is chosen, it says so; the deployments themselves are ranked in the next
section.

| | DynamoDB, on-demand table | Valkey on ElastiCache |
| --- | --- | --- |
| Durability of an acknowledged write | Written to several zones before the acknowledgement. No failover loses a write. | Depends on the deployment. Plain replication is asynchronous, so a failover loses the writes the replica had not received. The durability mode announced on 2 June 2026 (Valkey 9.0, cluster mode enabled, a Multi-AZ transactional log) has two settings: synchronous, where the primary acknowledges only after the log holds the write in two zones; and asynchronous, which can lose up to ten seconds. Serverless caches have no durability setting. |
| Strongly consistent read | A flag on `GetItem`, at twice the read units | Reads from the primary are strongly consistent. The client must be configured never to read from a replica. |
| Versioned replace | Native: `PutItem` under `attribute_exists(pk) AND version = :expected` | No native compare-and-set on a value. Either `WATCH`/`MULTI`/`EXEC` with a retry loop, or a Lua script or Valkey function that compares and writes in one step. The script is the condition's only home. |
| The P2-3 lease | One conditional `UpdateItem` on the session item, as change request P2-3 option (c) describes under interface (a) | `SET key token NX PX ttl` is the acquire, which is the idiom the engine was built for. The fenced release, compare the token then delete, is a second script. |
| Expiry | The table's time-to-live deletes lazily, typically within days of expiry, so the store reads `expiresAt <= now` as absent. Already designed. | `EXPIREAT` is exact. An expired record is gone, and no read rule is needed. |
| Find an app's sessions | A global secondary index on `appId`, eventually consistent, which the purge tolerates (`AppBoundTicketStore.Owned` treats a foreign record as absent) | A set per app of session keys, kept in the same transaction as the create and the remove, or `SCAN` over a prefix. In cluster mode the set and its members need a hash tag to share a slot. |
| Record size | A 400 KB item limit. A ticket with three Cognito tokens under data protection is a few kilobytes. | No practical limit at this size |
| Reach from the task | Today over the task's public address; in P2-4's private subnets over a gateway endpoint, which has no charge | Inside the cell's VPC: a subnet group and a security-group rule from the task's group. No endpoint. |
| Authentication | SigV4 from the task role, in the SDK | IAM authentication: a token good for fifteen minutes, TLS required, and a connection the service drops after twelve hours unless re-authenticated. Valkey GLIDE 2.2 and later generates and rotates the token itself; StackExchange.Redis does not, so the rotation would be hand-written. |
| Client package | `AWSSDK.DynamoDBv2` 4.0.108, Apache-2.0, already requested in change request P2-1 item 1 | `Valkey.Glide` 1.1.0 on nuget.org, 13 May 2026, Apache-2.0, a Rust core with a native library per runtime, so the ARM64 image needs its linux-arm64 binary; or `StackExchange.Redis`, MIT, managed, without IAM rotation |
| Hermetic proof | A fake JSON 1.0 server in `tests/Platform.Identity.Tests`, the `FakeS3Server` precedent, evaluating the five condition expressions the store sends (change request P2-1 section 5, option (a)) | No precedent. A RESP fake would have to run the Lua scripts to prove the replace and the release, which is writing an interpreter. A stub of the client interface proves the store called the client, not that the scripts are right. A container is ruled out for the suite by `CODING_STANDARDS.md` and CI. The scripts would be proved for the first time at the G6 deployed run. |
| Capacity and eviction | On-demand scales per request. Nothing to size. | Memory is a fixed quantity on a node, or metered on serverless. The engine's default eviction policy evicts live keys under memory pressure, which for sessions is a silent sign-out; `maxmemory-policy` must be `noeviction`, with an alarm, or the cache must be serverless. |
| Operations | No versions, no maintenance window, no patching. Point-in-time recovery is an option. | Engine versions, a maintenance window, parameter groups, node sizes, snapshots. Serverless removes the sizing and the nodes, not the versions. |
| Terraform | `aws_dynamodb_table` for years; P-01's test already names it as a Phase 1 tripwire (`infra/envs/poc/platform/tests/root.tftest.hcl:807`) | `aws_elasticache_replication_group` and `aws_elasticache_serverless_cache` for years. The `durability` argument shipped in provider 6.51.0 on 17 June 2026; the cell constrains `~> 6.37` and `docs/dependencies.md` pins 6.68.0, so it is reachable. |
| On the new AWS experience's supported lists | On the Free Tier list, with global tables excluded, which the one-Region rule excludes anyway | Not confirmed from this session; `aws freetier get-account-plan-state` and the lists in `docs/agents/aws-rules.md` decide it before any Valkey option is chosen |
| Latency per read | Single-digit milliseconds. With two reads per proxied API call (the ticket store's read and the token path's read, `src/Platform.Identity/AppSessions.cs:69,86`), the gateway adds a few milliseconds per call, and the no-cache rule means every call pays it. | Sub-millisecond on a node, about a millisecond on serverless |
| Extra uses the engine would offer | None beyond the store | Pub/sub for P2-5's convergence signal, a data-protection key ring for P2-2 through `Microsoft.AspNetCore.DataProtection.StackExchangeRedis`, and a shared cache for hosting's asset reads (risk 10). None is asked for: P2-5 is designed as every task reporting the revision, and P2-2 as Parameter Store under KMS. |

## What it costs per cell

A model, not a measurement: 2,000 active users, 300 gateway requests each per working day, 22 days,
two session reads per proxied API call, a 6 KB item, one login and eight refreshes per user per
day. Prices are US East list prices as read on 2026-10-20 from the sources at the end; the AWS
pricing pages themselves were not reachable from this session, so every figure below is from a
2026 third-party summary or the AWS China mirror and is marked for a check before it reaches
`docs/plan/05-infrastructure-and-cost.md`.

| Option | Floor, idle | At the model's traffic | How the figure arises |
| --- | --- | --- | --- |
| DynamoDB on-demand | $0 | About $9 | 26 M strongly consistent reads of 6 KB, 2 read units each, at $0.125 per million; 3.2 M write units at $0.625 per million; storage under a gigabyte. Sources disagree on whether the per-million prices are these or double them; these follow the 50% cut AWS announced for 1 November 2024. |
| ElastiCache Serverless for Valkey | About $6 | About $7 | The 100 MB minimum at $0.084 per GB-hour, plus about 160 M ECPUs at $0.0023 per million. No durability setting exists. |
| ElastiCache for Valkey, one `cache.t4g.micro` | About $9 | About $9 | $0.0128 per hour. One node, one zone: a node loss loses every session. |
| ElastiCache for Valkey, `cache.t4g.micro` with a replica, Multi-AZ | About $19 | About $19 | Two nodes. A failover loses the writes in flight. |
| ElastiCache for Valkey, durable, synchronous | About $150 to $185 | The same | Cluster mode, one shard, one replica, on the smallest family the mode supports: `cache.m7g.large` at $0.10 to $0.13 per hour, two nodes. The `t4g` family is not listed for the mode. Sources conflict on the hourly rate. |
| MemoryDB for Valkey | About $60 to $70 | About $62 to $72 | Two `db.t4g.small` nodes at a rate recalled from memory rather than read, plus $0.20 per GB written. Durable by design, with the same transactional-log architecture. |
| S3, one object per session | $0 | About $14 | 26 M GETs at $0.40 per million and 0.4 M PUTs at $5 per million; the bucket exists already |
| Aurora Serverless v2 PostgreSQL | $0 scaled to zero, about $44 at a half capacity unit | About $44 and up | Half an ACU at about $0.06 per hour; a cold start of seconds after idle |

Two readings of the table. The ALB is the Phase 1 floor at $16.43 and the whole Phase 1 cell is
about $30 (`docs/plan/05-infrastructure-and-cost.md` -> "Cost"); the one Valkey deployment that
meets P2-1's acceptance costs five times the cell. And P2-8 multiplies the floor by the number of
cells, which is where a $0 floor and a $150 floor diverge fastest.

## Where Valkey wins, and where it loses

**Wins.** Reads in under a millisecond against a few milliseconds, paid twice per API call. Exact
expiry with no read rule. The lease acquire in one native command. Pub/sub, should P2-5's design
ever change from reporting to signalling. Precedent: Valkey, and Redis before it, is the usual
answer to "where do ASP.NET Core sessions go", and `ITicketStore` over a distributed cache is a
documented pattern.

**Loses.** The durability it needs for "loses no session" and for revocation holding across tasks
is four months old, needs cluster mode and the larger node families, and costs an order of
magnitude more than the table; the cheaper Valkey deployments all lose writes on a failover, and
the write they can lose includes a logout. The versioned replace and the fenced release live in
scripts that this repository's test rules cannot prove before the deployed gate, where the DynamoDB
condition expressions are proved against a fake in the owner's project. IAM authentication adds a
fifteen-minute token and a twelve-hour connection to the store's lifecycle, handled by GLIDE and
not by StackExchange.Redis. Memory is a bound to size and an eviction policy to set, where the
table has neither. And the plan has already spent the design: the item layout, the condition table,
the fake's surface and the cost line all exist for DynamoDB, and a switch is an ADR superseding
part of ADR-0004, a schema enum value, a plan revision and a rewritten P2-1 request.

The one scenario that flips the comparison: a gate that measures gateway latency per call and sets
a budget the table cannot meet. No gate does, and P2-10's load test is the first place one could.
If that happens, the fix inside the current design is a short in-process read cache with the lease
and the end invalidating it, which P2-3's "no cache" rule would then have to be re-decided; that
is still cheaper than the engine swap.

## The field, ranked

Ranked for this platform's Phase 2, against the requirements table above, not in general.

| Rank | Option | Meets P2-1 and P2-3 as written | Floor per cell | Hermetic proof | Verdict |
| --- | --- | --- | --- | --- | --- |
| 1 | **DynamoDB, on-demand, one table per cell** | Yes: durable, conditional writes native, consistent reads on a flag, time to live on the table | $0 | A fake JSON 1.0 server, designed in change request P2-1 | **Keep.** The plan's choice, and the comparison confirms it. |
| 2 | MemoryDB for Valkey | Yes: durable by construction across zones, strongly consistent primary | About $60 to $70 | Scripts unproved until deployment | The Valkey that is durable without a mode to switch on, on small nodes, and mature. The best Valkey, and still seven times the cell's floor. |
| 3 | ElastiCache for Valkey, durable, synchronous writes | Yes | About $150 to $185 | As above | Meets the acceptance, and is the most expensive thing in this table. The asynchronous setting is cheaper only in latency, not in price, and reintroduces a ten-second loss window. |
| 4 | S3, one object per session, conditional writes | Yes, with care: an object write under `If-Match` on the ETag is the versioned replace, a read is strongly consistent, and the bucket is durable. Expiry is a lifecycle rule by day, so the read rule from the DynamoDB design carries over. The lease is an object of its own under `If-None-Match`. | $0 | `FakeS3Server` exists today, and `S3ObjectStore` is the shape | The honest fallback if DynamoDB were ever unavailable to the project: no new service, a proof pattern the repository already has. Tens of milliseconds per read, twice per API call, is why it is not first. |
| 5 | ElastiCache Serverless for Valkey | No: no durability setting, so a failover can lose a login, a refresh or a logout | About $6 | As rank 2 | The cheapest Valkey and the easiest to run. Right for a cache, wrong for the record of who is signed in. |
| 6 | ElastiCache for Valkey, node-based, Multi-AZ, not durable | No, as rank 5 | About $19 | As rank 2 | Rank 5's loss semantics with nodes to size and patch |
| 7 | Aurora Serverless v2 PostgreSQL, or RDS | Yes: a row with a version column and `UPDATE ... WHERE version = ?` | $0 to about $44 | A container, which the suite cannot have; or an in-process SQLite stand-in that proves another engine | A relational store for one record type. More service than the record needs, and a cold start on the login path when scaled to zero. |
| 8 | DAX in front of DynamoDB | No on its own: an eventually consistent cache in front of the table defeats "refused by the other task on its next request" | About $30 per node | None | Not a store. Listed because it is the usual answer to DynamoDB latency, and it is the wrong one here. |
| - | Sticky sessions on the ALB over the memory store | No: a killed task loses its sessions | $0 | n/a | Rejected. P2-4's acceptance is a killed task with live sessions. |
| - | The ticket in the cookie | No: tokens in the browser | $0 | n/a | Rejected by ADR-0004 and Phase 1 criterion 3. |
| - | Valkey run by the platform on ECS | Would need the platform to build durability | Task price | n/a | Rejected. A database to operate, for a team that chose managed services everywhere else. |

## What would change the ranking

- **A latency budget per call** that single-digit milliseconds, twice, cannot meet. Measured at P2-10, not assumed.
- **DynamoDB off the project's supported list**, or the table's pricing changing shape. Rank 4 is then the answer, not a Valkey, because it adds no service and keeps the proof pattern.
- **The durability mode reaching the `t4g` family or serverless**, which would bring rank 3's floor down toward rank 6's. Worth a look at each Phase 2 cost review.
- **A second consumer for Valkey**: pub/sub for convergence, a key ring, an asset cache. Three uses on one cache change the cost per use. None is in the plan, and each would be its own change request.

## What this session could not verify

`aws.amazon.com`, `docs.aws.amazon.com` and the AWS builder site were not reachable from the
session's proxy, so no price in this note was read from an AWS pricing page. The ElastiCache
rates come from 2026 third-party summaries and the AWS China mirror, which agree with each other
on the serverless minimum and the `t4g.micro` rate and disagree on `m7g`. The DynamoDB per-unit
prices are the two figures consistent with AWS's announcement of a 50% cut from 1 November 2024;
several 2026 summaries still print the pre-cut figures as current. The MemoryDB node rate is from
memory. The durability mode's node families and cluster-mode requirement come from a July 2026
engineering write-up, not from the AWS documentation. Before any figure here enters
`docs/plan/05-infrastructure-and-cost.md`, a session with the pricing pages open should re-read
it, as that document's own "Cost" section did for Phase 1.

## Sources

Read on 2026-10-20.

- AWS, "Amazon ElastiCache for Valkey now supports durability", 2 June 2026: https://aws.amazon.com/about-aws/whats-new/2026/06/durability-amazon-elasticache/
- AWS Database Blog, "Announcing durability for Amazon ElastiCache for Valkey": https://aws.amazon.com/blogs/database/announcing-durability-for-amazon-elasticache/
- ElastiCache developer guide, automatic failover and replication: https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/AutoFailover.html, https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/Replication.html
- ElastiCache developer guide, authenticating with IAM: https://docs.aws.amazon.com/AmazonElastiCache/latest/dg/auth-iam.html
- ElastiCache pricing, AWS China mirror: https://www.amazonaws.cn/en/elasticache/pricing/
- Upstash, "AWS ElastiCache pricing explained (2026)": https://upstash.com/blog/aws-elasticache-pricing-explained-2026-full-cost-breakdown
- AWS, "Amazon DynamoDB reduces prices for on-demand throughput and global tables", 14 November 2024: https://aws.amazon.com/about-aws/whats-new/2024/11/amazon-dynamo-db-reduces-prices-on-demand-throughput-global-tables
- Bytebase, "Understanding DynamoDB pricing": https://www.bytebase.com/blog/understanding-dynamodb-pricing/
- DynamoDB developer guide, time to live: https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/TTL.html
- AWS, "MemoryDB and MemoryDB for Valkey": https://aws.amazon.com/compare/memorydb-and-memorydb-for-valkey/, https://aws.amazon.com/memorydb/features
- AWS, supported services for the new sign-up experience: https://docs.aws.amazon.com/accounts/latest/reference/supported-services-sign-up-new.html
- `Valkey.Glide` 1.1.0 on nuget.org: https://www.nuget.org/packages/Valkey.Glide
- Valkey GLIDE, IAM authentication: https://glide.valkey.io/concepts/client-features/aws-iam-auth-with-glide, https://glide.valkey.io/how-to/security/iam-integration/
- StackExchange.Redis, IAM authentication issue: https://github.com/StackExchange/StackExchange.Redis/issues/1273
- Terraform AWS provider, "Durability in ElastiCache replication groups", shipped in 6.51.0: https://github.com/hashicorp/terraform-provider-aws/issues/48228
