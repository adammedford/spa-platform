# Terraform and provider versions for the bootstrap root.
#
# 1.11 is the floor because that release gave the S3 backend its native lock file
# (`use_lockfile`). Every other root in this repository locks that way, so no
# DynamoDB table exists anywhere in the platform. See infra/bootstrap/README.md.

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }

  # Local state, on purpose and only here. This root creates the bucket that holds
  # every other root's state, so it cannot keep its own state there. The state file
  # is ignored by .gitignore; keep it, because this root is applied once and the
  # resources it owns outlive every teardown.
  backend "local" {}
}
