# Plans the platform root against a mock AWS provider: no credentials, no backend and no
# certificate in ACM.
#   terraform -chdir=infra/envs/poc/platform init -backend=false
#   terraform -chdir=infra/envs/poc/platform test
#
# Task 1.3's load balancer is what this file checks. Expected values come from the files the
# root answers to where there is one: contracts/resource-tags.md for the tags.

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

  mock_data "aws_iam_policy_document" {
    defaults = {
      json = "{\"Version\":\"2012-10-17\",\"Statement\":[]}"
    }
  }

  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "111122223333"
    }
  }

  # Change request 1.5: the root looks the teardown role up by its derived name. The mock satisfies
  # the lookup, so what this file proves is the name and the wiring; that the role exists is a
  # property of the AWS plan, not of terraform test (ADR-0014 item 2).
  mock_data "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/spa-platform-poc-teardown"
    }
  }

  mock_data "aws_region" {
    defaults = {
      region = "us-east-1"
    }
  }

  mock_data "aws_partition" {
    defaults = {
      partition  = "aws"
      dns_suffix = "amazonaws.com"
    }
  }

  mock_resource "aws_lb" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:loadbalancer/app/spa-poc/0123456789abcdef"
      # A deliberate sentinel: this test proves output wiring, not AWS DNS generation.
      dns_name = "mock-alb-dns-sentinel"
    }
  }

  mock_resource "aws_lb_target_group" {
    defaults = {
      arn = "arn:aws:elasticloadbalancing:us-east-1:111122223333:targetgroup/spa-poc/0123456789abcdef"
    }
  }

  # Task 1.4a. A mock provider generates an opaque id for a computed attribute, and the provider
  # validates the two role ARNs the task definition carries, so those two need ARN-shaped values.
  mock_resource "aws_iam_role" {
    defaults = {
      arn = "arn:aws:iam::111122223333:role/spa-platform-poc"
    }
  }

  mock_resource "aws_ecs_cluster" {
    defaults = {
      arn = "arn:aws:ecs:us-east-1:111122223333:cluster/spa-poc"
      id  = "arn:aws:ecs:us-east-1:111122223333:cluster/spa-poc"
    }
  }

  mock_resource "aws_ecs_task_definition" {
    defaults = {
      arn = "arn:aws:ecs:us-east-1:111122223333:task-definition/spa-platform-poc:1"
    }
  }

  mock_resource "aws_cloudwatch_log_group" {
    defaults = {
      arn = "arn:aws:logs:us-east-1:111122223333:log-group:/spa-platform/poc/gateway"
    }
  }

  mock_resource "aws_ecr_repository" {
    defaults = {
      arn = "arn:aws:ecr:us-east-1:111122223333:repository/spa-poc"
    }
  }
}

# The apps root's outputs, in the shape infra/envs/poc/apps/outputs.tf gives them: one entry per
# app, keyed by appId, each naming the client id and its secret's parameter but never the secret.
# No value here is read into the task environment; only the two client ids reach the mock API.
override_data {
  target = data.terraform_remote_state.apps
  values = {
    outputs = {
      clients = {
        app1 = {
          clientId        = "1example23456789"
          secretParameter = "/spa-platform/poc/apps/app1/client-secret"
          secretVersion   = 1
        }
        app2 = {
          clientId        = "2example23456789"
          secretParameter = "/spa-platform/poc/apps/app2/client-secret"
          secretVersion   = 1
        }
        app3 = {
          clientId        = "3example23456789"
          secretParameter = "/spa-platform/poc/apps/app3/client-secret"
          secretVersion   = 1
        }
      }
    }
  }
}

variables {
  cell               = "poc"
  vpc_cidr           = "10.20.0.0/16"
  certificate_domain = "spa-poc.test"
  # Read only by the runs against modules/cell, which still takes the list as input. The root
  # derives it (change request 1.5).
  teardown_principal_arns = ["arn:aws:iam::111122223333:role/Admin"]

  # Task 1.4a. The sample image the service starts on, passed by digest like every other image.
  gateway_image = "public.ecr.aws/nginx/nginx@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

  issuers = {
    "cognito-poc" = {
      authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
      profile        = "cognito"
      browser_origin = "https://spa-poc.auth.us-east-1.amazoncognito.com"
    }
  }
}

run "the_root_publishes_the_load_balancer_dns_name" {
  command = apply

  assert {
    condition     = output.alb_dns_name == "mock-alb-dns-sentinel"
    error_message = "The root must publish the ALB resource DNS name as alb_dns_name, the contract's albDnsName."
  }
}

# The module's resources are not addressable from a root test, so these read them through the
# plan of a run against the module itself.
run "the_module_builds_one_listener_and_one_target_group" {
  command = apply

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
  }

  assert {
    condition     = aws_lb.cell.internal && aws_lb.cell.load_balancer_type == "application"
    error_message = "The load balancer must be an internal application load balancer."
  }

  assert {
    condition     = aws_lb.cell.preserve_host_header
    error_message = "The Host header must be preserved: the gateway resolves the app from it (ADR-0010)."
  }

  assert {
    condition     = aws_lb.cell.xff_header_processing_mode == "append"
    error_message = "The ALB must append the client address for the gateway's forward limit of one (ADR-0010, N-09)."
  }

  assert {
    condition     = aws_lb_listener.https.ssl_policy == "ELBSecurityPolicy-TLS13-1-2-2021-06"
    error_message = "The listener must pin the selected TLS 1.2/1.3 policy instead of the older provider default."
  }

  assert {
    condition     = aws_lb.cell.drop_invalid_header_fields
    error_message = "Invalid header fields must be dropped at the load balancer."
  }

  # The plan allowlist further down counts resource types, so it cannot see a block inside aws_lb.
  assert {
    condition     = length(aws_lb.cell.access_logs) == 0
    error_message = "Access logs stay off in Phase 1 (Q8 on #98): an entry records the login callback's query string, authorization code and state included (T13, ADR-0007). modules/cell/alb.tf says why."
  }

  assert {
    condition     = length(aws_lb.cell.subnets) == 2
    error_message = "The load balancer must sit in both of the cell's subnets."
  }

  assert {
    condition     = aws_lb.cell.security_groups == toset([aws_security_group.alb.id])
    error_message = "The load balancer must carry the ALB security group of task 1.2 and no other."
  }

  assert {
    condition     = aws_lb.cell.name == "spa-${var.cell}"
    error_message = "The load balancer is named spa-{cell}, which fits the 32-character limit."
  }

  assert {
    condition     = aws_lb_listener.https.port == 443 && aws_lb_listener.https.protocol == "HTTPS"
    error_message = "The one listener is HTTPS on 443."
  }

  assert {
    condition     = aws_lb_listener.https.certificate_arn == data.aws_acm_certificate.wildcard.arn
    error_message = "The listener must present the imported wildcard certificate."
  }

  assert {
    condition     = data.aws_acm_certificate.wildcard.domain == "*.${var.certificate_domain}" && data.aws_acm_certificate.wildcard.tags["Cell"] == var.cell
    error_message = "The certificate is found by its wildcard domain and this cell's Cell tag, the filter certs.sh uses."
  }

  assert {
    condition     = data.aws_acm_certificate.wildcard.types == tolist(["IMPORTED"])
    error_message = "Only an imported certificate is looked up: certs.sh imports it."
  }

  assert {
    condition     = toset(data.aws_acm_certificate.wildcard.key_types) == toset(split(",", regex("keyTypes=([A-Za-z0-9_,]+)", file("../../../../tools/scripts/certs.sh"))[0]))
    error_message = "The certificate lookup must accept exactly the key types certs.sh searches."
  }

  assert {
    condition = (
      length(aws_lb_listener.https.default_action) == 1
      && aws_lb_listener.https.default_action[0].type == "forward"
      && aws_lb_listener.https.default_action[0].target_group_arn == aws_lb_target_group.gateway.arn
    )
    error_message = "The listener forwards every request to the one target group (ADR-0010)."
  }

  assert {
    condition = (
      aws_lb_target_group.gateway.target_type == "ip"
      && aws_lb_target_group.gateway.protocol == "HTTP"
      && aws_lb_target_group.gateway.port == var.gateway_port
    )
    error_message = "The target group sends HTTP to the gateway's port on a Fargate task's address."
  }

  assert {
    condition     = aws_lb_target_group.gateway.health_check[0].path == "/"
    error_message = "The health-check path defaults to /, for the sample image."
  }
}

# Exercise both the naming boundary and port wiring with non-default inputs. The
# ordinary poc/default-port run cannot detect a long-name or hardcoded-port regression.
run "the_longest_cell_and_a_different_gateway_port" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    cell                  = "abcdefghijklmnopqrstuv"
    gateway_port          = 9090
  }

  assert {
    condition     = aws_lb.cell.name == "spa-${var.cell}" && length(aws_lb.cell.name) <= 32 && aws_lb_target_group.gateway.name == aws_lb.cell.name
    error_message = "Both ALB and target-group names must fit AWS's limit at the cell's maximum length."
  }

  assert {
    condition     = aws_lb_target_group.gateway.port == var.gateway_port
    error_message = "The target group must follow a non-default gateway port."
  }
}

run "the_health_check_path_follows_the_variable" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    health_check_path     = "/readyz"
  }

  assert {
    condition     = aws_lb_target_group.gateway.health_check[0].path == var.health_check_path
    error_message = "The health-check path is the variable: /readyz for the gateway."
  }
}

run "a_health_check_path_must_be_absolute" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    health_check_path     = "readyz"
  }

  expect_failures = [var.health_check_path]
}

# #1380. The root passes gateway_writable_root to the module, and its default is the gateway's
# read-only root. platform-tfvars.sh writes true beside the sample's pin, and deploy.sh and
# rollback.sh apply the gateway with false; the module holds what each value gives the container.
# Mutations that turn these two runs red: the root's argument to the module removed (the first); the
# root's default true (the second).
run "the_root_passes_gateway_writable_root_to_the_module" {
  command = plan

  variables {
    gateway_writable_root = true
  }

  assert {
    condition = try(
      one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "gateway"]).readonlyRootFilesystem == false,
      false
    )
    error_message = "gateway_writable_root = true reaches the module: the sample image's gateway container has a writable root (#1380)."
  }
}

run "the_root_s_default_gateway_root_is_read_only" {
  command = plan

  assert {
    condition = try(
      one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "gateway"]).readonlyRootFilesystem == true,
      false
    )
    error_message = "With gateway_writable_root unset the gateway's root is read-only (#787, #1380)."
  }
}

run "a_certificate_domain_carries_no_wildcard" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    # Task 1.4a made these two required in the module, because the root is where a policy bound is
    # decided. The root's own defaults are checked against the contract further down.
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    certificate_domain    = "*.spa-poc.test"
  }

  expect_failures = [var.certificate_domain]
}

run "tags_are_the_ones_the_contract_names" {
  command = plan

  # The first Platform row of the contract is the platform tag; a later one is the bootstrap
  # exemption, which this root must not carry.
  assert {
    condition     = local.tags.Platform == regex("\\| `Platform` \\| `([^`]+)` \\|", file("../../../../contracts/resource-tags.md"))[0]
    error_message = "Platform must be the platform tag in contracts/resource-tags.md: teardown and P-05 match it exactly."
  }

  assert {
    condition     = local.tags.Cell == var.cell
    error_message = "Cell must be the cell name (contracts/resource-tags.md)."
  }

  assert {
    condition     = strcontains(file("../../../../contracts/resource-tags.md"), "| `ManagedBy` | `${local.tags.ManagedBy}`")
    error_message = "ManagedBy must be a value contracts/resource-tags.md names."
  }

  assert {
    condition     = toset(keys(local.tags)) == toset(["Cell", "ManagedBy", "Platform"])
    error_message = "The root applies the contract's three tags and no others."
  }
}

# --- Task 1.4a ---------------------------------------------------------------------------------

# The cell module proves what the ECS resources are (modules/cell/tests/module.tftest.hcl). What is
# this root's own is the four fields it publishes for task 1.7's outputs.json, and the two policy
# bounds it defaults, which have to agree with the configuration contract's own example.
run "the_root_publishes_the_four_fields_task_1_4a_adds_to_the_outputs_contract" {
  command = apply

  assert {
    condition = alltrue([
      for field, value in {
        ecsCluster = output.ecs_cluster
        ecsService = output.ecs_service
        logGroup   = output.log_group
      } : jsondecode(file("../../../../contracts/infra-outputs.example.json"))[field] == value
    ])
    error_message = "ecs_cluster, ecs_service and log_group must be the values contracts/infra-outputs.example.json shows for this cell."
  }

  assert {
    condition     = output.gateway_image == var.gateway_image
    error_message = "gateway_image is the image this apply placed, echoed back so deploy.sh can assert the running digest against it."
  }

  # The contract is additionalProperties: false, so an output with no field in it is a trap for
  # whatever assembles outputs.json. Every output this root publishes has to be one of its fields,
  # under the camelCase spelling the contract uses.
  assert {
    condition = alltrue([
      for name, value in {
        region        = output.region
        cell          = output.cell
        bucket        = output.bucket
        alb_dns_name  = output.alb_dns_name
        ecr_gateway   = output.ecr_gateway
        ecr_mock_api  = output.ecr_mock_api
        ecs_cluster   = output.ecs_cluster
        ecs_service   = output.ecs_service
        log_group     = output.log_group
        gateway_image = output.gateway_image
        # #1167: null on these inputs, and named here all the same, since the name is what the
        # contract has to hold.
        ecs_bastion_service = output.ecs_bastion_service
        } : contains(
        keys(jsondecode(file("../../../../contracts/infra-outputs.schema.json")).properties),
        join("", [for index, part in split("_", name) : index == 0 ? part : title(part)])
      )
    ])
    error_message = "Every output this root publishes must be a field of contracts/infra-outputs.schema.json, in its camelCase spelling."
  }
}

run "the_policy_bounds_this_root_defaults_are_the_contract_s_own_example" {
  command = plan

  # Read from the contract's example rather than restated, so a change there fails here instead of
  # leaving the task environment describing a different proof of concept. These two bound what any
  # registry entry may claim, and a registry writer cannot widen them.
  assert {
    condition     = toset(var.allowed_host_suffixes) == toset(jsondecode(file("../../../../contracts/platform-config.example.json")).Policy.AllowedHostSuffixes)
    error_message = "allowed_host_suffixes must default to contracts/platform-config.example.json -> Policy.AllowedHostSuffixes."
  }

  assert {
    condition     = toset(var.allowed_upstreams) == toset(jsondecode(file("../../../../contracts/platform-config.example.json")).Policy.AllowedUpstreams)
    error_message = "allowed_upstreams must default to contracts/platform-config.example.json -> Policy.AllowedUpstreams."
  }

  # And the pair the apps actually name, which is the same list seen from the other side: the mock
  # API's two listeners are what fixtures/apps/*.json name as their upstreams.
  assert {
    condition = toset(var.allowed_upstreams) == toset(flatten([
      for f in fileset("../../../../fixtures/apps", "*.json") :
      [for api in jsondecode(file("../../../../fixtures/apps/${f}")).apis : api.upstream]
    ]))
    error_message = "allowed_upstreams must be exactly the upstreams the app definitions in fixtures/apps/ name, or an entry names an upstream the policy refuses."
  }
}

# Continuity (#347 Q7). The three inputs #338 gave the cell, and #347's multi_task switch, reach it
# through root variables that default to Phase 1's values, so a tfvars with none of them plans the
# cell G3 ran, and the G6 sitting (#348) sets them in its untracked terraform.tfvars. Read from
# main.tf's source, because a root test cannot address the module's resources: what this root holds
# is that each variable is passed, under its own name, to the one module call.
#
# Mutations that turn this run red: a root default of "dynamodb", or multi_task true; a variable
# declared but not passed; the prefix passed as a literal rather than the variable.
run "the_continuity_inputs_pass_through_the_root_and_default_to_phase_1" {
  command = plan

  assert {
    condition     = !var.multi_task && var.session_store == "memory" && var.data_protection_parameter_prefix == "" && var.data_protection_kms_key_id == ""
    error_message = "The root's continuity inputs default to Phase 1's: one public task, session_store memory, no ring prefix and no key id, so a terraform.tfvars that names none of them plans the one-task cell unchanged."
  }

  assert {
    condition = alltrue([
      for name in ["multi_task", "session_store", "data_protection_parameter_prefix", "data_protection_kms_key_id"] :
      length(regexall("(?m)^  ${name}\\s*=\\s*var\\.${name}$",
        one([for m in regexall("(?ms)^module\\s+\"cell\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) : m[0]])
      )) == 1
    ])
    error_message = "The module call passes multi_task, session_store, data_protection_parameter_prefix and data_protection_kms_key_id through from the root variable of the same name, each on its own line."
  }
}

# The G6 values plan at this root, and reach the cell: the two-task switch, the store, the cell's own
# ring prefix and no key id (#332 D6). The switch's shape is held in the module's suite and P-01's
# run under it; here it is that the switch with the G6 pair plans at this root, on the example's /16
# (the file's variables block). The module's
# validation, which refuses dynamodb with no prefix, is held in its own suite; this run holds that
# the root accepts the pair it documents in terraform.tfvars.example and that the gateway's task
# environment carries it, read through the module's
# continuity_environment output, which the run before this one cannot see. The output reads the
# gateway container's environment out of the task definition's container_definitions, not the
# platform_configuration local that environment is built from, so a cut between the two reaches
# this run (#642).
#
# Mutations that turn this run red: the module call given session_store = "memory" or
# data_protection_parameter_prefix = "" in place of the variable; either one dropped from the call;
# in the module's ecs.tf, the Session__ or DataProtection__ keys filtered out of gateway_environment
# between platform_configuration and the container's environment.
run "the_g6_continuity_inputs_plan_at_this_root" {
  command = plan

  variables {
    multi_task                       = true
    session_store                    = "dynamodb"
    data_protection_parameter_prefix = "/spa-platform/poc/data-protection/"
  }

  assert {
    condition     = module.cell.continuity_environment["Session__Store"] == "dynamodb" && module.cell.continuity_environment["Session__TableName"] != null
    error_message = "With session_store = \"dynamodb\" at the root, the gateway's task environment carries Session:Store dynamodb and a session table."
  }

  assert {
    condition     = module.cell.continuity_environment["DataProtection__ParameterPrefix"] == "/spa-platform/poc/data-protection/" && module.cell.continuity_environment["DataProtection__KmsKeyId"] == null
    error_message = "With the G6 prefix at the root, the gateway's task environment carries this cell's ring prefix and no key id."
  }
}

run "the_mock_api_s_allowlists_come_from_the_apps_root_s_state" {
  command = plan

  # docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries: this root reads two values from
  # the apps root's state, the client ids of apps 1 and 2. App 3 is not among them, which is what
  # keeps onboarding it out of this root's plan (F-07).
  assert {
    condition = local.mock_api_clients == {
      app1 = "1example23456789"
      app2 = "2example23456789"
    }
    error_message = "The two client ids must come from the apps root's state, not from a value typed into this root."
  }

  # The override above carries a third app, exactly as the apps root's state would once
  # fixtures/onboarding/app3.json is applied. The selection has to leave it out, which is what keeps
  # onboarding app 3 out of this root's plan (F-07) and what makes the mock API refuse its call.
  assert {
    condition     = !strcontains(jsonencode(local.mock_api_clients), local.app_clients["app3"].clientId)
    error_message = "App 3's client must not reach the mock API's allowlists: admission is the API owner's decision (contracts/upstream-contract.md), and F-07 logs app 3 in and still expects a refusal."
  }
}

# What main.tf's try() absorbs (Q4 on #98): an apps root state that exists and carries no clients
# output, which is what a destroyed apps root leaves. Both client ids are then empty, which this
# root accepts while mock_api_image is empty and refuses once it is set, as
# a_mock_api_image_with_no_client_ids_is_refused below shows. A
# missing state is the other half and is not absorbed: the read itself fails the plan, before try()
# runs, and a test that overrides the read cannot reach that failure.
run "an_apps_root_state_with_no_clients_output_leaves_the_mock_api_s_allowlists_empty" {
  command = plan

  override_data {
    target = data.terraform_remote_state.apps
    values = {
      outputs = {}
    }
  }

  assert {
    condition = local.mock_api_clients == {
      app1 = ""
      app2 = ""
    }
    error_message = "An apps root state with no clients output must leave both client ids empty, not fail the plan: that is the missing output main.tf's try() absorbs."
  }
}

# --- Change request 1.5: the teardown role and the cell name ------------------------------------

run "the_teardown_principal_is_the_role_bootstrap_creates_looked_up_by_its_derived_name" {
  command = plan

  assert {
    condition     = data.aws_iam_role.teardown.name == "spa-platform-poc-teardown"
    error_message = "The root derives spa-platform-<cell>-teardown, the role infra/bootstrap creates (ADR-0014)."
  }

  assert {
    condition     = local.teardown_principal_arns == ["arn:aws:iam::111122223333:role/spa-platform-poc-teardown"]
    error_message = "With no additional principals, the only teardown principal is the ARN AWS returned for the looked-up role."
  }
}

run "an_additional_teardown_principal_is_added_and_never_replaces_the_role" {
  command = plan

  variables {
    additional_teardown_principal_arns = ["arn:aws:iam::111122223333:role/Operator"]
  }

  assert {
    condition = local.teardown_principal_arns == [
      "arn:aws:iam::111122223333:role/spa-platform-poc-teardown",
      "arn:aws:iam::111122223333:role/Operator",
    ]
    error_message = "additional_teardown_principal_arns is concatenated after the derived role, so it cannot lock out the one principal that can empty the bucket."
  }
}

run "an_additional_teardown_principal_refuses_an_assumed_role_session_arn" {
  command = plan

  variables {
    additional_teardown_principal_arns = ["arn:aws:sts::111122223333:assumed-role/Operator/someone"]
  }

  expect_failures = [var.additional_teardown_principal_arns]
}

# Change request 1.5a: every consumer but infra/bootstrap reads this output rather than deriving the
# bucket's name, so the root publishes the name at plan, and it is the one the contract examples
# render for this file's placeholders. Bootstrap applies before the bucket exists and derives the
# same default; infra/bootstrap's tests check that its teardown grant names the bucket modules/cell
# creates.
run "the_root_publishes_the_bucket_the_contract_examples_render" {
  command = plan

  assert {
    condition     = output.bucket == jsondecode(file("../../../../contracts/infra-outputs.example.json")).bucket
    error_message = "The root's bucket output is spa-platform-<cell>-<account id>-<region>-an, as contracts/infra-outputs.example.json renders it for cell poc in account 111122223333 and us-east-1."
  }
}

run "cell_refuses_pc_at_this_root" {
  command = plan

  variables {
    cell = "pc"
  }

  expect_failures = [var.cell]
}

run "cell_refuses_a_trailing_hyphen_at_this_root" {
  command = plan

  variables {
    cell = "poc-"
  }

  expect_failures = [var.cell]
}


# --- Task 1.9: the platform plan allowlist (P-01) ------------------------------------------------
#
# P-01: "Exactly one ALB, one listener, one target group, one ECS service, one bucket. No NAT
# gateway, database or EC2 instance." (docs/plan/04-tests.md). Plan row 1.9 makes it an allowlist of
# resource types and counts, and ADR-0016 makes it an assertion on the configuration, planned
# against the mock provider above, because CI holds no AWS credentials. What these runs prove is
# the plan the configuration produces, never the account.
#
# They read the configuration's source, because a plan holds no list of its resources that a test
# can enumerate: a test addresses only resources it already knows the names of, and a root test
# cannot address a module's resources at all. What only this root can hold is that modules/cell is
# the whole of its plan, and how many of each type that plan contains.
#
# Each pattern takes a block's labels in quotes, the only form `terraform fmt` leaves: CI's Format
# step and tools/check.sh both run `fmt -check` before any test, and an unquoted label fails it. A
# name is whatever sits between the quotes, since Terraform accepts capitals and hyphens in one
# (modules/cell/tests/network.tftest.hcl cites the rule). Paths are built on path.module, the root
# itself, because Terraform refuses an assertion that refers to nothing in the configuration.

run "the_root_declares_no_resource_and_calls_the_cell_module_once_and_nothing_else" {
  command = plan

  # The next run finds the module's declarations with this same pattern, which is what shows that
  # the zero found here is the root's and not the pattern's.
  assert {
    condition = length(regexall(
      "(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"",
      join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])
    )) == 0
    error_message = "The platform root declares no resource of its own: its plan is modules/cell's, which the next run holds to the allowlist. A resource here, such as a NAT gateway, a rule on the task's group or a policy on its role, is in the plan with no allowlist reading it."
  }

  # A block written on one line holds one argument, and `terraform fmt -check` passes it as it is.
  # The module pattern below and the next two runs' meta-argument patterns end a block at a closing
  # brace in the first column, so they read past a one-line block into the next one, or past the
  # end. Two such blocks passed every run here: module "nat" { source = "./nat" } at the end of
  # versions.tf, which put a NAT gateway in this root's plan; and, with the allowlist raised to two
  # buckets, resource "aws_s3_bucket" "extra" { count = 2 } ahead of the platform bucket in
  # modules/cell, while the plan held three.
  assert {
    condition = alltrue([
      for header in regexall("(?m)^(?:resource|module)\\s[^\\n]*$", join("\n", concat(
        [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
        [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")],
      ))) : endswith(trimspace(header), "{")
    ])
    error_message = "Every resource and module block in the platform root and modules/cell opens a block of its own lines. A one-line block carries its source, count or for_each where the patterns that read them never look."
  }

  # Every pattern in this file finds a declaration where it starts a line. `terraform fmt -check`
  # re-indents a block and fails a comment between its keyword and its labels, but it passes a block
  # comment in front of the keyword: /* rationale */ resource "aws_s3_bucket" "unlisted_probe" {} in
  # modules/cell passed every run here, a second bucket in a plan held to one. So a line that names a
  # resource or module block names it at its start, or is a # or // comment.
  #
  # Terraform has three comment forms (https://developer.hashicorp.com/terraform/language/syntax/configuration#comments).
  # A # or // comment runs to the end of its line, so nothing after one is live; only a /* */ comment
  # can end in front of live text on its line, and inside one a line can start with #. The exemption
  # read /*<newline># rationale */ resource "aws_s3_bucket" "unlisted_probe" {} as a comment, and
  # every run passed with the second bucket. Stripping block comments before matching would mean
  # lexing strings, heredocs and nested interpolation in RE2, because these files hold /* inside
  # strings ("${aws_s3_bucket.platform.arn}/*"), and a lexer that misses one form is the next miss.
  # Every block comment ends in */ (an unterminated one does not parse), so files holding no */ hold
  # no block comment: write comments with #. A string that needs */ is built so the two characters
  # are not adjacent in the source, such as "apps/*${"/"}releases".
  assert {
    condition = !strcontains(join("\n", concat(
      [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
      [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")],
    )), "*/")
    error_message = "The platform root and modules/cell hold no /* */ comment: one can end in front of a declaration on the same line, where no pattern here reads it, and inside one a line can start with #. Write the comment with #."
  }

  assert {
    condition = alltrue([
      for line in regexall("(?m)^[^\\n]*\\b(?:resource|module)\\s+\"[^\\n]*$", join("\n", concat(
        [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")],
        [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")],
      ))) : can(regex("^(?:(?:resource|module)\\s|\\s*(?:#|//))", line))
    ])
    error_message = "Every resource and module block in the platform root and modules/cell starts its own line. A declaration behind a block comment, /* ... */ resource, is in the plan where no pattern here reads it."
  }

  assert {
    condition = [
      for m in regexall("(?ms)^module\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) :
      [m[0], try(regex("(?m)^\\s*source\\s*=\\s*\"([^\"]+)\"", m[1])[0], "")]
    ] == [["cell", "../../../modules/cell"]]
    error_message = "The platform root calls one module, cell, from ../../../modules/cell, and nothing else: another module call brings resources of any type into the plan, with no allowlist naming them."
  }

  # count or for_each on the call multiplies every resource in the module, so "exactly one ALB" would
  # read true of the source and false of the plan.
  assert {
    condition = alltrue([
      for m in regexall("(?ms)^module\\s+\"[^\"]+\"\\s*\\{(.*?)^\\}", join("\n", [for f in fileset(path.module, "*.tf") : file("${path.module}/${f}")])) :
      length(regexall("(?m)^\\s*(count|for_each)\\s*=", m[0])) == 0
    ])
    error_message = "The cell module is called with no count or for_each: either multiplies every resource in it, so one ALB becomes two."
  }

  # Terraform loads *.tf.json beside *.tf (https://developer.hashicorp.com/terraform/language/files),
  # and a block in JSON syntax is invisible to every pattern in this file.
  assert {
    condition     = length(fileset(path.module, "*.tf.json")) == 0 && length(fileset("${path.module}/../../../modules/cell", "*.tf.json")) == 0
    error_message = "The platform root and modules/cell are written in .tf files only: a .tf.json file declares blocks that no source-reading assertion sees."
  }

  assert {
    condition = length(regexall(
      "(?m)^module\\s+\"",
      join("\n", [for f in fileset("${path.module}/../../../modules/cell", "*.tf") : file("${path.module}/../../../modules/cell/${f}")])
    )) == 0
    error_message = "modules/cell calls no child module, so its own .tf files are the whole of what it adds to the plan, which is what the allowlist in the next run reads."
  }
}

# The allowlist: every type the platform plan may hold, and how many of each. The numbers come from
# the plan rows and the decisions recorded on them, not from a count of the source this reads. Row
# 1.2: a VPC, two public subnets in two zones with no NAT, so an internet gateway, a route table, its
# default route and an association per subnet; the ALB's and the task's security groups, with one
# rule into each and one out of each; and the VPC's default group, adopted with no rule (ticket 03,
# question 3). Row 1.3: one load balancer, one listener, one target group. Row 1.5: one bucket,
# private, versioned and TLS only, as its public access block, ownership controls, versioning and
# policy, with the module's default encryption beside them; and two image repositories. Row 1.4: one
# cluster, one task definition, one service and a log group, with a role for the agent and a role
# for the task, each with its one policy. Row P2-4 (#347): behind multi_task, two private subnets
# with a route table, a default route and an association each, one Elastic IP, one NAT gateway and
# two gateway endpoints, all of which plan no instance here. The tunnel's hop (#1167): behind
# bastion, a second service, its task definition, its task role and that role's policy, which plan
# no instance here either; the last run in this file counts them at one each.
#
# A run against the module rather than the root, because only that can address the module's
# resources, and three of them are for_each: their instance counts are in the plan, not the source.
# Every other declaration carries no count or for_each, so it is exactly one instance; the first
# assertion is what makes that true rather than assumed. The run before this one holds that the
# module is the root's whole plan.
#
# Adding a type, or another of one, means editing this map in the same change, which is where the
# author is made to say what the resource is for. The run after this one holds the part of P-01 no
# edit here can change.
run "the_platform_plan_holds_exactly_the_allowlisted_types_and_counts" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]

    # The root's own extra repository (main.tf, #1342): the module creates gateway alone by default,
    # so a run against the module counts the root's two only when it is given the root's set. The
    # root's run on ecr_mock_api holds that main.tf passes this set.
    extra_repositories = ["mock-api"]

    # modules/cell's source, read from the root's directory, which is where terraform test resolves
    # a relative path even in a run against a module.
    cell_source = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  # The two repositories are the root's two, by the keys their state addresses carry, so a key
  # renamed in main.tf, which would destroy one repository and create another, fails here.
  assert {
    condition     = toset(keys(aws_ecr_repository.repository)) == toset(["gateway", "mock-api"])
    error_message = "The platform plan's repositories are gateway and mock-api, the instance keys of module.cell.aws_ecr_repository.repository: a renamed key moves a repository's state address, and the apply destroys it."
  }

  # Only the four declarations whose instances the next assertion counts may carry count or
  # for_each. Two spaces is the canonical indent of a block's own argument, which `fmt -check` keeps,
  # and is what tells a meta-argument from a dynamic block's for_each further in.
  #
  # The session table is read through single instances' names, never as the whole resource, here
  # and below: the provider marks its index's deprecated hash_key, and when an assertion fails,
  # Terraform 1.16 panics printing any reference in it that holds the mark ("value has marks, so it
  # cannot be serialized as JSON"). A name carries no mark, so the failure reads as its message. No
  # reference names [1]: one that names an instance which does not exist beside one that does makes
  # the panic print the whole resource. So the table counts as can() on [0], one or none, which is
  # its whole range: the next run admits its declaration only with count = ... ? 1 : 0, and fails a
  # count of two there.
  assert {
    condition = toset([
      for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.cell_source) :
      "${m[0]}.${m[1]}" if length(regexall("(?m)^  (count|for_each)\\s*=", m[2])) > 0
      ]) == toset(keys({
        "aws_subnet.public"                   = aws_subnet.public
        "aws_route_table_association.public"  = aws_route_table_association.public
        "aws_ecr_repository.repository"       = aws_ecr_repository.repository
        "aws_ecr_lifecycle_policy.repository" = aws_ecr_lifecycle_policy.repository
        "aws_dynamodb_table.sessions"         = try(aws_dynamodb_table.sessions[0].name, null)
        "aws_subnet.private"                  = aws_subnet.private
        "aws_eip.nat"                         = aws_eip.nat
        "aws_nat_gateway.egress"              = aws_nat_gateway.egress
        "aws_route_table.private"             = aws_route_table.private
        "aws_route.private_egress"            = aws_route.private_egress
        "aws_route_table_association.private" = aws_route_table_association.private
        "aws_vpc_endpoint.gateway"            = aws_vpc_endpoint.gateway
        "aws_iam_role.bastion_task"           = aws_iam_role.bastion_task
        "aws_iam_role_policy.bastion_task"    = aws_iam_role_policy.bastion_task
        "aws_ecs_task_definition.bastion"     = aws_ecs_task_definition.bastion
        "aws_ecs_service.bastion"             = aws_ecs_service.bastion
    }))
    error_message = "Only the subnets, their route table associations, the image repositories and their lifecycle policies, the session table, the two-task cell's gated egress (#347) and the bastion's four declarations (#1167) may carry count or for_each: every other declaration is one instance, and that is how the next assertion counts it. A new count or for_each needs its instances counted there."
  }

  # The session table is counted at its planned instances, which on these inputs, Phase 1's, is none
  # (change request issue-338-p-01-dynamodb-tripwire, decision 1). The run after the next one holds
  # it at one under session_store = "dynamodb".
  assert {
    condition = {
      for type in distinct([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) : m[0]]) :
      type => sum([
        for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) :
        lookup({
          "aws_subnet.public"                   = length(aws_subnet.public)
          "aws_route_table_association.public"  = length(aws_route_table_association.public)
          "aws_ecr_repository.repository"       = length(aws_ecr_repository.repository)
          "aws_ecr_lifecycle_policy.repository" = length(aws_ecr_lifecycle_policy.repository)
          "aws_dynamodb_table.sessions"         = can(aws_dynamodb_table.sessions[0].name) ? 1 : 0
          "aws_subnet.private"                  = length(aws_subnet.private)
          "aws_eip.nat"                         = length(aws_eip.nat)
          "aws_nat_gateway.egress"              = length(aws_nat_gateway.egress)
          "aws_route_table.private"             = length(aws_route_table.private)
          "aws_route.private_egress"            = length(aws_route.private_egress)
          "aws_route_table_association.private" = length(aws_route_table_association.private)
          "aws_vpc_endpoint.gateway"            = length(aws_vpc_endpoint.gateway)
          "aws_iam_role.bastion_task"           = length(aws_iam_role.bastion_task)
          "aws_iam_role_policy.bastion_task"    = length(aws_iam_role_policy.bastion_task)
          "aws_ecs_task_definition.bastion"     = length(aws_ecs_task_definition.bastion)
          "aws_ecs_service.bastion"             = length(aws_ecs_service.bastion)
        }, "${m[0]}.${m[1]}", 1)
        if m[0] == type
      ])
      } == {
      aws_vpc                                            = 1
      aws_internet_gateway                               = 1
      aws_subnet                                         = 2
      aws_route_table                                    = 1
      aws_route                                          = 1
      aws_route_table_association                        = 2
      aws_eip                                            = 0
      aws_nat_gateway                                    = 0
      aws_vpc_endpoint                                   = 0
      aws_security_group                                 = 2
      aws_vpc_security_group_ingress_rule                = 2
      aws_vpc_security_group_egress_rule                 = 2
      aws_default_security_group                         = 1
      aws_lb                                             = 1
      aws_lb_listener                                    = 1
      aws_lb_target_group                                = 1
      aws_s3_bucket                                      = 1
      aws_s3_bucket_versioning                           = 1
      aws_s3_bucket_public_access_block                  = 1
      aws_s3_bucket_ownership_controls                   = 1
      aws_s3_bucket_server_side_encryption_configuration = 1
      aws_s3_bucket_policy                               = 1
      aws_ecr_repository                                 = 2
      aws_ecr_lifecycle_policy                           = 2
      aws_ecs_cluster                                    = 1
      aws_ecs_task_definition                            = 1
      aws_ecs_service                                    = 1
      aws_cloudwatch_log_group                           = 1
      aws_iam_role                                       = 2
      aws_iam_role_policy                                = 2
      aws_dynamodb_table                                 = 0
    }
    error_message = "The platform plan holds a type the allowlist does not name, or a different number of one it does (plan row 1.9, P-01). Name the type and its count in this map, in the same change, and say what it is for."
  }
}

# P-01's own sentence, held apart from the allowlist: an edit that adds a NAT gateway to the map
# above, or a second load balancer, still fails here, because P-01 forbids them and the allowlist's
# author does not get to decide otherwise. The five types are counted from the declarations, which
# the run before this one shows are single instances unless they are the four with count or
# for_each; none of the five is.
#
# The forbidden types are P-01's three words made specific. A NAT gateway. An EC2 instance, or the
# capacity that launches one: a launch template or configuration, an Auto Scaling group, a fleet or
# a spot request, a dedicated host, an ECS capacity provider (it exists only to put tasks on
# instances), an EKS node group, an EMR cluster or a Lightsail instance. A database, by every AWS
# database service's resource prefix; aws_dynamodb_table among them is a Phase 1 tripwire, because
# Session.Store admits dynamodb only in Phase 2 (Q81 in
# .scratch/overnight/questions-2026-10-15-answered.md, answered yes). The list is a floor, not a
# closure: the allowlist above is the closure, and this is what an edit to it cannot lower.
#
# One declaration is exempt, the session table (#338), and only in the form that plans no instance
# on Phase 1's inputs: aws_dynamodb_table "sessions", whose count is exactly
# local.session_store_is_dynamodb ? 1 : 0. The plan assertion after the tripwire holds that it is
# none here. Mutations that turn this run red: the count made ? 2 : 0, or written as
# var.session_store == "dynamodb" ? 1 : 0; a second DynamoDB declaration with the same count line;
# the condition inverted, so the default inputs plan one table. So what this run stops proving is
# that the module's source names no DynamoDB table; what it goes on proving is P-01's sentence, that
# the Phase 1 plan holds no database (change request issue-338-p-01-dynamodb-tripwire, decision 1).
#
# The second exemption is the NAT gateway (#347), in the same form: aws_nat_gateway "egress", whose
# count is exactly var.multi_task ? 1 : 0, and a plan assertion that the default inputs plan none.
# Mutations that turn this run red: the count made ? 2 : 0, or written as local.multi_task ? 1 : 0;
# a second NAT gateway with the same count line; the condition inverted.
#
# The bastion (#1167) is a second ECS service, so it is exempt from the one-of-each assertion in the
# same form: aws_ecs_service "bastion", whose count is exactly var.bastion ? 1 : 0, and a plan
# assertion that the default inputs plan none. P-01's "one ECS service" is read as holding on the
# default inputs, as #583 read the NAT gateway (#1167, the decision's reading of P-01). Mutations
# that turn this run red: the count made ? 2 : 0; the bastion input's default made true; a second
# aws_ecs_service with no count, or with the same count line.
run "the_platform_plan_holds_one_alb_listener_target_group_service_and_bucket_and_no_nat_gateway_instance_or_database" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    cell_source           = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  # One declaration of each, carrying no count or for_each, is exactly one instance of each. The
  # one exemption is the bastion service (#1167), which is left out of the list only when its
  # count is exactly the bastion switch.
  assert {
    condition = alltrue([
      for type in ["aws_lb", "aws_lb_listener", "aws_lb_target_group", "aws_ecs_service", "aws_s3_bucket"] : [
        for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.cell_source) :
        length(regexall("(?m)^  (count|for_each)\\s*=", m[2])) if m[0] == type
        && !(m[0] == "aws_ecs_service" && m[1] == "bastion"
        && length(regexall("(?m)^  count\\s*=\\s*var\\.bastion \\? 1 : 0$", m[2])) == 1)
      ] == [0]
    ])
    error_message = "P-01: exactly one ALB, one listener, one target group, one ECS service and one bucket on the default inputs. Each is one declaration with no count or for_each. The one exemption is aws_ecs_service \"bastion\" with exactly count = var.bastion ? 1 : 0 (#1167)."
  }

  # And on the default inputs, which are Phase 1's, the exempted bastion service plans no instance.
  assert {
    condition     = length(aws_ecs_service.bastion) == 0
    error_message = "P-01: on the default inputs the platform plan holds one ECS service. Only bastion = true creates a second (#1167)."
  }

  # The exemption reads the declaration's body, so it is matched against whole blocks; the first run
  # holds that every block opens on its own lines, so the block pattern finds each declaration the
  # header pattern would. The count line is matched at the two-space indent `fmt -check` keeps, so a
  # dynamic block's for_each further in cannot stand in for it.
  assert {
    condition = [
      for m in regexall("(?ms)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"\\s*\\{(.*?)^\\}", var.cell_source) : m[0]
      if can(regex("^aws_(nat_gateway|instance|spot_instance_request|spot_fleet_request|ec2_fleet|ec2_host|launch_template|launch_configuration|autoscaling_group|ecs_capacity_provider|eks_node_group|emr_cluster|lightsail_instance|db_.+|rds_.+|dynamodb_.+|docdb.*|neptune_.+|elasticache_.+|memorydb_.+|redshift.*|dsql_.+|keyspaces_.+|timestreamwrite_.+|opensearch_domain|elasticsearch_domain)$", m[0]))
      && !(m[0] == "aws_dynamodb_table" && m[1] == "sessions"
      && length(regexall("(?m)^  count\\s*=\\s*local\\.session_store_is_dynamodb \\? 1 : 0$", m[2])) == 1)
      && !(m[0] == "aws_nat_gateway" && m[1] == "egress"
      && length(regexall("(?m)^  count\\s*=\\s*var\\.multi_task \\? 1 : 0$", m[2])) == 1)
    ] == []
    error_message = "P-01: no NAT gateway, database or EC2 instance in the platform plan. The cell sends 443 out from the task's public address (plan row 1.2), runs on Fargate (plan row 1.4) and keeps its state in S3 (contracts/s3-layout.md). The two exemptions are aws_dynamodb_table \"sessions\" with exactly count = local.session_store_is_dynamodb ? 1 : 0, and aws_nat_gateway \"egress\" with exactly count = var.multi_task ? 1 : 0 (#347)."
  }

  # And on the default inputs, which are Phase 1's, the exempted table plans no instance.
  assert {
    condition     = !can(aws_dynamodb_table.sessions[0].name)
    error_message = "P-01: on the default inputs the platform plan holds no session table. Only session_store = \"dynamodb\" creates one."
  }

  # And on the default inputs, which are Phase 1's, the exempted NAT gateway plans no instance.
  assert {
    condition     = length(aws_nat_gateway.egress) == 0
    error_message = "P-01: on the default inputs the platform plan holds no NAT gateway. Only multi_task = true creates one (#347)."
  }
}

# The gate opens to one table and nothing else: under session_store = "dynamodb", with the cell's
# ring prefix its validation requires, the plan holds exactly one session table and every other
# count is the Phase 1 run's (change request issue-338-p-01-dynamodb-tripwire, decision 1).
#
# Mutations that turn this run red: the table's condition inverted (count =
# local.session_store_is_dynamodb ? 0 : 1); a second aws_dynamodb_table declaration with the same
# count line. A count of two fails the tripwire's exemption in the run before this one.
run "under_dynamodb_the_platform_plan_holds_one_session_table_and_every_other_count_unchanged" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes            = [".spa-poc.test:8443"]
    allowed_upstreams                = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    extra_repositories               = ["mock-api"]
    session_store                    = "dynamodb"
    data_protection_parameter_prefix = "/spa-platform/poc/data-protection/"
    cell_source                      = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  assert {
    condition = {
      for type in distinct([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) : m[0]]) :
      type => sum([
        for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) :
        lookup({
          "aws_subnet.public"                   = length(aws_subnet.public)
          "aws_route_table_association.public"  = length(aws_route_table_association.public)
          "aws_ecr_repository.repository"       = length(aws_ecr_repository.repository)
          "aws_ecr_lifecycle_policy.repository" = length(aws_ecr_lifecycle_policy.repository)
          "aws_dynamodb_table.sessions"         = can(aws_dynamodb_table.sessions[0].name) ? 1 : 0
          "aws_subnet.private"                  = length(aws_subnet.private)
          "aws_eip.nat"                         = length(aws_eip.nat)
          "aws_nat_gateway.egress"              = length(aws_nat_gateway.egress)
          "aws_route_table.private"             = length(aws_route_table.private)
          "aws_route.private_egress"            = length(aws_route.private_egress)
          "aws_route_table_association.private" = length(aws_route_table_association.private)
          "aws_vpc_endpoint.gateway"            = length(aws_vpc_endpoint.gateway)
          "aws_iam_role.bastion_task"           = length(aws_iam_role.bastion_task)
          "aws_iam_role_policy.bastion_task"    = length(aws_iam_role_policy.bastion_task)
          "aws_ecs_task_definition.bastion"     = length(aws_ecs_task_definition.bastion)
          "aws_ecs_service.bastion"             = length(aws_ecs_service.bastion)
        }, "${m[0]}.${m[1]}", 1)
        if m[0] == type
      ])
      } == {
      aws_vpc                                            = 1
      aws_internet_gateway                               = 1
      aws_subnet                                         = 2
      aws_route_table                                    = 1
      aws_route                                          = 1
      aws_route_table_association                        = 2
      aws_eip                                            = 0
      aws_nat_gateway                                    = 0
      aws_vpc_endpoint                                   = 0
      aws_security_group                                 = 2
      aws_vpc_security_group_ingress_rule                = 2
      aws_vpc_security_group_egress_rule                 = 2
      aws_default_security_group                         = 1
      aws_lb                                             = 1
      aws_lb_listener                                    = 1
      aws_lb_target_group                                = 1
      aws_s3_bucket                                      = 1
      aws_s3_bucket_versioning                           = 1
      aws_s3_bucket_public_access_block                  = 1
      aws_s3_bucket_ownership_controls                   = 1
      aws_s3_bucket_server_side_encryption_configuration = 1
      aws_s3_bucket_policy                               = 1
      aws_ecr_repository                                 = 2
      aws_ecr_lifecycle_policy                           = 2
      aws_ecs_cluster                                    = 1
      aws_ecs_task_definition                            = 1
      aws_ecs_service                                    = 1
      aws_cloudwatch_log_group                           = 1
      aws_iam_role                                       = 2
      aws_iam_role_policy                                = 2
      aws_dynamodb_table                                 = 1
    }
    error_message = "Under session_store = \"dynamodb\" the platform plan holds exactly one session table, and every other type at its Phase 1 count."
  }
}

# Under the switch the plan is D6's (#347): two more subnets, a route table, a route and an
# association for each, one Elastic IP, one NAT gateway and two gateway endpoints, with the session
# table the switch requires, and every other type at its Phase 1 count. Mutations that turn this run
# red: a NAT gateway per zone (aws_nat_gateway 2); the endpoints on one table only does not change a
# count here and is held by the module's own suite; the switch left out of desired_count is held
# there too.
run "under_multi_task_the_platform_plan_holds_d6_s_egress_and_every_other_count_unchanged" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes            = [".spa-poc.test:8443"]
    allowed_upstreams                = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    extra_repositories               = ["mock-api"]
    multi_task                       = true
    session_store                    = "dynamodb"
    data_protection_parameter_prefix = "/spa-platform/poc/data-protection/"
    cell_source                      = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  assert {
    condition = {
      for type in distinct([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) : m[0]]) :
      type => sum([
        for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) :
        lookup({
          "aws_subnet.public"                   = length(aws_subnet.public)
          "aws_route_table_association.public"  = length(aws_route_table_association.public)
          "aws_ecr_repository.repository"       = length(aws_ecr_repository.repository)
          "aws_ecr_lifecycle_policy.repository" = length(aws_ecr_lifecycle_policy.repository)
          "aws_dynamodb_table.sessions"         = can(aws_dynamodb_table.sessions[0].name) ? 1 : 0
          "aws_subnet.private"                  = length(aws_subnet.private)
          "aws_eip.nat"                         = length(aws_eip.nat)
          "aws_nat_gateway.egress"              = length(aws_nat_gateway.egress)
          "aws_route_table.private"             = length(aws_route_table.private)
          "aws_route.private_egress"            = length(aws_route.private_egress)
          "aws_route_table_association.private" = length(aws_route_table_association.private)
          "aws_vpc_endpoint.gateway"            = length(aws_vpc_endpoint.gateway)
          "aws_iam_role.bastion_task"           = length(aws_iam_role.bastion_task)
          "aws_iam_role_policy.bastion_task"    = length(aws_iam_role_policy.bastion_task)
          "aws_ecs_task_definition.bastion"     = length(aws_ecs_task_definition.bastion)
          "aws_ecs_service.bastion"             = length(aws_ecs_service.bastion)
        }, "${m[0]}.${m[1]}", 1)
        if m[0] == type
      ])
      } == {
      aws_vpc                                            = 1
      aws_internet_gateway                               = 1
      aws_subnet                                         = 4
      aws_route_table                                    = 3
      aws_route                                          = 3
      aws_route_table_association                        = 4
      aws_eip                                            = 1
      aws_nat_gateway                                    = 1
      aws_vpc_endpoint                                   = 2
      aws_security_group                                 = 2
      aws_vpc_security_group_ingress_rule                = 2
      aws_vpc_security_group_egress_rule                 = 2
      aws_default_security_group                         = 1
      aws_lb                                             = 1
      aws_lb_listener                                    = 1
      aws_lb_target_group                                = 1
      aws_s3_bucket                                      = 1
      aws_s3_bucket_versioning                           = 1
      aws_s3_bucket_public_access_block                  = 1
      aws_s3_bucket_ownership_controls                   = 1
      aws_s3_bucket_server_side_encryption_configuration = 1
      aws_s3_bucket_policy                               = 1
      aws_ecr_repository                                 = 2
      aws_ecr_lifecycle_policy                           = 2
      aws_ecs_cluster                                    = 1
      aws_ecs_task_definition                            = 1
      aws_ecs_service                                    = 1
      aws_cloudwatch_log_group                           = 1
      aws_iam_role                                       = 2
      aws_iam_role_policy                                = 2
      aws_dynamodb_table                                 = 1
    }
    error_message = "Under multi_task = true the platform plan holds D6's egress, one NAT gateway with its Elastic IP, two private subnets with their tables, routes and associations, and the two gateway endpoints, with the session table, and every other type at its Phase 1 count (#347)."
  }
}

# Under the bastion switch the plan holds the tunnel's hop (#1167): the bastion's service, its task
# definition, its task role and that role's policy, one of each, and every other type at its Phase 1
# count. With every other input at its default, so the one change from the Phase 1 map is the
# switch's own. Mutations that turn this run red: the service's count made ? 2 : 0; a second role
# policy on the bastion role. The four left out of this run's lookup map leave it green, since the
# map's default of one is each one's count under the switch; left out of the three maps above,
# they turn those runs red, where each plans none.
#
# The role is counted through its first instance's name, as the session table is above: the
# provider marks aws_iam_role's deprecated inline_policy and managed_policy_arns, and when an
# assertion that names the whole role fails, Terraform 1.16 panics printing it ("value has marks, so
# it cannot be serialized as JSON"), where a name carries no mark. Seen on this run with a wrong
# expected count. So this run holds the role at one or none; that its count is exactly
# var.bastion ? 1 : 0, never two, is held on the source by the module's suite
# (the_bastion_s_four_declarations_each_carry_exactly_the_bastion_switch).
run "under_bastion_the_platform_plan_holds_the_bastion_service_and_every_other_count_unchanged" {
  command = plan

  module {
    source = "../../../modules/cell"
  }

  variables {
    allowed_host_suffixes = [".spa-poc.test:8443"]
    allowed_upstreams     = ["http://127.0.0.1:5080", "http://127.0.0.1:5081"]
    extra_repositories    = ["mock-api"]
    bastion               = true
    bastion_image         = "public.ecr.aws/nginx/nginx-unprivileged@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    cell_source           = join("\n", [for f in fileset("../../../modules/cell", "*.tf") : file("../../../modules/cell/${f}")])
  }

  assert {
    condition = {
      for type in distinct([for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) : m[0]]) :
      type => sum([
        for m in regexall("(?m)^resource\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"", var.cell_source) :
        lookup({
          "aws_subnet.public"                   = length(aws_subnet.public)
          "aws_route_table_association.public"  = length(aws_route_table_association.public)
          "aws_ecr_repository.repository"       = length(aws_ecr_repository.repository)
          "aws_ecr_lifecycle_policy.repository" = length(aws_ecr_lifecycle_policy.repository)
          "aws_dynamodb_table.sessions"         = can(aws_dynamodb_table.sessions[0].name) ? 1 : 0
          "aws_subnet.private"                  = length(aws_subnet.private)
          "aws_eip.nat"                         = length(aws_eip.nat)
          "aws_nat_gateway.egress"              = length(aws_nat_gateway.egress)
          "aws_route_table.private"             = length(aws_route_table.private)
          "aws_route.private_egress"            = length(aws_route.private_egress)
          "aws_route_table_association.private" = length(aws_route_table_association.private)
          "aws_vpc_endpoint.gateway"            = length(aws_vpc_endpoint.gateway)
          "aws_iam_role.bastion_task"           = can(aws_iam_role.bastion_task[0].name) ? 1 : 0
          "aws_iam_role_policy.bastion_task"    = length(aws_iam_role_policy.bastion_task)
          "aws_ecs_task_definition.bastion"     = length(aws_ecs_task_definition.bastion)
          "aws_ecs_service.bastion"             = length(aws_ecs_service.bastion)
        }, "${m[0]}.${m[1]}", 1)
        if m[0] == type
      ])
      } == {
      aws_vpc                                            = 1
      aws_internet_gateway                               = 1
      aws_subnet                                         = 2
      aws_route_table                                    = 1
      aws_route                                          = 1
      aws_route_table_association                        = 2
      aws_eip                                            = 0
      aws_nat_gateway                                    = 0
      aws_vpc_endpoint                                   = 0
      aws_security_group                                 = 2
      aws_vpc_security_group_ingress_rule                = 2
      aws_vpc_security_group_egress_rule                 = 2
      aws_default_security_group                         = 1
      aws_lb                                             = 1
      aws_lb_listener                                    = 1
      aws_lb_target_group                                = 1
      aws_s3_bucket                                      = 1
      aws_s3_bucket_versioning                           = 1
      aws_s3_bucket_public_access_block                  = 1
      aws_s3_bucket_ownership_controls                   = 1
      aws_s3_bucket_server_side_encryption_configuration = 1
      aws_s3_bucket_policy                               = 1
      aws_ecr_repository                                 = 2
      aws_ecr_lifecycle_policy                           = 2
      aws_ecs_cluster                                    = 1
      aws_ecs_task_definition                            = 2
      aws_ecs_service                                    = 2
      aws_cloudwatch_log_group                           = 1
      aws_iam_role                                       = 3
      aws_iam_role_policy                                = 3
      aws_dynamodb_table                                 = 0
    }
    error_message = "Under bastion = true the platform plan holds two ECS services, two task definitions, three roles and three role policies, and every other type at its Phase 1 count (#1167)."
  }
}

# --- #1342: the mock API, this root's extra container ---------------------------------------------
#
# The cell module holds no fixture knowledge: it takes extra containers and extra repositories, and
# this root builds the mock API's container definition and passes it as the one extra container
# (main.tf). So the mock API's cases are this suite's. Each reads what the module was given back out
# of the task definition's container_definitions, through the module's container_definitions output,
# since a root test cannot address the module's resources; the root's local alone would not show
# that the definition reached the task. Expected values are read from the files the mock API answers
# to: fixtures/MockApi/Dockerfile for its keys, and this suite's override of the apps root's state
# for its client ids.

run "the_mock_api_container_is_absent_until_its_image_is_supplied" {
  command = plan

  assert {
    condition     = [for c in jsondecode(module.cell.container_definitions) : c.name] == ["gateway"]
    error_message = "With no mock API image the task holds the gateway alone, so the service applies before that image exists."
  }
}

# Mutations that turn this run red: the module call's extra_containers left empty; the issuer or a
# client id taken from the wrong place; a fourth MockApi__ key, or one of the three dropped; the
# mock API placed before the gateway.
run "the_mock_api_container_joins_with_the_client_ids_it_is_given" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
  }

  # After the gateway's, as the module appends an extra container.
  assert {
    condition     = [for c in jsondecode(module.cell.container_definitions) : c.name] == ["gateway", "mock-api"]
    error_message = "Supplying the mock API image puts its container in the task after the gateway's."
  }

  # The keys are read out of the image's own Dockerfile, which declares the three the process refuses
  # to start without (fixtures/MockApi/Program.cs -> Required).
  assert {
    condition = (
      toset(keys({
        for e in one([
          for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"
        ]).environment : e.name => e.value
      }))
      == toset([
        for m in regexall("(?m)^ENV (MockApi__[A-Za-z0-9]+)=", file("../../../../fixtures/MockApi/Dockerfile")) : m[0]
      ])
    )
    error_message = "The mock API container carries exactly the environment keys fixtures/MockApi/Dockerfile declares; the process refuses to start without them."
  }

  assert {
    condition = alltrue([
      for key, expected in {
        "MockApi__App1Client" = "1example23456789"
        "MockApi__App2Client" = "2example23456789"
        "MockApi__Issuer"     = values(var.issuers)[0].authority
        } : one([
          for e in one([
            for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"
          ]).environment : e if e.name == key
      ]).value == expected
    ])
    error_message = "Each listener's admitted client is app 1's or app 2's from the apps root's state, and the issuer is the one issuer: nothing about a client id is written into this root (contracts/upstream-contract.md -> One token per app)."
  }

  assert {
    condition = (
      one([
        for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"
      ]).image == var.mock_api_image
    )
    error_message = "The mock API's image is the variable it was given, so a routine apply never moves it."
  }
}

# fixtures/MockApi/Program.cs throws on a missing client id, so the alternative to this validation is
# a container that crash-loops after the apply reported success. The apps root's state here carries
# no clients output, which is what a destroyed apps root leaves.
run "a_mock_api_image_with_no_client_ids_is_refused" {
  command = plan

  override_data {
    target = data.terraform_remote_state.apps
    values = {
      outputs = {}
    }
  }

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
  }

  expect_failures = [var.mock_api_image]
}

# The one-issuer check (assumption A4): the mock API validates against one issuer, so a second one
# fails the plan rather than leaving the root to hand it the alphabetically first.
run "a_mock_api_image_with_two_issuers_is_refused" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
    issuers = {
      "cognito-poc" = {
        authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
        profile        = "cognito"
        browser_origin = "https://spa-poc.auth.us-east-1.amazoncognito.com"
      }
      "cognito-other" = {
        authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_OTHER"
        profile        = "cognito"
        browser_origin = "https://spa-other.auth.us-east-1.amazoncognito.com"
      }
    }
  }

  expect_failures = [var.mock_api_image]
}

# And the check is the mock API's alone: two issuers with no mock API image plan, so the run before
# this one fails on the image's rule and not on the issuers.
run "two_issuers_with_no_mock_api_image_plan" {
  command = plan

  variables {
    issuers = {
      "cognito-poc" = {
        authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_EXAMPLE"
        profile        = "cognito"
        browser_origin = "https://spa-poc.auth.us-east-1.amazoncognito.com"
      }
      "cognito-other" = {
        authority      = "https://cognito-idp.us-east-1.amazonaws.com/us-east-1_OTHER"
        profile        = "cognito"
        browser_origin = "https://spa-other.auth.us-east-1.amazoncognito.com"
      }
    }
  }

  assert {
    condition     = [for c in jsondecode(module.cell.container_definitions) : c.name] == ["gateway"]
    error_message = "Two issuers with no mock API image plan a task of the gateway alone: the one-issuer rule is the mock API's."
  }
}

# #470: the image is <repository>@sha256:<64 lowercase hex> or the plan stops. The client ids are
# given by the suite's state override, so the image's own rule is the only one that can stop these.
run "a_mock_api_image_with_an_empty_digest_is_refused" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@"
  }

  expect_failures = [var.mock_api_image]
}

run "a_mock_api_image_named_by_tag_is_refused" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api:latest"
  }

  expect_failures = [var.mock_api_image]
}

run "a_mock_api_image_with_a_short_digest_is_refused" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef01"
  }

  expect_failures = [var.mock_api_image]
}

# And the full form passes for both, in the two registries the cell pulls from: ECR Public for the
# sample image, the cell's own repositories afterwards.
run "full_digest_references_pass_for_both_images" {
  command = plan

  variables {
    gateway_image  = "public.ecr.aws/nginx/nginx-unprivileged@sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
  }

  assert {
    condition = (
      toset([for c in jsondecode(module.cell.container_definitions) : c.image])
      == toset([var.gateway_image, var.mock_api_image])
    )
    error_message = "A full digest reference for each image passes the plan, and each container runs the reference it was given."
  }
}

# Read out of the declaration, because no assertion on a value can show the absence of a default.
# The match is on the argument at the block's own indent, not on the word.
run "the_mock_api_image_defaults_to_absent_never_to_a_tag" {
  command = plan

  assert {
    condition = length(regexall(
      "(?m)^  default\\s*= \"\"$",
      one(regexall("(?ms)^variable \"mock_api_image\" \\{.*?^\\}", file("${path.module}/variables.tf")))
    )) == 1
    error_message = "mock_api_image defaults to absent, so the service applies before that image is built; a tag-naming default would move it instead."
  }
}

# The mock API halves of the module's container runs (#786, #787, #1006, #347). The module adds
# nothing to an extra container, so the mock API's hardening is this root's to write, and these hold
# it. Its linuxParameters are held equal to the gateway's as the module planned them, so the two
# cannot drift. try() so a missing key fails rather than errors.
#
# Mutations that turn this run red: readonlyRootFilesystem false or removed; a mount point on the
# mock API; portMappings added; stopTimeout added; drop emptied or ["NET_RAW"]; an add list naming
# SYS_PTRACE; initProcessEnabled false or removed; capabilities.add, systemControls or volumesFrom
# removed, or given an entry.
run "the_mock_api_container_is_read_only_drops_every_capability_and_spells_out_ecs_values" {
  command = plan

  variables {
    mock_api_image = "111122223333.dkr.ecr.us-east-1.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
  }

  # #787: read-only, explicitly, and with no writable path at all: #780's third answer names none
  # for the mock API. ECS reads an absent key as writable.
  assert {
    condition = try(
      one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).readonlyRootFilesystem == true
      && one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).mountPoints == [],
      false
    )
    error_message = "The mock API's root filesystem is read-only and it mounts nothing (#787; #780, answer 3): its read-only root is the whole of its filesystem."
  }

  # #786: every capability Docker grants dropped, none added, and an init process as PID 1, the
  # gateway's own value.
  assert {
    condition = try(
      one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).linuxParameters == {
        capabilities       = { add = [], drop = ["ALL"] }
        initProcessEnabled = true
      }
      && one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).linuxParameters
      == one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "gateway"]).linuxParameters,
      false
    )
    error_message = "The mock API drops ALL capabilities, adds none and runs an init process (#786), the gateway's linuxParameters exactly: the process needs no capability, and the init forwards SIGTERM to dotnet."
  }

  # #1006: the values ECS fills in, spelled out, so a re-plan after an apply replaces nothing.
  assert {
    condition = try(
      one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).systemControls == []
      && one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).volumesFrom == [],
      false
    )
    error_message = "The mock API spells out systemControls and volumesFrom as the empty lists ECS reads back (#1006)."
  }

  # No port: the listeners are on loopback inside the task. No stopTimeout: the mock API holds no
  # session and no lease, so it keeps Fargate's default (#347 Q6), and only the gateway's is F1's.
  assert {
    condition = (
      !can(one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).portMappings)
      && !can(one([for c in jsondecode(module.cell.container_definitions) : c if c.name == "mock-api"]).stopTimeout)
    )
    error_message = "The mock API publishes no port and sets no stopTimeout (#347 Q6): its listeners are reached on loopback, and it keeps Fargate's default."
  }
}

# The proof cell's plan is unchanged by #1342. With the inputs of terraform.tfvars.example (us-east-2,
# cell poc, the sample image by digest) and a mock API image, the task definition's
# container_definitions is held byte for byte to what this root planned before the mock API left the
# module, by its SHA-256. The digest was read at the fork of #1342's branch,
# 438d80e674eddd7d12532443f8b9a8636c77d901, by planning modules/cell's mock_api_image inputs with
# the client ids and issuer below; the module's container definitions then were what this root
# planned. Not at c3fa0e0, the ticket's reading point: #787 and #1168 changed the definition since
# (a read-only root, the /tmp volume), and that plan's digest, c85aafb0..., is no longer this cell's.
#
# A change to either container's definition, in this root or in the module, fails here, which is the
# point: the operator's next plan of this root would show it. Change the digest only with the change
# that means to move the task, and say so in that change.
run "with_the_example_inputs_and_a_mock_api_image_the_task_s_containers_are_the_ones_planned_before_1342" {
  command = plan

  override_data {
    target          = data.aws_region.current
    override_during = plan
    values          = { region = "us-east-2" }
  }

  override_data {
    target          = module.cell.data.aws_region.current
    override_during = plan
    values          = { region = "us-east-2" }
  }

  variables {
    region                             = "us-east-2"
    cell                               = "poc"
    vpc_cidr                           = "10.20.0.0/16"
    certificate_domain                 = "spa-poc.test"
    health_check_path                  = "/"
    additional_teardown_principal_arns = []
    gateway_image                      = "public.ecr.aws/nginx/nginx-unprivileged@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    mock_api_image                     = "111122223333.dkr.ecr.us-east-2.amazonaws.com/spa-poc/mock-api@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789"
  }

  assert {
    condition     = sha256(module.cell.container_definitions) == "43997e19d3768461462dac92128f270df55c88b799b279e159bee24b229f7c00"
    error_message = "The task definition's container_definitions are not the bytes this root planned before #1342 with these inputs: the operator's next plan of the platform root would replace the task definition and roll the service. Its digest is ${sha256(module.cell.container_definitions)}."
  }
}

# The root's ecr_mock_api output, which tools/scripts/cell_documents.py maps to the contract's
# ecrMockApi and verify-release-immutability.sh reads, is the module's repository at mock-api, and
# the module creates exactly the root's two. An apply on the mock provider, so the repository URLs
# exist; the mock gives each repository its own generated URL, so a root that read another key, or
# the gateway's output, differs from the one named.
run "the_root_s_ecr_mock_api_is_the_module_s_repository_at_mock_api" {
  command = apply

  assert {
    condition     = toset(keys(module.cell.ecr_repositories)) == toset(["gateway", "mock-api"])
    error_message = "The module creates the root's two repositories, gateway and mock-api, keyed by name: main.tf passes mock-api as its one extra repository."
  }

  assert {
    condition     = output.ecr_mock_api == module.cell.ecr_repositories["mock-api"] && output.ecr_mock_api != output.ecr_gateway
    error_message = "ecr_mock_api, the contract's ecrMockApi, reads the module's ecr_repositories at mock-api, which is not the gateway's repository."
  }

  assert {
    condition     = output.ecr_gateway == module.cell.ecr_repositories["gateway"]
    error_message = "ecr_gateway, the contract's ecrGateway, stays the gateway repository."
  }
}
