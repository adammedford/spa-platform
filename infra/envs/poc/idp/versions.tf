# The identity root: the cell's user pool, its managed-login domain, resource servers and test
# users. Its own state key, so its own lock (infra/bootstrap/README.md).
#
# 1.11 is the floor because that release gave the S3 backend its native lock file
# (`use_lockfile`), so no DynamoDB table exists anywhere in the platform.

terraform {
  required_version = ">= 1.11.0"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 6.0"
    }

    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
  }

  # Partial configuration: a backend block takes no variables, and the bucket name carries the
  # account id (spa-platform-<cell>-tfstate-<account id>, infra/bootstrap). Supply it at init:
  #   terraform init -backend-config="bucket=$(terraform -chdir=../../../bootstrap output -raw state_bucket)"
  backend "s3" {
    key          = "poc/idp/terraform.tfstate"
    region       = "us-east-1"
    encrypt      = true
    use_lockfile = true
  }
}
