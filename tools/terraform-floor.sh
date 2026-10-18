#!/usr/bin/env bash
# Which Terraform runs here, and whether it is the floor. Owner: integrator. Sourced, not run:
#
#     . tools/terraform-floor.sh
#
# The floor is the oldest Terraform this repository supports, and the one CI runs. One file names
# it, .terraform-version at the top level, and three things read that file through this one:
# tools/check.sh, which runs the floor when this machine has it; tools/preflight.sh, which says
# whether it does and how to install it; and CI's Terraform job, which installs exactly it. So
# raising the floor is a one-line change, and none of the three can read the file differently
# (#69). Every root's required_version says ">= " the same version, and those lines live with the
# roots under infra/.
#
# Why the check runs the floor rather than whatever `terraform` is on PATH: anything a later release
# accepts that the floor refuses would pass here and fail in CI. On 2026-10-17 this Mac ran 1.16.5,
# and six overnight agents each downloaded 1.11.0 into a scratch directory to run their tests on
# both, 36 tool calls repeated lane by lane.
#
# Where the floor is installed: ~/.local/share/terraform/<version>/terraform, one copy per machine
# for every clone and worktree. Outside PATH on purpose, so the `terraform` an apply runs stays
# whatever the person installed. HashiCorp's hc-install puts it there, verifying the release's
# checksums and signature (https://github.com/hashicorp/hc-install#cli); tools/preflight.sh prints
# the command. A `terraform` on PATH that reports the floor counts as well, which is how a version
# manager reading .terraform-version (tfenv, tfswitch) and CI's runner both satisfy it.
#
# Written for the bash 3.2 macOS ships, and safe under set -euo pipefail: no arrays, no external
# tools, and every function returns 0 unless its comment says otherwise.

# terraform_floor_read FILE: the version FILE names, on standard output. Fails, printing nothing,
# unless the whole file is one exact MAJOR.MINOR.PATCH version. setup-terraform would read a range
# such as "<1.12.0", or "latest", as an instruction to install the newest release that matches
# (https://github.com/hashicorp/setup-terraform/blob/v3/action.yml), and CI would quietly stop
# running the floor.
terraform_floor_read() {
  local text pattern='^[0-9]+\.[0-9]+\.[0-9]+$'
  [ -f "$1" ] || return 1
  # $(< file) drops the trailing newline and nothing else, so a second line, a space, a comment or
  # a carriage return all fail the pattern.
  text="$(< "$1")"
  [[ $text =~ $pattern ]] || return 1
  printf '%s\n' "$text"
}

# terraform_floor_dir VERSION: the directory the floor VERSION is installed in on this machine.
terraform_floor_dir() {
  printf '%s/.local/share/terraform/%s\n' "${HOME:-}" "$1"
}

# terraform_version_of BINARY: the version BINARY reports, such as 1.16.5, or nothing when it
# reports none. Terraform's first line of `terraform version` is "Terraform v<version>".
# CHECKPOINT_DISABLE stops `terraform version` asking checkpoint-api.hashicorp.com whether a newer
# release exists, which costs a network round trip and prints an upgrade notice
# (https://developer.hashicorp.com/terraform/cli/commands#upgrade-and-security-bulletin-checks).
terraform_version_of() {
  local out first newline='
'
  out="$(CHECKPOINT_DISABLE=1 "$1" version 2> /dev/null)" || out=""
  first="${out%%"$newline"*}"
  case "$first" in
    "Terraform v"?*) printf '%s\n' "${first#Terraform v}" ;;
  esac
  return 0
}

# terraform_choose FLOOR: picks the Terraform to run, setting two variables:
#   terraform_bin    the binary, or empty when there is none
#   terraform_used   the version it reports, which equals FLOOR when it is the floor
# The floor's directory first, then the terraform on PATH; the first that reports FLOOR is chosen.
# With neither at the floor, the terraform on PATH, whatever it reports: the caller says so.
terraform_choose() {
  local candidate version
  terraform_bin=""
  terraform_used=""
  candidate="$(terraform_floor_dir "$1")/terraform"
  if [ -x "$candidate" ]; then
    version="$(terraform_version_of "$candidate")"
    if [ "$version" = "$1" ]; then
      terraform_bin="$candidate"
      terraform_used="$version"
      return 0
    fi
  fi
  if candidate="$(command -v terraform)"; then
    terraform_bin="$candidate"
    terraform_used="$(terraform_version_of "$candidate")"
  fi
  return 0
}
