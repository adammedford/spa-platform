# Task 1.4a. The cell's two roles.
#
# Two, never one. The execution role is the ECS agent's: it pulls the image and opens the log
# stream. The task role is the workload's, and it is the security object of record for the whole
# cell — the one object here where a careless grant is easy to write and hard to notice. It holds
# exactly what plan row 1.4 enumerates: this cell's bucket prefixes, this cell's client-secret
# parameter path, and the four ssmmessages permissions the tunnel needs. Nothing else.
#
# Every statement below is asserted at plan time in tests/module.tftest.hcl, including one
# assertion that fails on a statement the test does not recognise, so a widened grant breaks a test
# instead of waiting for a reviewer to notice. Those runs, with the one that holds the bucket policy
# to refusals only, are P-03 (plan row 1.9), which ADR-0016 makes an assertion on the configuration
# against a mock provider rather than on an applied plan.

data "aws_partition" "current" {}
data "aws_region" "current" {}
data "aws_caller_identity" "current" {}

locals {
  # The two top-level prefixes contracts/s3-layout.md lays out: the registry object and every
  # release. Stated here and read out of the contract by the test, so the two have to agree.
  task_readable_prefixes = ["registry", "apps"]

  # The path the apps root writes a client secret to, one segment per app
  # (infra/envs/poc/apps/main.tf, contracts/infra-outputs.example.json -> clients[].secretParameter).
  # Scoped to this cell: another cell's secret is outside it.
  client_secret_parameter_arn = join("", [
    "arn:${data.aws_partition.current.partition}:ssm:",
    "${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}",
    ":parameter/spa-platform/${var.cell}/apps/*",
  ])

  # Continuity (#338). The session table and its appId index, behind session_store = "dynamodb".
  # The ARN is built from the table's name rather than read from aws_dynamodb_table because this
  # module declares no table: P-01's tripwire reads the module's source and refuses the type whatever
  # its count, and contracts/changes/issue-338-p-01-dynamodb-tripwire.md records the exemption that
  # admits the gated table and has this grant read the table's ARN when it is declared.
  session_store_is_dynamodb = var.session_store == "dynamodb"
  session_table_name        = "${local.name_prefix}-sessions"
  session_table_index_name  = "appId"
  session_table_arn = join("", [
    "arn:${data.aws_partition.current.partition}:dynamodb:",
    "${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}",
    ":table/${local.session_table_name}",
  ])

  # The key ring, behind data_protection_parameter_prefix (P2-2 decisions 8 and 12). The variable's
  # validation holds the prefix to /spa-platform/{cell}/data-protection/, so these ARNs are this
  # cell's ring and no other's. The prefix's own ARN carries no trailing slash: GetParametersByPath
  # is authorised against the path itself as well as its children (P2-2 decision 12).
  key_ring_enabled = var.data_protection_parameter_prefix != ""
  key_ring_path_arn = join("", [
    "arn:${data.aws_partition.current.partition}:ssm:",
    "${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}",
    ":parameter${trimsuffix(var.data_protection_parameter_prefix, "/")}",
  ])

  # The tags the gateway puts on each ring parameter it creates (#274): the platform's two, and
  # ManagedBy = gateway, since Terraform does not manage them (contracts/resource-tags.md).
  key_ring_tags = {
    Platform  = "spa-platform"
    Cell      = var.cell
    ManagedBy = "gateway"
  }
}

# --- Who may assume them ----------------------------------------------------------------------

# Shared by both roles: only the ECS tasks service, and only on this account's behalf.
#
# The two conditions are the confused-deputy guard AWS documents for a service role. Without them
# the trust policy says "ECS may assume this role" with no account named, and ECS acting for another
# account satisfies it:
# https://docs.aws.amazon.com/IAM/latest/UserGuide/confused-deputy.html
# https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task-iam-roles.html
data "aws_iam_policy_document" "ecs_tasks_trust" {
  statement {
    sid     = "EcsTasksAssumeRole"
    effect  = "Allow"
    actions = ["sts:AssumeRole"]

    principals {
      type        = "Service"
      identifiers = ["ecs-tasks.amazonaws.com"]
    }

    condition {
      test     = "StringEquals"
      variable = "aws:SourceAccount"
      values   = [data.aws_caller_identity.current.account_id]
    }

    # ArnLike with a trailing wildcard, because the task ARN is not known until the task runs: the
    # condition names this account's ECS in this region and nothing further.
    condition {
      test     = "ArnLike"
      variable = "aws:SourceArn"
      values = [
        "arn:${data.aws_partition.current.partition}:ecs:${data.aws_region.current.region}:${data.aws_caller_identity.current.account_id}:*",
      ]
    }
  }
}

# --- The execution role: pull an image, open a log stream -------------------------------------

resource "aws_iam_role" "task_execution" {
  name               = "${local.name_prefix}-task-execution"
  description        = "The ECS agent's role for cell ${var.cell}: pulls the task's images and opens its log streams."
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = { Name = "${local.name_prefix}-task-execution" }
}

# Written out rather than the AmazonECSTaskExecutionRolePolicy managed policy, which grants its ECR
# and logs actions on "*". Here the pull is scoped to this cell's two repositories and the log
# writes to this cell's one log group, which is the same reason infra/bootstrap/ci-role.tf writes
# its own policy instead of attaching ReadOnlyAccess.
data "aws_iam_policy_document" "task_execution" {
  # GetAuthorizationToken takes no resource-level condition: it returns a token for the caller's
  # own registry, so there is nothing to scope it to.
  # https://docs.aws.amazon.com/AmazonECR/latest/userguide/security_iam_service-with-iam.html
  statement {
    sid       = "EcrAuthorization"
    effect    = "Allow"
    actions   = ["ecr:GetAuthorizationToken"]
    resources = ["*"]
  }

  statement {
    sid    = "PullCellImages"
    effect = "Allow"

    actions = [
      "ecr:BatchCheckLayerAvailability",
      "ecr:BatchGetImage",
      "ecr:GetDownloadUrlForLayer",
    ]

    resources = [for repository in aws_ecr_repository.repository : repository.arn]
  }

  # CreateLogGroup is absent: Terraform creates the group (ecs.tf), so the agent only ever needs to
  # add a stream to it. Granting the create as well would let the agent write outside the one group
  # contracts/infra-outputs.schema.json has a field for.
  statement {
    sid    = "WriteTaskLogs"
    effect = "Allow"

    actions = [
      "logs:CreateLogStream",
      "logs:PutLogEvents",
    ]

    resources = ["${aws_cloudwatch_log_group.gateway.arn}:*"]
  }
}

resource "aws_iam_role_policy" "task_execution" {
  name   = "${local.name_prefix}-task-execution"
  role   = aws_iam_role.task_execution.id
  policy = data.aws_iam_policy_document.task_execution.json
}

# --- The task role: what the gateway process itself may reach ---------------------------------

resource "aws_iam_role" "task" {
  name               = "${local.name_prefix}-task"
  description        = "The gateway process's role for cell ${var.cell}: reads this cell's releases, registry and client secrets, and nothing else."
  assume_role_policy = data.aws_iam_policy_document.ecs_tasks_trust.json

  tags = { Name = "${local.name_prefix}-task" }
}

data "aws_iam_policy_document" "task" {
  # "The gateway cannot write. The task role reads this cell's bucket prefixes; it has no write to
  # the registry (P-03)." -- contracts/s3-layout.md. GetObject alone, and the bucket's own ARN is
  # not among the resources, so no listing is reachable even if an action were added: the gateway
  # does a conditional GET on the registry and a GET on a key the registry names.
  statement {
    sid       = "ReadRegistryAndReleases"
    effect    = "Allow"
    actions   = ["s3:GetObject"]
    resources = [for prefix in local.task_readable_prefixes : "${aws_s3_bucket.platform.arn}/${prefix}/*"]
  }

  # A registry entry's clientSecretRef names a parameter and a version, and the gateway resolves it
  # when it builds a snapshot (ADR-0003). GetParameter is the single read and GetParameters the
  # batched one; no write, no history and no listing.
  #
  # The parameters are SecureString under the AWS-managed key the apps root chose
  # (infra/envs/poc/apps/main.tf). No kms:Decrypt is granted here, because the AWS-managed key's own
  # policy admits this account's principals when the call arrives through Systems Manager:
  # https://docs.aws.amazon.com/systems-manager/latest/userguide/ps-restrict-decryption.html
  # A customer-managed replacement key requires a reviewed, explicit kms:Decrypt grant for that
  # key; the AWS-managed-key assumption does not extend to customer-managed keys. The key ring's
  # parameters below are SecureStrings the gateway writes as well as reads, so a customer-managed
  # key, if data_protection_kms_key_id ever sets one, needs its encrypt side granted too, beside
  # kms:Decrypt (P2-2 decision 12).
  statement {
    sid    = "ReadClientSecrets"
    effect = "Allow"

    actions = [
      "ssm:GetParameter",
      "ssm:GetParameters",
    ]

    resources = [local.client_secret_parameter_arn]
  }

  # The four the tunnel needs, and no fifth: "Its role needs the four ssmmessages permissions"
  # (docs/plan/05-infrastructure-and-cost.md -> Workstation access). They take no resource-level
  # condition, which is why this is the one statement here on "*" — and why the test asserts it is
  # the only one, so a later grant cannot hide behind the same shape.
  # https://docs.aws.amazon.com/AmazonECS/latest/developerguide/ecs-exec.html
  #
  # Exec logging is not configured, so none of the s3: or logs: permissions that option adds are
  # granted. All of this ends in Phase 2, where access comes over the corporate network.
  statement {
    sid    = "SessionManagerMessages"
    effect = "Allow"

    actions = [
      "ssmmessages:CreateControlChannel",
      "ssmmessages:CreateDataChannel",
      "ssmmessages:OpenControlChannel",
      "ssmmessages:OpenDataChannel",
    ]

    resources = ["*"]
  }

  # Continuity (#338). The three statements below exist only behind their inputs, so a Phase 1 cell
  # holds the three grants above and nothing else.
  #
  # The DynamoDB store's five operations (change request P2-1, item 5 and Decision 7): GetItem, a
  # consistent read; PutItem, create, replace and the lease's acquire, each conditional; DeleteItem,
  # remove and the lease's release; Query on the appId index and BatchWriteItem, RemoveAppAsync's
  # purge. No Scan, no UpdateItem and nothing of the table's administration. The index is named
  # rather than index/*, so the subject index G7 adds (ADR-0028) needs its own reviewed line.
  dynamic "statement" {
    for_each = local.session_store_is_dynamodb ? [1] : []

    content {
      sid    = "ReadWriteSessions"
      effect = "Allow"

      actions = [
        "dynamodb:GetItem",
        "dynamodb:PutItem",
        "dynamodb:DeleteItem",
        "dynamodb:Query",
        "dynamodb:BatchWriteItem",
      ]

      resources = [
        local.session_table_arn,
        "${local.session_table_arn}/index/${local.session_table_index_name}",
      ]
    }
  }

  # The key ring (P2-2 decision 12). PutParameter writes a new key and GetParametersByPath reads the
  # ring, and deliberately no ssm:DeleteParameter: nothing in the platform deletes a key, and a role
  # that cannot delete one cannot lose the ring by accident. Without it an explicit deletion through
  # IKeyManager fails and serving does not. teardown.sh deletes the ring as the operator.
  dynamic "statement" {
    for_each = local.key_ring_enabled ? [1] : []

    content {
      sid    = "ReadWriteKeyRing"
      effect = "Allow"

      actions = [
        "ssm:PutParameter",
        "ssm:GetParametersByPath",
      ]

      resources = [
        local.key_ring_path_arn,
        "${local.key_ring_path_arn}/*",
      ]
    }
  }

  # A PutParameter carrying Tags is authorised as ssm:AddTagsToResource as well (#274), and the
  # gateway tags each ring parameter it creates. The Service Authorization Reference gives the
  # operation both actions, each taking aws:RequestTag/${TagKey} and aws:TagKeys:
  # https://docs.aws.amazon.com/service-authorization/latest/reference/list_ssm.html#list_ssm-operation-PutParameter
  # StringEquals on each tag's value refuses a call that carries none of them, which ForAllValues on
  # aws:TagKeys alone would pass; aws:TagKeys refuses a fourth tag. So the role can tag a ring
  # parameter with the platform's tags and nothing else.
  dynamic "statement" {
    for_each = local.key_ring_enabled ? [1] : []

    content {
      sid       = "TagKeyRingOnCreate"
      effect    = "Allow"
      actions   = ["ssm:AddTagsToResource"]
      resources = ["${local.key_ring_path_arn}/*"]

      dynamic "condition" {
        for_each = local.key_ring_tags

        content {
          test     = "StringEquals"
          variable = "aws:RequestTag/${condition.key}"
          values   = [condition.value]
        }
      }

      condition {
        test     = "ForAllValues:StringEquals"
        variable = "aws:TagKeys"
        values   = keys(local.key_ring_tags)
      }
    }
  }
}

resource "aws_iam_role_policy" "task" {
  name   = "${local.name_prefix}-task"
  role   = aws_iam_role.task.id
  policy = data.aws_iam_policy_document.task.json
}
