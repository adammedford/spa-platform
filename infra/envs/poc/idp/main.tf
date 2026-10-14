provider "aws" {
  region = var.region

  default_tags {
    tags = local.tags
  }
}

data "aws_caller_identity" "current" {}

locals {
  # contracts/resource-tags.md: every resource in this root carries the platform tag, which
  # teardown.sh and P-05 match exactly. Set through default_tags, so a resource added later is
  # tagged without its author remembering to. The untaggable children (domain, resource servers,
  # users) are children of the tagged pool.
  tags = {
    Platform  = "spa-platform"
    Cell      = var.cell
    ManagedBy = "terraform"
  }

  # The resource servers and test users are the fixture's (task 5.5); the mock API reads the same
  # file, so the scopes it checks and the scopes the pool issues cannot drift apart.
  pool = jsondecode(file("${path.module}/../../../../fixtures/cognito/pool.json"))

  domain_prefix = (
    var.domain_prefix != ""
    ? var.domain_prefix
    : "spa-platform-${var.cell}-${data.aws_caller_identity.current.account_id}"
  )
}

module "idp" {
  source = "../../../modules/idp-cognito"

  cell             = var.cell
  domain_prefix    = local.domain_prefix
  resource_servers = local.pool.resourceServers
  users            = local.pool.users
}
