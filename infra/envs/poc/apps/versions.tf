# The apps root: one Cognito client, its managed-login branding style and one secret parameter
# per app definition in fixtures/apps/. Its own state key, so its own lock, and onboarding an app
# plans nothing in the identity or platform roots (docs/plan/05-infrastructure-and-cost.md ->
# Terraform boundaries).
#
# 1.11 is the floor for the S3 backend's native lock file, as in every root.

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # Partial configuration, as in the identity root: the bucket's name carries the account id, and
  # the Region is the project's selected Region, AWS_REGION in tools/aws-project.env. A backend
  # block cannot refer to a variable, so both are given at init (change request 1.1).
  # https://developer.hashicorp.com/terraform/language/backend#partial-configuration
  # https://developer.hashicorp.com/terraform/language/backend/s3 (region: required, and read from
  # AWS_REGION when no -backend-config gives it)
  # From the repository root, after sourcing tools/aws-project.env:
  #   terraform -chdir=infra/envs/poc/apps init \
  #     -backend-config="bucket=$(terraform -chdir=infra/bootstrap output -raw state_bucket)" \
  #     -backend-config="region=$AWS_REGION"
  backend "s3" {
    key          = "poc/apps/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
