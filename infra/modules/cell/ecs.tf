# The cell's compute: one ECS cluster, one ARM64 Fargate task, one service and one log group. This
# is the half of the cell's compute that can be decided and checked without AWS.
#
# What this file deliberately does not claim is a steady service on a healthy target. That needs
# credentials, an applied state bucket and a deployment, and a green plan is reviewed Terraform, not
# a running cell.
#
# The service starts on the public sample image and the health check follows the image: / for the
# sample and /readyz for the gateway (health_check_path). A gateway started against an empty bucket
# never becomes ready, which is why deploy.sh refuses to run before seed.sh.

locals {
  # contracts/infra-outputs.example.json fixes these three names: ecsCluster is spa-{cell},
  # ecsService is gateway, and logGroup is /spa-platform/{cell}/gateway. The outputs document is
  # assembled from them and the deployed tests read a run's log from the last.
  cluster_name   = "spa-${var.cell}"
  service_name   = "gateway"
  log_group_name = "/spa-platform/${var.cell}/gateway"

  gateway_container_name = "gateway"

  # The task volume the gateway mounts at /tmp (#787), named once for the volume and its mount point.
  gateway_tmp_volume_name = "gateway-tmp"

  # The bastion (#1167). The service is named inside the cluster, so "bastion" is this cell's alone;
  # the contract's ecsBastionService carries it. The family and the role (iam.tf) are account-wide,
  # so each carries an underscore, which the cell's own rule (variables.tf) never admits: no other
  # cell's names, all of them spa-platform-<cell> and its hyphenated suffixes, can equal them. A
  # hyphenated spa-platform-poc-bastion would be the gateway family of a cell named poc-bastion, and
  # teardown.sh, which deletes a family's inactive revisions by its exact name, would delete that
  # cell's. tools/scripts/cell-common.sh names the container and teardown.sh the family.
  bastion_service_name   = "bastion"
  bastion_container_name = "bastion"
  bastion_family         = "${local.name_prefix}_bastion"

  # Where the gateway's tasks run, and so where the bastion's run too: the same subnets and the same
  # public address or none, under either value of multi_task (#1167).
  task_subnet_ids       = [for subnet in(var.multi_task ? aws_subnet.private : aws_subnet.public) : subnet.id]
  task_assign_public_ip = !var.multi_task
}

resource "aws_ecs_cluster" "cell" {
  name = local.cluster_name

  # Container Insights is off on purpose. It is billed per metric and the cell's cost model has no
  # line for it, so a cluster that silently inherited an account-level opt-in would make the $30 a
  # month wrong.
  setting {
    name  = "containerInsights"
    value = "disabled"
  }

  tags = { Name = local.cluster_name }
}

resource "aws_cloudwatch_log_group" "gateway" {
  name = local.log_group_name

  # Explicit, because the alternative is not a default but "never expires": a retention is set so
  # that logs neither vanish nor accumulate without a decision.
  retention_in_days = var.log_retention_days

  tags = { Name = local.log_group_name }
}

# --- The task environment ---------------------------------------------------------------------
#
# contracts/platform-config.schema.json describes the configuration in its JSON form and names the
# deployed form: "environment variables, where a colon becomes a double underscore
# (Policy:AllowedUpstreams:0 is Policy__AllowedUpstreams__0) and every value arrives as a string
# that the binder converts". Hence tostring() on every number below; a JSON number in a container
# definition is refused by ECS, and a number that did get through would not be the string the
# binder expects.
#
# Nothing here is typed twice. The bucket is this module's bucket, the cell and the address range
# are this module's variables, and the rest are inputs the root supplies.

locals {
  # The subnets the internal load balancer sits in (alb.tf), in the order the subnet map's keys sort
  # in, so the indexed keys below are stable between plans.
  alb_subnet_cidrs = [for subnet in aws_subnet.public : subnet.cidr_block]

  # One entry per issuer, flattened rather than merged so an empty map stays an empty map. Each
  # issuer's three settings are the ones the contract requires of an issuer.
  issuer_environment = {
    for pair in flatten([
      for name, issuer in var.issuers : [
        { key = "Issuers__${name}__Authority", value = issuer.authority },
        { key = "Issuers__${name}__Profile", value = issuer.profile },
        { key = "Issuers__${name}__BrowserOrigin", value = issuer.browser_origin },
      ]
    ]) : pair.key => pair.value
  }

  platform_configuration = merge(
    {
      # The cell name, which is also the data-protection application name (ADR-0012).
      "Platform__Cell" = var.cell

      # This module's own bucket, both times: Phase 1 serves the registry and every release from
      # the one private bucket (contracts/s3-layout.md, contracts/platform-config.schema.json ->
      # Assets.Bucket, "One bucket serves both in Phase 1").
      "Registry__Bucket"      = aws_s3_bucket.platform.bucket
      "Registry__Key"         = var.registry_key
      "Registry__PollSeconds" = tostring(var.registry_poll_seconds)
      "Assets__Bucket"        = aws_s3_bucket.platform.bucket

      "Session__Store"       = var.session_store
      "Session__MaxSessions" = tostring(var.session_max_sessions)
    },
    # The two bounds a registry entry cannot widen: which hosts an entry may claim and which
    # upstreams it may name.
    { for index, suffix in var.allowed_host_suffixes : "Policy__AllowedHostSuffixes__${index}" => suffix },
    { for index, upstream in var.allowed_upstreams : "Policy__AllowedUpstreams__${index}" => upstream },
    # Fragments (#961). The schema marks both optional, and the gateway supplies its own placeholder
    # for an absent one (contracts/platform-config.schema.json -> Policy.FragmentsPerShell,
    # Policy.MergeBudget), so each is present only behind its input and the default inputs add no
    # key: a Phase 1 task definition is unchanged.
    var.fragments_per_shell != null ? { "Policy__FragmentsPerShell" = tostring(var.fragments_per_shell) } : {},
    var.merge_budget != null ? { "Policy__MergeBudget" = tostring(var.merge_budget) } : {},
    # UsePlatformForwardedHeaders trusts X-Forwarded-Proto and X-Forwarded-For from these addresses
    # and from nowhere else (contracts/pipeline.md, UsePlatformForwardedHeaders; N-09). The contract
    # names the value itself -- "The ALB subnets, in CIDR form"
    # (contracts/platform-config.schema.json -> ForwardedHeaders.TrustedNetworks) -- so it is the
    # subnets the load balancer sits in, which is where its addresses come from (alb.tf). The cell's
    # whole VPC range would also trust every address outside those two: the unallocated ones and,
    # under multi_task, the private subnets the tasks sit in.
    { for index, cidr in local.alb_subnet_cidrs : "ForwardedHeaders__TrustedNetworks__${index}" => cidr },
    local.issuer_environment,
    # Continuity (#338). Each is present only behind its input, so the default inputs add no key and
    # a Phase 1 task definition is unchanged. Session:TableName is change request P2-1's key, and the
    # DataProtection section is P2-2's; with no prefix the gateway registers no ring (P2-2 decision 5)
    # and the key id is absent rather than empty, which the package would read as no key.
    local.session_store_is_dynamodb ? { "Session__TableName" = local.session_table_name } : {},
    local.key_ring_enabled ? { "DataProtection__ParameterPrefix" = var.data_protection_parameter_prefix } : {},
    var.data_protection_kms_key_id != "" ? { "DataProtection__KmsKeyId" = var.data_protection_kms_key_id } : {},
  )

  # Not a platform setting, and deliberately not merged into platform_configuration: the
  # configuration contract's own $comment says the task environment "also carries settings that are
  # not the platform's (Logging, ASPNETCORE_*, OTEL_*)", so this belongs in the environment but not
  # in what the contract describes.
  #
  # It is here because var.gateway_port is one value in three places -- the target group sends to it
  # (alb.tf), the container publishes it, and the process has to listen on it -- and the third did
  # not follow the variable. src/Gateway/Dockerfile sets ASPNETCORE_HTTP_PORTS=8080, so at any other
  # value of the variable the load balancer would send to a port nothing answered on and the target
  # could never turn healthy. Setting it here makes the variable's own description true.
  gateway_runtime_environment = {
    "ASPNETCORE_HTTP_PORTS" = tostring(var.gateway_port)
  }

  gateway_environment = merge(local.platform_configuration, local.gateway_runtime_environment)

  # One value for the gateway's and the bastion's containers (#786), so the two cannot drift; the
  # comment at the gateway container's use says why each setting is there.
  #
  # add = [] is ECS's own value, spelled out (#1006). ECS returns capabilities.add as an empty list
  # when none was given, and the provider's comparison of the configured definitions with the ones
  # read back does not treat the two as equal: its reduce() empties to null the top-level lists
  # (mountPoints, systemControls, volumesFrom and others) and fills hostPort from containerPort under
  # awsvpc, but leaves the lists inside linuxParameters as they are. Without it every plan after an
  # apply replaced the task definition and rolled the service.
  # https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/internal/service/ecs/container_definitions.go
  container_linux_parameters = {
    capabilities = {
      add  = []
      drop = ["ALL"]
    }
    initProcessEnabled = true
  }

  gateway_container = {
    name      = local.gateway_container_name
    image     = var.gateway_image
    essential = true

    # Read-only (#787). The writable root was ECS Exec's condition while this task was the
    # tunnel's hop; the hop is the bastion's task now (#1167), Exec is off on this service and no
    # session agent writes here. The one writable path is /tmp, the task volume below (#780, answer
    # 3: "the safe contract and not a measured one"). Explicit rather than left to the default,
    # which is writable.
    readonlyRootFilesystem = true

    # hostPort is the value ECS fills in under awsvpc, where it must equal containerPort, spelled out
    # with the three empty lists below so the definition is the one ECS reads back (#1006). ECS's
    # parameter reference: "The hostPort can either be kept blank or be the same value as
    # containerPort", and "for containers in a task with the awsvpc network mode, the hostPort is set
    # to the same value as the containerPort".
    # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html#container_definition_portmappings
    portMappings = [{
      containerPort = var.gateway_port
      hostPort      = var.gateway_port
      protocol      = "tcp"
    }]

    # The task's one volume at /tmp, writable (#787; the task definition's volume block below). readOnly
    # is spelled out as the false ECS reads back, as #1006 spells out the other values ECS fills in;
    # the provider's comparison does not fill it in. The gateway's user, $APP_UID, owns nothing in it:
    # ECS gives a task volume "0755 and the owner as root" unless the image says otherwise
    # (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/bind-mounts.html), and
    # src/Gateway/Dockerfile does not. That the process can write there is not shown by any test
    # here; it is seen only on a running task (#787, "Not shown by this ticket").
    mountPoints = [{
      sourceVolume  = local.gateway_tmp_volume_name
      containerPath = "/tmp"
      readOnly      = false
    }]

    # ECS's values when none is given (#1006): no other container's volumes and no namespaced kernel
    # parameters.
    systemControls = []
    volumesFrom    = []

    # Hardening (#786). capabilities.drop removes Linux capabilities from the default set
    # Docker grants (--cap-drop); "ALL" is one of its valid values, and on Fargate add accepts
    # SYS_PTRACE alone, so nothing here could be added back. The process needs none: it runs as the
    # image's non-root user (USER $APP_UID in src/Gateway/Dockerfile) and binds var.gateway_port,
    # 8080 by default, above 1024, so it needs no NET_BIND_SERVICE either. initProcessEnabled runs
    # "an init process inside the container that forwards signals and reaps processes" (--init), so
    # dotnet is no longer PID 1: a zombie is reaped, and SIGTERM still reaches the process (see
    # stopTimeout below). ECS Exec also recommends the init process, to clean up the SSM agent's
    # orphaned and zombie child processes; Exec is off here since #787, and the bastion, where it is
    # on, shares this value.
    # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html
    # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html
    linuxParameters = local.container_linux_parameters

    # Continuity (#347, #332 F1). The seconds between SIGTERM and SIGKILL. A roll stops a task in this
    # order: the target group's deregistration delay (alb.tf, 30 s) drains it first, then ECS sends
    # SIGTERM, then after stopTimeout SIGKILL. The gateway's own ShutdownTimeout, the time the process
    # gives its in-flight work after SIGTERM, is 25 s, a Platform.Hosting constant (#363) this module
    # names and does not read. The relations F1 rests on: ShutdownTimeout (25) exceeds the refresh
    # holder's 20 s durable-write deadline, timed from its acquire's send (the 21 s lease less 1 s of
    # skew), so a successful refresh admitted before the host is told to stop writes its rotated
    # token before the host stops; and stopTimeout (30) is at least ShutdownTimeout, so the process
    # ends its shutdown on its own terms and is never killed through it. The durable write is the
    # promise, not every request's completion: F3b's failed or unknown write and F4's check-to-send
    # pause are its exceptions, and a loser's wait is F2's and F3's, which no number here bounds
    # (#332 F1; P2-3's second amendment, decision 2). The drain (alb.tf) promises no refresh's
    # completion. On the gateway alone: an extra container's stopTimeout is its caller's to write
    # (variables.tf -> extra_containers), and none of these relations is its. 30 is also
    # Fargate's default, written down because F1 depends on it. The init process (#786, above)
    # leaves these relations as they were: ECS sends SIGTERM to the container's PID 1, now the init,
    # which "forwards signals" to its child, and stopTimeout is "time duration (in seconds) to wait
    # before the container is forcefully killed if it doesn't exit normally on its own", timed from
    # the same SIGTERM. The child is dotnet itself because the Dockerfile's ENTRYPOINT is exec form
    # (["dotnet", "Gateway.dll"]); a shell-form entrypoint would put a shell between them, which
    # forwards nothing, and the process would see SIGKILL alone. Both quotes are from the page below.
    # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html
    stopTimeout = 30

    environment = [
      for key in sort(keys(local.gateway_environment)) : {
        name  = key
        value = local.gateway_environment[key]
      }
    ]

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.gateway.name
        "awslogs-region"        = data.aws_region.current.region
        "awslogs-stream-prefix" = local.gateway_container_name
      }
    }
  }

  # The gateway's first, then each extra container exactly as the root wrote it (variables.tf ->
  # extra_containers). concat rather than a list literal, so each container keeps its own shape: an
  # extra container need publish no port, and the gateway does. An extra container shares the task's
  # network namespace, so the gateway reaches a listener in it on loopback, which is what an upstream
  # such as http://127.0.0.1:5080 names (contracts/platform-config.example.json -> AllowedUpstreams).
  containers = concat([local.gateway_container], var.extra_containers)
}

resource "aws_ecs_task_definition" "gateway" {
  family = local.name_prefix

  # 0.25 vCPU and 1 GB on ARM64. ARM Fargate is about 20% below x86, which is the $8.51 a month the
  # cell's cost model prices the task at; the gateway's image is built for it
  # (src/Gateway/Dockerfile), and an extra container's image has to be too.
  cpu    = "256"
  memory = "1024"

  requires_compatibilities = ["FARGATE"]

  # Fargate requires awsvpc, and it is why the target group registers by address rather than by
  # instance (alb.tf) and why the task's containers reach each other over loopback.
  network_mode = "awsvpc"

  runtime_platform {
    cpu_architecture        = "ARM64"
    operating_system_family = "LINUX"
  }

  # Two roles, never one. The agent pulls the image and creates the log stream with the execution
  # role; the process reaches S3 and Parameter Store with the task role. Keeping them apart is what
  # makes the task role reviewable on its own (iam.tf).
  execution_role_arn = aws_iam_role.task_execution.arn
  task_role_arn      = aws_iam_role.task.arn

  container_definitions = jsonencode(local.containers)

  # The gateway's /tmp (#787, #780 answer 3). A volume with a name and no other setting is a bind
  # mount on the task's own ephemeral storage, which Fargate provides and removes with the task: no
  # host path, no Docker volume, no EFS or FSx share, and nothing outlives the task.
  # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/bind-mounts.html
  volume {
    name = local.gateway_tmp_volume_name
  }

  tags = { Name = local.name_prefix }
}

resource "aws_ecs_service" "gateway" {
  name            = local.service_name
  cluster         = aws_ecs_cluster.cell.id
  task_definition = aws_ecs_task_definition.gateway.arn
  desired_count   = var.multi_task ? 2 : 1

  # Two tasks over two private subnets in two zones (#347 Q14). Fargate takes no placement strategy
  # and spreads tasks across zones on a best effort; rebalancing starts a task in the zone with
  # fewest and stops one in the zone with most when the spread goes uneven. Set explicitly because
  # an update leaves a service that never had a value DISABLED. #348 shows one task per zone on
  # the deployed cell.
  # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-placement.html#fargate-launch-type
  # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-rebalancing.html
  availability_zone_rebalancing = var.multi_task ? "ENABLED" : null

  # Fargate by launch type rather than by capacity provider: the cell has no EC2 capacity to fall
  # back to, so a capacity-provider strategy would be a second way of saying the same thing.
  launch_type      = "FARGATE"
  platform_version = "LATEST"

  # Off (#787). The tunnel's hop is the bastion's task (#1167; aws_ecs_service.bastion below), and
  # tools/scripts/tunnel.sh forwards through no task of this service. With Exec off, the task role
  # holds no ssmmessages permission (iam.tf) and both roots are read-only: nothing in the shipping
  # task is reachable for an incident, which uses the logs, /readyz and the bastion's network path
  # (#780, answer 2).
  enable_execute_command = false

  # Continuity (#347). One task always serves during a roll: the replacement is started, and healthy
  # in the target group, before a task is deregistered and drained. These are ECS's defaults for a
  # replica service, written down because F1 rests on them. ECS keeps desiredCount x
  # minimumHealthyPercent/100 tasks running, rounded up, and starts up to desiredCount x
  # maximumPercent/100, rounded down
  # (https://docs.aws.amazon.com/AmazonECS/latest/APIReference/API_DeploymentConfiguration.html). So
  # 100 keeps every task serving at any count; 50 would keep the one task of a one-task cell, by the
  # rounding, but would let a two-task cell stop one of its two before its replacement is healthy.
  deployment_minimum_healthy_percent = 100
  deployment_maximum_percent         = 200

  # A bad image rolls back rather than retrying forever. rollback is the half that matters: the
  # circuit breaker is a rollback lever beside rollback.sh only if it restores the previous task
  # definition.
  deployment_circuit_breaker {
    enable   = true
    rollback = true
  }

  # The gateway answers /readyz 200 only once a registry snapshot is live (contracts/pipeline.md).
  # RegistryPoller reads the registry from S3 once at start-up and only then waits a poll interval,
  # so readiness follows that first read. The grace period keeps the scheduler from acting on an
  # unhealthy target for 60 s after the task starts: time for process start and that first read.
  # The target group (alb.tf) sets no interval or thresholds, so the provider's defaults hold:
  # interval 30 s, healthy and unhealthy thresholds 3. A new target is healthy on one pass and
  # unhealthy after three failures, (3 - 1) x 30 s after its first check (#171's reading: the guide
  # makes no exception for a target still in its initial checks, but does not say so outright), so
  # /readyz must answer 200 to the third check, first check + 60 s: that is the real bound, and a
  # slow image pull eats into it if the first check comes before the container starts (#171).
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/application/target-group-health-checks.html
  # https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/lb_target_group.html.markdown
  health_check_grace_period_seconds = 60

  network_configuration {
    subnets         = local.task_subnet_ids
    security_groups = [aws_security_group.task.id]

    # Phase 1's cell has no NAT gateway, so this public address is the task's only route out, to
    # Cognito, S3, Parameter Store, ECR and logs (network.tf). Nothing reaches it: the task security
    # group accepts the gateway's port from the ALB security group alone (test P-06). Under
    # multi_task the tasks sit in the private subnets with no address, and go out through the NAT
    # gateway and the two gateway endpoints (network.tf); P-06's second sentence then reads as "the
    # task has no public address" (#583).
    assign_public_ip = local.task_assign_public_ip
  }

  load_balancer {
    target_group_arn = aws_lb_target_group.gateway.arn
    container_name   = local.gateway_container_name
    container_port   = var.gateway_port
  }

  # The target group has to be attached to the load balancer before the service registers a task in
  # it, which is what outputs.tf's https_listener_arn was published for.
  depends_on = [aws_lb_listener.https]

  tags = { Name = "${local.name_prefix}-${local.service_name}" }
}

# --- The bastion: the tunnel's hop (#1167, #1168) -------------------------------------------------
#
# A second service in the cell's cluster behind the bastion input, off by default, which is Phase
# 1's: four declarations, each with exactly count = var.bastion ? 1 : 0, this file's task definition
# and service and iam.tf's role and its policy. Nothing else is declared for it: no log group, no
# security group and no rule. P-01 in the platform root's tests admits the service in that one form
# and holds the default inputs at one ECS service (#1167, item 2).
#
# tools/scripts/tunnel.sh lists this service's running tasks and starts its Session Manager port
# forward against the bastion container. The container runs the sample image, which carried that
# port forward as an ECS Exec target twice as the gateway service's task, on 2026-10-21 and
# 2026-10-24 (#57). What differs here: the task role holds the four ssmmessages permissions alone,
# the service has no load balancer and no health check grace period, the task has one container
# and no execution role, and the container drops every capability and runs an init process, as the
# gateway's have since #786, which both #57 runs predate.

locals {
  # Writable root: ECS Exec does not support a read-only one, "making the root file system read-only
  # using the readonlyRootFilesystem task definition parameter, or any other method, isn't supported"
  # (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html). The read-only
  # root is the shipping task definition's, set there by #787, not the bastion's (#1167).
  #
  # No portMappings: nothing connects to the container, which only carries the session; the image's
  # own listener on 8080 is in no target group. No logConfiguration, and so no execution role: the
  # image is pulled from a public registry with no credentials, and nothing here writes a log
  # stream. AWS lists a Fargate task's uses for one as an ECR private pull, the awslogs driver,
  # private registry credentials, Runtime Monitoring and secrets, and a public image is none of them
  # (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_execution_IAM_role.html,
  # read on 2026-11-18). The capabilities and the init process are the gateway's one value (#786), with ECS's
  # filled-in values spelled out as the gateway's are (#1006), so a re-plan after an apply replaces
  # nothing and never rolls the bastion under a live tunnel.
  bastion_container = {
    name      = local.bastion_container_name
    image     = var.bastion_image
    essential = true

    readonlyRootFilesystem = false
    linuxParameters        = local.container_linux_parameters

    mountPoints    = []
    systemControls = []
    volumesFrom    = []
  }
}

resource "aws_ecs_task_definition" "bastion" {
  count = var.bastion ? 1 : 0

  family = local.bastion_family

  # Fargate's smallest size for ARM64, 0.25 vCPU and 0.5 GB: the container holds a session agent
  # and an idle nginx. ARM64 like the gateway's task, and the sample image is built for it.
  cpu    = "256"
  memory = "512"

  requires_compatibilities = ["FARGATE"]
  network_mode             = "awsvpc"

  runtime_platform {
    cpu_architecture        = "ARM64"
    operating_system_family = "LINUX"
  }

  # Its own role, never the gateway's (iam.tf): the bastion reads no bucket and no secret.
  task_role_arn = aws_iam_role.bastion_task[0].arn

  container_definitions = jsonencode([local.bastion_container])

  tags = { Name = local.bastion_family }
}

resource "aws_ecs_service" "bastion" {
  count = var.bastion ? 1 : 0

  name            = local.bastion_service_name
  cluster         = aws_ecs_cluster.cell.id
  task_definition = aws_ecs_task_definition.bastion[0].arn

  # One task while the input is on and applied. Nothing starts or stops it but an apply (#1167,
  # sub-question 2.2).
  desired_count = 1

  launch_type      = "FARGATE"
  platform_version = "LATEST"

  # The session's way in: tools/scripts/tunnel.sh's port forward is an ECS Exec session on this
  # service's task.
  enable_execute_command = true

  # The gateway's network under either value of multi_task: its subnets, the task security group and
  # its public address or none. In that group no rule changes, so the load balancer's group still
  # admits 443 from the task group alone (network.tf, tests/network.tftest.hcl), and that is the
  # bastion's path to the load balancer. No load_balancer block: the bastion is in no target group.
  network_configuration {
    subnets          = local.task_subnet_ids
    security_groups  = [aws_security_group.task.id]
    assign_public_ip = local.task_assign_public_ip
  }

  tags = { Name = "${local.name_prefix}-${local.bastion_service_name}" }
}
