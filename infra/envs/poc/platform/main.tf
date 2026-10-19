# The platform root: the cell's runtime resources, one instance of modules/cell.
#
# Tasks 1.2, 1.3 and 1.5 are in place here: the network, the internal load
# balancer with its one listener and target group, the bucket and the two image
# repositories. The ECS cluster, task definition and service (task 1.4) are added to
# the same module.
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
  # an empty client id. That is harmless while mock_api_image is empty: the module needs a client id
  # only once that image is supplied, and refuses the image without one. A missing state fails the
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

module "cell" {
  source = "../../../modules/cell"

  cell                    = var.cell
  vpc_cidr                = var.vpc_cidr
  teardown_principal_arns = local.teardown_principal_arns
  certificate_domain      = var.certificate_domain
  health_check_path       = var.health_check_path

  # Task 1.4a. The images, which deploy.sh passes by digest and every other apply passes back
  # unchanged from state, so a routine apply never moves a running image.
  gateway_image           = var.gateway_image
  mock_api_image          = var.mock_api_image
  mock_api_app1_client_id = local.mock_api_clients.app1
  mock_api_app2_client_id = local.mock_api_clients.app2

  # Task 1.4a. The platform configuration the task environment carries. These are bounds a registry
  # entry cannot widen (contracts/platform-config.schema.json), which is why they are decided here
  # and not in an entry.
  issuers               = var.issuers
  allowed_host_suffixes = var.allowed_host_suffixes
  allowed_upstreams     = var.allowed_upstreams
}
