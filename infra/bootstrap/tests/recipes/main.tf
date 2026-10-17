# Test helper for infra/bootstrap/tests/recipes.tftest.hcl, not a root anyone applies.
#
# Reads the recipes out of README text, and the backend blocks out of Terraform source, so the test
# can assert on them (change request 1.1). It holds no expected value of its own: each output lists
# what breaks a rule, and the test says which of its inputs may appear there.

terraform {
  required_version = ">= 1.11.0"
}

variable "readmes" {
  description = "README text keyed by a label: the file's path under infra/, or the name of a mutation."
  type        = map(string)
}

variable "backends" {
  description = "Terraform source text keyed by a label: a .tf file's path under infra/, or the name of a mutation."
  type        = map(string)
  default     = {}
}

locals {
  # Each fenced bash block and the prose before it. A block is one recipe: what an operator pastes
  # into one shell.
  segments = {
    for label, text in var.readmes : label => [
      for m in regexall("(?s)(.*?)```bash\\n(.*?)```", text) : { prose = m[0], code = m[1] }
    ]
  }

  # A Terraform command: its subcommand, and the rest of that command up to a line end or a
  # separator, so an offline command joined to another by && or ; cannot hide the second.
  terraform_command = "\\bterraform(?:\\s+-chdir=\\S+)?\\s+([a-z-]+)([^\\n;&|]*)"

  # The commands that work on files alone, with no credentials and no project. init counts only
  # with -backend=false, which leaves the state bucket alone.
  offline = ["fmt", "validate", "test", "version"]

  # Whether each block reaches AWS from a workstation: an AWS CLI command, or a Terraform command
  # that is not offline. A block writing under $RUNNER_TEMP is CI's, which runs as the CI role, and
  # the project's profile does not exist on a runner, so no workstation rule applies to it.
  workstation = {
    for label, segments in local.segments : label => [
      for s in segments : !strcontains(s.code, "$RUNNER_TEMP") && (
        length(regexall("(?:^|[\\s(|;&])aws\\s+[a-z0-9-]+\\s", s.code)) > 0
        || anytrue([
          for c in regexall(local.terraform_command, s.code) :
          !(contains(local.offline, c[0]) || (c[0] == "init" && strcontains(c[1], "-backend=false")))
        ])
      )
    ]
  }

  # Whether each block applies: a workstation block that runs terraform apply. The first one in a
  # README is where its recipe has to have said what comes before it.
  applies = {
    for label, segments in local.segments : label => [
      for i, s in segments : local.workstation[label][i] && anytrue([
        for c in regexall(local.terraform_command, s.code) : c[0] == "apply"
      ])
    ]
  }

  # The prose a reader has passed on the way to a README's first apply recipe; empty when it has none.
  prose_before_first_apply = {
    for label, segments in local.segments : label => join("\n", [
      for i, s in segments : s.prose if i <= try(index(local.applies[label], true), -1)
    ])
  }
}

output "plans_inside_the_repository" {
  description = <<-EOT
    Per label, each plan path given to -out that is not in a directory outside the repository:
    one made by mktemp -d and held in $plan_dir, or $RUNNER_TEMP on a GitHub runner.
  EOT
  value = {
    for label, text in var.readmes : label => [
      for m in regexall("(?:^|\\s)-out(?:=|\\s+)(\\S+)", text) : m[0]
      if !(
        startswith(m[0], "\"$RUNNER_TEMP/")
        || (startswith(m[0], "\"$plan_dir/") && strcontains(text, "plan_dir=$(mktemp -d)"))
      )
    ]
  }
}

output "workstation_recipes" {
  description = "Per label, how many blocks reach AWS from a workstation: the blocks the project rule applies to."
  value       = { for label, flags in local.workstation : label => length([for f in flags : f if f]) }
}

output "recipes_without_step_0" {
  description = <<-EOT
    The labels whose first apply recipe is not preceded by step 0: prose naming Step 0 and
    `infra/bootstrap`, the root that creates the state bucket every other root's backend needs.
  EOT
  value = [
    for label, prose in local.prose_before_first_apply : label
    if anytrue(local.applies[label]) && !(strcontains(prose, "Step 0") && strcontains(prose, "`infra/bootstrap`"))
  ]
}

output "inits_without_the_region" {
  description = <<-EOT
    Per label, each workstation block that initialises an S3 backend, by giving it the state
    bucket, without also giving it the project's selected Region: -backend-config="region=$AWS_REGION".
  EOT
  value = {
    for label, segments in local.segments : label => [
      for i, s in segments : s.code
      if local.workstation[label][i]
      && strcontains(s.code, "-backend-config=\"bucket=")
      && !strcontains(s.code, "-backend-config=\"region=$AWS_REGION\"")
    ]
  }
}

output "backends_pinning_a_region" {
  description = "The labels whose backend \"s3\" block sets region itself instead of taking it at init."
  value = [
    for label, text in var.backends : label
    if can(regex("backend\\s+\"s3\"\\s*\\{[^}]*\\bregion\\s*=", text))
  ]
}

output "unnamed_projects" {
  description = <<-EOT
    Per label, each block that reaches AWS from a workstation without first sourcing
    tools/aws-project.env, which names the project's profile and its selected Region.
  EOT
  value = {
    for label, segments in local.segments : label => [
      for i, s in segments : s.code
      if local.workstation[label][i] && !can(regex("^(?:[ \\t]*(?:#[^\\n]*)?\\n)*set -a; \\. tools/aws-project\\.env; set \\+a", s.code))
    ]
  }
}
