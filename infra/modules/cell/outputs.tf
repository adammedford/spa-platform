# What the cell publishes to the root that calls it.
#
# The names follow contracts/infra-outputs.schema.json where that contract has a
# field for them. The rest are the module's own seams: the network, the repositories,
# the target group and the task's container definitions.

output "vpc_id" {
  description = "The cell's VPC."
  value       = aws_vpc.cell.id
}

output "public_subnet_ids" {
  description = "The two public subnets, one per zone. The load balancer needs both."
  value       = [for subnet in aws_subnet.public : subnet.id]
}

output "alb_security_group_id" {
  description = "The ALB security group: accepts 443 from the task security group only."
  value       = aws_security_group.alb.id
}

output "task_security_group_id" {
  description = "The task security group: accepts the gateway's port from the ALB security group only."
  value       = aws_security_group.task.id
}

output "bucket" {
  description = <<-EOT
    The platform bucket's name, as the infrastructure outputs contract's `bucket`. Every consumer
    reads it rather than deriving the name, except infra/bootstrap, which applies before the bucket
    exists and derives the same default (README.md -> The bucket's name). The configured name rather than
    the id, which the provider sets to the same string only after an apply, so a plan shows it.
  EOT
  value       = aws_s3_bucket.platform.bucket
}

output "bucket_arn" {
  description = "The platform bucket's ARN, which the task role's grants are scoped to."
  value       = aws_s3_bucket.platform.arn
}

output "ecr_gateway" {
  description = "The gateway repository's URL, as the contract's `ecrGateway`."
  value       = aws_ecr_repository.repository["gateway"].repository_url
}

output "ecr_repositories" {
  description = <<-EOT
    Every repository the module creates, keyed by its name, gateway and each of
    extra_repositories, to its URL. A root that publishes an extra repository in its own outputs
    reads it here by name.
  EOT
  value       = { for name, repository in aws_ecr_repository.repository : name => repository.repository_url }
}

output "alb_dns_name" {
  description = "The internal load balancer's DNS name, as the contract's `albDnsName`. The tunnel forwards to it."
  value       = aws_lb.cell.dns_name
}

output "target_group_arn" {
  description = "The one target group, which the gateway's service registers its task in."
  value       = aws_lb_target_group.gateway.arn
}

output "https_listener_arn" {
  description = "The one HTTPS listener. The gateway's service depends on it, so the target group is attached to the load balancer before the service registers a task."
  value       = aws_lb_listener.https.arn
}

# --- The cell's compute ------------------------------------------------------------------------

output "ecs_cluster" {
  description = "The cell's cluster, as the contract's `ecsCluster`. tunnel.sh needs it to build the Session Manager target."
  value       = aws_ecs_cluster.cell.name
}

output "ecs_service" {
  description = "The one service, as the contract's `ecsService`. deploy.sh waits on it for steady state."
  value       = aws_ecs_service.gateway.name
}

output "ecs_bastion_service" {
  description = "The bastion service, as the contract's optional `ecsBastionService`: null when bastion is false, which leaves the field out of the outputs document. tunnel.sh lists its tasks for the tunnel's hop (#1167)."
  value       = one(aws_ecs_service.bastion[*].name)
}

output "log_group" {
  description = "The task's log group, as the contract's `logGroup`. The deployed tests read a run's gateway log from it."
  value       = aws_cloudwatch_log_group.gateway.name
}

output "gateway_image" {
  description = <<-EOT
    The gateway image this apply placed, as the contract's `gatewayImage`. It is the input echoed
    back, which is the point: deploy.sh asserts the running digest against what it applied.
  EOT
  value       = var.gateway_image
}

# --- Continuity (#347) -------------------------------------------------------------------------

output "continuity_environment" {
  description = <<-EOT
    The continuity keys of the gateway's task environment, each null when the inputs leave it out:
    Session:Store, Session:TableName and the DataProtection pair. Read from the gateway container's
    environment in the task definition's container_definitions, rather than echoed from the inputs
    or from the locals that environment is built from, so the platform root's test sees what its
    pass-through delivered to the container, which a root test cannot read off the module's
    resources.
  EOT
  value = {
    for key in ["Session__Store", "Session__TableName", "DataProtection__ParameterPrefix", "DataProtection__KmsKeyId"] :
    key => one([
      for e in one([
        for c in jsondecode(aws_ecs_task_definition.gateway.container_definitions) : c if c.name == local.gateway_container_name
      ]).environment : e.value if e.name == key
    ])
  }
}

output "container_definitions" {
  description = <<-EOT
    The gateway's task's container definitions, the JSON its task definition carries: the gateway's
    container, then each of extra_containers. A root that adds a container reads here what the task
    will run, which a root's test cannot read off the module's resources.
  EOT
  value       = aws_ecs_task_definition.gateway.container_definitions
}
