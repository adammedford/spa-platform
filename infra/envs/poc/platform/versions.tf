# Terraform and provider versions for the platform root, and its backend.
#
# 1.11 is the floor because that release gave the S3 backend its native lock file,
# which is why no DynamoDB table exists anywhere in this platform.

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # Partially configured: a backend block takes no variables, and the state bucket's
  # name carries the account id, which is not known until infra/bootstrap is applied.
  # Give the bucket and the Region at init, with
  # `terraform init -backend-config=backend.hcl` filled in from the bootstrap root's
  # `backend_blocks` output; backend.hcl.example beside this file is that file's
  # shape. Never paste them here: the bucket's name carries the account id, and the
  # Region is the project's, which tools/aws-project.env names (change requests 1.5
  # and 1.1).
  backend "s3" {
    key          = "poc/platform/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
