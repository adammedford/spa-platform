# Terraform and provider versions for the cell module.
#
# 1.11 is the floor for the whole repository, because that release gave the S3
# backend its native lock file. A module declares no backend: the root that calls
# it does (infra/envs/poc/platform).
#
# 6.37 is this module's provider floor: aws_s3_bucket's bucket_namespace argument,
# which storage.tf sets, arrived in 6.37.0 (the provider's CHANGELOG, #46917). An
# older provider cannot create the bucket in the account regional namespace, so init
# refuses one by this constraint rather than validate failing later on an unknown
# argument (change request 1.5a). The roots pin 6.68.0 in their lock files.

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.37"
    }
  }
}
