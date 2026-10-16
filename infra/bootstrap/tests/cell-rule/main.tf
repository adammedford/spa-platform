# Test helper for infra/bootstrap/tests/root.tftest.hcl, not a root anyone applies.
#
# Reads the cell-name rule out of Terraform source text, and the appId definition out of the frozen
# registry schema, so the test can assert on them. It holds no expected value of its own: the
# decision's expression and the probe verdicts are written in the test, independently of this.

terraform {
  required_version = ">= 1.11.0"
}

variable "sources" {
  description = "Terraform source text keyed by a label, each expected to validate var.cell with can(regex(\"...\", var.cell))."
  type        = map(string)
}

variable "registry_schema" {
  description = "The text of contracts/registry.schema.json."
  type        = string
}

output "rules" {
  description = "The regular expression each source validates var.cell with, or null when the source has none or more than one."
  value = {
    for label, text in var.sources :
    label => try(one(regexall("can\\(regex\\(\"([^\"]*)\", var\\.cell\\)\\)", text))[0], null)
  }
}

output "app_id" {
  description = "The appId definition: minLength, maxLength and pattern."
  value       = jsondecode(var.registry_schema)["$defs"]["appId"]
}
