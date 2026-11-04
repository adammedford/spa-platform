# PingFederate in the poc cell: a second service behind the internal ALB, the licence as a secret, sizing, cost and teardown

- **Ticket:** #948, "PingFederate in the poc cell: a second service behind the internal ALB, the licence
  as a secret, sizing, cost and teardown" (`wayfinder:research`), on the map #944.
- **Date:** 2026-11-04. **Branch:** `research/pingfederate-in-the-poc-cell`, pushed and never merged, as
  the map rules.
- **Method:** docs-only. Repository reading at commit `8ceeee3` (origin/main on 2026-11-04):
  `infra/modules/cell` (every `.tf`, the README and both `tests/*.tftest.hcl`), `infra/envs/poc/platform`
  (`main.tf`, `variables.tf`, `outputs.tf`, `terraform.tfvars.example`, the untracked `terraform.tfvars`,
  `tests/root.tftest.hcl`), `infra/envs/poc/idp` and `infra/envs/poc/apps`,
  `contracts/infra-outputs.schema.json`, `contracts/platform-config.schema.json`,
  `contracts/resource-tags.md`, `contracts/changes/p2-6-pingfederate-profile.md`,
  `docs/plan/04-tests.md` (P-01, P-05, P-06), `docs/plan/05-infrastructure-and-cost.md`,
  `docs/runbooks/deploy.md`, `docs/runbooks/teardown.md`, ADR-0029, `docs/agents/aws-rules.md` (read
  for the Region and spend-limit rules; nothing was acted on), `tools/scripts/tunnel.sh`,
  `verify-tunnel.sh`, `certs.sh`, `teardown.sh`, `verify-teardown.sh`,
  `src/Platform.Security/SecurityHeaders.cs`, `src/Gateway/Dockerfile`, `.scratch/phase-2/spec-p2-6-pingfederate.md`
  and `contracts/gates.md`. `tools/aws-project.env` was read for the Region name alone (`us-east-2`).
  Primary pages fetched on 2026-11-04 are listed in section 7 with what each gave. Two public price-list
  documents were downloaded over plain HTTPS (no credential) and parsed with `python3 -I`; the Ping
  DevOps repositories were read through the GitHub contents API (`gh api`, public repositories). **Not
  done:** no `aws` CLI call, no `terraform plan` or `apply`, no credential step, no read of the cell;
  nothing was executed against PingFederate, because no Ping DevOps user key or PingFederate licence
  exists on this machine (Docker Desktop 29.9.1, arm64, Compose v5 is present but unused here). Every
  provider claim is read from a primary page; a claim that no page read states is marked "not found".
  The charting session's fact-check was a starting point only; nothing below cites it.

## Answer in brief

1. **The cell today refuses the shape as written, in five places, and the tunnel already carries it.**
   `alb.tf` has "No listener rules, ever (ADR-0010)" and one target group behind one listener on 443;
   P-01 reads "Exactly one ALB, one listener, one target group, one ECS service, one bucket", and
   `infra/envs/poc/platform/tests/root.tftest.hcl` holds the counts (`aws_lb_target_group = 1`,
   `aws_ecs_service = 1`, `aws_iam_role = 2`, `aws_ecr_repository = 2`, `aws_cloudwatch_log_group = 1`,
   no `aws_lb_listener_rule` in its map); `tests/network.tftest.hcl` holds exactly two security groups
   and four rules; `tests/module.tftest.hcl` holds the module's resource-type allowlist; and
   `verify-teardown.sh`'s by-name reader is held to `iam.tf`'s two role suffixes by its test companion.
   So a second service is a change request on P-01's reading and an amendment of ADR-0010 (integrator
   paths), carried out as the repository already carries gated shapes: one input defaulting off
   (`multi_task`, `session_store`), under which the default plan is unchanged (section 1). The tunnel
   needs nothing: it is one Session Manager port forward from local 8443 to the ALB's DNS name on 443
   through the gateway task, the ALB answers every `*.spa-poc.test` host on that listener, the imported
   certificate's one SAN is `*.spa-poc.test`, and `verify-tunnel.sh` only checks that the host resolves to
   `127.0.0.1`; `idp.spa-poc.test` is one more hosts-file line.
2. **The provider service: one ARM64 Fargate task, 1 vCPU and 4 GB, HTTPS target group on the engine
   port with `/pf/heartbeat.ping`, the admin port never in a target group; and the issuer URL is the
   unresolved decision.** The image is `pingidentity/pingfederate` (13.1.4 tags published 2026-11-03,
   amd64 and arm64, about 610 MB compressed); Ping qualifies 13.1 on Graviton 2, names AWS Fargate among
   the platforms it "has run well on", and asks 4 GB of RAM with 1.5 GB available to PingFederate and,
   on AWS, 2 cores; Fargate's table admits 1 vCPU with 2 to 8 GB. The engine listens HTTPS on 9031
   (plain HTTP is disabled by default) and the admin console and API on 9999; both ports are container
   variables. A host rule `idp.spa-poc.test` forwards to a second target group; the ALB does not validate
   a target's certificate, so PingFederate's self-signed engine certificate is fine behind it. Two facts
   force the issuer decision: the browser reaches the provider as `https://idp.spa-poc.test:8443` through
   the tunnel, while inside the VPC nothing resolves that name and the ALB listens on 443, not 8443; and
   `src/Gateway/Dockerfile` installs no trust for the local authority that signs every `*.spa-poc.test`
   certificate, so the gateway's back-channel calls to any `https://idp.spa-poc.test...` fail TLS today.
   Three shapes are laid out in section 2; the one that keeps the issuer a single string with the fewest
   new resource types moves the cell's one listener from 443 to 8443 and adds a Route 53 private hosted
   zone for `spa-poc.test` aliased to the ALB, so browser and task both use
   `https://idp.spa-poc.test:8443` and PingFederate's back-channel logout POST can reach
   `https://app1.spa-poc.test:8443/...` by the same zone. Whichever shape, the gateway image must trust the
   local authority (an integrator change). `Issuers:{name}:BrowserOrigin` is placed in `form-action` and
   nowhere else; `connect-src` stays `'self'`; the `form_post` page is the provider's. Where PingFederate
   takes the discovery `issuer` from is not stated on any page read (the getting-started profile sets
   `"baseUrl": "https://localhost:9031"` and an empty virtual-host list): the prototype reads it live.
3. **The secret is the DevOps user and key, not a licence file, and every task start fetches a fresh
   evaluation licence.** The image's hook `17-check-license.sh` uses
   `${LICENSE_DIR}/pingfederate.lic` if present, otherwise with `PING_IDENTITY_DEVOPS_USER` and
   `PING_IDENTITY_DEVOPS_KEY` it downloads a licence from `https://license.pingidentity.com/devops/license`
   (four tries) and fails the container (`container_failure 17`) if neither works; `PING_IDENTITY_ACCEPT_EULA=YES`
   is required. So three SecureStrings under `/spa-platform/{cell}/pingfederate/` (user, key, admin
   password `PING_IDENTITY_PASSWORD`) go into the task definition as `secrets`/`valueFrom`: injected as
   environment variables at container start, read by the task **execution** role with `ssm:GetParameters`
   (no `kms:Decrypt` under the AWS-managed key), and a changed value reaches no running task until a new
   one launches. The task's egress must reach the licence server on 443. The 30 days therefore count
   from each start, which outlives any sitting; a task that stays up past them hits Ping's licensing
   events (60-day warning, expiry at the beginning of the day, a grace period whose length no page read
   states, then "PingFederate Has Stopped Processing Requests"). Not found: whether each fetch yields a
   full 30 days, and the licence's clause on hosting, which the map's licence ticket owns. The
   getting-started profile ships its admin password, a PKCS#12 server key pair and the system keys in
   clear text in `env_vars`: the cell's profile generates its own.
4. **Configuration at start: a server profile from a git URL or baked under `/opt/in`, applied by the
   image's hooks at container start; a change is a new task.** `SERVER_PROFILE_URL` (HTTPS git only; a
   private repository means `username:token` in the URL, a secret in an environment variable),
   `SERVER_PROFILE_PATH`, `SERVER_PROFILE_BRANCH`, `SERVER_PROFILE_UPDATE=false` by default; or a
   profile mounted or built under `/opt/in`, which is the shape for an image in the cell's registry.
   The profile is an overlay on the install: `env_vars`, `instance/bulk-config/data.json.subst` (the
   whole configuration, substituted and imported at start), `instance/server/default/data/pf.jwk`, and
   the licence at `instance/server/default/conf/pingfederate.lic` if one is supplied. Fargate storage is
   ephemeral and the cell mounts nothing, so a change made live through the admin API or console
   (port 9999, private) dies with the task; the profile is the source of truth and a configuration
   change is a new image or a new task (force new deployment). The ECR repository is a third one in
   `storage.tf` (a count P-01 holds) or a pull-through cache, which for Docker Hub needs a Secrets
   Manager secret under `ecr-pullthroughcache/`, a resource type no teardown reader sees today.
5. **Cost: about $0.05 an hour for the provider task, about $0.21 for a four-hour sitting, $0.42 for
   eight, and the teardown removes it with the platform root if every new resource is tagged and typed
   for the readers.** US East (Ohio) list prices from the public price list (AmazonECS offer version
   20261019224317): Fargate ARM $0.03238 per vCPU-hour and $0.00356 per GB-hour (x86: $0.04048 and
   $0.004445), the same figures the pricing page's us-east-1 examples give. 1 vCPU and 4 GB on ARM is
   $0.0466/h; 2 vCPU and 4 GB is $0.0790/h; plus $0.005/h for the task's public IPv4 address in the
   Phase 1 shape (under `multi_task` the NAT carries it instead: $0.045/h and $0.045/GB in Ohio, already
   in #347's $0.10/h). The ALB is unchanged ($0.0225/h and $0.008 per LCU-hour in Ohio; the first ten
   rules evaluate free). The whole Phase 1 cell goes from about $0.039/h to about $0.091/h, so a
   four-hour sitting costs about $0.36 and a day left running about $2.18; $37.68 a month for the
   provider task alone if forgotten. The spend limit's figure is not in the repository (`aws-rules.md`
   says only that one may exist). Teardown: `terraform destroy` of the platform root removes a gated
   service, task definition, target group, listener rule, log group, roles, repository and parameters
   with it; `teardown.sh` already deletes inactive task definitions by the family prefix
   `spa-platform-<cell>`, which a `spa-platform-<cell>-pingfederate` family matches; the by-name role
   reader and the module's allowlist must name the new roles and types; Cloud Map is avoided because its
   resources "must be cleaned up manually".

## 1. The cell today

### What `modules/cell` builds, and what holds it there

- **One internal ALB, one HTTPS listener on 443, one target group, no rules.** `infra/modules/cell/alb.tf`:
  "ADR-0010 is the whole design: a wildcard certificate on one listener, forwarding to a single target
  group with the Host header preserved, and no listener rules" (lines 4-7); `default_action` forwards to
  `aws_lb_target_group.gateway`, and "No listener rules, ever (ADR-0010): every host goes to the one
  target group, and the gateway answers 404 to a host the registry does not know" (lines 134-135).
  `preserve_host_header = true`, so the port the browser sent (`app1.spa-poc.test:8443`) reaches the
  target. The target group is `target_type = "ip"`, HTTP on `var.gateway_port` (8080), health check on
  `var.health_check_path`, `deregistration_delay = 30`.
- **The certificate** is found, not created: `data "aws_acm_certificate" "wildcard"` by domain
  `*.${var.certificate_domain}`, type IMPORTED, tag `Cell = var.cell`. `tools/scripts/certs.sh` issues
  it from a local authority with one subject alternative name, `DNS.1 = *.spa-poc.test` (lines 312-315),
  imports it tagged `Platform=spa-platform`, and tells the operator to add
  `127.0.0.1 app1.spa-poc.test app2.spa-poc.test app3.spa-poc.test` to `/etc/hosts` (line 649).
- **The network** (`network.tf`): a VPC, two public subnets (`map_public_ip_on_launch = true`), an internet
  gateway, no NAT in the default shape, two security groups and exactly four rules: ALB ingress 443 from
  the task group ("the path of the Session Manager tunnel"), ALB egress to the gateway port on the task
  group, task ingress the gateway port from the ALB group (P-06), task egress 443 to `0.0.0.0/0` ("Out to
  Cognito, S3, Parameter Store, ECR and CloudWatch Logs. HTTPS only"). The VPC's default group is adopted
  empty. Under `multi_task` (#347, #583) two private subnets, one NAT gateway with an EIP, per-zone route
  tables and the S3 and DynamoDB gateway endpoints are planned; the task's egress stays 443 alone.
- **The compute** (`ecs.tf`): cluster `spa-{cell}`, service `gateway`, log group
  `/spa-platform/{cell}/gateway`, one task definition `spa-platform-{cell}` at `cpu = "256"`,
  `memory = "1024"`, `ARM64`, `awsvpc`, two containers (gateway with the one port mapping; mock API with
  none, reached over loopback), `enable_execute_command = true` ("The tunnel's hop"),
  `platform_version = "LATEST"`, `assign_public_ip = !var.multi_task`, `health_check_grace_period_seconds = 60`,
  both containers hardened with `capabilities.drop = ["ALL"]` and `initProcessEnabled`. The issuers
  arrive as `var.issuers` (`authority`, `profile`, `browser_origin`) and become
  `Issuers__{name}__Authority|Profile|BrowserOrigin`; a precondition requires exactly one issuer while the
  mock API image is set (assumption A4).
- **IAM** (`iam.tf`): two roles, `spa-platform-{cell}-task-execution` (ECR pull on the cell's two
  repositories, log writes to the one group; no `ssm:` action) and `spa-platform-{cell}-task` (S3 reads,
  `ssm:GetParameter(s)` on `/spa-platform/{cell}/apps/*`, the four `ssmmessages` actions, the gated
  DynamoDB and key-ring grants). The test suite "fails on a statement the test does not recognise".
- **Storage** (`storage.tf`): one bucket, and `repositories = toset(["gateway", "mock-api"])` with
  `image_tag_mutability = "IMMUTABLE"` and `force_delete = true`.
- **Outputs**: the module publishes `vpc_id`, `public_subnet_ids`, the two security-group ids,
  `https_listener_arn`, `target_group_arn`, `ecs_cluster` and the rest; the platform root publishes only
  the contract's fields, because `contracts/infra-outputs.schema.json` is `additionalProperties: false`
  and "an output this root publishes that the contract has no field for is a trap for whatever
  assembles outputs.json" (`infra/envs/poc/platform/outputs.tf`).

### The tests that hold the shape

| What | Where | Holds |
| --- | --- | --- |
| P-01 | `docs/plan/04-tests.md:76` | "Exactly one ALB, one listener, one target group, one ECS service, one bucket. No NAT gateway, database or EC2 instance." |
| The counts | `infra/envs/poc/platform/tests/root.tftest.hcl` (`the_platform_plan_holds_exactly_the_allowlisted_types_and_counts`) | `aws_lb = 1`, `aws_lb_listener = 1`, `aws_lb_target_group = 1`, `aws_ecs_service = 1`, `aws_ecs_task_definition = 1`, `aws_cloudwatch_log_group = 1`, `aws_iam_role = 2`, `aws_iam_role_policy = 2`, `aws_ecr_repository = 2`, `aws_security_group = 2`, two ingress and two egress rules; a type not in the map fails ("Name the type and its count in this map, in the same change, and say what it is for") |
| The type allowlist | `infra/modules/cell/tests/module.tftest.hcl` (`the_module_declares_only_the_cell_s_intended_resource_types`) | the module's resource types; `aws_lb_listener_rule`, `aws_route53_zone`, `aws_route53_record`, `aws_ssm_parameter`, `aws_service_discovery_*` are not among them |
| The network boundary | `infra/modules/cell/tests/network.tftest.hcl` | "The cell admits two things and no third", "sends two things and no third", "has two security groups ... A third group is a third boundary nothing here checks" |
| The teardown readers | `tools/scripts/verify-teardown.sh:71` (`CELL_ROLE_SUFFIXES="task-execution task"`), held to `iam.tf` by `verify_teardown_tests.py` | a third role fails the companion until named |

The repository's precedent for a shape that changes those counts is a gated input: `multi_task` plans
the NAT gateway, the EIP, the endpoints and the private subnets only when true, and the root test keeps
a run per shape ("Mutations that turn this run red: a root default of \"dynamodb\", or multi_task
true", `root.tftest.hcl:466`); `session_store = "dynamodb"` plans the table and its grant the same way
(#338's P-01 tripwire, change request `issue-338-p-01-dynamodb-tripwire`, decision 1). An
`evaluation_provider`-style input (name for the spec to choose) that plans the PingFederate service,
target group, listener rule, roles, log group, repository and parameters only when set keeps the
default plan identical and gives P-01's sentence its gated reading, as #583 gave it for the NAT. The
alternative, a fourth root `infra/envs/poc/pingfederate`, sidesteps the platform root's counts but
needs the listener ARN, the VPC, the subnets and the security groups, which the platform root does not
publish (the outputs-contract trap above), so it would look them up by name or tag; and it adds a
fourth destroy to `teardown.sh`'s order. Either way ADR-0010's "no listener rules" line is amended,
which is the integrator's.

### How the tunnel presents `*.spa-poc.test:8443`

- `tools/scripts/tunnel.sh` builds `TARGET="ecs:${CLUSTER}_${TASK}_${RUNTIME}"` on the gateway
  container of "the first task a session can reach" and starts
  `AWS-StartPortForwardingSessionToRemoteHost` with
  `{"host":["<albDnsName>"],"portNumber":["443"],"localPortNumber":["8443"]}` (lines 179-181). It
  reads the three names from `infra/envs/poc/outputs.json` and nothing else. AWS: "The `host` value
  represents the hostname or IP address of the remote host that you want to connect to. General
  connectivity and name resolution requirements between the managed node and the remote host still
  apply" ([Start a session](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html)).
  One session forwards one local port to one host and port.
- The tunnel is TCP: "the browser's TLS handshake reaches the ALB unchanged, and the wildcard certificate
  certs.sh imported is the one presented" (`tunnel.sh` header; `docs/plan/05-infrastructure-and-cost.md`
  -> Workstation access). Every name under `*.spa-poc.test` that the hosts file points at `127.0.0.1`
  therefore reaches the same listener with its own `Host`; `verify-tunnel.sh` passes when the name
  "resolved to the loopback address" and TLS "validated against this workstation's own trust store".
- So `idp.spa-poc.test` costs one hosts-file entry on the workstation and nothing in the tunnel; the
  certificate already covers it (`certs.sh verify` checks a host "is under ${DOMAIN}, so the wildcard"
  covers it, lines 668-674).
- The hop stays the gateway task: `deploy.sh` replaces it and the tunnel with it (`docs/runbooks/deploy.md`
  step "Then check it from the workstation"); a PingFederate task that is not the hop is not affected by
  a gateway deployment.

### What the teardown proof reads

`teardown.sh` (header, lines 15-35): empties the bucket, `terraform destroy` in platform, apps, idp,
deletes the certificate (found by its `Platform` and `Cell` tags), "the cell's inactive task
definitions" (`ecs list-task-definitions --family-prefix "$FAMILY"` with `FAMILY="spa-platform-${CELL}"`,
line 269; a prefix match, so a family `spa-platform-poc-pingfederate` is included), and the key ring
under `/spa-platform/<cell>/data-protection/` "which no root's state holds"; then `verify-teardown.sh`,
whose four readers are IAM roles by tag, IAM customer managed policies by tag, the cell's roles by name
and the tagging API in the selected Region, dropping INACTIVE ECS records and EC2 ids EC2 no longer
knows (ADR-0029, decision 1). `contracts/resource-tags.md`: every root applies `Platform`, `Cell`,
`ManagedBy` through `default_tags`, so a resource added to the platform root is tagged without its
author remembering; "A root that adds an untaggable resource which is *not* a child of a parent some
reader sees makes P-05 vacuous for that resource ... Workstream 1 files a change request in that case".

## 2. The provider service

### The image

- Docker Hub `pingidentity/pingfederate`: tags `13.1.4-latest`, `latest`, `edge`, `13.1.4-edge`,
  `2610-13.1.4` and the alpine/ubi variants, all `last_updated` 2026-11-03T18:4x Z, each with `amd64`
  and `arm64` images of about 610 MB (610,792,844 and 610,075,168 bytes as the tags API reports for
  the alpine-based tags; the UBI tag is about 679 MB)
  ([tags API](https://hub.docker.com/v2/repositories/pingidentity/pingfederate/tags?page_size=25&ordering=last_updated)).
  The licence rule "can only be used with images published in the last 90 days" (section 3) makes a
  digest pin go stale by rule; the map's "image pin" line already notes this.
- Ping's 13.1 system requirements: "4 GB of RAM", "Ensure that at least 1.5 GB are available to
  PingFederate", "a minimum of four processing cores" in general and, for AWS, "a minimum 2 processor
  cores and 4 GB of RAM"; "PingFederate is qualified to run on the Graviton 2 processor architecture";
  on virtualisation, PingFederate "has run well on several, including AWS Fargate, Hyper-V, VMWare, and
  Xen", none of them qualified; Docker 28.2.2 is listed; Java 17, 21 and 25 (Corretto, OpenJDK, Oracle)
  ([pf_system_requirements, 13.1](https://docs.pingidentity.com/pingfederate/13.1/installing_and_uninstalling_pingfederate/pf_system_requirements.html)).
- The DevOps image's own knobs: `MAX_HEAP_SIZE` default `384m`, `JAVA_RAM_PERCENTAGE` default `75.0`
  ("DO NOT set to 100% or your JVM will exit with OutOfMemory errors"), `OPERATIONAL_MODE` `STANDALONE`
  (one container is console and engine), `PING_IDENTITY_ACCEPT_EULA` "Must be set to 'YES' for the
  container to start", `ROOT_USER` `administrator`, `PING_IDENTITY_PASSWORD` default `2FederateM0re`,
  `STARTUP_COMMAND` `${SERVER_ROOT_DIR}/bin/run.sh`, `TAIL_LOG_FILES` `server.log` and `init.log` to the
  container output ([image README](https://developer.pingidentity.com/devops/docker-images/pingfederate/README.html)).

### CPU and memory on Fargate

Fargate's valid task sizes ([task definition parameters -> Task size](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html)):
`256 (.25 vCPU)`: 512 MiB, 1 GB, 2 GB; `512 (.5 vCPU)`: 1 to 4 GB; `1024 (1 vCPU)`: 2 to 8 GB;
`2048 (2 vCPU)`: 4 to 16 GB. Ping's 4 GB with 1.5 GB to the process fits `1024`/`4096` (1 vCPU, 4 GB) on
ARM64; `2048`/`4096` meets Ping's "2 processor cores" line for AWS literally. ARM64 on Fargate needs
"Fargate platform version 1.4.0 or later" and Linux ([ARM64 task definitions](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-arm64.html));
the cell's service runs `platform_version = "LATEST"` and the image has an `arm64` manifest. Whether
`MAX_HEAP_SIZE` should be raised from 384m inside a 4 GB task, and how long PingFederate takes to answer
its heartbeat after start (against the cell's 60 s health-check grace), are not found on any page read:
the prototype measures both. Ephemeral storage is 20 GB by default and ephemeral
([Fargate pricing](https://aws.amazon.com/fargate/pricing/); `ephemeralStorage` in the task definition page).

### Ports, the target group and the health check

- `pf.https.port`: "Defines the port on which PingFederate listens for encrypted HTTPS (SSL/TLS)
  traffic. The default value is 9031." `pf.http.port`: "For security reasons, this port is disabled by
  default." `pf.admin.https.port`: "Defines the port on which the PingFederate administrative console
  runs. The default value is 9999." `pf.admin.baseurl` populates the admin node's resource references
  ([run.properties reference, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_pf_propert.html)).
  In the image: `PF_ENGINE_PORT` 9031, `PF_ADMIN_PORT` 9999, `PF_ENGINE_SECONDARY_PORT` -1 (mutual TLS,
  off), `PF_ENGINE_PUBLIC_HOSTNAME` `localhost`, `PF_ADMIN_PUBLIC_BASEURL` `https://localhost:9999`
  (README above); the pre-hook `04-check-variables.sh.pre` lets `PF_RUN_PF_HTTPS_PORT` override the
  engine port and `PF_RUN_PF_ADMIN_HTTPS_PORT` the admin port
  ([hook source](https://github.com/pingidentity/pingidentity-docker-builds/blob/master/pingfederate/opt/staging/hooks/04-check-variables.sh.pre)).
- Health: `/pf/heartbeat.ping` "returns an HTTP status code of 200 and a message body of OK if the
  PingFederate runtime server is up and functional"; "Load balancers can use this endpoint to determine
  the status of PingFederate"; a GET that gets a connection error or a status other than 200 means
  down ([system-services endpoints](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_sys_services_endpoints.html)).
  `pf.heartbeat.system.monitoring` "Enables or disables (the default)" the verbose statistics form, which
  Ping says to keep behind the load balancer (run.properties page). It is served on the engine port.
- Target group: protocol HTTPS, port the engine port, `target_type = "ip"`, health check HTTPS
  `/pf/heartbeat.ping` matcher 200. "The load balancer establishes TLS connections with the targets using
  certificates that you install on the targets. The load balancer does not validate these certificates.
  Therefore, you can use self-signed certificates or certificates that have expired"
  ([target groups](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-target-groups.html)).
  So PingFederate's default self-signed engine certificate is enough behind the ALB; the browser sees
  the listener's wildcard certificate.
- The admin port 9999 goes into no target group and no rule from the ALB group. The PingFederate security
  group admits the engine port from the ALB group (and, for the direct shape below, from the task
  group) and nothing else; its egress is 443 out (licence server, git host, ECR, logs) plus whatever
  the chosen shape needs. No page read documents a way to disable the admin listener outright (not
  found). To reach the console or the admin API for the Terraform provider, the existing hop serves: a
  second `AWS-StartPortForwardingSessionToRemoteHost` through the gateway task to the PingFederate
  task's private address on 9999, which needs one rule, 9999 from the task security group on the
  PingFederate group, and no ECS Exec on the provider's service; the forward originates at the gateway
  task's interface, which "general connectivity ... requirements" above cover.

### The host rule

- Rules: "evaluated in priority order, from the lowest value to the highest value. The default rule is
  evaluated last"; "Each rule must include exactly one of the following actions: forward, redirect, or
  fixed-response" ([listener rules](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/listener-rules.html)).
  Quotas: 100 rules per ALB, 100 target groups per ALB, 5 target groups per action, 5 condition values
  per rule ([quotas](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-limits.html)).
- Host condition `Values`: "The host names. The maximum length of each string is 128 characters. The
  comparison is case insensitive. The following wildcard characters are supported: * ... and ? ... You
  must include at least one \".\" character. You can include only alphabetical characters after the
  final \".\" character"; `RegexValues` are "regular expressions to compare against the host header"
  ([HostHeaderConditionConfig](https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_HostHeaderConditionConfig.html);
  [condition types](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/rule-condition-types.html)).
  The browser sends `Host: idp.spa-poc.test:8443` and `preserve_host_header` passes it on. A `Values`
  entry cannot be written with the port (digits after the final dot); whether the comparison ignores a
  port is **not found** on the pages read. A `RegexValues` entry such as `^idp\.spa-poc\.test(:[0-9]+)?$`
  matches either form; the sitting verifies which form the rule needs.
- Cost of the rule: none. "The first 10 processed rules are free" and "If you have 10 or fewer rules
  configured, the rule evaluations dimension is ignored in LCU computation"
  ([ELB pricing](https://aws.amazon.com/elasticloadbalancing/pricing/)).

### The issuer URL: the task's path, the browser's path, and TLS trust

The handler discovers from `Issuers:{name}:Authority` ("The issuer the OpenID Connect handler discovers
from", `contracts/platform-config.schema.json`), and OpenID Connect requires the discovery document's
`issuer` to be that value; the browser is sent to the authorize endpoint the document names. Three
facts from the repository fix the problem:

1. **The browser's host carries the tunnel's port.** Every host is `<name>.spa-poc.test:8443`
   (`allowed_host_suffixes` default `[".spa-poc.test:8443"]`; the Cognito callback
   `https://${host}/signin-oidc` in `infra/envs/poc/apps/main.tf`). The provider's browser-facing origin is
   therefore `https://idp.spa-poc.test:8443`.
2. **Inside the VPC that name resolves nowhere and that port answers nowhere.** The cell has no Route 53
   zone (no `route53`, `service_discovery` or `lb_listener_rule` anywhere under `infra/`), the ALB
   listens on 443 only, and `extraHosts` "isn't supported for tasks that use the awsvpc network mode"
   (task definition parameters page), so a hosts-file trick in the task is out.
3. **The gateway task trusts no local authority.** `src/Gateway/Dockerfile` has no CA bundle step (grep
   for `SSL_CERT`, `ca-certificates`, `trust`: nothing), and every `*.spa-poc.test` certificate chains to
   `certs.sh`'s local authority, trusted on the workstation alone. Any `https://idp.spa-poc.test...`
   authority fails the handler's TLS validation until the image trusts that authority (a `src/Gateway`
   change, the integrator's) or the task is given a certificate it already trusts, which no `.test` name
   can have from a public authority.

PingFederate's side: the base URL is "The fully qualified host name, port, and path (if applicable) on
which the PingFederate server runs. This field is used to populate configuration settings in metadata
files" ([Federation Info, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_protocolsettingstasklet_federationinfostate.html));
virtual host names are "alternate domain names at which PingFederate receives application and protocol
messages", and when a request's host matches one PingFederate "honors the originally requested host
throughout all browser redirects and metadata retrieval"
([virtual host names, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_virtual_host_names.html));
virtual issuers (host and optional path) make PingFederate populate "the issuer claim according to the
virtual issuer setting and the authorization request"
([virtual issuers](https://docs.pingidentity.com/pingfederate/latest/administrators_reference_guide/pf_adding_virtual_issuers_openid_connect.html));
a separate **Token Endpoint Base URL** makes the discovery document advertise the token endpoint at
that base and is also accepted when validating a `private_key_jwt` assertion's `aud`
([Authorization Server Settings, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_authorizationserversettingstasklet_oauthauthorizationserversettingsstate.html)).
The discovery page's example has `"issuer": "https://localhost:9031"` and
`"token_endpoint": "https://www.example.com:9031/as/token.oauth2"` from such a setting
([OpenID Provider configuration endpoint](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_openid_provider_config_endpoint.html)).
**Which setting the discovery `issuer` is derived from is not stated on any page read**; the
getting-started profile sets `"baseUrl": "https://localhost:9031"`, `"virtualHostNames": []` and
`"tokenEndpointBaseUrl": ""` in `data.json.subst`. The prototype reads the live document; the shape
below assumes the issuer follows the base URL and is written with the port.

The shapes, each with one issuer string for browser and task:

| Shape | Browser path | Task path | New resources | What it costs |
| --- | --- | --- | --- | --- |
| **A. Listener on 8443 and a private zone to the ALB** | `idp.spa-poc.test:8443` -> tunnel -> ALB:8443 -> host rule -> PF target group (HTTPS, engine port) | the same URL: a Route 53 private hosted zone `spa-poc.test` on the cell's VPC with an alias record for `idp` (and `app1..3` for item 7) to the internal ALB; the task's existing 443 egress becomes 8443 to the ALB group, which already admits the task group | one `aws_route53_zone`, records, one listener rule, one target group, the service and its parts | the listener port moves from 443 to 8443 (`alb.tf`, `tunnel.sh`'s `portNumber`, the `alb_https_from_task` rule); the plan text says "forwards local port 8443 to the ALB on 443" (`docs/plan/05`, frozen; runbooks are editable). Gives PingFederate's back-channel logout POST a reachable `https://app1.spa-poc.test:8443/signout-backchannel-oidc` by the same zone (PingFederate then needs the local authority among its trusted CAs). |
| **B. Direct reach on the engine port** | as A but the ALB stays on 443 | `idp.spa-poc.test:8443` resolved by a private zone (or ECS service discovery) to the PingFederate task's address, with `PF_RUN_PF_HTTPS_PORT=8443`; a fifth and sixth security-group rule (task group egress 8443 to the PF group; PF group ingress 8443 from the task group) | as A, minus the listener change; service discovery adds Cloud Map, whose resources "must be cleaned up manually" ([service discovery](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-discovery.html)), so a plain zone with a record is preferred, but a plain record needs the task's address, which service discovery supplies and a static record cannot | the task validates PingFederate's own certificate, so the profile must install the wildcard leaf and key (held on the workstation by `certs.sh`) as PingFederate's SSL server key pair; back-channel logout to `app1...:8443` still has no listener on 8443 |
| **C. A third container in the gateway task** | as A | loopback would be ideal, but the name cannot be made to resolve (`extraHosts` unsupported under `awsvpc`), so a zone is still needed; the provider restarts, and re-fetches its licence, on every `deploy.sh`; its state dies with each gateway task | fewest AWS resources | ties the provider's life to the gateway's rolls and to the hop |

Shape A keeps the ALB's "does not validate target certificates" on the task's path too, keeps the
engine port at its default, leaves the four-rule boundary at four rules (one port number changes), and
is the only one of the three under which item 7's back-channel POST has somewhere to land. Its price is
the listener port and a Route 53 zone, both reversible. Every shape needs the gateway image to trust the
local authority. The choice is the spec's, after the prototype has shown what PingFederate puts in
`issuer`.

### `BrowserOrigin` and the content security policy

`Issuers:{name}:BrowserOrigin` is "The origin the browser visits at the provider: scheme and authority
only. It goes into form-action and is the base of Cognito's logout URL" (pattern `^https://[^\s/?#]+$`,
which admits a port). `src/Platform.Security/SecurityHeaders.cs` builds the policy from a ten-directive
baseline in which `connect-src` is `'self'` and `form-action` is `'self'` plus that origin, read "as
written: a Uri would add a trailing slash" (lines 29-33, 112-128); no other directive takes it. So for
PingFederate: `BrowserOrigin = https://idp.spa-poc.test:8443`, `Authority` the issuer string the chosen
shape fixes, `Profile = pingfederate`. The `form_post` auto-submitting form is the provider's page under
the provider's policy; the gateway's `form-action 'self' https://idp.spa-poc.test:8443` governs the
logout form, as change request P2-6 item 3 states. The mock API validates against one issuer, so a
pingfederate issuer alongside the Cognito one trips `ecs.tf`'s precondition (`length(var.issuers) == 1`
while `mock_api_image` is set): the deployed sitting runs one issuer at a time, or the mock API learns a
second, which is workstream 5's and outside this ticket.

## 3. The licence and the admin password as secrets

### How the image gets a licence

`runtime/common/staging/hooks/17-check-license.sh` in `pingidentity/pingidentity-docker-builds`
(read through the GitHub contents API on 2026-11-04):

- `LICENSE_FILE="${LICENSE_DIR}/${LICENSE_FILE_NAME}"`; for PingFederate `LICENSE_DIR` is
  `${SERVER_ROOT_DIR}/server/default/conf` and `LICENSE_FILE_NAME` `pingfederate.lic` (image README).
- If the file exists it is used, after "Verifying license with a network query to
  https://license.pingidentity.com" (`/devops/check-license`) unless `MUTE_LICENSE_VERIFICATION` is set.
- Otherwise, "if PING_IDENTITY_DEVOPS_USER and PING_IDENTITY_DEVOPS_KEY defined make call to obtain a
  license from license server": up to four `curl` calls to
  `https://license.pingidentity.com/devops/license` with headers `product`, `version`, `devops-user`,
  `devops-key`, `devops-app`, writing the body to `LICENSE_FILE`; on success the EULA check runs
  (`container_failure 17 "You must accept the EULA by providing the environment variable
  PING_IDENTITY_ACCEPT_EULA=YES"`); on failure the file is removed and the reason printed.
- With neither: `container_failure 17 "License File absent"`.

So the container needs 443 out to `license.pingidentity.com` at every start, and the secret the cell
holds is the user and key pair. "If you have provided license files via the volume mount and a DevOps
User/Key, it will ignore the DevOps User/Key"; the mount path for PingFederate is
`/opt/in/instance/server/default/conf/pingfederate.lic`
([existing licence](https://developer.pingidentity.com/devops/how-to/existingLicense.html)). A licence
file obtained once could instead be baked into the profile, but it is itself 30 days old at most.

### The licence's terms

"Evaluation licenses are short-lived (30 days) and **must not** be used in production deployments";
"Evaluation licenses can only be used with images published in the last 90 days"; registering for the
DevOps program issues "credentials that automate the process of retrieving an evaluation product
license" ([prerequisites](https://developer.pingidentity.com/devops/get-started/prereqs.html)).
Registration needs "a registered account with Ping Identity"; "the DevOps credentials will be forwarded
to the email address associated with your Ping Identity account"; "Containers read these variables to
retrieve evaluation licenses" ([registration](https://developer.pingidentity.com/devops/how-to/devopsRegistration.html)).
**Not found** on the pages read: whether each fetch yields a licence dated from that day (so a task
started on day 31 runs another 30 days) or one with a fixed end; and any clause on where the software
may be hosted (the EULA itself was not read; the map's licence research ticket owns it).

### What happens when it lapses

PingFederate's licensing events: a warning "60 days ahead of the expiration" (so from day one of a
30-day licence); "If the license specifies an expiration date, the license expires at the beginning of
that day"; an expiration notification; and "PingFederate Has Stopped Processing Requests" when "The
current license and the grace period, if any, had lapsed"; the grace period's length is not stated
([licensing events, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_licensing_events.html)).
A replacement is imported at System > Server > License and "saved in the
`<pf_install>/pingfederate/server/default/conf/pingfederate.lic` file"; an invalid one is refused and the
existing one kept ([install a replacement licence key, 13.1](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_install_replacement_license_key.html)).
For a cell that outlives a sitting: a task that stays up past its licence stops serving after the
grace period; a task replaced (any new deployment, or `aws ecs update-service --force-new-deployment`)
fetches again. Whether a cell left standing should run the provider at all between sittings is a cost
question (section 5) before it is a licence one.

### Parameter Store, read at start

- The ECS form: a container's `secrets` list, each `name` the environment variable and `valueFrom` "the
  full ARN of the parameter in the AWS Systems Manager Parameter Store", or its name when the parameter
  is in the task's Region ([task definition parameters -> secrets](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html)).
  "Sensitive data is injected into your container when the container is initially started. If the
  secret is subsequently updated or rotated, the container will not receive the updated value
  automatically. You must either launch a new task or if your task is part of a service you can update
  the service and use the **Force new deployment** option"; Fargate platform 1.3.0 or later for a whole
  secret ([SSM parameters through environment variables](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/secrets-envvar-ssm-paramstore.html)).
- Who reads it: "Your task definition must use a task execution role with the additional permissions
  for Systems Manager Parameter Store"; `ssm:GetParameters` "Required if you are referencing a Systems
  Manager Parameter Store parameter in a task definition", `kms:Decrypt` "Required only if your secret
  uses a customer managed key and not the default key"
  ([task execution IAM role](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_execution_IAM_role.html)).
  The cell's execution role grants no `ssm:` action today and its policy test refuses an unrecognised
  statement, so the provider gets its own execution role (one `ssm:GetParameters` on
  `/spa-platform/{cell}/pingfederate/*`, the log group, the repository) rather than a widened gateway
  one; the by-name teardown reader then names it.
- The parameters: `SecureString`, Standard tier, the AWS-managed key, as `infra/envs/poc/apps/main.tf`
  writes a client secret (`lifecycle { ignore_changes = [value] }` so an apply never puts an old value
  back). Three under the cell's prefix: the DevOps user, the DevOps key and `PING_IDENTITY_PASSWORD`
  (the administrator's password, used for the console and the admin API; the image's default is
  `2FederateM0re`). Their values come from the operator at the sitting, never from a file in the
  repository, and never into a log: the hook prints the user name truncated (`${PING_IDENTITY_DEVOPS_USER}...`)
  and `PING_DEBUG=true` would `cat` the licence file, so that flag stays off. The ring precedent (P2-2
  decision 17, `teardown.sh` step 3) shows how a parameter outside Terraform's state is deleted at
  teardown; parameters the platform root creates go with its destroy.
- The assertion key the map decided ("one RSA key per cell per issuer ... in Parameter Store beside the
  client secrets") is the key ticket's; it sits under the same prefix and is read by the gateway's task
  role, not the provider's.

## 4. Configuration at start

- **A server profile** is "a profile (set of files)" layered "on top of a product's base install";
  `SERVER_PROFILE_URL` takes "A valid git HTTPS URL (not ssh)", `SERVER_PROFILE_PATH` "The subdirectory
  in the git repo", `SERVER_PROFILE_BRANCH` an optional branch, `SERVER_PROFILE_UPDATE` (default
  `false`) "Whether to update the server profile on container restart", `SERVER_PROFILE_URL_REDACT`
  keeps the URL out of the container output; a private repository "use[s] the `username:token` format so
  the container can access the repository" ([profiles](https://developer.pingidentity.com/devops/how-to/profiles.html);
  image README). A token in the URL is a secret in an environment variable, printed unless redacted; a
  public repository (this one) needs none.
- **Or `/opt/in`:** "if a mounted `/opt/in` directory exists, our containers reference this directory at
  startup for any server profile structures"; for the cell this is a derived image `FROM
  pingidentity/pingfederate:<tag>` with the profile copied under `/opt/in`, pushed to the cell's
  registry by digest as the gateway image is, which also removes the git clone from the task's start and
  its egress.
- **The layout**, from `pingidentity/pingidentity-server-profiles` at `getting-started/pingfederate`:
  `env_vars`, `instance/bulk-config/data.json.subst`, `instance/server/default/data/pf.jwk`, `motd`,
  `README.md` ("These files are laid over a vanilla installation of pingfederate"). `data.json.subst` is
  the bulk-configuration export with `${...}` substitutions, carrying the whole configuration (server
  settings with `"baseUrl": "https://localhost:9031"`, `"virtualHostNames": []`,
  `"tokenEndpointBaseUrl": ""`, the OAuth clients, the access token managers, the policies); the image's
  PingFederate hooks include `85-import-configuration.sh` (name read from the repository tree; its
  content was not read). `env_vars` in the getting-started profile exports the admin password default,
  a PKCS#12 SSL server key pair with its password and the two system keys, all in clear text: the
  cell's profile (workstream 5's `fixtures/pingfederate/`) generates its own key pair and system keys,
  and the three secrets of section 3 arrive from Parameter Store, not from `env_vars`.
- **Does a change need a new task?** Yes, in the cell. The task has no volume (Fargate's storage is
  ephemeral; the cell's task definition mounts nothing), so what the admin console or the admin API on
  9999 changes live is gone when the task is replaced. The profile is the configuration of record: a
  change is a commit, then either a new image digest (the `/opt/in` shape) or a force-new-deployment
  (the git shape with `SERVER_PROFILE_UPDATE` left false, since the clone happens at start). The
  Terraform provider `pingidentity/pingfederate` (the map's note: v1.11.0) applies against the admin
  API and would need the 9999 forward of section 2; it is the live alternative for a sitting, not the
  record.
- **The image repository.** Fargate pulls Docker Hub images anonymously or through ECR. A pull-through
  cache for Docker Hub "requires authentication with AWS Secrets Manager secret" whose name "must use
  the `ecr-pullthroughcache/` prefix" ([pull through cache](https://docs.aws.amazon.com/AmazonECR/latest/userguide/pull-through-cache.html)),
  a resource type no reader sees today and a secret store the cell does not use. The repository
  pattern is a third entry in `storage.tf`'s `repositories` set (immutable tags, `force_delete`),
  pushed by the sitting's build step as `deploy.sh` pushes the gateway; `aws_ecr_repository = 2` in the
  root test becomes 3 under the gated input. ECR storage is "cents per GB"
  (`docs/plan/05-infrastructure-and-cost.md`, not re-priced here).

## 5. Cost and teardown

### Prices in the project's Region

Read from the public AWS price list for `us-east-2` (no credential; the files are the offer
documents the pricing API publishes):

| Item | Price, US East (Ohio) | Source |
| --- | --- | --- |
| Fargate, Linux/ARM | $0.03238 per vCPU-hour, $0.00356 per GB-hour | `AmazonECS` offer, version `20261019224317`, published 2026-10-19, usage types `USE2-Fargate-ARM-vCPU-Hours:perCPU`, `USE2-Fargate-ARM-GB-Hours` ([index](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonECS/current/us-east-2/index.json)) |
| Fargate, Linux/x86 | $0.04048 per vCPU-hour, $0.004445 per GB-hour | same document, `USE2-Fargate-vCPU-Hours:perCPU`, `USE2-Fargate-GB-Hours` |
| Application Load Balancer | $0.0225 per hour, $0.008 per LCU-hour | `AWSELB` offer, version `20261102102527`, `USE2-LoadBalancerUsage` / `USE2-LCUUsage`, operation `LoadBalancing:Application` ([index](https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AWSELB/current/us-east-2/index.json)) |
| Public IPv4 address | $0.005 per hour, "across all AWS commercial ... regions" | [VPC pricing](https://aws.amazon.com/vpc/pricing/) |
| NAT gateway (the `multi_task` shape) | $0.045 per hour, $0.045 per GB | same page, Ohio |

The Fargate pricing page gives the same ARM figures in its us-east-1 examples ($0.0000089944 per vCPU
per second and $0.0000009889 per GB per second), "Pricing is calculated per second with a 1-minute
minimum", and "20 GB of ephemeral storage is available for all Fargate Tasks and Pods by default"
([Fargate pricing](https://aws.amazon.com/fargate/pricing/)). The plan's cost model is "for
us-east-1" (`contracts/infra-outputs.schema.json` -> `region`); Ohio's list prices above are identical
for every line used here.

### The provider task

| Task size (ARM64) | Compute per hour | With a public IPv4 (Phase 1 shape) | 4-hour sitting | 8-hour sitting | 24 hours | 730 hours |
| --- | --- | --- | --- | --- | --- | --- |
| 0.5 vCPU, 4 GB | $0.0304 | $0.0354 | $0.14 | $0.28 | $0.85 | $25.86 |
| **1 vCPU, 4 GB** | **$0.0466** | **$0.0516** | **$0.21** | **$0.41** | **$1.24** | **$37.68** |
| 2 vCPU, 4 GB | $0.0790 | $0.0840 | $0.34 | $0.67 | $2.02 | $61.32 |
| 1 vCPU, 4 GB on x86 | $0.0583 | $0.0633 | $0.25 | $0.51 | $1.52 | $46.18 |

Arithmetic: 1 vCPU x $0.03238 + 4 GB x $0.00356 = $0.04662. The recommended row is 1 vCPU and 4 GB on
ARM64: Ping's 4 GB, the image's `arm64` manifest, and Graviton's qualification. Under `multi_task` the
task sits in a private subnet with no public address and its egress (the licence fetch, the git clone
or the ECR pull of about 0.6 GB, the logs) goes through the NAT at $0.045 per GB: a few cents a
sitting.

### The cell with the provider

Today's Phase 1 cell per hour: ALB $0.0225 + gateway task (0.25 vCPU, 1 GB, ARM) $0.0117 + one IPv4
$0.005 = about $0.039, plus LCU cents (the plan's "About $30" a month). With the provider task and its
address: about $0.091 per hour, about $0.36 for a four-hour sitting, about $0.73 for eight, about
$2.20 for a day left standing. On the G6 two-task shape (#347 Q10: about $0.10 per hour, $70.50 a
month) the provider adds its $0.047 without an address: about $0.15 per hour. The docs-only reading of
`docs/agents/aws-rules.md` gives no number for the spend limit ("The user may have a spend limit if
they are on the paid plan"); it is read in AWS Settings, which this ticket does not open. Neither
figure is near the plan's own monthly total; the one that matters is the teardown at the sitting's end,
which the map's "deployed sitting" already assumes.

### What the teardown removes

- Everything the platform root plans under the gated input is in its state and goes with
  `terraform destroy`: the ECS service (draining the target group, 30 s), the task definition's active
  revision, the target group and the listener rule, the log group, the two roles and their inline
  policies, the ECR repository (`force_delete`), the SSM parameters (unless `ignore_changes` on the
  value is paired with a lifecycle that keeps them; the apps root's pattern deletes with the root), and
  the Route 53 zone and records of shape A. All of them are tagged through `default_tags`
  (`contracts/resource-tags.md`); whether the tagging API's `GetResources` lists a listener rule and a
  private hosted zone was not verified on a page here, and `verify-teardown.sh`'s tag reader counts
  whatever it lists, so the first teardown of the shape is the test.
- Inactive task definitions of the new family are deleted by `teardown.sh`'s prefix query
  (`--family-prefix spa-platform-<cell>`), as the gateway's are.
- New roles: the by-name reader and its companion test must name the provider's role suffixes
  (`CELL_ROLE_SUFFIXES`, held to `iam.tf`).
- New types: `tests/module.tftest.hcl`'s allowlist and `root.tftest.hcl`'s count map name
  `aws_lb_listener_rule`, `aws_route53_zone`, `aws_route53_record` and `aws_ssm_parameter` with their
  gated counts, "in the same change", as the file requires.
- Not chosen, for the reader's sake: ECS service discovery ("The AWS Cloud Map resources created when
  service discovery is used must be cleaned up manually") and a pull-through cache (a Secrets Manager
  secret and a service-linked role, neither a type any reader names).
- What a sitting's teardown leaves: nothing of the provider's; the DevOps user and key stay with the
  operator, as the Cognito test users' passwords stay in their parameters today, and the licence is
  re-fetched next time.

## 6. What this leaves for the spec and the next tickets

- The change request on P-01's gated reading and ADR-0010's amendment (integrator), with the counts
  moved in both test files (workstream 1): the same change.
- The gateway image's trust of the local authority, under every shape (integrator, `src/Gateway`).
- The issuer string, decided once the prototype shows what PingFederate's discovery document reports as
  `issuer` for a base URL carrying a port; shape A if it follows the base URL.
- Item 7's deployed path: shape A's zone gives `https://app1.spa-poc.test:8443/signout-backchannel-oidc`
  somewhere to land; PingFederate's trusted-CA store then needs the local authority.
- The licence's hosting clause and the per-fetch 30 days: the licence ticket; the grace period's length:
  the prototype, or Ping's EULA.
- A4 ("one issuer per environment") and the mock API's single issuer: whether the deployed sitting
  swaps issuers or the mock API learns two.

## 7. What was executed and what was read

**Executed, 2026-11-04, nothing against AWS or PingFederate:**

- `git fetch origin`; `git worktree add` on `origin/main` at `8ceeee3`; `gh issue view 948`, `gh issue view 944`,
  `gh issue view 348`; `gh issue edit 948 --add-assignee @me`.
- `gh api` reads of public repositories: `pingidentity/pingidentity-docker-builds` (tree; `runtime/common/staging/hooks/17-check-license.sh`;
  `pingfederate/opt/staging/hooks/04-check-variables.sh.pre`) and `pingidentity/pingidentity-server-profiles`
  (tree; `getting-started/pingfederate/{README.md,env_vars,instance/bulk-config/data.json.subst}`, the last
  grepped for `baseUrl`, `virtualHostNames`, `tokenEndpointBaseUrl`, `license`).
- `curl` of two public price-list documents to the session scratchpad and `python3 -I` to print the
  Ohio rows: `AmazonECS` (version 20261019224317) and `AWSELB` (version 20261102102527).
- `grep` and `cat` over the repository files named in Method.

**Read (fetched 2026-11-04 unless noted):**

- Repository, at `8ceeee3`: the files in Method; `infra/envs/poc/platform/terraform.tfvars` (untracked,
  for the live issuer shape only; no secret in it).
- AWS: [Fargate pricing](https://aws.amazon.com/fargate/pricing/) (examples are us-east-1; the page's
  tables did not render); [ELB pricing](https://aws.amazon.com/elasticloadbalancing/pricing/);
  [VPC pricing](https://aws.amazon.com/vpc/pricing/); the two price-list indexes above;
  [task definition parameters](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html)
  (task size table, `secrets`, `cpuArchitecture`, `extraHosts`, `ephemeralStorage`);
  [SSM parameters as environment variables](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/secrets-envvar-ssm-paramstore.html);
  [task execution IAM role](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_execution_IAM_role.html);
  [ARM64 task definitions](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-arm64.html);
  [service discovery](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-discovery.html);
  [ECR pull through cache](https://docs.aws.amazon.com/AmazonECR/latest/userguide/pull-through-cache.html);
  [ALB listeners](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-listeners.html),
  [listener rules](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/listener-rules.html),
  [condition types](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/rule-condition-types.html),
  [HostHeaderConditionConfig](https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_HostHeaderConditionConfig.html),
  [target groups](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-target-groups.html),
  [quotas](https://docs.aws.amazon.com/elasticloadbalancing/latest/application/load-balancer-limits.html);
  [Session Manager: start a session](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager-working-with-sessions-start.html).
  The first fetch of the sensitive-data page returned its title only and was not used; the
  `secrets-envvar-ssm-paramstore` page replaced it.
- Ping, PingFederate 13.1 / latest: [system requirements](https://docs.pingidentity.com/pingfederate/13.1/installing_and_uninstalling_pingfederate/pf_system_requirements.html);
  [run.properties reference](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_config_pf_propert.html)
  (first 100,000 characters);
  [system-services endpoints](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_sys_services_endpoints.html);
  [OpenID Provider configuration endpoint](https://docs.pingidentity.com/pingfederate/latest/developers_reference_guide/pf_openid_provider_config_endpoint.html);
  [Federation Info](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_protocolsettingstasklet_federationinfostate.html);
  [virtual host names](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_virtual_host_names.html);
  [virtual issuers](https://docs.pingidentity.com/pingfederate/latest/administrators_reference_guide/pf_adding_virtual_issuers_openid_connect.html);
  [Authorization Server Settings](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/help_authorizationserversettingstasklet_oauthauthorizationserversettingsstate.html);
  [licensing events](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_licensing_events.html);
  [install a replacement licence key](https://docs.pingidentity.com/pingfederate/13.1/administrators_reference_guide/pf_install_replacement_license_key.html).
- Ping DevOps: [prerequisites](https://developer.pingidentity.com/devops/get-started/prereqs.html);
  [registration](https://developer.pingidentity.com/devops/how-to/devopsRegistration.html);
  [DevOps user and key](https://developer.pingidentity.com/devops/how-to/devopsUserKey.html);
  [existing licence](https://developer.pingidentity.com/devops/how-to/existingLicense.html);
  [profiles](https://developer.pingidentity.com/devops/how-to/profiles.html);
  [PingFederate image README](https://developer.pingidentity.com/devops/docker-images/pingfederate/README.html)
  (in two reads; the variables table and the run example). `devops.pingidentity.com` URLs redirect to
  these. [Docker Hub tags API](https://hub.docker.com/v2/repositories/pingidentity/pingfederate/tags?page_size=25&ordering=last_updated).
- Not read, and so not claimed: Ping's EULA; the `85-import-configuration.sh` hook's body; the second
  half of the run.properties page (`pf.runtime.bind.address`); any page on the discovery `issuer`'s
  derivation; the tagging API's resource-type coverage for listener rules and hosted zones.
