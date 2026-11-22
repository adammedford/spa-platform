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
    The cell's address range. Two /24 subnets are carved out of it, one per zone, and
    two more under multi_task, which then needs /22 or larger.
    Platform configuration's ForwardedHeaders:TrustedNetworks names the two public
    subnets the load balancer sits in, not this whole range, so the gateway trusts
    X-Forwarded-For from the load balancer and from nowhere else; under multi_task
    the private subnets stay untrusted.

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
    output rather than deriving the name, except infra/bootstrap, which applies first:
    set its platform_bucket_name to the same value (README.md -> The bucket's name). This is the platform's one private bucket; the Terraform state bucket is a
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
    additional principals (change request 1.5, ADR-0014; README.md -> The bucket's
    name, for the role's name and the role-ARN rule).
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

variable "gateway_writable_root" {
  description = <<-EOT
    true only while the gateway's container runs the public sample image, on a fresh cell's
    first apply: it gives that container a writable root and no /tmp volume, the shape it had
    before #787. The sample image, nginx-unprivileged, runs as uid 101 and writes its temp files
    under /tmp, which a read-only root's task volume, owned by root at 0755, refuses (#1380).
    tools/scripts/platform-tfvars.sh writes true beside the sample's pin; deploy.sh and
    rollback.sh apply the gateway with false. The default is the gateway's read-only shape.
  EOT
  type        = bool
  default     = false

  # The sample answers its health check on / (health_check_path), the gateway on /readyz. A root
  # whose terraform.tfvars still says true from the sample's first apply is refused once an apply
  # names the gateway's health check, rather than shipping the gateway on a writable root.
  validation {
    condition     = !var.gateway_writable_root || var.health_check_path == "/"
    error_message = "gateway_writable_root is the sample image's, whose health check is /: a writable root with any other health_check_path, such as the gateway's /readyz, is refused. deploy.sh and rollback.sh pass gateway_writable_root = false."
  }
}

# --- The gateway's image ---------------------------------------------------------------------

variable "gateway_image" {
  description = <<-EOT
    The gateway container's image. No default, and in particular no default that names a tag:
    deploy.sh builds and pushes by digest and then asserts the running digest, and every other
    apply passes the digest already in state, which is what keeps a routine apply from moving a
    running image: which image runs is a deployment's decision, never Terraform's.

    The first apply of this root passes the public sample image, because the service is applied
    before the gateway image exists; the health check follows the image (health_check_path).
  EOT
  type        = string

  # A digest reference and nothing else: <repository>@sha256:<64 lowercase hex>. The repository half
  # is the contract's own gatewayImage pattern, read rather than copied; the digest half is held to
  # its full 64 characters here because that pattern admits 8, for its abbreviated example
  # (contracts/infra-outputs.schema.json -> gatewayImage). A SHA-256 digest is 64 lowercase hex:
  # https://github.com/opencontainers/image-spec/blob/v1.1.1/descriptor.md#sha-256
  #
  # #470: on 2026-10-27 a rewrite of the platform root's terraform.tfvars, its digest lookup answered
  # 429 by ECR Public, wrote gateway_image = "public.ecr.aws/nginx/nginx-unprivileged@", and the
  # length check this replaces would have passed it to the plan (#242).
  validation {
    condition = (
      can(regex(jsondecode(file("${path.module}/../../../contracts/infra-outputs.schema.json")).properties.gatewayImage.pattern, var.gateway_image))
      && can(regex("@sha256:[0-9a-f]{64}$", var.gateway_image))
    )
    error_message = "gateway_image is a digest reference, <repository>@sha256:<64 lowercase hex>, such as public.ecr.aws/nginx/nginx-unprivileged@sha256:<digest> for the sample image on the first apply. A tag, an empty digest or a short one is refused."
  }
}

# --- Extra containers and repositories: what a root adds to the gateway's task ------------------

variable "extra_containers" {
  description = <<-EOT
    ECS container definitions appended to the gateway's task after the gateway's own, each exactly
    as written, in the form the task definition's container_definitions takes
    (https://docs.aws.amazon.com/AmazonECS/latest/developerguide/task_definition_parameters.html#container_definitions).
    Empty, the default, is a task of the gateway alone.

    An extra container holds the task's role. The task role is the task's, not a container's, so a
    sidecar can do whatever the gateway's role allows: read this cell's bucket prefixes and its
    client-secret parameters, and under the durable store its session table and key ring (iam.tf).
    Add only a container trusted with that.

    An extra container shares the task's network namespace, so the gateway reaches a listener in it
    on loopback, which is how an upstream in allowed_upstreams can name 127.0.0.1. It shares the
    task's 0.25 vCPU and 1 GB too. The module adds nothing to an extra container: its image, its
    hardening, its log configuration and the values ECS fills in are the caller's to write, and the
    gateway's own container (ecs.tf) shows each. Its image is pulled with the execution role, which
    may pull from this module's repositories (extra_repositories) and from no other private one.
  EOT
  type        = any
  default     = []

  # Each is named, and no name repeats or takes the gateway's, which the service registers in the
  # target group by name: ECS refuses a task definition with two containers of one name at apply,
  # after the roles and the bucket were created. try() is what refuses a container with no name, and
  # a value that is not a list of objects: reading its name fails.
  validation {
    condition = try(
      length(distinct(concat(["gateway"], [for c in var.extra_containers : tostring(c.name)]))) == length(var.extra_containers) + 1,
      false,
    )
    error_message = "extra_containers is a list of container definitions, each with a name, no two with one name and none named gateway, the gateway's own container."
  }
}

variable "extra_repositories" {
  description = <<-EOT
    The names of image repositories the module creates beside the gateway's, each as
    spa-{cell}/{name} with the gateway repository's settings: immutable tags, scan on push, the same
    lifecycle policy and force_delete. Empty, the default, creates the gateway repository alone. The
    execution role may pull from every repository the module creates (iam.tf), and the
    ecr_repositories output names each by its URL.
  EOT
  type        = set(string)
  default     = []

  # ECR's repository-name rule for the segment after spa-{cell}/, with no further slash, and never
  # gateway, which the module creates already.
  # https://docs.aws.amazon.com/AmazonECR/latest/APIReference/API_CreateRepository.html
  validation {
    condition = alltrue([
      for name in var.extra_repositories : can(regex("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", name)) && name != "gateway"
    ])
    error_message = "Each extra repository name is lowercase letters and digits, separated by single dots, hyphens or underscores, and is not gateway."
  }
}

# --- Platform configuration the task environment carries ---------------------------------------
#
# contracts/platform-config.schema.json is the shape. Everything here is a bound a registry entry
# cannot widen, which is why these are the root's inputs and not an entry's fields.

variable "issuers" {
  description = <<-EOT
    The issuers a registry entry may name, keyed by the name an entry uses in oauth.issuer. The
    values come from the identity provider's own configuration, its issuer and its browser-facing
    origin, which is why this is an input rather than a lookup: the cell and the identity provider
    are separate states.
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
    one. A host reached through a local tunnel carries the local port, such as
    app1.spa-poc.test:8443 (contracts/platform-config.example.json -> Policy.AllowedHostSuffixes).
  EOT
  type        = list(string)

  validation {
    condition     = length(var.allowed_host_suffixes) > 0 && length(var.allowed_host_suffixes) == length(distinct(var.allowed_host_suffixes))
    error_message = "Name at least one host suffix, with no duplicates (contracts/platform-config.schema.json -> Policy.AllowedHostSuffixes)."
  }
}

variable "allowed_upstreams" {
  description = <<-EOT
    The upstream origins a registry entry may name. An upstream in an extra container of the task is
    a loopback port inside it, such as http://127.0.0.1:5080
    (contracts/platform-config.example.json -> Policy.AllowedUpstreams).
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
    long an activation or a rollback takes to go live, since the gateway serves the registry it last
    read (contracts/platform-config.schema.json -> Registry.PollSeconds).
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

  # The gateway's own start-up rule, held at plan so that the cell never deploys a gateway that would
  # refuse to start (P2-2 decision 16): the DynamoDB store protects its tickets under the key ring
  # (P2-1 item 6), so durable sessions under a per-task ring are sessions no other task can read.
  validation {
    condition     = var.session_store != "dynamodb" || var.data_protection_parameter_prefix != ""
    error_message = "session_store = \"dynamodb\" needs data_protection_parameter_prefix: the DynamoDB store protects its tickets under the shared key ring, and the gateway refuses Session:Store = dynamodb with no DataProtection:ParameterPrefix at start (P2-2 decision 16)."
  }
}

# --- Continuity (#338): the durable store's table and the shared key ring --------------------------

variable "data_protection_parameter_prefix" {
  description = <<-EOT
    The Parameter Store prefix the cell's data-protection key ring lives under, or empty for none.
    Empty is Phase 1: the gateway registers no ring and keeps the framework's default, per task
    (P2-2 decision 5), and nothing below is granted or set. Set, it is exactly
    /spa-platform/{cell}/data-protection/, the per-cell convention that keeps two cells from sharing
    a ring (P2-2 decision 8). The task role may write and read under it and may tag what it writes;
    it may not delete there. tools/scripts/teardown.sh deletes that prefix by the same convention.
  EOT
  type        = string
  default     = ""

  # The convention and nothing else, because three things read the prefix from the cell name rather
  # than from this value: teardown's deletion, the grant's scope and the invariant that two cells
  # never share one ring. A free-form value could put the ring where teardown never looks, or widen
  # the grant to another cell's ring.
  validation {
    condition     = var.data_protection_parameter_prefix == "" || var.data_protection_parameter_prefix == "/spa-platform/${var.cell}/data-protection/"
    error_message = "data_protection_parameter_prefix is empty or exactly /spa-platform/{cell}/data-protection/, this cell's own ring prefix (P2-2 decision 8): teardown.sh deletes that path, and the task role's grant is scoped to it."
  }
}

variable "data_protection_kms_key_id" {
  description = <<-EOT
    The KMS key the ring's SecureString parameters are encrypted under, or empty for the account's
    aws/ssm key. Empty at G6 (#332 D6: no customer-managed key). A customer-managed key needs its
    kms:Encrypt and kms:Decrypt granted to the task role, which this module does not hold; see the
    comment beside the client secrets' grant in iam.tf.
  EOT
  type        = string
  default     = ""

  # The API's own KeyId pattern (P2-2 decision 15), which admits a key id, a key ARN, an alias and
  # an alias ARN.
  validation {
    condition     = var.data_protection_kms_key_id == "" || can(regex("^[a-zA-Z0-9:/_-]{1,256}$", var.data_protection_kms_key_id))
    error_message = "data_protection_kms_key_id is empty, or a KMS key id, key ARN, alias or alias ARN (^[a-zA-Z0-9:/_-]{1,256}$)."
  }

  validation {
    condition     = var.data_protection_kms_key_id == "" || var.data_protection_parameter_prefix != ""
    error_message = "data_protection_kms_key_id encrypts the key ring, so it needs data_protection_parameter_prefix: with no prefix there is no ring for it to encrypt."
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

# --- Fragments (#961): the two policy values a registry entry's fragments answer to ---------------
#
# Both are optional in the contract, with the gateway's placeholder standing in for an absent one, so
# both default to null here and null sets no key (ecs.tf). Each is refused below 1 when set: the
# schema lets FragmentsPerShell be 0, but a cell that sets the cap to 0 refuses every entry that
# lists a fragment, which is what leaving fragments out of the registry already does.

variable "fragments_per_shell" {
  description = <<-EOT
    The most fragments one registry entry may list, or null to leave the key out and take the
    gateway's placeholder (contracts/platform-config.schema.json -> Policy.FragmentsPerShell).
  EOT
  type        = number
  default     = null

  validation {
    condition     = var.fragments_per_shell == null ? true : (var.fragments_per_shell >= 1 && floor(var.fragments_per_shell) == var.fragments_per_shell)
    error_message = "fragments_per_shell is null, or a whole number of fragments, at least 1 (contracts/platform-config.schema.json -> Policy.FragmentsPerShell)."
  }
}

variable "merge_budget" {
  description = <<-EOT
    How many attempts a fragment callback's versioned replace makes, or null to leave the key out
    and take the gateway's placeholder (contracts/platform-config.schema.json -> Policy.MergeBudget).
  EOT
  type        = number
  default     = null

  validation {
    condition     = var.merge_budget == null ? true : (var.merge_budget >= 1 && floor(var.merge_budget) == var.merge_budget)
    error_message = "merge_budget is null, or a whole number of attempts, at least 1 (contracts/platform-config.schema.json -> Policy.MergeBudget)."
  }
}

variable "log_retention_days" {
  description = <<-EOT
    How long the task's log group keeps a log. Explicit, because the alternative is not a default
    but "never expires", and a retention is a decision either way. The deployed tests read a run's
    gateway log from this group, so it has to outlive a gate run comfortably.
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

# --- Continuity (#347): the two-task shape ----------------------------------------------------------

variable "multi_task" {
  description = <<-EOT
    The two-task shape of #332 D6, or Phase 1's one public task (false). True sets the whole shape
    at once (#347 Q4): desired_count 2 over two private subnets in two zones with no public
    address, availability-zone rebalancing on, one NAT gateway with its Elastic IP in the first
    zone's public subnet, a private route table per zone routing out through it, and the S3 and
    DynamoDB gateway endpoints on both. The load balancer stays in the public subnets.
  EOT
  type        = bool
  default     = false

  # Two tasks on in-memory sessions and per-task key rings would sign users out at random (#347
  # Q12), so the switch needs the durable store, which needs the shared ring (session_store's rule).
  validation {
    condition     = !var.multi_task || var.session_store == "dynamodb"
    error_message = "multi_task = true needs session_store = \"dynamodb\": two tasks on the memory store hold two session sets, and a request that lands on the other task is signed out (#347 Q12)."
  }

  # Four /24 subnets under the switch, so the range is /22 or larger; vpc_cidr's own rule holds /23
  # for Phase 1's two. Inside try() for the reason vpc_cidr's rule is inside can(): a value with no
  # "/" would raise an evaluation error instead of showing error_message.
  validation {
    condition     = !var.multi_task || try(tonumber(split("/", var.vpc_cidr)[1]) <= 22, false)
    error_message = "multi_task = true carves four /24 subnets out of vpc_cidr, two public and two private, so the range is /22 or larger."
  }
}

# --- #1167, #1168: the bastion, the tunnel's hop --------------------------------------------------

variable "bastion" {
  description = <<-EOT
    The tunnel's hop (#1167): a second ECS service in the cell's cluster, "bastion", running one
    task of bastion_image with ECS Exec on, under a task role whose whole grant is the four
    ssmmessages permissions. tools/scripts/tunnel.sh forwards through it, and refuses without it.
    False, the default and Phase 1's, declares none of it: the cell has no hop, so nothing on a
    workstation reaches the internal load balancer. True and applied means one running bastion
    task until an apply turns it off.
  EOT
  type        = bool
  default     = false
}

variable "bastion_image" {
  description = <<-EOT
    The bastion container's image: the sample image, public.ecr.aws/nginx/nginx-unprivileged, by
    digest (#1167, sub-question 3.1), which tools/scripts/platform-tfvars.sh already resolves for
    gateway_image. Held to gateway_image's rule, a digest reference and never a tag, so an apply
    never moves the running bastion. Empty, the default, is allowed only while bastion is false.
  EOT
  type        = string
  default     = ""

  # gateway_image's rule, read from the same contract pattern, with empty admitted for a cell that
  # declares no bastion.
  validation {
    condition = var.bastion_image == "" || (
      can(regex(jsondecode(file("${path.module}/../../../contracts/infra-outputs.schema.json")).properties.gatewayImage.pattern, var.bastion_image))
      && can(regex("@sha256:[0-9a-f]{64}$", var.bastion_image))
    )
    error_message = "bastion_image is a digest reference, <repository>@sha256:<64 lowercase hex>, such as public.ecr.aws/nginx/nginx-unprivileged@sha256:<digest>. A tag, an empty digest or a short one is refused."
  }

  # Refused at plan rather than left to ECS, which would refuse a container with no image at apply,
  # after the role and its policy were created.
  validation {
    condition     = !var.bastion || var.bastion_image != ""
    error_message = "bastion = true needs bastion_image: the sample image by digest, public.ecr.aws/nginx/nginx-unprivileged@sha256:<digest>."
  }
}
