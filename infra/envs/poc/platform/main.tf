# The platform root: the cell's runtime resources, one instance of modules/cell.
#
# Tasks 1.2 and 1.5 are in place here: the network, the bucket and the two image
# repositories. The load balancer (task 1.3) and the ECS cluster, task definition
# and service (task 1.4) are added to the same module and appear here without this
# file changing shape.
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
  # root deliberately carries a different value, because its two resources outlive
  # every teardown.
  tags = {
    Platform  = "spa-platform"
    Cell      = var.cell
    ManagedBy = "terraform"
  }
}

module "cell" {
  source = "../../../modules/cell"

  cell                    = var.cell
  vpc_cidr                = var.vpc_cidr
  teardown_principal_arns = var.teardown_principal_arns
}
