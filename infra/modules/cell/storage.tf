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

locals {
  bucket_name = var.bucket_name != "" ? var.bucket_name : local.name_prefix

  # Everything under apps/ is a release. The registry object is not: spactl writes it
  # conditional on the live version, which is If-Match and not If-None-Match, and
  # rolling back reads its version history.
  releases = "${aws_s3_bucket.platform.arn}/apps/*"
}

resource "aws_s3_bucket" "platform" {
  bucket = local.bucket_name

  tags = { Name = local.bucket_name }
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
  # One consequence, from the AWS documentation: a bucket policy that enforces
  # conditional writes refuses copy operations into the prefix it covers. Nothing
  # copies into apps/ — spactl puts objects there and nothing else writes at all.
  statement {
    sid       = "DenyReleaseWriteWithoutIfNoneMatch"
    effect    = "Deny"
    actions   = ["s3:PutObject"]
    resources = [local.releases]

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

    resources = [local.releases]

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

resource "aws_ecr_repository" "image" {
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

  image_scanning_configuration {
    scan_on_push = true
  }

  tags = { Name = "spa-${var.cell}/${each.key}" }
}
