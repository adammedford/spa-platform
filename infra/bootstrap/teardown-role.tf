# The teardown role: the one principal the platform bucket's policy lets delete under apps/
# (contracts/s3-layout.md, ADR-0014).
#
# It lives here because it has to outlive what it tears down: a role in the platform root would be
# destroyed by the teardown it exists to permit. Its name is derived from the cell, so the platform
# root and teardown.sh compute it without reading this root's local state; the platform root then
# looks it up, so the ARN in the bucket policy's condition is AWS's, not an assembled string.
#
# Break-glass, and narrow by separation of powers: it may delete a release and may write nothing,
# so it cannot replace one. A conditional write succeeds over a delete marker, which is why no
# principal may hold both. Never give it a write "to repair a prefix". P-04 never runs as it.
# Conditional write over a delete marker: Amazon S3 User Guide, conditional requests,
# https://docs.aws.amazon.com/AmazonS3/latest/userguide/conditional-requests.html
#
# The policies are built with jsonencode rather than aws_iam_policy_document so that
# tests/root.tftest.hcl can read the statements a mock provider would otherwise make up.

locals {
  teardown_role_name = "${local.name_prefix}-teardown"

  teardown_trusted_users = [for arn in var.teardown_trusted_principal_arns : arn if can(regex(":user/", arn))]
  teardown_trusted_roles = [for arn in var.teardown_trusted_principal_arns : arn if can(regex(":role/", arn))]

  teardown_trust = {
    Version = "2012-10-17"
    Statement = concat(
      # Long-term keys alone cannot assume it: Bool, not BoolIfExists, so an absent MFA key fails.
      # https://docs.aws.amazon.com/IAM/latest/UserGuide/reference_policies_condition-keys.html#condition-keys-multifactorauthpresent
      length(local.teardown_trusted_users) == 0 ? [] : [{
        Sid       = "NamedUsersWithMfa"
        Effect    = "Allow"
        Action    = "sts:AssumeRole"
        Principal = { AWS = local.teardown_trusted_users }
        Condition = { Bool = { "aws:MultiFactorAuthPresent" = "true" } }
      }],
      # Federated sessions may carry no MFA key at all, so the condition above would lock them out
      # and cannot attest to their MFA anyway. Their identity provider enforces it; listing the
      # role here is the explicit allowlisting ADR-0014 item 5 asks for.
      # Same IAM reference as above: the key can be absent for federated sessions.
      length(local.teardown_trusted_roles) == 0 ? [] : [{
        Sid       = "NamedFederatedRoles"
        Effect    = "Allow"
        Action    = "sts:AssumeRole"
        Principal = { AWS = local.teardown_trusted_roles }
      }],
    )
  }

  teardown_permissions = {
    Version = "2012-10-17"
    Statement = [
      {
        Sid      = "DeleteReleases"
        Effect   = "Allow"
        Action   = ["s3:DeleteObject", "s3:DeleteObjectVersion"]
        Resource = "${local.platform_bucket_arn}/apps/*"
      },
      {
        Sid      = "ListPlatformBucket"
        Effect   = "Allow"
        Action   = ["s3:ListBucket", "s3:ListBucketVersions"]
        Resource = local.platform_bucket_arn
      },
    ]
  }
}

# Fail the apply, rather than create a role nobody may assume or one CI may assume.
resource "terraform_data" "teardown_trust_is_configured" {
  input = local.teardown_role_name

  lifecycle {
    precondition {
      condition     = length(var.teardown_trusted_principal_arns) > 0
      error_message = "Set teardown_trusted_principal_arns. The teardown role needs at least one human who can assume it, or nothing can ever empty the platform bucket's apps/ prefix."
    }

    precondition {
      condition     = !anytrue([for arn in var.teardown_trusted_principal_arns : endswith(arn, ":role/${local.ci_role_name}")])
      error_message = "The CI plan role must not assume the teardown role: CI plans, it never deletes."
    }
  }
}

resource "aws_iam_role" "teardown" {
  name                 = local.teardown_role_name
  description          = "Break-glass role teardown.sh assumes to empty apps/ in cell ${var.cell}'s platform bucket. Deletes only."
  assume_role_policy   = jsonencode(local.teardown_trust)
  max_session_duration = 3600

  depends_on = [terraform_data.teardown_trust_is_configured]
}

resource "aws_iam_role_policy" "teardown" {
  name   = local.teardown_role_name
  role   = aws_iam_role.teardown.id
  policy = jsonencode(local.teardown_permissions)
}
