provider "aws" {
  region = var.region

  default_tags {
    tags = local.tags
  }
}

data "aws_caller_identity" "current" {}
data "aws_partition" "current" {}

locals {
  account_id = data.aws_caller_identity.current.account_id
  partition  = data.aws_partition.current.partition

  # Every resource name carries the cell name (ADR-0012).
  name_prefix = "spa-platform-${var.cell}"

  state_bucket_name = (
    var.state_bucket_name != ""
    ? var.state_bucket_name
    : "${local.name_prefix}-tfstate-${local.account_id}"
  )

  ci_role_name = "${local.name_prefix}-ci-plan"

  # The platform tag that teardown queries is Platform = spa-platform (test P-05).
  # Bootstrap resources deliberately carry a different value: the state bucket and
  # the CI role outlive every teardown, so if they answered that query, P-05 could
  # never pass. tools/scripts/teardown.sh (task 1.7) queries the platform value.
  tags = {
    Platform  = "spa-platform-bootstrap"
    Cell      = var.cell
    Component = "bootstrap"
    ManagedBy = "terraform"
    Root      = "infra/bootstrap"
  }

  github_oidc_provider_arn = (
    var.create_github_oidc_provider
    ? try(aws_iam_openid_connect_provider.github[0].arn, "")
    : var.github_oidc_provider_arn
  )

  trust_github    = var.github_repository != ""
  github_subjects = [for claim in var.github_subject_claims : "repo:${var.github_repository}:${claim}"]
}

# Fail the apply, rather than create a role nobody can assume, when no principal
# was named. This is the loud failure the plan asks for in place of a silent one.
resource "terraform_data" "trust_is_configured" {
  input = local.ci_role_name

  lifecycle {
    precondition {
      condition     = local.trust_github || length(var.additional_trusted_principal_arns) > 0
      error_message = "Set github_repository, or additional_trusted_principal_arns, or both. The CI plan role needs at least one principal that can assume it."
    }

    precondition {
      condition     = !local.trust_github || length(var.github_subject_claims) > 0
      error_message = "github_subject_claims is empty, so no GitHub workflow could assume the role. Name the refs that run plans."
    }

    precondition {
      condition     = var.create_github_oidc_provider || !local.trust_github || var.github_oidc_provider_arn != ""
      error_message = "create_github_oidc_provider is false, so github_oidc_provider_arn must hold the ARN of the account's existing GitHub Actions OIDC provider."
    }
  }
}
