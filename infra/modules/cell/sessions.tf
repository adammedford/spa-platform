# Continuity (#338, #417). The DynamoDB session store's table, behind session_store = "dynamodb".
#
# The count line is the one P-01 admits: the platform root's tripwire refuses every DynamoDB
# declaration in this module's source except this one, with exactly this count, and a plan
# assertion there holds that the default inputs plan no instance of it (change request
# issue-338-p-01-dynamodb-tripwire, decision 1). So a Phase 1 cell plans no table, and changing
# the count, the type or the name fails P-01.
#
# The key, the index and the time-to-live attribute are the ones the store reads and writes (change
# request P2-1, item 5 and Decision 7): pk is the session's key, appId is what RemoveAppAsync queries
# to purge an app's sessions, and expiresAt is the absolute expiry DynamoDB's TTL sweeps on. The
# index projects keys only, because the purge deletes by pk and reads nothing else. On-demand
# billing, so an idle cell pays for no provisioned capacity. Encryption is DynamoDB's default: no
# customer-managed key at G6 (#332 D6). No index on subject: that is G7's (ADR-0028), and the task
# role's grant names the appId index alone, so a second index needs its own reviewed grant too.
resource "aws_dynamodb_table" "sessions" {
  count = local.session_store_is_dynamodb ? 1 : 0

  name         = local.session_table_name
  billing_mode = "PAY_PER_REQUEST"
  hash_key     = "pk"

  attribute {
    name = "pk"
    type = "S"
  }

  attribute {
    name = "appId"
    type = "S"
  }

  # key_schema rather than the index's own hash_key, which the provider deprecates for it from
  # 6.68.0, the version the roots pin.
  global_secondary_index {
    name            = local.session_table_index_name
    projection_type = "KEYS_ONLY"

    key_schema {
      attribute_name = "appId"
      key_type       = "HASH"
    }
  }

  ttl {
    attribute_name = "expiresAt"
    enabled        = true
  }

  tags = { Name = local.session_table_name }
}
