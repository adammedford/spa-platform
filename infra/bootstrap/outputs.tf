output "state_bucket" {
  description = "Name of the Terraform state bucket. Every other root names it in its S3 backend block."
  value       = aws_s3_bucket.state.id
}

output "region" {
  description = "Region the state bucket and the CI plan role live in."
  value       = var.region
}

output "cell" {
  description = "Cell these resources belong to."
  value       = var.cell
}

output "ci_plan_role_arn" {
  description = "ARN of the read-only role CI assumes to run plans."
  value       = aws_iam_role.ci_plan.arn
}

output "github_oidc_provider_arn" {
  description = "ARN of the GitHub Actions OIDC provider trusted by the CI plan role, or empty when no GitHub workflow is trusted."
  value       = local.github_oidc_provider_arn
}

output "state_keys" {
  description = "The state key each root uses. One key per root, so one lock per root."
  value = {
    idp      = "${var.cell}/idp/terraform.tfstate"
    apps     = "${var.cell}/apps/terraform.tfstate"
    platform = "${var.cell}/platform/terraform.tfstate"
  }
}

output "backend_blocks" {
  description = <<-EOT
    The backend block for each root, ready to paste into its backend.tf. The native
    lock file (use_lockfile) replaces a DynamoDB table, so no table exists.
  EOT
  value = {
    for root, key in {
      idp      = "${var.cell}/idp/terraform.tfstate"
      apps     = "${var.cell}/apps/terraform.tfstate"
      platform = "${var.cell}/platform/terraform.tfstate"
    } : root => <<-EOT
      terraform {
        backend "s3" {
          bucket       = "${aws_s3_bucket.state.id}"
          key          = "${key}"
          region       = "${var.region}"
          encrypt      = true
          use_lockfile = true
        }
      }
    EOT
  }
}
