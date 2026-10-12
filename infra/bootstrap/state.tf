# The Terraform state bucket: private and versioned.
#
# This is the second bucket in the account. It holds no platform data; the brief's
# "one private bucket" is the platform's, created by the platform root in task 1.5.
# The security posture matches that one: TLS only, public access blocked,
# encryption on.

resource "aws_s3_bucket" "state" {
  bucket = local.state_bucket_name

  # Teardown destroys the three platform roots and then deletes this bucket by
  # hand, after checking that nothing else needs its state. force_destroy is off
  # so that `terraform destroy` here cannot empty it by accident.
  lifecycle {
    prevent_destroy = true
  }
}

resource "aws_s3_bucket_versioning" "state" {
  bucket = aws_s3_bucket.state.id

  versioning_configuration {
    status = "Enabled"
  }
}

resource "aws_s3_bucket_public_access_block" "state" {
  bucket = aws_s3_bucket.state.id

  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_ownership_controls" "state" {
  bucket = aws_s3_bucket.state.id

  rule {
    object_ownership = "BucketOwnerEnforced"
  }
}

# SSE-S3 rather than SSE-KMS: state is encrypted at rest with no key to manage, no
# monthly key charge, and no kms:Decrypt to add to the CI plan role. A cell that
# must hold its state under a customer-managed key changes this one block.
resource "aws_s3_bucket_server_side_encryption_configuration" "state" {
  bucket = aws_s3_bucket.state.id

  rule {
    apply_server_side_encryption_by_default {
      sse_algorithm = "AES256"
    }
    bucket_key_enabled = true
  }
}

resource "aws_s3_bucket_lifecycle_configuration" "state" {
  bucket = aws_s3_bucket.state.id

  # Superseded state versions are the way back from a bad apply. They are expired
  # on a long window rather than kept forever, which keeps this bucket in the
  # "cents per GB" line of the cost model.
  rule {
    id     = "expire-noncurrent-state-versions"
    status = "Enabled"

    filter {}

    noncurrent_version_expiration {
      noncurrent_days = var.state_noncurrent_version_retention_days
    }

    # A lock file is a tiny object written and deleted on every apply. Clearing the
    # delete markers it leaves behind stops them accumulating without limit.
    expiration {
      expired_object_delete_marker = true
    }
  }

  rule {
    id     = "abort-incomplete-uploads"
    status = "Enabled"

    filter {}

    abort_incomplete_multipart_upload {
      days_after_initiation = 7
    }
  }

  depends_on = [aws_s3_bucket_versioning.state]
}

data "aws_iam_policy_document" "state_bucket" {
  # TLS only. Without this a request over plain HTTP would carry state, which holds
  # every resource attribute, in clear text.
  statement {
    sid    = "DenyInsecureTransport"
    effect = "Deny"

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    actions = ["s3:*"]

    resources = [
      aws_s3_bucket.state.arn,
      "${aws_s3_bucket.state.arn}/*",
    ]

    condition {
      test     = "Bool"
      variable = "aws:SecureTransport"
      values   = ["false"]
    }
  }

  statement {
    sid    = "DenyOutdatedTlsVersions"
    effect = "Deny"

    principals {
      type        = "*"
      identifiers = ["*"]
    }

    actions = ["s3:*"]

    resources = [
      aws_s3_bucket.state.arn,
      "${aws_s3_bucket.state.arn}/*",
    ]

    condition {
      test     = "NumericLessThan"
      variable = "s3:TlsVersion"
      values   = ["1.2"]
    }
  }
}

resource "aws_s3_bucket_policy" "state" {
  bucket = aws_s3_bucket.state.id
  policy = data.aws_iam_policy_document.state_bucket.json

  # A bucket policy that denies on a public-access-block-less bucket is the wrong
  # order: block first, then write the policy.
  depends_on = [aws_s3_bucket_public_access_block.state]
}
