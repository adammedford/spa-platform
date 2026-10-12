using System.Text.Json.Nodes;

namespace Contracts.Tests;

/// <summary>
/// Every validation rule the Contracts tab lists for the registry document, with a passing and a
/// failing case each. The rules a schema cannot express are listed in contracts/README.md and
/// belong to the one validation library of task 2.1; ContractIndexTests holds that list in place.
/// </summary>
public sealed class RegistryRuleTests
{
    [Fact]
    public void SchemaVersion_is_one()
    {
        Schemas.AcceptsRegistry(Documents.Registry(), "schemaVersion 1");
        Schemas.RejectsRegistry(Documents.Registry(d => d["schemaVersion"] = 2), "schemaVersion 2");
        Schemas.RejectsRegistry(Documents.Registry(d => d["schemaVersion"] = "1"), "schemaVersion as a string");
        Schemas.RejectsRegistry(Documents.Registry(d => d.Remove("schemaVersion")), "a document with no schemaVersion");
    }

    [Fact]
    public void Revision_is_a_positive_integer()
    {
        // "greater than the live one" compares two documents, so task 2.1 checks that part.
        Schemas.AcceptsRegistry(Documents.Registry(d => d["revision"] = 43), "a higher revision");
        Schemas.AcceptsRegistry(Documents.Registry(d => d["revision"] = 1), "revision 1");
        Schemas.RejectsRegistry(Documents.Registry(d => d["revision"] = 0), "revision 0");
        Schemas.RejectsRegistry(Documents.Registry(d => d["revision"] = -1), "a negative revision");
        Schemas.RejectsRegistry(Documents.Registry(d => d["revision"] = 42.5), "a fractional revision");
        Schemas.RejectsRegistry(Documents.Registry(d => d.Remove("revision")), "a document with no revision");
    }

    [Fact]
    public void AppId_is_3_to_40_lowercase_letters_digits_or_hyphens()
    {
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["appId"] = "abc"), "the shortest appId");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["appId"] = new string('a', 40)), "a 40-character appId");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["appId"] = "app-1"), "a hyphen in an appId");

        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["appId"] = "ab"), "a two-character appId");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["appId"] = new string('a', 41)), "a 41-character appId");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["appId"] = "App1"), "an uppercase letter in an appId");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["appId"] = "app_1"), "an underscore in an appId");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["appId"] = "app.1"), "a dot in an appId");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e.Remove("appId")), "an entry with no appId");
    }

    [Fact]
    public void Host_is_a_lowercase_authority_with_an_optional_port()
    {
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["host"] = "app1.spa-poc.test:8443"), "an authority with a port");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["host"] = "app1.spa-poc.test"), "an authority with no port");

        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["host"] = "App1.spa-poc.test:8443"), "an uppercase host");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["host"] = "https://app1.spa-poc.test:8443"), "a host with a scheme");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["host"] = "app1.spa-poc.test:8443/app"), "a host with a path");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["host"] = "app1.spa-poc.test."), "a host with a trailing dot");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["host"] = "user@app1.spa-poc.test"), "a host with userinfo");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["host"] = ""), "an empty host");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e.Remove("host")), "an entry with no host");
    }

    [Fact]
    public void Release_is_1_to_64_letters_digits_dots_hyphens_or_underscores()
    {
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["release"] = "2026-10-10.1"), "the plan's release name");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["release"] = new string('r', 64)), "a 64-character release");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["release"] = "Release_1"), "letters, an underscore and a digit");

        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["release"] = new string('r', 65)), "a 65-character release");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["release"] = ""), "an empty release");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["release"] = "2026-10-10.1/index.html"), "a slash in a release");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["release"] = "release 1"), "a space in a release");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e.Remove("release")), "an entry with no release");
    }

    [Fact]
    public void OAuth_issuer_is_a_configuration_name()
    {
        // That the name exists under Issuers:{name} is task 2.1's check, not this schema's.
        Schemas.AcceptsRegistry(Documents.RegistryOAuth(o => o["issuer"] = "cognito-poc"), "the plan's issuer name");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["issuer"] = "Cognito-Poc"), "an uppercase issuer name");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["issuer"] = ""), "an empty issuer name");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o.Remove("issuer")), "an oauth section with no issuer");
    }

    [Fact]
    public void ClientId_is_present_and_has_no_whitespace()
    {
        // Uniqueness across apps is task 2.1's check.
        Schemas.AcceptsRegistry(Documents.RegistryOAuth(o => o["clientId"] = "1example23456789"), "the plan's placeholder client id");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["clientId"] = ""), "an empty client id");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["clientId"] = "1example 23456789"), "a space in a client id");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o.Remove("clientId")), "an oauth section with no clientId");
    }

    [Fact]
    public void ClientSecretRef_names_a_parameter_and_a_version()
    {
        Schemas.AcceptsRegistry(
            Documents.RegistryOAuth(o => o["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app1/client-secret:1"),
            "a parameter and a version");
        Schemas.AcceptsRegistry(
            Documents.RegistryOAuth(o => o["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app1/client-secret:12"),
            "a two-digit version");

        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app1/client-secret"),
            "a reference with no version");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["clientSecretRef"] = "ssm:/spa-platform/poc/apps/app1/client-secret:0"),
            "version 0");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["clientSecretRef"] = "/spa-platform/poc/apps/app1/client-secret:1"),
            "a reference with no ssm: prefix");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["clientSecretRef"] = "ssm:spa-platform/poc:1"),
            "a relative parameter name");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o.Remove("clientSecretRef")),
            "an oauth section with no clientSecretRef");
    }

    [Fact]
    public void A_secret_value_can_never_be_written_into_an_entry()
    {
        // The registry is a versioned S3 object with every historical value kept (ADR-0003), so the
        // only way to name a secret is by reference.
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["clientSecret"] = "not-a-real-secret"),
            "an oauth section carrying a secret value");
    }

    [Fact]
    public void Scopes_include_openid()
    {
        Schemas.AcceptsRegistry(Documents.RegistryOAuth(o => o["scopes"] = Documents.Strings("openid")), "openid alone");
        Schemas.AcceptsRegistry(
            Documents.RegistryOAuth(o => o["scopes"] = Documents.Strings("openid", "profile")),
            "openid with another scope");

        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["scopes"] = Documents.Strings("profile", "email")),
            "scopes without openid");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["scopes"] = new JsonArray()), "an empty scope list");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["scopes"] = Documents.Strings("openid", "openid")),
            "a repeated scope");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["scopes"] = Documents.Strings("openid profile")),
            "two scopes in one string");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o.Remove("scopes")), "an oauth section with no scopes");
    }

    [Fact]
    public void Resource_when_present_is_one_https_url()
    {
        Schemas.AcceptsRegistry(Documents.RegistryOAuth(o => o.Remove("resource")), "an entry with no resource");
        Schemas.AcceptsRegistry(
            Documents.RegistryOAuth(o => o["resource"] = "https://orders.spa-poc.test"),
            "one https URL");

        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["resource"] = "http://orders.spa-poc.test"),
            "an http resource");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["resource"] = "https://orders.spa-poc.test https://reports.spa-poc.test"),
            "two URLs in one resource");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["resource"] = ""), "an empty resource");
        Schemas.RejectsRegistry(
            Documents.RegistryOAuth(o => o["resource"] = Documents.Strings("https://orders.spa-poc.test")),
            "a list of resources");
    }

    [Fact]
    public void Api_name_is_1_to_32_lowercase_letters_digits_or_hyphens()
    {
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["name"] = "o"), "a one-character api name");
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["name"] = new string('o', 32)), "a 32-character api name");
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["name"] = "orders-v2"), "a hyphen in an api name");

        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["name"] = new string('o', 33)), "a 33-character api name");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["name"] = ""), "an empty api name");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["name"] = "Orders"), "an uppercase api name");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["name"] = "orders/v2"), "a slash in an api name");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a.Remove("name")), "an api with no name");
    }

    [Fact]
    public void Api_upstream_is_one_origin_with_no_path_query_or_credentials()
    {
        // That it matches Policy:AllowedUpstreams is task 2.1's check.
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["upstream"] = "http://127.0.0.1:5080"), "an http origin with a port");
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["upstream"] = "https://orders.internal"), "an https origin");

        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "http://127.0.0.1:5080/orders"), "an upstream with a path");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "http://127.0.0.1:5080/"), "an upstream with a trailing slash");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "http://127.0.0.1:5080?x=1"), "an upstream with a query");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "http://127.0.0.1:5080#f"), "an upstream with a fragment");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "http://user:pass@127.0.0.1:5080"), "an upstream with credentials");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "ftp://127.0.0.1"), "an upstream with another scheme");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["upstream"] = "127.0.0.1:5080"), "an upstream with no scheme");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a.Remove("upstream")), "an api with no upstream");
    }

    [Fact]
    public void Api_timeoutSeconds_is_a_positive_integer()
    {
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["timeoutSeconds"] = 1), "a one-second timeout");
        Schemas.AcceptsRegistry(Documents.RegistryApi(a => a["timeoutSeconds"] = 30), "the plan's timeout");

        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["timeoutSeconds"] = 0), "a zero timeout");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["timeoutSeconds"] = -1), "a negative timeout");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["timeoutSeconds"] = 30.5), "a fractional timeout");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a["timeoutSeconds"] = "30"), "a timeout as a string");
        Schemas.RejectsRegistry(Documents.RegistryApi(a => a.Remove("timeoutSeconds")), "an api with no timeout");
    }

    [Fact]
    public void An_entry_may_list_no_apis_and_several()
    {
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e.Remove("apis")), "an app with no upstream");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["apis"] = new JsonArray()), "an empty api list");

        // Two upstream origins on one entry is a warning, not an error: the schema accepts it and
        // task 2.1 warns.
        Schemas.AcceptsRegistry(
            Documents.RegistryEntry(e => e["apis"] = new JsonArray(
                new JsonObject { ["name"] = "orders", ["upstream"] = "http://127.0.0.1:5080", ["timeoutSeconds"] = 30 },
                new JsonObject { ["name"] = "reports", ["upstream"] = "http://127.0.0.1:5081", ["timeoutSeconds"] = 30 })),
            "two upstream origins");

        Schemas.RejectsRegistry(
            Documents.RegistryApi(a => a["retries"] = 3),
            "an unknown property on an api");
    }

    [Fact]
    public void Csp_rejects_the_sources_the_contract_names()
    {
        Schemas.AcceptsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["img-src"] = Documents.Strings("data:") }),
            "data: in img-src, which the plan's own example uses");
        Schemas.AcceptsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["connect-src"] = Documents.Strings("https://metrics.spa-poc.test") }),
            "an origin added to connect-src");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e.Remove("csp")), "an entry that adds nothing");

        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["img-src"] = Documents.Strings("*") }),
            "* in any directive");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = Documents.Strings("'unsafe-eval'") }),
            "'unsafe-eval' in script-src");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["style-src"] = Documents.Strings("'unsafe-eval'") }),
            "'unsafe-eval' in any other directive");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["style-src"] = Documents.Strings("'UNSAFE-EVAL'") }),
            "'unsafe-eval' in another letter case, because the keyword is case-insensitive to a browser");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = Documents.Strings("'unsafe-inline'") }),
            "'unsafe-inline' in script-src");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = Documents.Strings("data:") }),
            "data: in script-src");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["script-src"] = Documents.Strings("DATA:text/javascript") }),
            "a data: source in script-src in another letter case");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["img-src"] = new JsonArray() }),
            "a directive that adds no source");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["media-src"] = Documents.Strings("'self'") }),
            "a directive the baseline does not set");
        Schemas.RejectsRegistry(
            Documents.RegistryEntry(e => e["csp"] = new JsonObject { ["img-src"] = "data:" }),
            "a directive whose value is not a list");
    }

    [Fact]
    public void Session_lifetime_is_between_5_and_720_minutes()
    {
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["session"] = new JsonObject { ["lifetimeMinutes"] = 5 }), "five minutes");
        Schemas.AcceptsRegistry(Documents.RegistryEntry(e => e["session"] = new JsonObject { ["lifetimeMinutes"] = 720 }), "720 minutes");

        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["session"] = new JsonObject { ["lifetimeMinutes"] = 4 }), "four minutes");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["session"] = new JsonObject { ["lifetimeMinutes"] = 721 }), "721 minutes");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["session"] = new JsonObject { ["lifetimeMinutes"] = 480.5 }), "a fractional lifetime");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["session"] = new JsonObject()), "a session section with no lifetime");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e.Remove("session")), "an entry with no session, because the plan fixes no default");
    }

    [Fact]
    public void A_typo_is_rejected_rather_than_ignored()
    {
        Schemas.RejectsRegistry(Documents.Registry(d => d["apps "] = new JsonArray()), "an unknown property on the document");
        Schemas.RejectsRegistry(Documents.RegistryEntry(e => e["releases"] = "2026-10-10.1"), "release misspelled on an entry");
        Schemas.RejectsRegistry(Documents.RegistryOAuth(o => o["scope"] = Documents.Strings("openid")), "scopes misspelled in the oauth section");
    }

    [Fact]
    public void Apps_is_a_list_of_entries()
    {
        Schemas.AcceptsRegistry(Documents.Registry(d => d["apps"] = new JsonArray()), "a document with no apps, which deployment refuses but the document allows");
        Schemas.RejectsRegistry(Documents.Registry(d => d["apps"] = new JsonObject()), "apps as an object");
        Schemas.RejectsRegistry(Documents.Registry(d => d.Remove("apps")), "a document with no apps property");

        var duplicated = Documents.Registry();
        duplicated["apps"]!.AsArray().Add(ContractFiles.Example(ContractFiles.RegistryExample)["apps"]![0]!.DeepClone());
        Schemas.RejectsRegistry(duplicated, "the same entry twice");
    }
}
