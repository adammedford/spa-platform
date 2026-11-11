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

    # bucket_name overrides the name, never the namespace: change request 1.5a fixes the namespace
    # (decision 1) and keeps the override (decision 2), so an override carries this account's and
    # Region's suffix. The provider checks only the suffix's shape, and only at apply; AWS refuses
    # another account's suffix.
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

  # How many tagged images each repository keeps (#784). rollback.sh applies the previous task
  # definition's images and refuses one the repository no longer holds, so the running image and the
  # previous one must both survive. deploy.sh pushes one tag per run before its apply, so a run whose
  # apply failed, or whose deployment the circuit breaker rolled back, adds a tagged image that never
  # ran: at a count of two, one such run expires the previous task definition's image. Five leaves
  # room for three of them between two good deploys.
  kept_tagged_images = 5

  # The rules, as a local so that tests/module.tftest.hcl decodes the planned policy and compares
  # its structure. Rule properties: https://docs.aws.amazon.com/AmazonECR/latest/userguide/lifecycle_policy_parameters.html
  #
  #   1. An untagged image expires a day after its push. deploy.sh pushes an image index under its
  #      run's tag (#783), and the index's children, the linux/arm64 image and BuildKit's attestation
  #      manifest, are untagged. They are kept for as long as the index is, by the vendor's rule: "If
  #      an image is referenced by a manifest list, it cannot be expired or archived without the
  #      manifest list being deleted or archived first"
  #      (https://docs.aws.amazon.com/AmazonECR/latest/userguide/LifecyclePolicies.html). So no
  #      attestation of a kept image expires; once rule 2 expires an index, its children follow on a
  #      later evaluation. By age and not by count, so a child pushed moments before its index is
  #      never a candidate while the push is still under way.
  #   2. A tagged image is kept by count, the newest kept_tagged_images, and never by age: an age
  #      rule would expire the running image of a cell nobody has deployed for that long. The
  #      pattern "*" counts every tag, deploy.sh's and any pushed by hand.
  #
  # No rule selects tagStatus "any": it would count the untagged children among the images kept.
  lifecycle_rules = [
    {
      rulePriority = 1
      description  = "Expire an untagged image a day after its push, once no kept image index references it"
      selection = {
        tagStatus   = "untagged"
        countType   = "sinceImagePushed"
        countUnit   = "days"
        countNumber = 1
      }
      action = { type = "expire" }
    },
    {
      rulePriority = 2
      description  = "Keep the five most recently pushed tagged images: rollback.sh applies the previous task definition's"
      selection = {
        tagStatus      = "tagged"
        tagPatternList = ["*"]
        countType      = "imageCountMoreThan"
        countNumber    = local.kept_tagged_images
      }
      action = { type = "expire" }
    },
  ]
}

# Cost: storage only. ECR's pricing page: "You pay only for the amount of data you store in your
# public or private repositories and for data transferred to the internet"
# (https://aws.amazon.com/ecr/pricing/). Basic scanning is not one of those, so it is free; the
# enhanced level is Amazon Inspector's and priced on its page (https://aws.amazon.com/inspector/pricing/),
# which is why this module stays at basic. Storage is the "S3 and ECR storage" line of
# docs/plan/05-infrastructure-and-cost.md, cents per GB and under $0.50 a month, which the lifecycle
# policy below keeps true as deploys accumulate.
#
# Encryption is ECR's default, AES256, which needs no key managed here (#779, answer 6).
resource "aws_ecr_repository" "repository" {
  for_each = local.repositories

  name = "spa-${var.cell}/${each.key}"

  # Immutable tags, so a tag names one image for ever. deploy.sh pushes by digest and
  # asserts the running digest afterwards, which only means anything if a tag cannot
  # be moved underneath it.
  image_tag_mutability = "IMMUTABLE"

  # Every pushed image is scanned at the basic level, the registry's default scan type (#779,
  # answer 1: https://github.com/adammedford/spa-platform/issues/779#issuecomment-6767167834).
  # Set on the repository, not by a registry scanning configuration: that one is a single setting
  # for the whole account and Region, which a module instantiated once per cell cannot own. ECR's
  # API reference marks the repository-level call as being deprecated in favour of the registry's
  # (https://docs.aws.amazon.com/AmazonECR/latest/APIReference/API_PutImageScanningConfiguration.html);
  # if it stops being honoured, the scan moves to a registry rule filtered on spa-{cell}/*.
  image_scanning_configuration {
    scan_on_push = true
  }

  # Teardown destroys the roots in reverse order and proves the account is empty by
  # tag query (P-05). A repository with images in it refuses to be destroyed, so the
  # proof would never pass.
  force_delete = true

  tags = { Name = "spa-${var.cell}/${each.key}" }
}

# One policy for each repository: rollback.sh applies the previous gateway image and the mock API
# image deployed with it, so both repositories keep the same images. The rules are above.
resource "aws_ecr_lifecycle_policy" "repository" {
  for_each = local.repositories

  repository = aws_ecr_repository.repository[each.key].name
  policy     = jsonencode({ rules = local.lifecycle_rules })
}
