using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>
/// Platform configuration bounds what any registry entry may say, so a registry writer cannot widen
/// it. Task 1.4 sets it in the task environment.
/// </summary>
public sealed class PlatformConfigurationTests
{
    [Theory]
    [InlineData("Platform")]
    [InlineData("Registry")]
    [InlineData("Assets")]
    [InlineData("Issuers")]
    [InlineData("Policy")]
    [InlineData("ForwardedHeaders")]
    [InlineData("Session")]
    public void Every_section_the_contract_lists_is_required(string section)
    {
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c.Remove(section)),
            $"configuration with no {section} section");
    }

    [Fact]
    public void The_task_environment_may_carry_settings_the_platform_does_not_own()
    {
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c => c["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Information" } }),
            "Logging beside the platform's own sections");
    }

    [Fact]
    public void A_typo_inside_a_platform_section_is_rejected()
    {
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["Registry"]!.AsObject()["Buckets"] = "spa-platform-poc"),
            "Bucket misspelled in the Registry section");
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["Policy"]!.AsObject()["AllowedUpstream"] = Documents.Strings("http://127.0.0.1:5080")),
            "AllowedUpstreams misspelled in the Policy section");
    }

    [Fact]
    public void Cell_takes_the_same_form_as_an_appId()
    {
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Platform"]!.AsObject()["Cell"] = "poc"), "the cell poc");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Platform"]!.AsObject()["Cell"] = "POC"), "an uppercase cell name");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Platform"]!.AsObject()["Cell"] = "p"), "a one-character cell name");
    }

    [Fact]
    public void Poll_interval_is_a_positive_integer()
    {
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Registry"]!.AsObject()["PollSeconds"] = 15), "fifteen seconds");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Registry"]!.AsObject()["PollSeconds"] = 0), "a zero poll interval");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Registry"]!.AsObject()["PollSeconds"] = "15"), "a poll interval as a string");
    }

    [Fact]
    public void Registry_key_is_a_bucket_key_and_not_a_path()
    {
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Registry"]!.AsObject()["Key"] = "registry/registry.json"), "the registry key");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Registry"]!.AsObject()["Key"] = "/registry/registry.json"), "a leading slash");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Registry"]!.AsObject()["Key"] = ""), "an empty key");
    }

    [Fact]
    public void An_issuer_carries_an_authority_a_profile_and_a_browser_origin()
    {
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Issuers"] = new JsonObject()), "no issuer at all");

        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["Issuers"]!["cognito-poc"]!.AsObject().Remove("Authority")),
            "an issuer with no Authority");
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["Issuers"]!["cognito-poc"]!.AsObject()["Authority"] = "http://cognito-idp.us-east-1.amazonaws.com/x"),
            "an http authority");
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["Issuers"]!["cognito-poc"]!.AsObject()["BrowserOrigin"] = "https://spa-poc.auth.us-east-1.amazoncognito.com/login"),
            "a browser origin with a path");
    }

    [Fact]
    public void The_issuer_name_is_the_name_an_entry_may_use()
    {
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c =>
            {
                var issuers = c["Issuers"]!.AsObject();
                JsonNode issuer = issuers["cognito-poc"]!.DeepClone();
                issuers.Remove("cognito-poc");
                issuers["Cognito-Poc"] = issuer;
            }),
            "an issuer name an entry's oauth.issuer could never match");
    }

    [Fact]
    public void Profile_is_an_IIdpProfile_name()
    {
        // Frozen at G1b: ADR-0017 folded the G0 findings in and the member set is settled, so a
        // change here needs a change request.
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Issuers"]!["cognito-poc"]!.AsObject()["Profile"] = "standard"), "the standard profile");
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Issuers"]!["cognito-poc"]!.AsObject()["Profile"] = "cognito"), "the cognito profile");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Issuers"]!["cognito-poc"]!.AsObject()["Profile"] = "pingfederate"), "a profile that does not exist yet");
    }

    [Fact]
    public void Policy_bounds_are_lists_with_at_least_one_entry()
    {
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Policy"]!.AsObject()["AllowedHostSuffixes"] = new JsonArray()), "no allowed host suffix");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Policy"]!.AsObject()["AllowedUpstreams"] = new JsonArray()), "no allowed upstream");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Policy"]!.AsObject()["AllowedHostSuffixes"] = ".spa-poc.test:8443"), "a suffix that is not in a list");
    }

    [Fact]
    public void An_allowed_upstream_takes_the_same_form_as_an_entrys_upstream()
    {
        // The bound and the bounded value are the same shape, or an entry could never match one.
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["Policy"]!.AsObject()["AllowedUpstreams"] = Documents.Strings("http://127.0.0.1:5080/orders")),
            "an allowed upstream with a path");
    }

    [Fact]
    public void Trusted_networks_are_CIDR_blocks()
    {
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["ForwardedHeaders"]!.AsObject()["TrustedNetworks"] = Documents.Strings("10.20.0.0/16")), "a CIDR block");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["ForwardedHeaders"]!.AsObject()["TrustedNetworks"] = Documents.Strings("10.20.0.0")), "an address with no prefix length");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["ForwardedHeaders"]!.AsObject()["TrustedNetworks"] = new JsonArray()), "no trusted network");
    }

    [Fact]
    public void Session_store_is_memory_in_phase_1_and_dynamodb_in_phase_2()
    {
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Session"]!.AsObject()["Store"] = "memory"), "the in-memory store");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c =>
            {
                // With its table, since #343: The_dynamodb_store_requires_a_table_name holds the rule.
                c["Session"]!.AsObject()["Store"] = "dynamodb";
                c["Session"]!.AsObject()["TableName"] = "spa-platform-poc-sessions";
            }),
            "the Phase 2 store");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Session"]!.AsObject()["Store"] = "redis"), "a store the plan does not name");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Session"]!.AsObject()["MaxSessions"] = 0), "a zero session bound");
    }

    [Fact]
    public void The_session_table_is_an_optional_DynamoDB_table_name()
    {
        // Change request P2-1 Decision 3: optional, flat, DynamoDB's naming rule
        // (https://docs.aws.amazon.com/amazondynamodb/latest/developerguide/HowItWorks.NamingRulesDataTypes.html).
        // The requirement under Store = dynamodb is the test below, since G3's tag (#343).
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c =>
            {
                c["Session"]!.AsObject()["Store"] = "dynamodb";
                c["Session"]!.AsObject()["TableName"] = "spa-platform_dev.sessions";
            }),
            "a table name of letters, digits, underscore, dot and hyphen");
        Schemas.AcceptsPlatformConfig(Documents.PlatformConfig(c => c["Session"]!.AsObject().Remove("TableName")), "no table name");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Session"]!.AsObject()["TableName"] = "ab"), "a two-character table name");
        Schemas.RejectsPlatformConfig(Documents.PlatformConfig(c => c["Session"]!.AsObject()["TableName"] = "spa-platform/sessions"), "a table name carrying a slash");
    }

    [Fact]
    public void The_dynamodb_store_requires_a_table_name()
    {
        // Change request P2-1 Decision 3 deferred the requirement to the next gate; #343 landed it with
        // G3's tag, so the schema says what the store's FromConfiguration has enforced since P2-1.
        // Mutation: drop the if/then under Session, and the first case passes.
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c =>
            {
                c["Session"]!.AsObject()["Store"] = "dynamodb";
                c["Session"]!.AsObject().Remove("TableName");
            }),
            "the dynamodb store with no table name");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c =>
            {
                c["Session"]!.AsObject()["Store"] = "dynamodb";
                c["Session"]!.AsObject()["TableName"] = "spa-platform-poc-sessions";
            }),
            "the dynamodb store with a table name");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c =>
            {
                c["Session"]!.AsObject()["Store"] = "memory";
                c["Session"]!.AsObject().Remove("TableName");
            }),
            "the memory store with no table name");
    }

    [Fact]
    public void The_data_protection_section_is_closed_and_optional()
    {
        // Change request P2-2 Decisions 14 and 15: a closed object with no required member, so a cell
        // with no ring (every memory cell, every hermetic host) still validates and the Terraform test
        // that walks each section's required keeps passing. The prefix is a Parameter Store path and
        // KmsKeyId takes the API's own KeyId pattern. The code refuses the section without its prefix
        // at start (Hosting, decision 15); the schema only shapes it. Landed with G3's tag (#343).
        // Mutation: drop the section from the schema, and the open root accepts the first two refusals.
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject { ["ParameterPrefix"] = "/spa-platform/poc/data-protection/", ["Prefix"] = "/x/" }),
            "a DataProtection section with an unknown key");
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject { ["ParameterPrefix"] = "spa-platform/poc/data-protection/" }),
            "a prefix with no leading slash");
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject { ["ParameterPrefix"] = "/spa-platform//poc/" }),
            "a prefix with an empty segment");
        Schemas.RejectsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject { ["ParameterPrefix"] = "/spa-platform/poc/data-protection/", ["KmsKeyId"] = "" }),
            "an empty key id, which the package would read as no key");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject { ["ParameterPrefix"] = "/spa-platform/poc/data-protection/", ["KmsKeyId"] = "alias/spa-platform-poc" }),
            "a prefix and a key alias");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject { ["ParameterPrefix"] = "/spa-platform/poc/data-protection", ["KmsKeyId"] = "arn:aws:kms:us-east-1:123456789012:key/1234abcd-12ab-34cd-56ef-1234567890ab" }),
            "a prefix with no trailing slash and a key ARN");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c => c["DataProtection"] = new JsonObject()),
            "an empty section, which the schema leaves to the code to refuse");
        Schemas.AcceptsPlatformConfig(
            Documents.PlatformConfig(c => c.Remove("DataProtection")),
            "a cell with no section");
    }
}
