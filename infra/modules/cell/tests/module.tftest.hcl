# Task 1.4a. Checks the cell module against a mock AWS provider: no credentials, no AWS apply
# and no network beyond `terraform init`. Permission assertions use plan; the final attachment
# assertion uses only the mock provider's in-memory apply.
#   terraform -chdir=infra/modules/cell init -backend=false
#   terraform -chdir=infra/modules/cell test
#
# What this file proves is the configuration the module produces, which is everything about the
# cell's ECS resources that can be decided without AWS. What it does not prove is row 1.4's own
# done-when: a steady service, a healthy target, a successful image pull or a working tunnel. A
# green run here is reviewed Terraform, not a running cell. Those four need the apply, and the apply
# needs credentials, a state bucket and a human (plan row 1.4, ticket 05).
#
# Expected values are read from the files the module answers to, never copied here:
# contracts/platform-config.schema.json for the task environment, contracts/s3-layout.md for the
# bucket prefixes the task role may read, contracts/infra-outputs.{schema,example}.json for the
# outputs, and fixtures/MockApi/Dockerfile for the keys the mock API's process requires.
#
# Prior art for the seam: infra/modules/idp-cognito/tests/module.tftest.hcl.

mock_provider "aws" {
  mock_data "aws_availability_zones" {
    defaults = {
      names = ["us-east-1a", "us-east-1b", "us-east-1c"]
    }
  }

  mock_data "aws_acm_certificate" {
    defaults = {
      arn = "arn:aws:acm:us-east-1:111122223333:certificate/00000000-0000-0000-0000-000000000000"
    }
  }

  # A mock provider returns a generated value for `json`, not the rendered policy, so no assertion
  # below treats it as rendered permissions. Permission claims read the configured statement
  # blocks; the final wiring run uses distinct JSON sentinels to identify attached documents.
  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }
}

# Distinct JSON values make attachment wiring observable despite the mock provider. Only json
# is overridden: the statement blocks remain the actual configuration checked by the grant tests.
# Sid distinguishes valid policy documents without duplicating the permissions they test.
override_data {
  target          = data.aws_iam_policy_document.task
  override_during = plan
  values = {
    json = jsonencode({ Version = "2012-10-17", Statement = [{
      Sid = "task", Effect = "Deny", Action = "sts:AssumeRole", Resource = "*"
    }] })
  }
}

override_data {
  target          = data.aws_iam_policy_document.task_execution
  override_during = plan
  values = {
    json = jsonencode({ Version = "2012-10-17", Statement = [{
      Sid = "taskexecution", Effect = "Deny", Action = "sts:AssumeRole", Resource = "*"
    }] })
  }
}

override_data {
  target          = data.aws_iam_policy_document.ecs_tasks_trust
  override_during = plan
  values = {
    json = jsonencode({ Version = "2012-10-17", Statement = [{
      Sid = "ecstaskstrust", Effect = "Deny", Action = "sts:AssumeRole", Resource = "*"
    }] })
  }
}

# The three account facts the task role's ARNs are built from. `override_during = plan` is what
# makes them known during a plan rather than only after an apply, which is what keeps every run in
# this file a plan and still lets it assert a whole ARN.
override_data {
  target          = data.aws_partition.current
  override_during = plan
  values          = { partition = "aws", dns_suffix = "amazonaws.com" }
}

override_data {
  target          = data.aws_region.current
  override_during = plan
  values          = { region = "us-east-1" }
}

override_data {
  target          = data.aws_caller_identity.current
  override_during = plan
  values          = { account_id = "111122223333" }
}

# The bucket's ARN, which the task role's two prefixes are built from. Overridden rather than
# asserted around: the point of the assertions below is the prefix, and a generated ARN would make
# them unreadable.
override_resource {
  target          = aws_s3_bucket.platform
  override_during = plan
  values          = { arn = "arn:aws:s3:::spa-platform-poc" }
}

override_resource {
  target          = aws_lb_target_group.gateway
  override_during = plan
  values          = { arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:targetgroup/spa-poc/0123456789abcdef" }
}

override_resource {
  target          = aws_subnet.public["us-east-1a"]
  override_during = plan
  values          = { id = "subnet-0a" }
}

override_resource {
  target          = aws_subnet.public["us-east-1b"]
  override_during = plan
  values          = { id = "subnet-0b" }
}

override_resource {
  target          = aws_security_group.task
  override_during = plan
  values          = { id = "sg-task" }
}

# The log group and the two repositories, which the execution role's two grants are scoped to.
override_resource {
  target          = aws_cloudwatch_log_group.gateway
  override_during = plan
  values          = { arn = "arn:aws:logs:us-east-1:111122223333:log-group:/spa-platform/poc/gateway" }
}

override_resource {
  target          = aws_ecr_repository.repository["gateway"]
  override_during = plan
  values          = { arn = "arn:aws:ecr:us-east-1:111122223333:repository/spa-poc/gateway" }
}

override_resource {
  target          = aws_ecr_repository.repository["mock-api"]
  override_during = plan
  values          = { arn = "arn:aws:ecr:us-east-1:111122223333:repository/spa-poc/mock-api" }
}

# The two role ARNs, so the task definition can be checked to carry each in its own place rather
# than the same one twice.
override_resource {
  target          = aws_iam_role.task
  override_during = plan
  values          = { arn = "arn:aws:iam::111122223333:role/spa-platform-poc-task" }
}

override_resource {
  target          = aws_iam_role.task_execution
  override_during = plan
  values          = { arn = "arn:aws:iam::111122223333:role/spa-platform-poc-task-execution" }
}

variables {
  cell                    = "poc"
  vpc_cidr                = "10.20.0.0/16"
  certificate_domain      = "spa-poc.test"
  teardown_principal_arns = ["arn:aws:iam::111122223333:role/Admin"]

  # No tag anywhere in it: deploy.sh pushes by digest and asserts the running digest afterwards
  # (plan row 1.7), and the sample image the service starts on is passed the same way.
  gateway_image = "public.ecr.aws/nginx/nginx@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

  # Assumption A4: one issuer per environment. The value comes from the identity root's `issuer`
  # and `cognito_domain` outputs, which is why it is an input here and not a lookup.
  issuers = {
    "cognito-poc" = {
      authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
      profile        = "cognito"
      browser_origin = "https://spa-poc.auth.us-east-1.amazoncognito.com"
    }
  }

  allowed_host_suffixes = [".spa-poc.test:8443"]
  allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
}

# --- The resource set -------------------------------------------------------------------------

# Plan row 1.9 polices the applied plan. This reads the module's own source instead, so a resource
# type added here fails before an apply exists to police: the allowlist below has to be edited in
# the same change, which is where the author is made to say what the new type is for.
#
# It is also where "no NAT gateway, no bastion, no database" (plan row 1.2, test P-01) is held
# statically rather than only against a deployed account.
run "the_module_declares_only_the_cell_s_intended_resource_types" {
  command = plan

  assert {
    condition = toset([
      for m in regexall(
        "(?m)^resource \"([a-z0-9_]+)\"",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : m[0]
      ]) == toset([
      # Task 1.2, network.tf
      "aws_vpc",
      "aws_internet_gateway",
      "aws_subnet",
      "aws_route_table",
      "aws_route",
      "aws_route_table_association",
      "aws_security_group",
      "aws_vpc_security_group_ingress_rule",
      "aws_vpc_security_group_egress_rule",
      # Task 1.3, alb.tf
      "aws_lb",
      "aws_lb_target_group",
      "aws_lb_listener",
      # Task 1.5, storage.tf
      "aws_s3_bucket",
      "aws_s3_bucket_versioning",
      "aws_s3_bucket_public_access_block",
      "aws_s3_bucket_ownership_controls",
      "aws_s3_bucket_server_side_encryption_configuration",
      "aws_s3_bucket_policy",
      "aws_ecr_repository",
      # Task 1.4a, ecs.tf and iam.tf
      "aws_ecs_cluster",
      "aws_ecs_task_definition",
      "aws_ecs_service",
      "aws_cloudwatch_log_group",
      "aws_iam_role",
      "aws_iam_role_policy",
    ])
    error_message = "The cell module declares a resource type this allowlist does not name, or no longer declares one it does. Plan row 1.2 forbids a NAT gateway, a bastion and a database; plan row 1.9 polices the rest."
  }
}

# --- The task's shape ------------------------------------------------------------------------

run "the_task_is_arm64_at_a_quarter_vcpu_and_a_gigabyte" {
  command = plan

  assert {
    condition     = aws_ecs_task_definition.gateway.runtime_platform[0].cpu_architecture == "ARM64"
    error_message = "The task must be ARM64 (plan row 1.4): ARM Fargate is about 20% below x86, which is the cost model in docs/plan/05-infrastructure-and-cost.md."
  }

  assert {
    condition     = aws_ecs_task_definition.gateway.runtime_platform[0].operating_system_family == "LINUX"
    error_message = "The task runs Linux; the images are Linux ARM64 (src/Gateway/Dockerfile)."
  }

  assert {
    condition     = aws_ecs_task_definition.gateway.cpu == "256" && aws_ecs_task_definition.gateway.memory == "1024"
    error_message = "The task is 0.25 vCPU and 1 GB (plan row 1.4): 256 CPU units and 1024 MiB, which is what the $8.51 a month in the cost model is priced on."
  }

  assert {
    condition     = aws_ecs_task_definition.gateway.network_mode == "awsvpc"
    error_message = "Fargate requires awsvpc, and the target group registers by address because of it (task 1.3)."
  }

  assert {
    condition     = aws_ecs_task_definition.gateway.requires_compatibilities == toset(["FARGATE"])
    error_message = "The task is Fargate only: the cell has no EC2 capacity and no NAT gateway to give it."
  }
}

run "the_service_registers_with_the_load_balancer_s_existing_target_group" {
  command = plan

  assert {
    condition     = one(aws_ecs_service.gateway.load_balancer).target_group_arn == aws_lb_target_group.gateway.arn
    error_message = "The service must register in the target group task 1.3 already created, which is the target the one listener has been forwarding to with nothing behind it."
  }

  assert {
    condition = (
      one(aws_ecs_service.gateway.load_balancer).container_name == "gateway"
      && one(aws_ecs_service.gateway.load_balancer).container_port == var.gateway_port
    )
    error_message = "The registered target is the gateway container on the gateway's port, which is the port the target group sends to (task 1.3)."
  }
}

# var.gateway_port is one value in three places: the target group sends to it (task 1.3), the
# container publishes it, and the process has to listen on it. The third did not follow the
# variable -- the image's own ASPNETCORE_HTTP_PORTS=8080 decided it (src/Gateway/Dockerfile) -- so
# at any other value the load balancer sent to a port nothing answered on and the target could
# never turn healthy, which is row 1.4's own done-when.
#
# Run with the variable changed, because at its default the three agree by coincidence and the
# assertion could not fail. The root's own test already exercises gateway_port = 9090.
run "the_gateway_process_listens_on_the_port_the_load_balancer_sends_to" {
  command = plan

  variables {
    gateway_port = 9090
  }

  assert {
    condition = (
      one([
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
        ]).environment : e if e.name == "ASPNETCORE_HTTP_PORTS"
      ]).value == tostring(var.gateway_port)
    )
    error_message = "The gateway container must be told to listen on var.gateway_port: its image listens on 8080 unless told otherwise (src/Gateway/Dockerfile), so at any other value the load balancer sends to a port nothing answers on."
  }

  assert {
    condition = (
      one(one([
        for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
      ]).portMappings).containerPort == var.gateway_port
      && one(aws_ecs_service.gateway.load_balancer).container_port == var.gateway_port
      && aws_lb_target_group.gateway.port == var.gateway_port
    )
    error_message = "The published container port, the registered target port and the target group's port are all var.gateway_port: one value in three places, so none of them can drift from the others."
  }
}

run "the_deployment_circuit_breaker_rolls_a_bad_image_back" {
  command = plan

  assert {
    condition     = aws_ecs_service.gateway.deployment_circuit_breaker[0].enable
    error_message = "The deployment circuit breaker must be enabled (plan row 1.4), or a bad image retries forever instead of failing."
  }

  assert {
    condition     = aws_ecs_service.gateway.deployment_circuit_breaker[0].rollback
    error_message = "The circuit breaker must roll back, not merely stop: docs/plan/05-infrastructure-and-cost.md names it as the rollback lever beside rollback.sh."
  }
}

run "ecs_exec_is_on_and_the_task_s_root_filesystem_stays_writable" {
  command = plan

  assert {
    condition     = aws_ecs_service.gateway.enable_execute_command
    error_message = "ECS Exec must be on: the Session Manager tunnel of task 1.6b uses the task as its hop, and there is no bastion (docs/plan/05-infrastructure-and-cost.md -> Workstation access)."
  }

  # The third of the three conditions that route comes with, beside Exec on the service and the four
  # ssmmessages permissions on the task role: "Its root filesystem must be writable."
  assert {
    condition = alltrue([
      for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c.readonlyRootFilesystem == false
    ])
    error_message = "No container may have a read-only root filesystem: the Session Manager agent writes into it, which is the third condition docs/plan/05-infrastructure-and-cost.md attaches to the tunnel."
  }
}

run "the_log_group_carries_the_contract_s_name_and_an_explicit_retention" {
  command = plan

  assert {
    condition     = aws_cloudwatch_log_group.gateway.name == jsondecode(file("../../../contracts/infra-outputs.example.json")).logGroup
    error_message = "The log group's name must be the contract's logGroup for this cell (contracts/infra-outputs.example.json): the deployed tests read a run's gateway log from it."
  }

  assert {
    condition     = aws_cloudwatch_log_group.gateway.retention_in_days > 0
    error_message = "The log group needs an explicit retention (plan row 1.4), or logs accumulate for ever without a decision having been taken."
  }

  # Both containers log to the one group the contract has a field for, told apart by stream prefix.
  assert {
    condition = alltrue([
      for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : (
        c.logConfiguration.logDriver == "awslogs"
        && c.logConfiguration.options["awslogs-group"] == aws_cloudwatch_log_group.gateway.name
        && c.logConfiguration.options["awslogs-stream-prefix"] == c.name
      )
    ])
    error_message = "Every container logs to this cell's one log group with its own stream prefix, because contracts/infra-outputs.schema.json has one logGroup field."
  }
}

run "the_task_takes_a_public_address_in_this_cell_s_public_subnets" {
  command = plan

  # The no-NAT design (plan row 1.2): without a public address the task cannot pull an image, reach
  # Cognito, S3, Parameter Store or logs, and a NAT gateway costs more than the rest of the cell.
  assert {
    condition     = aws_ecs_service.gateway.network_configuration[0].assign_public_ip
    error_message = "The task needs a public address: the cell has no NAT gateway, so this is the only route out (plan row 1.2)."
  }

  assert {
    condition     = aws_ecs_service.gateway.network_configuration[0].subnets == toset([for s in aws_subnet.public : s.id])
    error_message = "The task sits in this cell's own public subnets and no others."
  }

  assert {
    condition     = aws_ecs_service.gateway.network_configuration[0].security_groups == toset([aws_security_group.task.id])
    error_message = "The task carries the task group of task 1.2 and nothing else: that group is what accepts the gateway's port from the load balancer alone (test P-06)."
  }
}

# --- The task environment --------------------------------------------------------------------

# Derived from contracts/platform-config.schema.json rather than listed here, so a section or a
# setting added to the contract fails this run until the module supplies it.
run "the_task_environment_carries_every_setting_the_configuration_contract_requires" {
  command = plan

  assert {
    condition = alltrue([
      for section in jsondecode(file("../../../contracts/platform-config.schema.json")).required :
      anytrue([
        for key in keys({
          for e in one([
            for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
          ]).environment : e.name => e.value
        }) : startswith(key, "${section}__")
      ])
    ])
    error_message = "The gateway's environment is missing a section contracts/platform-config.schema.json marks required."
  }

  # The leaves of the closed sections. An array setting arrives as Section__Name__0, so a key that
  # equals the name or begins with it followed by __ both count.
  assert {
    condition = alltrue(flatten([
      for section, spec in jsondecode(file("../../../contracts/platform-config.schema.json")).properties :
      [
        for required_leaf in try(spec.required, []) :
        anytrue([
          for key in keys({
            for e in one([
              for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
            ]).environment : e.name => e.value
          }) : key == "${section}__${required_leaf}" || startswith(key, "${section}__${required_leaf}__")
        ])
      ] if section != "Issuers"
    ]))
    error_message = "The gateway's environment is missing a setting one of the contract's closed sections marks required."
  }

  # Issuers is a map in the contract, so its required leaves sit under each issuer's own name.
  assert {
    condition = alltrue(flatten([
      for issuer_name in keys(var.issuers) : [
        for required_leaf in jsondecode(file("../../../contracts/platform-config.schema.json")).properties.Issuers.additionalProperties.required :
        contains(keys({
          for e in one([
            for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
          ]).environment : e.name => e.value
        }), "Issuers__${issuer_name}__${required_leaf}")
      ]
    ]))
    error_message = "Each issuer must carry every setting contracts/platform-config.schema.json requires of an issuer: Authority, Profile and BrowserOrigin."
  }
}

run "the_environment_is_the_contract_s_double_underscore_form_and_its_integers_render_as_decimals" {
  command = plan

  # "a colon becomes a double underscore (Policy:AllowedUpstreams:0 is Policy__AllowedUpstreams__0)
  # and every value arrives as a string that the binder converts" -- contracts/platform-config.schema.json.
  assert {
    condition = alltrue([
      for e in one([
        for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
      ]).environment : !strcontains(e.name, ":") && can(regex("^[A-Za-z0-9_-]+$", e.name))
    ])
    error_message = "An environment key must carry no colon: the contract's nested keys arrive in the double-underscore form, and the only other character a section or an issuer name contributes is the hyphen contracts/platform-config.example.json already shows in cognito-poc."
  }

  # That every value is a string needs no assertion: the provider refuses a container definition
  # whose environment holds a number outright ("cannot unmarshal number into Go struct field
  # KeyValuePair.Environment.Value of type string"), so an assertion on it could not fail and would
  # report confidence it had not earned.
  #
  # What can go wrong, and what this checks, is the rendering. The contract's integer settings have
  # to arrive as the decimal form the binder converts back: "15s" or "1.5e+01" are strings the
  # provider accepts and the gateway cannot read. The settings are found by their type in the
  # schema, so an integer setting added to the contract is covered without an edit here.
  assert {
    condition = alltrue(flatten([
      for section, spec in jsondecode(file("../../../contracts/platform-config.schema.json")).properties : [
        for leaf, leaf_spec in try(spec.properties, {}) :
        [
          for e in one([
            for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
          ]).environment : can(regex("^(0|[1-9][0-9]*)$", e.value)) if e.name == "${section}__${leaf}"
        ]
        if try(leaf_spec.type, "") == "integer"
      ] if section != "Issuers"
    ]))
    error_message = "A setting contracts/platform-config.schema.json types as an integer must arrive as its plain decimal form: the binder converts \"15\", not \"15s\" and not \"1.5e+01\"."
  }

  assert {
    condition = (
      one([
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
        ]).environment : e if e.name == "Policy__AllowedUpstreams__0"
      ]).value == var.allowed_upstreams[0]
    )
    error_message = "An array setting is indexed in the key, which is the form the schema's own example gives: Policy__AllowedUpstreams__0."
  }
}

# Run with every sourced value changed at once. The default-input run cannot tell a value read from
# the module's own bucket and variables from one typed into the environment twice.
run "the_registry_and_cell_settings_come_from_the_module_s_own_bucket_and_variables" {
  command = plan

  variables {
    cell                  = "other"
    bucket_name           = "spa-platform-somewhere-else"
    registry_key          = "registry/other.json"
    registry_poll_seconds = 7
  }

  assert {
    condition = alltrue([
      for key, expected in {
        "Platform__Cell"        = var.cell
        "Registry__Bucket"      = aws_s3_bucket.platform.bucket
        "Registry__Key"         = var.registry_key
        "Registry__PollSeconds" = tostring(var.registry_poll_seconds)
        "Assets__Bucket"        = aws_s3_bucket.platform.bucket
        } : one([
          for e in one([
            for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
          ]).environment : e if e.name == key
      ]).value == expected
    ])
    error_message = "The bucket, the registry key, the poll interval and the cell name must come from the module's own bucket and variables, not be restated in the environment."
  }

  # Both buckets are the one bucket in Phase 1, which is what the contract's Assets:Bucket says.
  assert {
    condition = (
      one([
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
        ]).environment : e if e.name == "Assets__Bucket"
      ]).value == "spa-platform-somewhere-else"
    )
    error_message = "Releases and the registry live in the one bucket this module creates (contracts/s3-layout.md)."
  }
}

run "the_trusted_networks_are_the_load_balancer_s_own_subnets_and_nothing_wider" {
  command = plan

  variables {
    vpc_cidr = "10.31.0.0/20"
  }

  # Read off the load balancer rather than restated: the contract's own words for this field are
  # "The ALB subnets, in CIDR form" (contracts/platform-config.schema.json), so the expected value
  # is the CIDR of each subnet aws_lb.cell actually sits in. Moving the load balancer moves this.
  assert {
    condition = (
      toset([
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
        ]).environment : e.value if startswith(e.name, "ForwardedHeaders__TrustedNetworks__")
      ])
      == toset([for s in aws_subnet.public : s.cidr_block if contains(aws_lb.cell.subnets, s.id)])
    )
    error_message = "ForwardedHeaders:TrustedNetworks is the CIDR of each subnet the load balancer sits in: stage 1 trusts X-Forwarded-For from the load balancer alone (contracts/pipeline.md stage 1, N-09)."
  }

  # The anti-widening half. The cell's whole VPC range passes the first assertion's shape only by
  # accident and admits every unallocated address in the cell, which is not the load balancer.
  assert {
    condition = !contains(
      [
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
        ]).environment : e.value if startswith(e.name, "ForwardedHeaders__TrustedNetworks__")
      ],
      var.vpc_cidr
    )
    error_message = "The cell's whole VPC range is not a trusted network: only two subnets of it are allocated, so the range would trust addresses no load balancer holds."
  }

  # One entry per subnet, indexed from zero, which is the array form the contract's own example
  # gives for a list setting.
  assert {
    condition = (
      length([
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
        ]).environment : e.value if startswith(e.name, "ForwardedHeaders__TrustedNetworks__")
      ]) == length(aws_lb.cell.subnets)
    )
    error_message = "Every subnet the load balancer sits in contributes one TrustedNetworks entry; a dropped one would stop stage 1 trusting that availability zone's node."
  }
}

# --- The task role, which is the security object of record ------------------------------------

run "the_task_role_reads_this_cell_s_bucket_prefixes_and_no_others" {
  command = plan

  # The prefixes are read out of the contract's own layout block, so widening one by a segment or
  # dropping one fails here. contracts/s3-layout.md -> registry/registry.json and
  # apps/{appId}/releases/{release}/**.
  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadRegistryAndReleases"]).resources
      == toset([
        for prefix in distinct([
          for m in regexall("(?m)^([a-z]+)/", file("../../../contracts/s3-layout.md")) : m[0]
        ]) : "${aws_s3_bucket.platform.arn}/${prefix}/*"
      ])
    )
    error_message = "The task role reads exactly the prefixes contracts/s3-layout.md lays out, under this cell's bucket: registry/* and apps/*, and nothing else."
  }

  # "The gateway cannot write. The task role reads this cell's bucket prefixes; it has no write to
  # the registry (P-03)." -- contracts/s3-layout.md.
  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadRegistryAndReleases"]).actions
      == toset(["s3:GetObject"])
    )
    error_message = "The gateway reads objects and nothing more: a write, a delete or a list here would contradict contracts/s3-layout.md and test P-03."
  }

  # The bucket itself is not in the grant, so no ListBucket can be reached even if an action were
  # added: contracts/s3-layout.md gives the gateway a conditional GET and a GET, never a listing.
  assert {
    condition = !contains(
      one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadRegistryAndReleases"]).resources,
      aws_s3_bucket.platform.arn
    )
    error_message = "The bucket's own ARN is not granted: the gateway never lists the bucket, it GETs keys the registry names."
  }
}

run "the_task_role_reads_this_cell_s_client_secret_path_and_no_wider_path" {
  command = plan

  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadClientSecrets"]).resources
      == toset(["arn:aws:ssm:us-east-1:111122223333:parameter/spa-platform/poc/apps/*"])
    )
    error_message = "The task role reads this cell's app parameter path only (contracts/infra-outputs.example.json -> clients[].secretParameter, ADR-0003)."
  }

  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadClientSecrets"]).actions
      == toset(["ssm:GetParameter", "ssm:GetParameters"])
    )
    error_message = "Reading a client secret is GetParameter by name and version (ADR-0003). Writing or listing parameters is not the gateway's."
  }

  # The anti-widening half, and the reason this run exists at all: the granted prefix admits this
  # cell's own parameters and refuses another cell's. A grant of /spa-platform/* or /* passes the
  # first assertion's shape by accident and fails here.
  assert {
    condition = (
      startswith(
        jsondecode(file("../../../contracts/infra-outputs.example.json")).clients.app1.secretParameter,
        trimsuffix(replace(
          one(one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadClientSecrets"]).resources),
          "/^arn:[^:]*:ssm:[^:]*:[^:]*:parameter/", ""
        ), "*")
      )
    )
    error_message = "The granted parameter path must admit this cell's own client secret (contracts/infra-outputs.example.json -> clients.app1.secretParameter)."
  }

  assert {
    condition = !startswith(
      "/spa-platform/another-cell/apps/app1/client-secret",
      trimsuffix(replace(
        one(one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "ReadClientSecrets"]).resources),
        "/^arn:[^:]*:ssm:[^:]*:[^:]*:parameter/", ""
      ), "*")
    )
    error_message = "The granted parameter path must refuse another cell's client secret: a grant widened to /spa-platform/* reaches every cell in the account."
  }
}

run "the_task_role_holds_exactly_the_four_ssmmessages_permissions" {
  command = plan

  # "Its role needs the four ssmmessages permissions" -- docs/plan/05-infrastructure-and-cost.md.
  # Exactly four, so turning Exec on does not become a general messaging grant.
  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task.statement : s if s.sid == "SessionManagerMessages"]).actions
      == toset([
        "ssmmessages:CreateControlChannel",
        "ssmmessages:CreateDataChannel",
        "ssmmessages:OpenControlChannel",
        "ssmmessages:OpenDataChannel",
      ])
    )
    error_message = "The tunnel needs these four ssmmessages actions and no fifth (docs/plan/05-infrastructure-and-cost.md -> Workstation access)."
  }
}

# The assertion that makes the four above worth having: a statement the test does not recognise
# fails, so a widened grant cannot pass by being unmentioned.
run "the_task_role_holds_no_unrecognised_statement" {
  command = plan

  assert {
    condition = (
      toset([for s in data.aws_iam_policy_document.task.statement : s.sid])
      == toset(["ReadRegistryAndReleases", "ReadClientSecrets", "SessionManagerMessages"])
    )
    error_message = "The task role holds exactly the three grants plan row 1.4 names, each with a sid this test recognises. A fourth is a widening and belongs in a change request."
  }

  # A sid this test knows is no defence if the statement under it reaches further than its name
  # says. These four close the ways a recognised statement can still be widened.
  assert {
    condition     = alltrue([for s in data.aws_iam_policy_document.task.statement : s.effect == "Allow"])
    error_message = "Every statement in the task role is an Allow. A Deny here would be a second policy language on top of a role that is meant to be readable once."
  }

  assert {
    condition = alltrue([
      for s in data.aws_iam_policy_document.task.statement :
      !anytrue([for action in s.actions : strcontains(action, "*")])
    ])
    error_message = "No action in the task role carries a wildcard: s3:* or ssm:Get* passes a sid check and grants far more than the grant's name."
  }

  assert {
    condition = (
      toset([for s in data.aws_iam_policy_document.task.statement : s.sid if contains(s.resources, "*")])
      == toset(["SessionManagerMessages"])
    )
    error_message = "Only the ssmmessages grant is on \"*\", because those four actions take no resource. Every other grant names this cell's own resources."
  }

  assert {
    # Unset is null rather than empty here, so each is read for the value it has when the statement
    # does not use it.
    condition = alltrue([
      for s in data.aws_iam_policy_document.task.statement :
      length(coalesce(s.not_actions, toset([]))) == 0 && length(coalesce(s.not_resources, toset([]))) == 0
    ])
    error_message = "No statement is written as NotAction or NotResource: both grant everything except what they name, which is the opposite of a reviewable role."
  }

  # Everything above reads data.aws_iam_policy_document.task, so it is a closed account of the
  # role's grants only while that document is the whole of what the role holds. Three declarations
  # add a grant without passing through it, and each was watched leaving all 25 runs green before
  # these assertions existed (see the ticket's Review section): a second aws_iam_role_policy on the
  # same role, aws_iam_role's own inline_policy block, and managed_policy_arns -- which is one line
  # and arn:aws:iam::aws:policy/AdministratorAccess.
  #
  # Read from the module's source rather than from a value, because the two role attributes are
  # Optional+Computed in provider v6: correctly unset, they read as unknown at plan time, so an
  # assertion on their value could not pass cleanly. This is the technique the resource-type
  # allowlist and the no-default check already use for the same reason. A separate
  # aws_iam_role_policy_attachment needs nothing here: the resource-type allowlist does not name
  # that type.
  assert {
    condition = toset([
      for m in regexall(
        "(?m)^resource \"aws_iam_role_policy\" \"([a-z0-9_]+)\"",
        join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
      ) : m[0]
    ]) == toset(["task", "task_execution"])
    error_message = "Each role carries exactly one inline policy, and they are the two documents this file reads. A third aws_iam_role_policy widens a role without changing either document, so every assertion above would still pass."
  }

  assert {
    condition = length(regexall(
      "(?m)^\\s*(managed_policy_arns\\s*=|inline_policy\\s*\\{)",
      join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
    )) == 0
    error_message = "No role here attaches a managed policy or declares an inline_policy block: either adds a grant that data.aws_iam_policy_document.task never sees, and AdministratorAccess is one line of managed_policy_arns."
  }

  # And each of the two policies is on its own role, so the agent's grants cannot also land on the
  # workload's role under a name this file already recognises.
  assert {
    condition = (
      aws_iam_role_policy.task.role == aws_iam_role.task.id
      && aws_iam_role_policy.task_execution.role == aws_iam_role.task_execution.id
    )
    error_message = "The task policy is on the task role and the execution policy on the execution role: attaching either to the other role would make the two-role split cosmetic."
  }


}

run "the_execution_role_is_separate_and_holds_only_image_pull_and_log_writes" {
  command = plan

  assert {
    condition     = aws_iam_role.task_execution.name != aws_iam_role.task.name
    error_message = "Pulling an image and running the workload are distinct grants, so they are distinct roles."
  }

  assert {
    condition = (
      aws_ecs_task_definition.gateway.execution_role_arn == aws_iam_role.task_execution.arn
      && aws_ecs_task_definition.gateway.task_role_arn == aws_iam_role.task.arn
      && aws_ecs_task_definition.gateway.execution_role_arn != aws_ecs_task_definition.gateway.task_role_arn
    )
    error_message = "The task definition must carry both roles, in their own places: the agent pulls and logs with the execution role, the process reaches AWS with the task role."
  }

  assert {
    condition = (
      toset([for s in data.aws_iam_policy_document.task_execution.statement : s.sid])
      == toset(["EcrAuthorization", "PullCellImages", "WriteTaskLogs"])
    )
    error_message = "The execution role holds exactly image pull and log writes. Anything the workload itself needs belongs in the task role."
  }

  # The workload's own role must not be able to pull or push an image, and the agent's must not be
  # able to read a secret. Crossing either way would make the split cosmetic.
  assert {
    condition = alltrue([
      for s in data.aws_iam_policy_document.task.statement :
      !anytrue([for action in s.actions : startswith(action, "ecr:") || startswith(action, "logs:")])
    ])
    error_message = "The task role holds no ecr: or logs: action: those are the execution role's, and the task's own logging goes through the agent."
  }

  assert {
    condition = alltrue([
      for s in data.aws_iam_policy_document.task_execution.statement :
      !anytrue([for action in s.actions : startswith(action, "ssm:") || startswith(action, "ssmmessages:")])
    ])
    error_message = "The execution role reaches no parameter and no messaging channel: a client secret is the workload's to read, not the agent's."
  }

  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task_execution.statement : s if s.sid == "WriteTaskLogs"]).resources
      == toset(["${aws_cloudwatch_log_group.gateway.arn}:*"])
    )
    error_message = "Log writes are scoped to this cell's own log group, not to every group in the account."
  }

  # The pull is scoped to this cell's own two repositories, which is what the managed
  # AmazonECSTaskExecutionRolePolicy does not do: it grants its ECR actions on "*".
  assert {
    condition = (
      one([for s in data.aws_iam_policy_document.task_execution.statement : s if s.sid == "PullCellImages"]).resources
      == toset([for repository in aws_ecr_repository.repository : repository.arn])
    )
    error_message = "The image pull names this cell's gateway and mock API repositories and no others."
  }

  # CreateLogGroup is not among them: Terraform creates the group, so the agent only adds streams.
  assert {
    condition = !anytrue(flatten([
      for s in data.aws_iam_policy_document.task_execution.statement : [for action in s.actions : action == "logs:CreateLogGroup"]
    ]))
    error_message = "The execution role must not be able to create a log group: ecs.tf creates the one group contracts/infra-outputs.schema.json has a field for."
  }

  # The same anti-widening shape the task role gets. GetAuthorizationToken is the one action here
  # that takes no resource-level condition, so it is the one grant allowed on "*".
  assert {
    condition = (
      toset([for s in data.aws_iam_policy_document.task_execution.statement : s.sid if contains(s.resources, "*")])
      == toset(["EcrAuthorization"])
    )
    error_message = "Only the ECR authorization grant is on \"*\"; the pull and the log writes name this cell's own resources."
  }

  assert {
    condition = alltrue([
      for s in data.aws_iam_policy_document.task_execution.statement :
      !anytrue([for action in s.actions : strcontains(action, "*")])
    ])
    error_message = "No action in the execution role carries a wildcard."
  }
}

run "both_roles_are_assumable_only_by_ecs_tasks_in_this_account" {
  command = plan

  assert {
    condition = alltrue([
      for s in data.aws_iam_policy_document.ecs_tasks_trust.statement : (
        s.actions == toset(["sts:AssumeRole"])
        && alltrue([for p in s.principals : p.type == "Service" && p.identifiers == toset(["ecs-tasks.amazonaws.com"])])
      )
    ])
    error_message = "Only the ECS tasks service may assume these roles."
  }

  # CODING_STANDARDS.md: names alone do not hold a trust boundary. A mutation replacing the
  # SourceAccount value with "*" passed all 26 runs before this assertion; now it fails this run.
  # AWS specifies both the operator and the account/region-scoped values:
  # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html
  assert {
    condition = length(data.aws_iam_policy_document.ecs_tasks_trust.statement) == 1 && alltrue([
      for s in data.aws_iam_policy_document.ecs_tasks_trust.statement : (
        s.effect == "Allow"
        && length(s.principals) == 1
        && length(s.condition) == 2
        && alltrue([
          for c in s.condition : (
            c.variable == "aws:SourceAccount"
            ? c.test == "StringEquals" && toset(c.values) == toset([data.aws_caller_identity.current.account_id])
            : c.variable == "aws:SourceArn" && c.test == "ArnLike" && toset(c.values) == toset([
              "arn:${data.aws_partition.current.partition}:ecs:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:*",
            ])
          )
        ])
      )
    ])
    error_message = "ECS trust must use the documented operators and this account's exact account/region bounds, with no additional statement or principal."
  }

  # The confused-deputy conditions AWS documents for a service role: without them, ECS in any
  # account could be asked to assume this role.
  # https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html
  assert {
    condition = (
      toset(flatten([
        for s in data.aws_iam_policy_document.ecs_tasks_trust.statement : [for c in s.condition : c.variable]
      ])) == toset(["aws:SourceAccount", "aws:SourceArn"])
    )
    error_message = "The trust policy must hold aws:SourceAccount and aws:SourceArn, or ECS in another account can be made to assume this role on that account's behalf."
  }
}

# --- The two containers ----------------------------------------------------------------------

run "the_mock_api_container_is_absent_until_its_image_is_supplied" {
  command = plan

  assert {
    condition = (
      [for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c.name] == ["gateway"]
    )
    error_message = "With no mock API image the task holds the gateway alone, so the service applies before that image exists (plan row 1.4)."
  }
}

run "the_mock_api_container_joins_with_the_client_ids_it_is_given" {
  command = plan

  variables {
    mock_api_image          = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
    mock_api_app1_client_id = "1example23456789"
    mock_api_app2_client_id = "2example23456789"
  }

  assert {
    condition = (
      toset([for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c.name])
      == toset(["gateway", "mock-api"])
    )
    error_message = "Supplying the mock API image must put its container in the task beside the gateway."
  }

  # The keys are read out of the image's own Dockerfile, which is where workstream 5 declares the
  # three the process refuses to start without (fixtures/MockApi/Program.cs -> Required).
  assert {
    condition = (
      toset(keys({
        for e in one([
          for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "mock-api"
        ]).environment : e.name => e.value
      }))
      == toset([
        for m in regexall("(?m)^ENV (MockApi__[A-Za-z0-9]+)=", file("../../../fixtures/MockApi/Dockerfile")) : m[0]
      ])
    )
    error_message = "The mock API container carries exactly the environment keys fixtures/MockApi/Dockerfile declares; the process refuses to start without them."
  }

  assert {
    condition = alltrue([
      for key, expected in {
        "MockApi__App1Client" = var.mock_api_app1_client_id
        "MockApi__App2Client" = var.mock_api_app2_client_id
        "MockApi__Issuer"     = values(var.issuers)[0].authority
        } : one([
          for e in one([
            for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "mock-api"
          ]).environment : e if e.name == key
      ]).value == expected
    ])
    error_message = "Each listener's admitted client is the input it was given: the apps root decides client ids, and nothing about them is written into this module (plan row 1.4, contracts/upstream-contract.md -> One token per app)."
  }

  assert {
    condition = (
      one([
        for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "mock-api"
      ]).image == var.mock_api_image
    )
    error_message = "The mock API's image is the variable it was given, so a routine apply never moves it."
  }
}

run "a_mock_api_image_with_no_client_ids_is_refused" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
  }

  # fixtures/MockApi/Program.cs throws on a missing client id, so the alternative to this validation
  # is a container that crash-loops after the apply reported success.
  expect_failures = [var.mock_api_app1_client_id, var.mock_api_app2_client_id]
}

run "each_container_image_is_the_variable_it_was_given_with_no_tag_naming_default" {
  command = plan

  variables {
    gateway_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/gateway@sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
  }

  assert {
    condition = (
      one([
        for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == "gateway"
      ]).image == var.gateway_image
    )
    error_message = "The gateway's image is the variable it was given: deploy.sh passes a digest and every other apply passes the digest already in state, which is what keeps a routine apply from moving a running image."
  }

  # Read out of the declaration, because no assertion on a value can show the absence of a default.
  # The gateway's image has no default at all; the mock API's defaults to absent, never to a tag.
  # The match is on the argument at the block's own indent, not on the word, which appears in both
  # descriptions.
  assert {
    condition = (
      length(regexall(
        "(?ms)^variable \"gateway_image\" \\{.*?^\\}",
        file("${path.module}/variables.tf")
      )) == 1
      && length(regexall(
        "(?m)^  default\\s*=",
        one(regexall("(?ms)^variable \"gateway_image\" \\{.*?^\\}", file("${path.module}/variables.tf")))
      )) == 0
    )
    error_message = "gateway_image must declare no default: a default that names a tag would let an apply move a running image (docs/plan/05-infrastructure-and-cost.md -> What stays out of Terraform)."
  }

  assert {
    condition = length(regexall(
      "(?m)^  default\\s*= \"\"$",
      one(regexall("(?ms)^variable \"mock_api_image\" \\{.*?^\\}", file("${path.module}/variables.tf")))
    )) == 1
    error_message = "mock_api_image defaults to absent, so the service applies before that image is built; a tag-naming default would move it instead."
  }
}

# --- Outputs and names -----------------------------------------------------------------------

run "the_new_outputs_match_the_infrastructure_outputs_contract" {
  command = plan

  assert {
    condition = alltrue([
      for field, expected in {
        ecsCluster = output.ecs_cluster
        ecsService = output.ecs_service
        logGroup   = output.log_group
      } : jsondecode(file("../../../contracts/infra-outputs.example.json"))[field] == expected
    ])
    error_message = "ecs_cluster, ecs_service and log_group must be the values contracts/infra-outputs.example.json shows for this cell: task 1.7 assembles outputs.json from them."
  }

  assert {
    condition = alltrue([
      for value in [output.ecs_cluster, output.ecs_service] :
      can(regex(jsondecode(file("../../../contracts/infra-outputs.schema.json"))["$defs"].awsName.pattern, value))
    ])
    error_message = "ecs_cluster and ecs_service must satisfy the contract's awsName pattern."
  }

  assert {
    condition = can(regex(
      jsondecode(file("../../../contracts/infra-outputs.schema.json")).properties.logGroup.pattern,
      output.log_group
    ))
    error_message = "log_group must satisfy the contract's logGroup pattern: an absolute path of the characters CloudWatch allows."
  }

  # The digest a deployment passes comes back out unchanged, so task 1.7 can assert the running
  # image against what it applied. The contract's gatewayImage pattern is the check.
  assert {
    condition = can(regex(
      jsondecode(file("../../../contracts/infra-outputs.schema.json")).properties.gatewayImage.pattern,
      output.gateway_image
    ))
    error_message = "gateway_image must come back out in the contract's gatewayImage form, pinned by digest."
  }
}

run "every_new_resource_names_itself_so_the_root_s_default_tags_reach_it" {
  command = plan

  # The module sets no provider and so no tags: the calling root applies Platform, Cell and
  # ManagedBy through default_tags (contracts/resource-tags.md), and those reach every taggable
  # resource whether or not it declares a tags argument. What this run proves is that each new
  # resource is of a type that takes tags and says which resource it is, so teardown's tag query
  # and P-05 find it by name as well as by tag. The resource-type allowlist above is what forces a
  # newly added type past this assertion rather than around it.
  #
  # aws_iam_role_policy takes no tags and is not listed: it is an inline policy on a tagged role,
  # which is the child-of-a-tagged-parent rule in contracts/resource-tags.md.
  assert {
    condition = alltrue([
      aws_ecs_cluster.cell.tags["Name"] == "spa-${var.cell}",
      aws_cloudwatch_log_group.gateway.tags["Name"] == aws_cloudwatch_log_group.gateway.name,
      aws_ecs_task_definition.gateway.tags["Name"] == aws_ecs_task_definition.gateway.family,
      aws_ecs_service.gateway.tags["Name"] == "spa-platform-${var.cell}-${aws_ecs_service.gateway.name}",
      aws_iam_role.task_execution.tags["Name"] == aws_iam_role.task_execution.name,
      aws_iam_role.task.tags["Name"] == aws_iam_role.task.name,
    ])
    error_message = "Every new resource names itself in a Name tag, which merges with the three tags the root applies through default_tags (contracts/resource-tags.md)."
  }
}

run "the_longest_cell_name_keeps_every_new_name_legal" {
  command = plan

  variables {
    cell = "abcdefghijklmnopqrstuv"
  }

  # The cell name's own validation allows 22 characters, and task 1.3 already found two names that
  # do not fit at that length. These are the new ones.
  assert {
    condition     = length(aws_ecs_cluster.cell.name) <= 255 && can(regex("^[A-Za-z0-9][A-Za-z0-9_-]*$", aws_ecs_cluster.cell.name))
    error_message = "The cluster's name must stay within what ECS accepts and what the contract's awsName allows at the cell name's maximum length."
  }

  assert {
    condition     = length(aws_ecs_service.gateway.name) <= 255 && can(regex("^[A-Za-z0-9][A-Za-z0-9_-]*$", aws_ecs_service.gateway.name))
    error_message = "The service's name must stay within what ECS accepts at the cell name's maximum length."
  }

  assert {
    condition = (
      length(aws_iam_role.task.name) <= 64
      && length(aws_iam_role.task_execution.name) <= 64
    )
    error_message = "An IAM role name is capped at 64 characters, and both role names carry spa-platform-{cell}."
  }

  assert {
    condition     = length(aws_cloudwatch_log_group.gateway.name) <= 512
    error_message = "A log group name is capped at 512 characters."
  }

  # Both roles stay inside the name the CI plan role is allowed to read
  # (infra/bootstrap/ci-role.tf -> ReadPlatformRoles grants role/spa-platform-{cell}-*).
  assert {
    condition = alltrue([
      for name in [aws_iam_role.task.name, aws_iam_role.task_execution.name] :
      startswith(name, "spa-platform-${var.cell}-")
    ])
    error_message = "Both role names begin with spa-platform-{cell}-, which is the prefix infra/bootstrap/ci-role.tf lets a plan read."
  }
}

run "an_empty_issuer_map_is_refused" {
  command = plan

  variables {
    issuers = {}
  }

  # contracts/platform-config.schema.json gives Issuers minProperties 1: a gateway with no issuer
  # can authenticate nobody, and the configuration binder would see the section missing entirely.
  expect_failures = [var.issuers]
}

# A mock-only apply resolves document values deferred by the resource dependencies. Terraform's
# mock provider stores fake resources only in its in-memory test state; no AWS API is called.
# https://developer.hashicorp.com/terraform/language/tests/mocking
run "the_attached_grants_and_trust_are_the_checked_documents" {
  command = apply

  override_resource {
    target = aws_lb.cell
    values = { arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:loadbalancer/app/spa-poc/0123456789abcdef" }
  }

  override_resource {
    target          = aws_s3_bucket.platform
    override_during = apply
    values          = { arn = "arn:aws:s3:::spa-platform-poc" }
  }
  override_resource {
    target          = aws_lb_target_group.gateway
    override_during = apply
    values          = { arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:targetgroup/spa-poc/0123456789abcdef" }
  }
  override_resource {
    target          = aws_subnet.public["us-east-1a"]
    override_during = apply
    values          = { id = "subnet-0a" }
  }
  override_resource {
    target          = aws_subnet.public["us-east-1b"]
    override_during = apply
    values          = { id = "subnet-0b" }
  }
  override_resource {
    target          = aws_security_group.task
    override_during = apply
    values          = { id = "sg-task" }
  }
  override_resource {
    target          = aws_cloudwatch_log_group.gateway
    override_during = apply
    values          = { arn = "arn:aws:logs:us-east-1:111122223333:log-group:/spa-platform/poc/gateway" }
  }
  override_resource {
    target          = aws_ecr_repository.repository["gateway"]
    override_during = apply
    values          = { arn = "arn:aws:ecr:us-east-1:111122223333:repository/spa-poc/gateway" }
  }
  override_resource {
    target          = aws_ecr_repository.repository["mock-api"]
    override_during = apply
    values          = { arn = "arn:aws:ecr:us-east-1:111122223333:repository/spa-poc/mock-api" }
  }
  override_resource {
    target          = aws_iam_role.task
    override_during = apply
    values          = { arn = "arn:aws:iam::111122223333:role/spa-platform-poc-task" }
  }
  override_resource {
    target          = aws_iam_role.task_execution
    override_during = apply
    values          = { arn = "arn:aws:iam::111122223333:role/spa-platform-poc-task-execution" }
  }

  # Distinct provider sentinels above make the document selected by each attachment observable.
  assert {
    condition = (
      aws_iam_role_policy.task.policy == data.aws_iam_policy_document.task.json
      && aws_iam_role_policy.task_execution.policy == data.aws_iam_policy_document.task_execution.json
      && aws_iam_role.task.assume_role_policy == data.aws_iam_policy_document.ecs_tasks_trust.json
      && aws_iam_role.task_execution.assume_role_policy == data.aws_iam_policy_document.ecs_tasks_trust.json
    )
    error_message = "Both grants and both trust attachments must select their checked policy document."
  }

}
