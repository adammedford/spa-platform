# Task 1.4a. The cell's compute: one ECS cluster, one ARM64 Fargate task, one service and one log
# group. This is the half of plan row 1.4 that can be decided and checked without AWS.
#
# What this file deliberately does not claim is the row's own done-when: a steady service on a
# healthy target. That needs credentials, an applied state bucket and the first deployment order in
# docs/plan/05-infrastructure-and-cost.md, and it stays with row 1.4. A green plan is reviewed
# Terraform, not a running cell.
#
# The service starts on the public sample image and the health check follows the image: / for the
# sample and /readyz for the gateway (task 1.3's health_check_path). A gateway started against an
# empty bucket never becomes ready, which is why deploy.sh refuses to run before seed.sh.

locals {
  # contracts/infra-outputs.example.json fixes these three names: ecsCluster is spa-{cell},
  # ecsService is gateway, and logGroup is /spa-platform/{cell}/gateway. Task 1.7 assembles
  # outputs.json from them and the deployed tests read a run's log from the last.
  cluster_name   = "spa-${var.cell}"
  service_name   = "gateway"
  log_group_name = "/spa-platform/${var.cell}/gateway"

  gateway_container_name  = "gateway"
  mock_api_container_name = "mock-api"
}

resource "aws_ecs_cluster" "cell" {
  name = local.cluster_name

  # Container Insights is off on purpose. It is billed per metric and the cost model in
  # docs/plan/05-infrastructure-and-cost.md has no line for it, so a cluster that silently
  # inherited an account-level opt-in would make the $30 a month wrong.
  setting {
    name  = "containerInsights"
    value = "disabled"
  }

  tags = { Name = local.cluster_name }
}

resource "aws_cloudwatch_log_group" "gateway" {
  name = local.log_group_name

  # Explicit, because the alternative is not a default but "never expires": plan row 1.4 asks for a
  # retention so that logs neither vanish nor accumulate without a decision.
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
    # UsePlatformForwardedHeaders trusts X-Forwarded-Proto and X-Forwarded-For from these addresses
    # and from nowhere else (contracts/pipeline.md, UsePlatformForwardedHeaders; N-09). The contract
    # names the value itself -- "The ALB subnets, in CIDR form"
    # (contracts/platform-config.schema.json -> ForwardedHeaders.TrustedNetworks) -- so it is the
    # subnets the load balancer sits in, which is where its addresses come from (alb.tf). The cell's
    # whole VPC range would also trust every unallocated address in it, and only two subnets out of
    # the range are allocated.
    { for index, cidr in local.alb_subnet_cidrs : "ForwardedHeaders__TrustedNetworks__${index}" => cidr },
    local.issuer_environment,
  )

  # Assumption A4 is one issuer per environment, and the mock API validates against one issuer
  # (fixtures/MockApi/Program.cs). values() on a map is ordered by key, so with the single issuer
  # the task definition's precondition requires this is deterministic.
  mock_api_issuer_authority = length(var.issuers) > 0 ? values(var.issuers)[0].authority : ""

  mock_api_configuration = {
    "MockApi__Issuer"     = local.mock_api_issuer_authority
    "MockApi__App1Client" = var.mock_api_app1_client_id
    "MockApi__App2Client" = var.mock_api_app2_client_id
  }

  # Not a platform setting, and deliberately not merged into platform_configuration: the
  # configuration contract's own $comment says the task environment "also carries settings that are
  # not the platform's (Logging, ASPNETCORE_*, OTEL_*)", so this belongs in the environment but not
  # in what the contract describes.
  #
  # It is here because var.gateway_port is one value in three places -- the target group sends to it
  # (task 1.3), the container publishes it, and the process has to listen on it -- and the third did
  # not follow the variable. src/Gateway/Dockerfile sets ASPNETCORE_HTTP_PORTS=8080, so at any other
  # value of the variable the load balancer would send to a port nothing answered on and the target
  # could never turn healthy. Setting it here makes the variable's own description true.
  gateway_runtime_environment = {
    "ASPNETCORE_HTTP_PORTS" = tostring(var.gateway_port)
  }

  gateway_environment = merge(local.platform_configuration, local.gateway_runtime_environment)

  gateway_container = {
    name      = local.gateway_container_name
    image     = var.gateway_image
    essential = true

    # Writable, because that is the third condition the Session Manager route comes with, beside
    # Exec on the service and the four ssmmessages permissions on the task role: "Its root
    # filesystem must be writable" (docs/plan/05-infrastructure-and-cost.md -> Workstation access).
    # Explicit rather than left to the default, because task 1.6b's tunnel depends on it.
    readonlyRootFilesystem = false

    portMappings = [{
      containerPort = var.gateway_port
      protocol      = "tcp"
    }]

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

  # The three keys fixtures/MockApi/Dockerfile declares and fixtures/MockApi/Program.cs refuses to
  # start without. The client ids arrive as inputs: the apps root decides them, and nothing about a
  # client id is written into this module, which is what keeps onboarding a third app out of the
  # platform root's plan (docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries, F-07).
  mock_api_container = {
    name      = local.mock_api_container_name
    image     = var.mock_api_image
    essential = true

    # The same reason as the gateway's: one task, one Session Manager agent, one writable root.
    readonlyRootFilesystem = false

    # No portMappings. The two listeners are on loopback ports 5080 and 5081 and are reached from
    # the gateway container inside the task's own network namespace, never from outside it
    # (fixtures/MockApi/Dockerfile, contracts/platform-config.example.json -> AllowedUpstreams).
    environment = [
      for key in sort(keys(local.mock_api_configuration)) : {
        name  = key
        value = local.mock_api_configuration[key]
      }
    ]

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = aws_cloudwatch_log_group.gateway.name
        "awslogs-region"        = data.aws_region.current.region
        "awslogs-stream-prefix" = local.mock_api_container_name
      }
    }
  }

  # concat rather than a conditional over two lists, so the two container shapes stay their own
  # shapes: the mock API publishes no port and the gateway does.
  containers = concat(
    [local.gateway_container],
    var.mock_api_image != "" ? [local.mock_api_container] : [],
  )
}

resource "aws_ecs_task_definition" "gateway" {
  family = local.name_prefix

  # 0.25 vCPU and 1 GB on ARM64 (plan row 1.4). ARM Fargate is about 20% below x86, which is the
  # $8.51 a month in docs/plan/05-infrastructure-and-cost.md; both images are built for it
  # (src/Gateway/Dockerfile, fixtures/MockApi/Dockerfile).
  cpu    = "256"
  memory = "1024"

  requires_compatibilities = ["FARGATE"]

  # Fargate requires awsvpc, and it is why the target group registers by address rather than by
  # instance (task 1.3) and why the two containers reach each other over loopback.
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

  tags = { Name = local.name_prefix }

  lifecycle {
    # The mock API validates against one issuer, and A4 is one issuer per environment. Without this
    # the module would silently pick the alphabetically first of several, and the listener would
    # refuse every token from the others.
    precondition {
      condition     = var.mock_api_image == "" || length(var.issuers) == 1
      error_message = "The mock API validates against one issuer (fixtures/MockApi/Program.cs, assumption A4). Supply one issuer, or leave mock_api_image empty."
    }
  }
}

resource "aws_ecs_service" "gateway" {
  name            = local.service_name
  cluster         = aws_ecs_cluster.cell.id
  task_definition = aws_ecs_task_definition.gateway.arn
  desired_count   = 1

  # Fargate by launch type rather than by capacity provider: the cell has no EC2 capacity to fall
  # back to, so a capacity-provider strategy would be a second way of saying the same thing.
  launch_type      = "FARGATE"
  platform_version = "LATEST"

  # The tunnel's hop. Workstation access goes through a Session Manager port forward with this task
  # in the middle and no bastion anywhere (docs/plan/05-infrastructure-and-cost.md), so without this
  # the internal load balancer is unreachable from a workstation and task 1.6b has nothing to
  # connect through.
  enable_execute_command = true

  # A bad image rolls back rather than retrying forever (plan row 1.4). rollback is the half that
  # matters: docs/plan/05-infrastructure-and-cost.md names the circuit breaker beside rollback.sh as
  # a rollback lever, which it only is if it restores the previous task definition.
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
  # unhealthy after three failures, (3 - 1) x 30 s after its first check, so /readyz must answer
  # the third check, first check + 60 s: that is the real bound, and a slow image pull eats into it
  # if the first check comes before the container starts (#171).
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/application/target-group-health-checks.html
  # https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/lb_target_group.html.markdown
  health_check_grace_period_seconds = 60

  network_configuration {
    subnets         = [for subnet in aws_subnet.public : subnet.id]
    security_groups = [aws_security_group.task.id]

    # The cell has no NAT gateway, so this public address is the task's only route out, to Cognito,
    # S3, Parameter Store, ECR and logs (plan row 1.2). Nothing reaches it: the task security group
    # accepts the gateway's port from the ALB security group alone (test P-06).
    assign_public_ip = true
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
