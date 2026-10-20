# Workstation tools for macOS.
#
#   brew bundle check --verbose   # what is missing
#   brew bundle                   # install it
#
# tools/preflight.sh checks the same tools plus credentials and network.
# Versions Homebrew offered on 11 October 2026 are in docs/dependencies.md.

tap "hashicorp/tap"

cask "dotnet-sdk"                # .NET SDK 10.0. Everything that builds.
brew "hashicorp/tap/terraform"   # At least the floor in .terraform-version. Task 1.0 onwards.
brew "hashicorp/tap/hc-install"  # Installs the floor itself, which preflight prints the line for.
brew "awscli"                    # AWS CLI v2. Terraform credentials, certs.sh, tunnel.sh.
brew "gh"                        # The GitHub CLI: the tracker and tools/land.sh. Then gh auth login.
cask "session-manager-plugin"    # The tunnel to the internal ALB. Task 1.6b.

# A container engine is needed from task 1.7, when deploy.sh builds the two images for ARM64.
# Docker Desktop, by decision D-2 in docs/dependencies.md; Colima with the docker and
# docker-buildx formulas is the swap if the licence terms stop fitting.
cask "docker-desktop"            # The container engine and docker buildx. Task 1.7.
