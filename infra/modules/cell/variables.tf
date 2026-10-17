variable "cell" {
  description = <<-EOT
    The cell name. A cell is the deployment unit: ALB, service, bucket, registry,
    secrets prefix and session store (ADR-0012). It is part of every resource name,
    and a Phase 2 cell is one more instance of this module.
  EOT
  type        = string

  # The same rule as infra/bootstrap and the platform root, character for character (change
  # request 1.5, ADR-0014 item 7). 3 is the frozen appId minimum the cell is published as
  # (contracts/infra-outputs.schema.json -> cell); 22 keeps spa-<cell> within the load balancer's
  # 32-character name limit. No edge hyphen (ADR-0014 item 7); a trailing one would also end
  # spa-<cell>, the load balancer's and target group's name and the first segment of each
  # repository's, and AWS refuses a hyphen at the end of either.
  # https://docs.aws.amazon.com/elasticloadbalancing/latest/APIReference/API_CreateLoadBalancer.html
  # https://docs.aws.amazon.com/AmazonECR/latest/APIReference/API_CreateRepository.html
  validation {
    condition     = can(regex("^[a-z0-9][a-z0-9-]{1,20}[a-z0-9]$", var.cell))
    error_message = "The cell name is 3 to 22 lowercase letters, digits or hyphens, and does not start or end with a hyphen. It goes into bucket and repository names."
  }
}

variable "vpc_cidr" {
  description = <<-EOT
    The cell's address range. Two /24 subnets are carved out of it, one per zone.
    Platform configuration's ForwardedHeaders:TrustedNetworks names this range, so
    the gateway trusts X-Forwarded-For from the load balancer and from nowhere else.

    No default: the root decides the range, and this module is called by each cell's
    root. The poc root's default is 10.20.0.0/16.
  EOT
  type        = string

  validation {
    # The whole condition is inside can(), because a value with no "/" would make
    # split(...)[1] raise an evaluation error instead of showing error_message.
    #
    # /23 is the floor, not /24: two /24 subnets are carved out, and at /24 there are
    # no bits left to carve with, so cidrsubnet would fail at apply rather than here.
    condition     = can(cidrnetmask(var.vpc_cidr)) && can(tonumber(split("/", var.vpc_cidr)[1]) <= 23) && tonumber(split("/", var.vpc_cidr)[1]) <= 23
    error_message = "vpc_cidr is an IPv4 CIDR block of /23 or larger, because two /24 subnets are carved out of it."
  }
}

variable "gateway_port" {
  description = <<-EOT
    The port the gateway container listens on. The load balancer sends to it and
    nothing else reaches it: test P-06 shows a connection from outside to the task's
    public address on this port timing out.
  EOT
  type        = number
  default     = 8080
}

variable "bucket_name" {
  description = <<-EOT
    The platform bucket's name. Empty means the convention,
    spa-platform-{cell}-{account id}-{region}-an, from the account and Region the
    root's provider points at: the name contracts/infra-outputs.example.json and
    Registry:Bucket in platform configuration show (change request 1.5a).

    The bucket is always created in S3's account regional namespace, so an override
    keeps the -{account id}-{region}-an suffix and changes only what comes before it;
    the plan fails on any other name. Every consumer reads the platform root's `bucket`
    output rather than deriving the name (contracts/s3-layout.md), except
    infra/bootstrap, which applies first: set its platform_bucket_name to the same
    value. This is the platform's one private bucket; the Terraform state bucket is a
    different one, created by infra/bootstrap.
  EOT
  type        = string
  default     = ""
}

variable "teardown_principal_arns" {
  description = <<-EOT
    The principals allowed to delete objects under apps/. Everyone else is denied,
    which is what makes a release prefix write-once: without the delete rule a
    delete followed by a write would replace a release (contracts/s3-layout.md).

    It cannot be empty. A bucket nobody may empty is a bucket teardown.sh cannot
    destroy, and P-05 would then fail forever. The platform root passes the
    teardown role infra/bootstrap creates, looked up by its derived name, then any
    additional principals (change request 1.5, ADR-0014).
  EOT
  type        = list(string)

  validation {
    condition     = length(var.teardown_principal_arns) > 0
    error_message = "Name at least one principal that may delete under apps/, or nothing can ever empty the bucket and teardown cannot destroy it."
  }
}

variable "certificate_domain" {
  description = <<-EOT
    The domain the cell's wildcard certificate covers, without the wildcard: the
    load balancer's listener presents *.{certificate_domain}. tools/scripts/certs.sh
    imports that certificate into ACM, tagged with this cell, and the module finds it
    there; Terraform never creates it.

    No default: the domain belongs to the cell, so the root decides it. The poc
    root's default is spa-poc.test, which is certs.sh's default too.
  EOT
  type        = string

  validation {
    # The base certificate domain uses the same DNS grammar as albDnsName. Read
    # the repository contract rather than maintaining a second copy of its pattern.
    condition     = can(regex(jsondecode(file("${path.module}/../../../contracts/infra-outputs.schema.json"))["$defs"].dnsName.pattern, var.certificate_domain))
    error_message = "certificate_domain is a dotted lowercase name with no wildcard and no trailing dot, such as spa-poc.test."
  }
}

variable "health_check_path" {
  description = <<-EOT
    The path the load balancer's health check requests. It follows the image the
    service runs: / for the public sample image the service starts on, and /readyz
    for the gateway, which answers 200 only once a registry snapshot is live
    (contracts/pipeline.md).
  EOT
  type        = string
  default     = "/"

  validation {
    condition     = can(regex("^/[A-Za-z0-9._~/-]*$", var.health_check_path))
    error_message = "health_check_path is an absolute path with no query string, such as / or /readyz."
  }
}

# --- Task 1.4a: the images -------------------------------------------------------------------

variable "gateway_image" {
  description = <<-EOT
    The gateway container's image. No default, and in particular no default that names a tag:
    deploy.sh builds and pushes by digest and then asserts the running digest, and every other
    apply passes the digest already in state, which is what keeps a routine apply from moving a
    running image (docs/plan/05-infrastructure-and-cost.md -> What stays out of Terraform).

    The first apply of this root passes the public sample image, because the service is applied
    before the gateway image exists; the health check follows the image (health_check_path).
  EOT
  type        = string

  validation {
    condition     = length(var.gateway_image) > 0
    error_message = "gateway_image is the image the task runs. Pass the sample image on the first apply, and a digest afterwards."
  }
}

variable "mock_api_image" {
  description = <<-EOT
    The mock API container's image, or empty for a task with the gateway alone. Empty is the
    default so the service applies cleanly before that image is built: the mock API is task 5.3's
    and the first deployment reaches it after this root (docs/plan/05-infrastructure-and-cost.md ->
    First deployment).

    Like gateway_image, no default that names a tag.
  EOT
  type        = string
  default     = ""
}

variable "mock_api_app1_client_id" {
  description = <<-EOT
    The client the mock API's orders listener admits, which is app 1's
    (fixtures/MockApi/MockApiOptions.cs -> Default, port 5080). An input, because the apps root
    decides client ids and nothing about one is written into this module; that is also what keeps
    onboarding a third app out of the platform root's plan (F-07).

    Admission is the API owner's decision and not the gateway's (contracts/upstream-contract.md).
  EOT
  type        = string
  default     = ""

  validation {
    # fixtures/MockApi/Program.cs throws on a missing client id, so the alternative to failing the
    # plan here is a container that crash-loops after the apply reported success.
    condition     = var.mock_api_image == "" || length(var.mock_api_app1_client_id) > 0
    error_message = "A mock API image needs mock_api_app1_client_id: fixtures/MockApi/Program.cs refuses to start without it."
  }
}

variable "mock_api_app2_client_id" {
  description = <<-EOT
    The client the mock API's reports listener admits, which is app 2's
    (fixtures/MockApi/MockApiOptions.cs -> Default, port 5081). An input, for the same reason as
    mock_api_app1_client_id.
  EOT
  type        = string
  default     = ""

  validation {
    condition     = var.mock_api_image == "" || length(var.mock_api_app2_client_id) > 0
    error_message = "A mock API image needs mock_api_app2_client_id: fixtures/MockApi/Program.cs refuses to start without it."
  }
}

# --- Task 1.4a: platform configuration the task environment carries ---------------------------
#
# contracts/platform-config.schema.json is the shape. Everything here is a bound a registry entry
# cannot widen, which is why these are the root's inputs and not an entry's fields.

variable "issuers" {
  description = <<-EOT
    The issuers a registry entry may name, keyed by the name an entry uses in oauth.issuer. The
    values come from the identity root's `issuer` and `cognito_domain` outputs, which is why this is
    an input rather than a lookup: the platform root and the identity root are separate states
    (docs/plan/05-infrastructure-and-cost.md -> Terraform boundaries).

    Assumption A4 is one issuer per environment. A task carrying the mock API requires exactly one,
    because that process validates against one issuer (fixtures/MockApi/Program.cs).
  EOT
  type = map(object({
    authority      = string
    profile        = string
    browser_origin = string
  }))

  validation {
    # contracts/platform-config.schema.json gives Issuers minProperties 1: a gateway with no issuer
    # can authenticate nobody, and the binder would see the section missing altogether.
    condition     = length(var.issuers) > 0
    error_message = "Name at least one issuer: contracts/platform-config.schema.json requires the Issuers section to hold one."
  }

  validation {
    # The permitted values are IIdpProfile.Name, read from the contract rather than copied, so a
    # profile added or removed at G1b does not leave a stale list here.
    condition = alltrue([
      for issuer in values(var.issuers) : contains(
        jsondecode(file("${path.module}/../../../contracts/platform-config.schema.json")).properties.Issuers.additionalProperties.properties.Profile.enum,
        issuer.profile,
      )
    ])
    error_message = "Each issuer's profile must be one of the values contracts/platform-config.schema.json lists for Issuers:{name}:Profile."
  }

  validation {
    condition = alltrue([
      for issuer in values(var.issuers) :
      can(regex("^https://[^\\s]+$", issuer.authority)) && can(regex("^https://[^\\s/?#]+$", issuer.browser_origin))
    ])
    error_message = "An issuer's authority is an https URL, and its browser origin is https plus an authority only: it goes into form-action (contracts/platform-config.schema.json)."
  }
}

variable "allowed_host_suffixes" {
  description = <<-EOT
    The suffixes a registry host may end with, lowercase and including the port when hosts carry
    one. In the proof of concept a host is reached through the tunnel, so it carries the local
    port: app1.spa-poc.test:8443 (docs/plan/05-infrastructure-and-cost.md -> Workstation access).
  EOT
  type        = list(string)

  validation {
    condition     = length(var.allowed_host_suffixes) > 0 && length(var.allowed_host_suffixes) == length(distinct(var.allowed_host_suffixes))
    error_message = "Name at least one host suffix, with no duplicates (contracts/platform-config.schema.json -> Policy.AllowedHostSuffixes)."
  }
}

variable "allowed_upstreams" {
  description = <<-EOT
    The upstream origins a registry entry may name. The two the mock API listens on in the proof of
    concept are loopback ports inside the task (fixtures/apps/*.json -> apis[].upstream).
  EOT
  type        = list(string)

  validation {
    condition     = length(var.allowed_upstreams) > 0 && length(var.allowed_upstreams) == length(distinct(var.allowed_upstreams))
    error_message = "Name at least one upstream origin, with no duplicates (contracts/platform-config.schema.json -> Policy.AllowedUpstreams)."
  }
}

variable "registry_key" {
  description = <<-EOT
    The registry object's key in the cell's bucket. registry/registry.json in every environment so
    far, which is the one object contracts/s3-layout.md puts outside apps/.
  EOT
  type        = string
  default     = "registry/registry.json"

  validation {
    # The contract's own pattern: a key, so no leading slash and no whitespace.
    condition = can(regex(
      jsondecode(file("${path.module}/../../../contracts/platform-config.schema.json")).properties.Registry.properties.Key.pattern,
      var.registry_key,
    ))
    error_message = "registry_key is an S3 key with no leading slash, such as registry/registry.json (contracts/platform-config.schema.json -> Registry.Key)."
  }
}

variable "registry_poll_seconds" {
  description = <<-EOT
    The registry's conditional-GET interval, jittered by up to 20% in the gateway. It is also how
    long an activation or a rollback takes to go live (docs/plan/05-infrastructure-and-cost.md ->
    Deployment and rollback).
  EOT
  type        = number
  default     = 15

  validation {
    condition     = var.registry_poll_seconds >= 1 && floor(var.registry_poll_seconds) == var.registry_poll_seconds
    error_message = "registry_poll_seconds is a whole number of seconds, at least 1 (contracts/platform-config.schema.json -> Registry.PollSeconds)."
  }
}

variable "session_store" {
  description = <<-EOT
    Where sessions live: memory in Phase 1, dynamodb in Phase 2 (ADR-0004, ADR-0012). In Phase 1 a
    gateway deployment therefore signs every user out.
  EOT
  type        = string
  default     = "memory"

  validation {
    condition = contains(
      jsondecode(file("${path.module}/../../../contracts/platform-config.schema.json")).properties.Session.properties.Store.enum,
      var.session_store,
    )
    error_message = "session_store must be one of the values contracts/platform-config.schema.json lists for Session:Store."
  }
}

variable "session_max_sessions" {
  description = "The bound on the in-memory session store (contracts/platform-config.schema.json -> Session.MaxSessions)."
  type        = number
  default     = 10000

  validation {
    condition     = var.session_max_sessions >= 1 && floor(var.session_max_sessions) == var.session_max_sessions
    error_message = "session_max_sessions is a whole number of sessions, at least 1."
  }
}

variable "log_retention_days" {
  description = <<-EOT
    How long the task's log group keeps a log. Explicit, because the alternative is not a default
    but "never expires", and plan row 1.4 asks for a decision either way. The deployed tests read a
    run's gateway log from this group, so it has to outlive a gate run comfortably.
  EOT
  type        = number
  default     = 14

  validation {
    # The values CloudWatch Logs accepts; 0 would mean never expire, which is the decision this
    # variable exists to avoid taking by accident.
    # https://docs.aws.amazon.com/AmazonCloudWatchLogs/latest/APIReference/API_PutRetentionPolicy.html
    condition = contains(
      [1, 3, 5, 7, 14, 30, 60, 90, 120, 150, 180, 365, 400, 545, 731, 1096, 1827, 2192, 2557, 2922, 3288, 3653],
      var.log_retention_days,
    )
    error_message = "log_retention_days must be one of the retention periods CloudWatch Logs accepts, and not 0, which means never expire."
  }
}
