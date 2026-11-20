# The platform root: the cell's runtime resources, one instance of modules/cell.
#
# Tasks 1.2, 1.3 and 1.5 are in place here: the network, the internal load
# balancer with its one listener and target group, the bucket and the image
# repositories. The ECS cluster, task definition and service (task 1.4) are added to
# the same module. The mock API is this root's, not the module's (#1342): the root
# writes its container definition and hands it to the module as an extra container,
# with its repository as an extra repository, the two inputs an adopter uses for a
# sidecar of its own.
#
# What this root never contains is a per-app resource. Cognito clients, branding
# styles and secret parameters belong to envs/poc/apps, which is what makes
# onboarding app 3 leave this root's plan empty (F-07).

provider "aws" {
  region = var.region

  default_tags {
    tags = local.tags
  }
}

locals {
  # contracts/resource-tags.md. Platform = spa-platform is the platform tag:
  # teardown.sh queries it and P-05 asserts the query returns nothing. The bootstrap
  # root deliberately carries a different value, because its three resources outlive
  # every teardown.
  tags = {
    Platform  = "spa-platform"
    Cell      = var.cell
    ManagedBy = "terraform"
  }
}

data "aws_caller_identity" "current" {}

# The apps root's outputs: the client ids of apps 1 and 2, for the mock API's two allowlists. Read
# from state rather than copied by hand, which is what docs/plan/05-infrastructure-and-cost.md ->
# Terraform boundaries says this root does: "The platform root reads two values from the apps root's
# state: the client ids of apps 1 and 2, for the mock API's allowlists. App 3 is not among them, so
# onboarding it leaves the platform plan empty."
#
# The key is the apps root's backend key (infra/envs/poc/apps/versions.tf), and the bucket name is
# built the same way the apps root builds the identity root's.
data "terraform_remote_state" "apps" {
  backend = "s3"

  config = {
    bucket = (
      var.state_bucket != ""
      ? var.state_bucket
      : "spa-platform-${var.cell}-tfstate-${data.aws_caller_identity.current.account_id}"
    )
    key    = "poc/apps/terraform.tfstate"
    region = var.region
  }
}

locals {
  # App 1 is the orders listener's client and app 2 the reports listener's, which is the pairing
  # fixtures/MockApi/MockApiOptions.cs -> Default fixes along with the two ports. App 3 is
  # deliberately not read: F-07 logs it in and has the mock API refuse its call.
  #
  # try() absorbs a missing output, never a missing state (Q4 on #98). A state with no clients
  # output, which is what a destroyed apps root leaves, or with no entry for app 1 or app 2, gives
  # an empty client id. That is harmless while mock_api_image is empty: this root needs a client id
  # only once that image is supplied, and refuses the image without one (variables.tf). A missing state fails the
  # read above with "Unable to find remote state" before try() runs, and that hard dependency is
  # kept on purpose: the first deployment applies the apps root before this one
  # (docs/plan/05-infrastructure-and-cost.md -> First deployment, steps 2 and 3).
  # https://github.com/hashicorp/terraform/blob/v1.11.0/internal/builtin/providers/terraform/data_source_state.go#L162-L168
  app_clients = try(data.terraform_remote_state.apps.outputs.clients, {})

  # The two the mock API admits, and only these two. Named here rather than inline in the module
  # call so that the selection itself is something a test can read.
  mock_api_clients = {
    app1 = try(local.app_clients["app1"].clientId, "")
    app2 = try(local.app_clients["app2"].clientId, "")
  }
}

# The teardown role infra/bootstrap creates (change request 1.5, ADR-0014). The name is derived and
# the ARN is looked up, so a missing or mis-derived role is a plan error here rather than a bucket
# nobody can empty, found at teardown. The ARN lands in a policy condition, where AWS validates
# nothing, which is why it is never assembled from a string.
# aws:PrincipalArn carries the role ARN for a role session, never the assumed-role session ARN:
# https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html#condition-keys-principalarn
data "aws_iam_role" "teardown" {
  name = "spa-platform-${var.cell}-teardown"
}

locals {
  # The role first, and anything additional after it: an extra principal cannot replace it.
  teardown_principal_arns = concat([data.aws_iam_role.teardown.arn], var.additional_teardown_principal_arns)
}

# The region the provider points at, for the mock API's log configuration. A data source and not
# var.region, so the mock API's awslogs-region is the gateway's, which the module reads the same
# way, whatever the provider's region came from.
data "aws_region" "current" {}

# --- The mock API: this root's extra container (#1342) ------------------------------------------
#
# fixtures/MockApi/Dockerfile declares three keys and fixtures/MockApi/Program.cs refuses to start
# without them. The client ids come from the apps root's state above: nothing about a client id is
# written into this root or the module, which is what keeps onboarding a third app out of this
# root's plan (docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries, F-07).
locals {
  mock_api_container_name = "mock-api"

  # Assumption A4 is one issuer per environment, and the mock API validates against one issuer
  # (fixtures/MockApi/Program.cs). values() on a map is ordered by key, so with the single issuer
  # mock_api_image's validation requires this is deterministic.
  mock_api_issuer_authority = length(var.issuers) > 0 ? values(var.issuers)[0].authority : ""

  mock_api_configuration = {
    "MockApi__Issuer"     = local.mock_api_issuer_authority
    "MockApi__App1Client" = local.mock_api_clients.app1
    "MockApi__App2Client" = local.mock_api_clients.app2
  }

  # The module adds nothing to an extra container, so this definition carries what the gateway's
  # carries in the module (modules/cell/ecs.tf), for the same reasons, and the suite holds the two
  # in step:
  #   - a read-only root with no writable path at all (#787): #780's third answer names none for
  #     the mock API, and tools/image-check.sh starts its image on a read-only root with no tmpfs
  #     (#1169);
  #   - every capability dropped, none added and an init process (#786): a non-root user (USER
  #     $APP_UID in fixtures/MockApi/Dockerfile), listeners on 5080 and 5081, above 1024, and an
  #     exec-form ENTRYPOINT, so the init forwards SIGTERM to dotnet;
  #   - ECS's own values spelled out (#1006): the empty capabilities.add, mountPoints,
  #     systemControls and volumesFrom, so a re-plan after an apply replaces nothing.
  # No portMappings: the two listeners are on loopback ports 5080 and 5081, reached from the gateway
  # container inside the task's own network namespace, never from outside it
  # (fixtures/MockApi/Dockerfile, contracts/platform-config.example.json -> AllowedUpstreams). No
  # stopTimeout: the mock API holds no session and no lease, so it keeps Fargate's default. Its log
  # goes to the cell's one log group under its own stream prefix.
  mock_api_container = {
    name      = local.mock_api_container_name
    image     = var.mock_api_image
    essential = true

    readonlyRootFilesystem = true

    linuxParameters = {
      capabilities = {
        add  = []
        drop = ["ALL"]
      }
      initProcessEnabled = true
    }

    mountPoints    = []
    systemControls = []
    volumesFrom    = []

    environment = [
      for key in sort(keys(local.mock_api_configuration)) : {
        name  = key
        value = local.mock_api_configuration[key]
      }
    ]

    logConfiguration = {
      logDriver = "awslogs"
      options = {
        "awslogs-group"         = module.cell.log_group
        "awslogs-region"        = data.aws_region.current.region
        "awslogs-stream-prefix" = local.mock_api_container_name
      }
    }
  }
}

module "cell" {
  source = "../../../modules/cell"

  cell                    = var.cell
  vpc_cidr                = var.vpc_cidr
  teardown_principal_arns = local.teardown_principal_arns
  certificate_domain      = var.certificate_domain
  health_check_path       = var.health_check_path
  gateway_writable_root   = var.gateway_writable_root

  # Task 1.4a. The images, which deploy.sh passes by digest and every other apply passes back
  # unchanged from state, so a routine apply never moves a running image.
  gateway_image = var.gateway_image

  # #1342. The mock API joins the task once its image is supplied, and its repository is created
  # whether or not it is: the first deployment pushes the image there before it passes it. The key
  # stays mock-api, so the repository's address, module.cell.aws_ecr_repository.repository
  # ["mock-api"], is the one it had when the module created it unasked.
  extra_containers   = var.mock_api_image != "" ? [local.mock_api_container] : []
  extra_repositories = ["mock-api"]

  # Task 1.4a. The platform configuration the task environment carries. These are bounds a registry
  # entry cannot widen (contracts/platform-config.schema.json), which is why they are decided here
  # and not in an entry.
  issuers               = var.issuers
  allowed_host_suffixes = var.allowed_host_suffixes
  allowed_upstreams     = var.allowed_upstreams

  # Continuity (#347 Q7). The two-task switch, the durable store, the shared key ring and its key,
  # each defaulting to Phase 1's value and set by the G6 sitting in terraform.tfvars
  # (terraform.tfvars.example).
  multi_task                       = var.multi_task
  session_store                    = var.session_store
  data_protection_parameter_prefix = var.data_protection_parameter_prefix
  data_protection_kms_key_id       = var.data_protection_kms_key_id

  # #1167. The tunnel's hop, off by default and set in terraform.tfvars (terraform.tfvars.example).
  bastion       = var.bastion
  bastion_image = var.bastion_image
}
