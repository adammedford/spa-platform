# Terraform and provider versions for the cell module.
#
# 1.11 is the floor for the whole repository, because that release gave the S3
# backend its native lock file. A module declares no backend: the root that calls
# it does (infra/envs/poc/platform).

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }
  }
}
