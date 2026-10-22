# Whether the key ring's parameters can carry the platform's tags

A research finding for the Phase 2 wayfinder (issue #215), answering its research ticket #274,
which holds the two facts that `contracts/changes/p2-2-data-protection-key-ring.md` decision 17
(from #222, Q8) deferred the tags on. It decides nothing. Written 2026-10-21 against `origin/main`
at `115e79d`. No AWS credentials were available, so **nothing was executed against AWS**: every
fact below was **read** from AWS documentation, fetched with `curl -sL` on 2026-10-21. The
"Sources" list at the end holds the URLs once.

Why the earlier fetches came back empty: three of the four URLs the ticket names no longer exist.
`list_awssystemsmanager.html` and `resourcegroupstagging/.../supported-services.html` now answer
`302` to their guide's index, and `systems-manager/.../tagging-parameters.html` serves the user
guide's shell with no body. The current pages are `list_ssm.html` (found through the reference's
`toc-contents.json`) and the Tagging API's `Welcome.html`, which now carries the supported-services
list. No Parameter Store tagging page remains in the Systems Manager user guide's table of contents.

## 1. The grant: yes, a tagged `PutParameter` needs `ssm:AddTagsToResource` too

**Read.** The Service Authorization Reference's "API operations defined by AWS Systems Manager"
table, which "maps API operations to the IAM actions they authorize", gives the `PutParameter`
operation two rows: `ssm:AddTagsToResource` (access level "Tagging, Write") and `ssm:PutParameter`
("Write").
<https://docs.aws.amazon.com/service-authorization/latest/reference/list_ssm.html#list_ssm-operation-PutParameter>

CloudFormation's page for the same resource says it in a sentence: "To create an SSM parameter, you
must have the AWS Identity and Access Management (IAM) permissions `ssm:PutParameter` and
`ssm:AddTagsToResource`."
<https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-ssm-parameter.html>

The `PutParameter` API reference confirms `Tags` is a create-time request field ("Optional metadata
that you assign to a resource") and adds: "To add tags to an existing Systems Manager parameter, use
the AddTagsToResource operation."
<https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_PutParameter.html>

The resource for both actions is the parameter:
`arn:${Partition}:ssm:${Region}:${Account}:parameter/${ParameterNameWithoutLeadingSlash}`
(`list_ssm.html`, resource type `parameter`). So the grant is the cell's ring prefix,
`arn:aws:ssm:<region>:<account>:parameter/spa-platform/<cell>/data-protection/*`, the same resource
the decided `PutParameter` and `GetParametersByPath` grants (#222 Q3) use.

**Condition keys (read, same page, Actions table).** `PutParameter` takes `aws:RequestTag/${TagKey}`,
`aws:ResourceTag/${TagKey}`, `aws:TagKeys`, `ssm:Overwrite`, `ssm:Policies` and
`ssm:resourceTag/tag-key`; `AddTagsToResource` takes `aws:RequestTag/${TagKey}`,
`aws:ResourceTag/${TagKey}` and `aws:TagKeys` on the `parameter` resource. The page defines
`aws:RequestTag` as "Filters access by 'Create' requests based on the allowed set of values for a
specified tags" and `aws:TagKeys` as "Filters access by 'Create' requests based on whether mandatory
tags are included in the request". So the tagging grant can be pinned to exactly the three tags and
their values, for example `StringEquals` on `aws:RequestTag/Platform = spa-platform`,
`aws:RequestTag/Cell = <cell>`, `aws:RequestTag/ManagedBy = gateway`, and
`ForAllValues:StringEquals` on `aws:TagKeys = [Platform, Cell, ManagedBy]`. `ForAllValues` passes
vacuously when the request carries no tags, so it needs the `StringEquals` keys beside it (or a
`Null` check) to refuse an untagged call.

**Not verified.** Whether IAM evaluates `ssm:AddTagsToResource` on a `PutParameter` that carries
**no** `Tags`. The operations table lists both actions without saying "when Tags is present", and
CloudFormation's sentence speaks of any create. That matters only to the untagged shape, and only if
it does: the decided grants (#222 Q3) would then be one short even untagged. A first deployed
`PutParameter` settles it either way; it was not run here.

## 2. The query: yes, `GetResources` returns SSM parameters, and teardown's query does not filter by type

**Read.** The Tagging API's Welcome page, which now holds the supported-services list: "The
`GetResources`, `GetTagKeys`, and `GetTagValues` operations support all resource types." It names
two exceptions: IAM ("The `GetResources` operation does not currently support IAM users and roles")
and EC2 Auto Scaling groups ("the `GetTagKeys`, `GetTagValues` and `GetResources` operations aren't
supported at this time and return an empty response for this service"). Systems Manager is not among
them. (Systems Manager is also not in that page's list of services for `TagResources` and
`UntagResources`, which is why the gateway has to tag through SSM's own `PutParameter` /
`AddTagsToResource`; it does not affect reading.)
<https://docs.aws.amazon.com/resourcegroupstagging/latest/APIReference/Welcome.html>

Corroborating, not the same API: the Resource Groups user guide's "Resource types you can use with
AWS Resource Groups and Tag Editor", AWS Systems Manager section, row `AWS::SSM::Parameter`: Tag
Editor Tagging **Yes**, Tag-based Groups **Yes**, CloudFormation Stack-based Groups **Yes**.
<https://docs.aws.amazon.com/ARG/latest/userguide/supported-resources.html>

**Read (repository).** `tools/scripts/teardown.sh:299-301` on `origin/main` calls
`resourcegroupstaggingapi get-resources --tag-filters "Key=${PLATFORM_TAG_KEY},Values=${PLATFORM_TAG_VALUE}"`
with **no** `--resource-type-filters` (the only typed call in the script is the certificate lookup
at `:254-255`, `acm:certificate`). The `GetResources` reference: `ResourceTypeFilters` "Specifies the
resource types that you want included in the response", so leaving it out returns every type,
`ssm:parameter` included. A ring parameter tagged `Platform=spa-platform` would therefore appear in
`remaining` and fail the check, which is the loud failure #222 Q8 wanted.

**Not verified.** That a deleted parameter leaves the query promptly. The tagging API is read on a
poll (`TAG_WAIT_SECONDS`) for exactly this lag; how long it is for SSM parameters was not measured.

## Consequence for #222's two shapes

Both of Q8's assumptions hold as documented, so decision 17's reason for deferring is gone.

- **Tagged on create.** `Platform.Hosting` sets `PersistOptions.Tags` to `Platform = spa-platform`,
  `Cell = {cell}`, `ManagedBy = gateway`. The task role gains **one action**,
  `ssm:AddTagsToResource`, on the ring prefix, beside the decided `ssm:PutParameter` and
  `ssm:GetParametersByPath`; optionally conditioned on `aws:RequestTag/*` and `aws:TagKeys` as above
  so it can apply only the three tags and only this cell's value. `contracts/resource-tags.md` gains
  a row saying the gateway applies them (`ManagedBy = gateway` is new to its table). Teardown's
  prefix deletion still runs first (decision 17); a ring it failed to delete then fails P-05 by
  name. Cost: the role can write tags on its own ring's parameters, nothing wider.
- **Untagged, prefix deletion only.** No new action, no contract row. A ring that teardown fails to
  delete, or that a later prefix change puts outside the deleted path, is invisible to P-05 and
  survives into a rebuilt cell of the same name. If the unverified point in §1 goes the wrong way,
  this shape needs `ssm:AddTagsToResource` anyway, and then saves nothing.

**Recommendation: tag on create**, with `ssm:AddTagsToResource` on the ring prefix, conditioned on
the three `aws:RequestTag` values and `aws:TagKeys`. It is one action on a resource the role already
writes, it turns a silent leak into a P-05 failure, and the condition keys keep it from tagging
anything else. The Terraform test for the task role should assert the conditions are present, as it
already asserts the shape of the role's other statements. The first deployed `PutParameter` (G6)
confirms both the grant and the query in one run: the parameter is created and tagged, and
`get-resources` lists it until teardown deletes it.

## Sources

- Service Authorization Reference, AWS Systems Manager (operations table, actions table, `parameter`
  resource, condition keys): <https://docs.aws.amazon.com/service-authorization/latest/reference/list_ssm.html>
- `PutParameter`, AWS Systems Manager API Reference: <https://docs.aws.amazon.com/systems-manager/latest/APIReference/API_PutParameter.html>
- `AWS::SSM::Parameter`, CloudFormation Template Reference: <https://docs.aws.amazon.com/AWSCloudFormation/latest/TemplateReference/aws-resource-ssm-parameter.html>
- Systems Manager user guide, "How AWS Systems Manager works with IAM" (parameters among the types
  taggable "when you create or update them"): <https://docs.aws.amazon.com/systems-manager/latest/userguide/security_iam_service-with-iam.html>
- Resource Groups Tagging API, Welcome (supported services): <https://docs.aws.amazon.com/resourcegroupstagging/latest/APIReference/Welcome.html>
- Resource Groups Tagging API, `GetResources`: <https://docs.aws.amazon.com/resourcegroupstagging/latest/APIReference/API_GetResources.html>
- AWS Resource Groups user guide, supported resource types: <https://docs.aws.amazon.com/ARG/latest/userguide/supported-resources.html>
- Repository, `origin/main` at `115e79d`: `tools/scripts/teardown.sh:254-255,297-301`;
  `contracts/resource-tags.md`; `infra/modules/cell/iam.tf:142-197` (task role, no ring grants yet);
  `contracts/changes/p2-2-data-protection-key-ring.md` decision 17; issues #222 (Q3, Q8) and #274.
