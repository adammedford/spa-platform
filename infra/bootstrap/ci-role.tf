# The read-only role CI assumes to run plans.
#
# A plan reads: the state in the bucket above, and the current shape of every
# resource the three platform roots own. It writes nothing, which is why CI runs
# `terraform plan -lock=false` unless allow_state_lock is turned on.
#
# Scoped to what a plan needs and nothing more. No test reads these permissions
# but the bucket statement's (tests/root.tftest.hcl), so a widened statement here
# has to fail review.

resource "aws_iam_openid_connect_provider" "github" {
  count = var.create_github_oidc_provider && local.trust_github ? 1 : 0

  url             = "https://token.actions.githubusercontent.com"
  client_id_list  = ["sts.amazonaws.com"]
  thumbprint_list = ["ffffffffffffffffffffffffffffffffffffffff"]

  # GitHub rotates the certificate behind this endpoint, and IAM has validated the
  # OIDC thumbprint against its own trust store since July 2023. The value above is
  # the documented placeholder: it is required by the API and ignored by IAM.
  lifecycle {
    ignore_changes = [thumbprint_list]
  }
}

data "aws_iam_policy_document" "ci_plan_trust" {
  dynamic "statement" {
    for_each = local.trust_github ? [1] : []

    content {
      sid     = "GitHubActionsOidc"
      effect  = "Allow"
      actions = ["sts:AssumeRoleWithWebIdentity"]

      principals {
        type        = "Federated"
        identifiers = [local.github_oidc_provider_arn]
      }

      condition {
        test     = "StringEquals"
        variable = "token.actions.githubusercontent.com:aud"
        values   = ["sts.amazonaws.com"]
      }

      # The subject claim is what ties the role to this repository and these refs.
      # Without it, any GitHub workflow in the world could assume the role.
      condition {
        test     = "StringEquals"
        variable = "token.actions.githubusercontent.com:sub"
        values   = local.github_subjects
      }
    }
  }

  dynamic "statement" {
    for_each = length(var.additional_trusted_principal_arns) > 0 ? [1] : []

    content {
      sid     = "NamedPrincipals"
      effect  = "Allow"
      actions = ["sts:AssumeRole"]

      principals {
        type        = "AWS"
        identifiers = var.additional_trusted_principal_arns
      }
    }
  }
}

resource "aws_iam_role" "ci_plan" {
  name                 = local.ci_role_name
  description          = "Read-only role CI assumes to run Terraform plans for cell ${var.cell}."
  assume_role_policy   = data.aws_iam_policy_document.ci_plan_trust.json
  max_session_duration = 3600

  depends_on = [terraform_data.trust_is_configured]
}

data "aws_iam_policy_document" "ci_plan" {
  # 1. The state itself. Read only: a plan needs the current state, not a new one.
  statement {
    sid    = "ReadState"
    effect = "Allow"

    actions = [
      "s3:GetObject",
      "s3:GetObjectVersion",
    ]

    resources = ["${aws_s3_bucket.state.arn}/*"]
  }

  statement {
    sid    = "ListStateBucket"
    effect = "Allow"

    actions = [
      "s3:ListBucket",
      "s3:ListBucketVersions",
      "s3:GetBucketVersioning",
      "s3:GetBucketLocation",
    ]

    resources = [aws_s3_bucket.state.arn]
  }

  # 2. The backend lock file, only when allow_state_lock says so. Narrowed to keys
  #    ending .tflock, so this cannot write a state file.
  dynamic "statement" {
    for_each = var.allow_state_lock ? [1] : []

    content {
      sid    = "WriteBackendLockFileOnly"
      effect = "Allow"

      actions = [
        "s3:PutObject",
        "s3:DeleteObject",
      ]

      resources = ["${aws_s3_bucket.state.arn}/*.tflock"]
    }
  }

  # 3. The network and load balancer of the cell module. EC2 and Elastic Load
  #    Balancing Describe actions take no resource-level condition, so they are
  #    granted on "*" and held down by being read-only.
  statement {
    sid    = "DescribeNetworkAndLoadBalancing"
    effect = "Allow"

    actions = [
      "ec2:DescribeVpcs",
      "ec2:DescribeVpcAttribute",
      "ec2:DescribeSubnets",
      "ec2:DescribeRouteTables",
      "ec2:DescribeInternetGateways",
      "ec2:DescribeSecurityGroups",
      "ec2:DescribeSecurityGroupRules",
      "ec2:DescribeNetworkInterfaces",
      "ec2:DescribeAvailabilityZones",
      "ec2:DescribeTags",
      "elasticloadbalancing:DescribeLoadBalancers",
      "elasticloadbalancing:DescribeLoadBalancerAttributes",
      "elasticloadbalancing:DescribeListeners",
      "elasticloadbalancing:DescribeListenerAttributes",
      "elasticloadbalancing:DescribeListenerCertificates",
      "elasticloadbalancing:DescribeRules",
      "elasticloadbalancing:DescribeTargetGroups",
      "elasticloadbalancing:DescribeTargetGroupAttributes",
      "elasticloadbalancing:DescribeTargetHealth",
      "elasticloadbalancing:DescribeTags",
    ]

    resources = ["*"]
  }

  # 4. The compute half of the cell: cluster, task definitions, service, images,
  #    log group.
  statement {
    sid    = "DescribeCompute"
    effect = "Allow"

    actions = [
      "ecs:DescribeClusters",
      "ecs:DescribeServices",
      "ecs:DescribeTaskDefinition",
      "ecs:DescribeTasks",
      "ecs:ListClusters",
      "ecs:ListServices",
      "ecs:ListTaskDefinitions",
      "ecs:ListTasks",
      "ecs:ListTagsForResource",
      "ecr:DescribeRepositories",
      "ecr:DescribeImages",
      "ecr:ListImages",
      "ecr:ListTagsForResource",
      "ecr:GetRepositoryPolicy",
      "ecr:GetLifecyclePolicy",
      "logs:DescribeLogGroups",
      "logs:ListTagsForResource",
    ]

    resources = ["*"]
  }

  # 5. The platform bucket's settings. Its ARN alone, the one the teardown role's grant names:
  #    a spa-platform-<cell>* glob would miss a bucket_name override and match another cell's
  #    bucket, spa-platform-poc2-... for cell poc (change request 1.5a, ADR-0012).
  statement {
    sid    = "DescribePlatformBucket"
    effect = "Allow"

    actions = [
      "s3:GetAccelerateConfiguration",
      "s3:GetBucketAcl",
      "s3:GetBucketCORS",
      "s3:GetBucketLocation",
      "s3:GetBucketLogging",
      "s3:GetBucketNotification",
      "s3:GetBucketObjectLockConfiguration",
      "s3:GetBucketOwnershipControls",
      "s3:GetBucketPolicy",
      "s3:GetBucketPublicAccessBlock",
      "s3:GetBucketRequestPayment",
      "s3:GetBucketTagging",
      "s3:GetBucketVersioning",
      "s3:GetBucketWebsite",
      "s3:GetEncryptionConfiguration",
      "s3:GetLifecycleConfiguration",
      "s3:GetReplicationConfiguration",
      "s3:ListBucket",
    ]

    resources = [local.platform_bucket_arn]
  }

  # 6. The identity provider: user pool, domain, resource servers, app clients and
  #    their branding. List actions here take no resource condition.
  statement {
    sid    = "DescribeIdentityProvider"
    effect = "Allow"

    actions = [
      "cognito-idp:DescribeUserPool",
      "cognito-idp:DescribeUserPoolClient",
      "cognito-idp:DescribeUserPoolDomain",
      "cognito-idp:DescribeResourceServer",
      "cognito-idp:DescribeManagedLoginBranding",
      "cognito-idp:DescribeManagedLoginBrandingByClient",
      "cognito-idp:GetUserPoolMfaConfig",
      "cognito-idp:ListUserPools",
      "cognito-idp:ListUserPoolClients",
      "cognito-idp:ListResourceServers",
      "cognito-idp:ListUsers",
      # The identity root's test users (task 1.1): aws_cognito_user's read is one AdminGetUser
      # call, so without it a plan of that root fails refreshing each user. Read-only: it returns
      # attributes, never a password.
      # https://github.com/hashicorp/terraform-provider-aws/blob/main/internal/service/cognitoidp/user.go
      "cognito-idp:AdminGetUser",
      "cognito-idp:ListTagsForResource",
    ]

    resources = ["*"]
  }

  # 7. The imported wildcard certificate, which the ALB listener finds by domain
  #    name. certs.sh imports it; Terraform only reads it.
  statement {
    sid    = "DescribeCertificates"
    effect = "Allow"

    actions = [
      "acm:ListCertificates",
      "acm:DescribeCertificate",
      "acm:ListTagsForCertificate",
    ]

    resources = ["*"]
  }

  # 8. The roles and policies the platform root manages, by name. iam:List*
  #    actions that enumerate the account take no resource condition, so they are
  #    kept to the three a plan actually calls.
  statement {
    sid    = "ReadPlatformRoles"
    effect = "Allow"

    actions = [
      "iam:GetRole",
      "iam:GetRolePolicy",
      "iam:ListRolePolicies",
      "iam:ListAttachedRolePolicies",
      "iam:ListRoleTags",
    ]

    resources = ["arn:${local.partition}:iam::${local.account_id}:role/${local.name_prefix}-*"]
  }

  statement {
    sid    = "ReadPlatformPolicies"
    effect = "Allow"

    actions = [
      "iam:GetPolicy",
      "iam:GetPolicyVersion",
      "iam:ListPolicyVersions",
      "iam:ListPolicyTags",
    ]

    resources = ["arn:${local.partition}:iam::${local.account_id}:policy/${local.name_prefix}-*"]
  }

  statement {
    sid    = "ListRolesAndPolicies"
    effect = "Allow"

    actions = [
      "iam:ListRoles",
      "iam:ListPolicies",
    ]

    resources = ["*"]
  }

  # 9. Parameter Store. Describe is always needed; reading values is what
  #    plan_can_read_parameters decides. Both are scoped to this cell's path.
  statement {
    sid       = "DescribeParameters"
    effect    = "Allow"
    actions   = ["ssm:DescribeParameters"]
    resources = ["*"]
  }

  dynamic "statement" {
    for_each = var.plan_can_read_parameters ? [1] : []

    content {
      sid    = "ReadCellParameters"
      effect = "Allow"

      actions = [
        "ssm:GetParameter",
        "ssm:GetParameters",
        "ssm:GetParameterHistory",
        "ssm:ListTagsForResource",
      ]

      resources = ["arn:${local.partition}:ssm:${var.region}:${local.account_id}:parameter/spa-platform/${var.cell}/*"]
    }
  }
}

resource "aws_iam_policy" "ci_plan" {
  name        = local.ci_role_name
  description = "Read-only permissions a Terraform plan of cell ${var.cell} needs."
  policy      = data.aws_iam_policy_document.ci_plan.json
}

resource "aws_iam_role_policy_attachment" "ci_plan" {
  role       = aws_iam_role.ci_plan.name
  policy_arn = aws_iam_policy.ci_plan.arn
}
