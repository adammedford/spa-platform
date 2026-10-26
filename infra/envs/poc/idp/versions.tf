# The identity root: the cell's user pool, its managed-login domain, resource servers and test
# users. Its own state key, so its own lock (infra/bootstrap/README.md).
#
# 1.11 is the floor because that release gave the S3 backend its native lock file
# (`use_lockfile`), so no DynamoDB lock table exists anywhere in the platform.

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

  # Partial configuration: a backend block cannot refer to a variable, so the two values that come
  # from outside this root are given at init. The bucket's name carries the account id
  # (spa-platform-<cell>-tfstate-<account id>, infra/bootstrap). The Region is the project's
  # selected Region, AWS_REGION in tools/aws-project.env, which terraform.tfvars also gives the
  # provider; written here it would be a second copy that nothing keeps in step (change request
  # 1.1).
  # https://developer.hashicorp.com/terraform/language/backend#partial-configuration
  # https://developer.hashicorp.com/terraform/language/backend/s3 (region: required, and read from
  # AWS_REGION when no -backend-config gives it)
  # From the repository root, after sourcing tools/aws-project.env:
  #   terraform -chdir=infra/envs/poc/idp init \
  #     -backend-config="bucket=$(terraform -chdir=infra/bootstrap output -raw state_bucket)" \
  #     -backend-config="region=$AWS_REGION"
  backend "s3" {
    key          = "poc/idp/terraform.tfstate"
    encrypt      = true
    use_lockfile = true
  }
}
