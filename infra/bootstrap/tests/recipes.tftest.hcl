# The recipes in every README under infra/, read as text (change request 1.1).
#   terraform -chdir=infra/bootstrap init -backend=false
#   terraform -chdir=infra/bootstrap test
#
# The READMEs are found by glob rather than listed, so a README added under infra/ is held to the
# same rules the day it lands. Each rule has a mutation beside it: the same extraction applied to a
# README with the rule broken on purpose must report it, or the rule's assertion proves nothing. A
# mutation is a replace() on a real README, so one whose text no longer matches changes nothing,
# is reported clean, and fails its own assertion instead of passing quietly.

variables {
  readmes = merge(
    { for f in fileset("..", "**/README.md") : f => file("../${f}") if !strcontains(f, ".terraform/") },
    {
      # The plan path shortened back to a relative one: the edit the first rule exists to stop.
      "mutation: a relative plan path" = replace(file("../envs/poc/idp/README.md"), "-out=\"$plan_dir/", "-out=\"")
      # The line that names the project dropped from every recipe in the identity README.
      "mutation: the project left unnamed" = replace(file("../envs/poc/idp/README.md"), "set -a; . tools/aws-project.env; set +a", "")
      # The apps README's recipe starting where the identity root's leaves off, with no step 0.
      "mutation: no step 0" = replace(file("../envs/poc/apps/README.md"), "Step 0", "First")
      # The state's Region written out at init instead of taken from the project.
      "mutation: an init with a Region of its own" = replace(file("../envs/poc/idp/README.md"), "region=$AWS_REGION", "region=us-east-1")
      # The identity README's recipes fenced as sh rather than bash, with the project's line dropped.
      "mutation: recipes fenced as sh" = replace(replace(file("../envs/poc/idp/README.md"), "```bash", "```sh"), "set -a; . tools/aws-project.env; set +a", "")
      # The identity README's apply split after `terraform`, with the project's line dropped.
      "mutation: a command split before its subcommand" = replace(file("../envs/poc/idp/README.md"), "set -a; . tools/aws-project.env; set +a   # AWS_PROFILE and the Region, or the wrong project\nterraform -chdir=infra/envs/poc/idp apply", "terraform \\\n  -chdir=infra/envs/poc/idp apply")
      # The identity README's apply naming $RUNNER_TEMP in a comment, with the project's line dropped.
      "mutation: a workstation block naming $RUNNER_TEMP" = replace(file("../envs/poc/idp/README.md"), "set -a; . tools/aws-project.env; set +a   # AWS_PROFILE and the Region, or the wrong project\nterraform -chdir=infra/envs/poc/idp apply", "# Not CI's, though it names $RUNNER_TEMP\nterraform -chdir=infra/envs/poc/idp apply")
    }
  )

  backends = merge(
    { for f in fileset("..", "**/*.tf") : f => file("../${f}") if !strcontains(f, ".terraform/") },
    {
      # The identity root's backend block given a Region again, as it carried before this rule.
      "mutation: a backend pinning its region" = replace(file("../envs/poc/idp/versions.tf"), "use_lockfile = true", "use_lockfile = true\n    region       = \"us-east-1\"")
    }
  )
}

# The three READMEs change request 1.1 names. Each must yield recipes, or a rule below could pass
# because the extraction found nothing to check.
run "the_three_readmes_hold_recipes_that_reach_aws" {
  command = plan

  module {
    source = "./tests/recipes"
  }

  assert {
    condition = alltrue([
      for label in ["bootstrap/README.md", "envs/poc/idp/README.md", "envs/poc/apps/README.md"] :
      output.workstation_recipes[label] > 0
    ])
    error_message = "infra/bootstrap, infra/envs/poc/idp and infra/envs/poc/apps must each have a README with at least one recipe that reaches AWS: ${jsonencode(output.workstation_recipes)}."
  }
}

run "plans_are_written_outside_the_repository" {
  command = plan

  module {
    source = "./tests/recipes"
  }

  # A saved plan holds, in cleartext, every value its changes carry
  # (https://developer.hashicorp.com/terraform/cli/commands/plan#out-filename): the identity root's
  # carries every generated test-user password and the apps root's every OAuth client secret. A
  # plan inside the working tree is one `git add -A` from a commit.
  assert {
    condition = alltrue([
      for label, paths in output.plans_inside_the_repository : length(paths) == 0 if !startswith(label, "mutation:")
    ])
    error_message = "A README under infra/ writes a plan inside the repository: ${jsonencode({ for label, paths in output.plans_inside_the_repository : label => paths if length(paths) > 0 && !startswith(label, "mutation:") })}. Write it to a directory made by plan_dir=$(mktemp -d), or under $RUNNER_TEMP in CI (change request 1.1)."
  }

  assert {
    condition     = length(output.plans_inside_the_repository["mutation: a relative plan path"]) > 0
    error_message = "Mutation check: the identity README with its plan path made relative again must be reported, or the rule above proves nothing."
  }
}

run "every_recipe_that_reaches_aws_names_the_project_first" {
  command = plan

  module {
    source = "./tests/recipes"
  }

  # The default profile and the profile named for this work point at different projects, and a
  # sitting once applied the bootstrap root into the wrong one. tools/aws-project.env names the
  # intended profile and Region; a recipe that does not source it builds wherever the shell points.
  assert {
    condition = alltrue([
      for label, blocks in output.unnamed_projects : length(blocks) == 0 if !startswith(label, "mutation:")
    ])
    error_message = "A recipe under infra/ reaches AWS before it names the project: ${jsonencode({ for label, blocks in output.unnamed_projects : label => blocks if length(blocks) > 0 && !startswith(label, "mutation:") })}. Begin it with: set -a; . tools/aws-project.env; set +a (change request 1.1)."
  }

  assert {
    condition     = length(output.unnamed_projects["mutation: the project left unnamed"]) > 0
    error_message = "Mutation check: the identity README with the project's line removed must be reported, or the rule above proves nothing."
  }

  # Three forms the extraction must still read, each with the project's line dropped: a block fenced
  # sh, a command continued before its subcommand, and a block that names $RUNNER_TEMP without
  # saving a plan there. Each is a form an edit can take without meaning to leave the rules.
  assert {
    condition     = length(output.unnamed_projects["mutation: recipes fenced as sh"]) > 0
    error_message = "Mutation check: the identity README fenced as sh, with the project's line removed, must be reported, or a recipe fenced sh escapes every rule here."
  }

  assert {
    condition     = length(output.unnamed_projects["mutation: a command split before its subcommand"]) > 0
    error_message = "Mutation check: the identity README's apply split after `terraform`, with the project's line removed, must be reported, or a continued command escapes the rule above."
  }

  assert {
    condition     = length(output.unnamed_projects["mutation: a workstation block naming $RUNNER_TEMP"]) > 0
    error_message = "Mutation check: a workstation apply that names $RUNNER_TEMP only in a comment must be reported: only a block that saves its plan there is CI's."
  }
}

run "every_apply_recipe_begins_at_step_0" {
  command = plan

  module {
    source = "./tests/recipes"
  }

  # Every other root keeps its state in the bucket infra/bootstrap creates, so none of them can be
  # initialised before it: a recipe that starts anywhere else starts from a bucket that may not
  # exist.
  assert {
    condition     = length([for label in output.recipes_without_step_0 : label if !startswith(label, "mutation:")]) == 0
    error_message = "An apply recipe under infra/ does not begin at step 0: ${jsonencode([for label in output.recipes_without_step_0 : label if !startswith(label, "mutation:")])}. Before its first apply, say that Step 0 is `infra/bootstrap` (change request 1.1)."
  }

  assert {
    condition     = contains(output.recipes_without_step_0, "mutation: no step 0")
    error_message = "Mutation check: the apps README with its step 0 removed must be reported, or the rule above proves nothing."
  }
}

run "the_state_takes_the_projects_region_at_init" {
  command = plan

  module {
    source = "./tests/recipes"
  }

  # A backend block cannot read a variable, so the Region a root's terraform.tfvars sets never
  # reaches it. Written into the block, the Region is a second copy of the project's that nothing
  # keeps in step; given at init from AWS_REGION, it is the one tools/aws-project.env names.
  assert {
    condition = alltrue([
      for label, blocks in output.inits_without_the_region : length(blocks) == 0 if !startswith(label, "mutation:")
    ])
    error_message = "A recipe under infra/ initialises the S3 backend without the project's Region: ${jsonencode({ for label, blocks in output.inits_without_the_region : label => blocks if length(blocks) > 0 && !startswith(label, "mutation:") })}. Pass -backend-config=\"region=$AWS_REGION\" beside the bucket (change request 1.1)."
  }

  assert {
    condition     = length([for label in output.backends_pinning_a_region : label if !startswith(label, "mutation:")]) == 0
    error_message = "A backend \"s3\" block under infra/ sets its own region: ${jsonencode([for label in output.backends_pinning_a_region : label if !startswith(label, "mutation:")])}. Leave it out and give it at init, from AWS_REGION (change request 1.1)."
  }

  assert {
    condition     = length(output.inits_without_the_region["mutation: an init with a Region of its own"]) > 0
    error_message = "Mutation check: the identity README initialising with a Region written out must be reported, or the first rule here proves nothing."
  }

  assert {
    condition     = contains(output.backends_pinning_a_region, "mutation: a backend pinning its region")
    error_message = "Mutation check: the identity root's backend block with a region added back must be reported, or the second rule here proves nothing."
  }
}
