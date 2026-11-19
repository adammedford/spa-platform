variable "cell" {
  description = "The cell name (ADR-0012). The same value as Platform:Cell and as cell in the infrastructure outputs."
  type        = string
  default     = "poc"
}

variable "region" {
  description = "AWS region. Every price in the cost model is for us-east-1."
  type        = string
  default     = "us-east-1"
}

variable "state_bucket" {
  description = <<-EOT
    The Terraform state bucket, where the identity root's state is read from. Leave empty for
    spa-platform-<cell>-tfstate-<account id>, the bootstrap's default name (infra/bootstrap/main.tf).
  EOT
  type        = string
  default     = ""
}

variable "definition_dirs" {
  description = <<-EOT
    The directories whose *.json files are app definitions (contracts/app-definition.schema.json).
    One client, one branding style and one parameter per file, and the same three per entry of a
    file's fragments[] (#999). The default is fixtures/apps/; onboarding an app is copying its
    definition there (docs/plan/05-infrastructure-and-cost.md -> Workstation access). The
    fragment-bearing fixture definitions sit elsewhere, so no ordinary apply creates a fragment
    client (#961, "The deployment prerequisite").
  EOT
  type        = list(string)
  default     = ["../../../../fixtures/apps"]
}
