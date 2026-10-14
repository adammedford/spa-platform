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

  # Partial configuration, as in the identity root: the bucket name carries the account id.
  #   terraform init -backend-config="bucket=$(terraform -chdir=../../../bootstrap output -raw state_bucket)"
  backend "s3" {
    key          = "poc/apps/terraform.tfstate"
    region       = "us-east-1"
    encrypt      = true
    use_lockfile = true
  }
}
