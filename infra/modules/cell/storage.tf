# Task 1.5. The platform's one private bucket, laid out as contracts/s3-layout.md
# says, and the two image repositories.
#
# Two rules in that contract are enforced here and nowhere else, because the bucket
# enforces them rather than gateway code:
#
#   - a release prefix is written once. spactl sends If-None-Match: * on every
#     object, and the policy below refuses a write under apps/ that does not;
#   - a delete under apps/ is refused to everyone but the teardown role, because a
#     delete followed by a write would otherwise replace a release.
#
# Test P-04 proves both against the deployed bucket: "S3 refuses a second write to
# an existing release key, and refuses the delete that would allow one."
# tests/module.tftest.hcl holds both statements as a plan would apply them.

locals {
  # The suffix S3's account regional namespace requires: "<prefix>-<account id>-<region>-an", and it
  # counts towards the 63-character name limit. Built from the account and Region data sources in
  # iam.tf, so the name follows wherever the root's provider points (change request 1.5a).
  # https://docs.aws.amazon.com/AmazonS3/latest/userguide/gpbucketnamespaces.html#account-regional-naming
  bucket_namespace_suffix = "-${data.aws_caller_identity.current.account_id}-${data.aws_region.current.region}-an"

  bucket_name = var.bucket_name != "" ? var.bucket_name : "${local.name_prefix}${local.bucket_namespace_suffix}"

  # Everything under apps/ is a release. The registry object is not: spactl writes it
  # conditional on the live version, which is If-Match and not If-None-Match, and
  # rolling back reads its version history.
  release_objects_arn = "${aws_s3_bucket.platform.arn}/apps/*"
}

# In the account regional namespace, so no other account can create this name between a teardown
# and the next deployment, which task 6.9 does on purpose (change request 1.5a, decision 1). The
# argument is what opts in: a suffixed name alone does not, and the provider refuses one in the
# global namespace at apply. bucket_namespace arrived in hashicorp/aws 6.37.0 (its CHANGELOG,
# #46917), which is why versions.tf makes that release the floor.
# https://github.com/hashicorp/terraform-provider-aws/blob/v6.68.0/website/docs/r/s3_bucket.html.markdown
resource "aws_s3_bucket" "platform" {
  bucket           = local.bucket_name
  bucket_namespace = "account-regional"

  tags = { Name = local.bucket_name }

  lifecycle {
    # Checked here rather than as a narrower cell bound, because the suffix's length follows the
    # Region: a 22-character cell fits in us-east-2 and not in a 14-character Region (change request
    # 1.5a, decision 4). The provider's own length check would also stop the plan, but it names
    # neither the suffix nor the fix.
    precondition {
      condition     = length(local.bucket_name) <= 63
      error_message = "The platform bucket's name, ${local.bucket_name}, is ${length(local.bucket_name)} characters; S3 allows 63, and the account regional suffix counts towards them. Choose a shorter cell name or bucket_name."
    }

    # bucket_name overrides the name, never the namespace (change request 1.5a, decision 2), so an
    # override carries this account's and Region's suffix. The provider checks only the suffix's
    # shape, and only at apply; AWS refuses another account's suffix.
    precondition {
      condition     = endswith(local.bucket_name, local.bucket_namespace_suffix)
      error_message = "bucket_name is ${local.bucket_name}, which is not in this account's regional namespace: the platform bucket's name ends ${local.bucket_namespace_suffix}. Leave bucket_name empty for ${local.name_prefix}${local.bucket_namespace_suffix}."
    }

    # AWS: "You can create buckets in your account regional namespace in all AWS Regions except
    # Middle East (Bahrain) and Middle East (UAE)", checked 17 October 2026. Refused here rather
    # than given a global name, which another account could claim after a teardown (change request
    # 1.5a, decision 1). If AWS adds either Region, this list is what to shorten.
    # https://docs.aws.amazon.com/AmazonS3/latest/userguide/gpbucketnamespaces.html#account-regional-gp-buckets
    precondition {
      condition     = !contains(["me-south-1", "me-central-1"], data.aws_region.current.region)
      error_message = "${data.aws_region.current.region} does not offer S3's account regional namespace, which the platform bucket requires. Deploy the cell in another Region; the bucket never falls back to the global namespace."
    }
  }
}

# Versioning is part of the S3 contract and not an operational detail: "spactl
# registry rollback reads S3's version history".
resource "aws_s3_bucket_versioning" "platform" {
  bucket = aws_s3_bucket.platform.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "platform" {
  bucket = aws_s3_bucket.platform.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

# No ACLs at all. Every asset is served through the gateway, which needs a session
# for it (ADR-0008), so nothing in this bucket is ever public.
resource "aws_s3_bucket_ownership_controls" "platform" {
  bucket = aws_s3_bucket.platform.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

resource "aws_s3_bucket_server_side_encryption_configuration" "platform" {
  bucket = aws_s3_bucket.platform.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }

    bucket_key_enabled = true
  }
}

data "aws_iam_policy_document" "platform_bucket" {
  # "Private, versioned, TLS only." An unencrypted request is refused outright
  # rather than redirected.
  statement {
    sid     = "DenyInsecureTransport"
    effect  = "Deny"
    actions = ["s3:*"]

    resources = [
      aws_s3_bucket.platform.arn,
      "${aws_s3_bucket.platform.arn}/*",
    ]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }

  # The state bucket's posture, matched here: TLS 1.2 or better, not merely TLS.
  statement {
    sid     = "DenyOutdatedTlsVersions"
    effect  = "Deny"
    actions = ["s3:*"]

    resources = [
      aws_s3_bucket.platform.arn,
      "${aws_s3_bucket.platform.arn}/*",
    ]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    condition {
      test     = "NumericLessThan"
      variable = "s3:TlsVersion"
      values   = ["1.2"]
    }
  }

  # A write under apps/ that carries no If-None-Match header is refused, so a
  # release prefix is written once. The s3:if-none-match condition key enforces the
  # header S3 would otherwise merely honour when offered.
  #
  # The Bool on s3:ObjectCreationOperation is not decoration: CreateMultipartUpload,
  # UploadPart and UploadPartCopy accept no conditional header, so without it this
  # statement would refuse the parts of an upload rather than the write. The S3
  # contract says spactl uses single-part uploads, so that exemption should never be
  # reached here; it is kept because reaching it would otherwise deny a part rather
  # than fail the write, which is the harder failure to read.
  #
  # Both condition keys and this Deny shape are AWS's own, from "Enforce conditional
  # writes on Amazon S3 buckets" in the S3 user guide, example 2. Enforcement for
  # general purpose buckets shipped in November 2024. It is written down here because
  # a reader who cannot find the second key assumes the statement never fires, and
  # the failure mode if that were true is a bucket that looks compliant and fails
  # P-04 at G3.
  #
  # One consequence, from the AWS documentation: a bucket policy that enforces
  # conditional writes refuses copy operations into the prefix it covers. Nothing
  # copies into apps/ — spactl puts objects there and nothing else writes at all.
  statement {
    sid       = "DenyReleaseWriteWithoutIfNoneMatch"
    effect    = "Deny"
    actions   = ["s3:PutObject"]
    resources = [local.release_objects_arn]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    condition {
      test     = "Null"
      variable = "s3:if-none-match"
      values   = ["true"]
    }

    condition {
      test     = "Bool"
      variable = "s3:ObjectCreationOperation"
      values   = ["true"]
    }
  }

  # And the other half: with the write refused but a delete allowed, a release could
  # be replaced in two steps. Only the teardown principals may delete under apps/,
  # and they exist so that teardown.sh can empty the bucket before destroying it.
  statement {
    sid    = "DenyReleaseDeleteExceptTeardown"
    effect = "Deny"

    actions = [
      "s3:DeleteObject",
      "s3:DeleteObjectVersion",
    ]

    resources = [local.release_objects_arn]

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    condition {
      test     = "ArnNotLike"
      variable = "aws:PrincipalArn"
      values   = var.teardown_principal_arns
    }
  }
}

resource "aws_s3_bucket_policy" "platform" {
  bucket = aws_s3_bucket.platform.id
  policy = data.aws_iam_policy_document.platform_bucket.json

  # The public access block must be in place first. A bucket policy applied to a
  # bucket that still allows public policies is a window, however short.
  depends_on = [aws_s3_bucket_public_access_block.platform]
}

# --- Image repositories -----------------------------------------------------------

locals {
  # The names contracts/infra-outputs.example.json shows under ecrGateway and
  # ecrMockApi: spa-{cell}/gateway and spa-{cell}/mock-api.
  repositories = toset(["gateway", "mock-api"])
}

resource "aws_ecr_repository" "repository" {
  for_each = local.repositories

  name = "spa-${var.cell}/${each.key}"

  # Immutable tags, so a tag names one image for ever. deploy.sh pushes by digest and
  # asserts the running digest afterwards, which only means anything if a tag cannot
  # be moved underneath it.
  image_tag_mutability = "IMMUTABLE"

  # Teardown destroys the roots in reverse order and proves the account is empty by
  # tag query (P-05). A repository with images in it refuses to be destroyed, so the
  # proof would never pass.
  force_delete = true

  tags = { Name = "spa-${var.cell}/${each.key}" }
}
