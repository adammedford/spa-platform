using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fixtures.Tests;

/// <summary>
/// Task 5.1: three static SPAs with no build step, each of which shows the session, calls its API,
/// posts with the CSRF header and logs out, with a visible marker and one hashed asset, and app 2
/// carrying one extra CSP source.
///
/// Each directory under <c>fixtures/spa/</c> is a release exactly as <c>spactl release publish</c>
/// uploads it (docs/plan/05 -> "Onboarding a third SPA", step 2), so the seam is the directory
/// itself, checked against the app's definition and contracts/http-contract.md. Every expected value
/// is read from one of those two, never restated.
///
/// Nothing here executes the script. These tests prove what the files say and that the policy lets
/// them say it; that the script then behaves in a browser is proved with task 5.2's headless browser.
/// </summary>
public sealed partial class StaticSpaFixtureTests
{
    [Fact]
    public void There_is_one_spa_per_app_definition()
    {
        // App 3 is published during F-07, so its release exists before its definition reaches the apps root.
        Assert.Equal(
            FixtureFiles.AllDefinitions.Select(Path.GetFileNameWithoutExtension),
            Directory.EnumerateDirectories(FixtureFiles.Path("fixtures", "spa")).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_shows_a_visible_marker_naming_its_app(string appId)
    {
        // F-01 tells two shells apart by what they show, so the marker is text, not an attribute.
        SpaRelease release = SpaRelease.Of(appId);

        Match marker = Regex.Match(release.IndexHtml, "<h1 id=\"marker\">([^<]*)</h1>", RegexOptions.None, RegexTimeout.Value);

        Assert.True(marker.Success, $"{appId}'s index.html has no <h1 id=\"marker\">.");
        Assert.Contains(AppIdOf(appId), marker.Groups[1].Value, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_release_has_one_hashed_asset_and_the_shell_loads_it(string appId)
    {
        // contracts/s3-layout.md caches assets/** for a year as immutable, which is safe only when a
        // change to the bytes changes the name. With no build step the name is set by hand, so the
        // test recomputes it from the bytes.
        SpaRelease release = SpaRelease.Of(appId);

        string asset = Assert.Single(release.Assets);
        Match name = Regex.Match(asset, "^assets/[a-z]+\\.(?<hash>[0-9a-f]{8})\\.js$", RegexOptions.None, RegexTimeout.Value);
        Assert.True(name.Success, $"{asset} is not named <name>.<8 hex digits>.js.");
        Assert.Equal(release.ContentHash(asset), name.Groups["hash"].Value);

        Assert.Equal(["/" + asset], release.ScriptSources);
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_release_has_an_index_and_nothing_under_a_reserved_path(string appId)
    {
        // The browser contract's reserved paths: the gateway answers those paths itself, so a file
        // there would be unreachable at best. Each fixture is a shell's release, so both kinds apply.
        SpaRelease release = SpaRelease.Of(appId);
        IReadOnlyList<string> reserved = ReservedPaths();

        Assert.Contains("index.html", release.Files);
        foreach (string file in release.Files)
        {
            Assert.False(IsReserved("/" + file, reserved), $"{appId} has {file} under a reserved path.");
        }
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_fits_its_apps_own_policy(string appId)
    {
        SpaRelease release = SpaRelease.Of(appId);
        IReadOnlyDictionary<string, IReadOnlyList<string>> policy =
            ContentSecurityPolicyFit.For(FixtureFiles.Definition(appId));

        Assert.Empty(ContentSecurityPolicyFit.Violations(release.IndexHtml, release.Scripts, policy));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void An_app_needs_more_than_the_baseline_exactly_when_its_definition_adds_a_source(string appId)
    {
        // "App 2 needs one extra CSP source, to prove per-app policy": under the bare baseline app 2's
        // shell must be refused once, for the very source its definition adds, and apps 1 and 3,
        // which add nothing, must not be refused at all. Keyed off the definition, so moving the
        // addition to another app moves the expectation with it.
        SpaRelease release = SpaRelease.Of(appId);
        JsonObject? additions = FixtureFiles.Definition(appId)["csp"] as JsonObject;

        IReadOnlyList<string> refused = ContentSecurityPolicyFit.Violations(
            release.IndexHtml, release.Scripts, ContentSecurityPolicyFit.Baseline);

        if (additions is null)
        {
            Assert.Empty(refused);
        }
        else
        {
            (string directive, JsonNode? sources) = Assert.Single(additions);
            string source = Assert.Single(sources!.AsArray())!.GetValue<string>();
            string violation = Assert.Single(refused);
            Assert.StartsWith($"{directive}: {source}", violation, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_names_its_app_and_its_first_api(string appId)
    {
        // The script builds /api/{name} from data-api, so the name must be one the definition lists,
        // or the gateway answers 404 unknown_api. The first is the app's own API; the second exists
        // for N-17 to refuse (task 5.5).
        SpaRelease release = SpaRelease.Of(appId);
        JsonNode definition = FixtureFiles.Definition(appId);

        Assert.Equal(AppIdOf(appId), release.MainAttribute("data-app"));
        Assert.Equal(definition["apis"]![0]!["name"]!.GetValue<string>(), release.MainAttribute("data-api"));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_script_reads_the_session_and_signs_in_by_navigation(string appId)
    {
        // The paths and how each is made come from the contract's endpoint table, not from here.
        string scripts = SpaRelease.Of(appId).Scripts;
        (_, string session) = ContractEndpoint("Fetch");
        (_, string login) = ContractEndpoint("Navigation");

        Assert.Contains($"fetch(\"{session}\"", scripts, StringComparison.Ordinal);
        Assert.Contains($"window.location.assign(\"{login}?returnUrl=\"", scripts, StringComparison.Ordinal);
        Assert.DoesNotContain($"fetch(\"{login}", scripts, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_script_calls_its_api_and_sends_the_csrf_header_on_unsafe_methods_only(string appId)
    {
        // The header the contract names, on every unsafe method and nowhere else (the token travels
        // in this header and nowhere else). The routes are the mock API's, task 5.3: /whoami, /echo.
        string header = CsrfHeaderName();
        List<string> calls = FetchCalls(SpaRelease.Of(appId).Scripts);

        string read = Assert.Single(calls, call => call.StartsWith("api + \"/whoami\"", StringComparison.Ordinal));
        Assert.DoesNotContain(header, read, StringComparison.Ordinal);

        string post = Assert.Single(calls, call => call.StartsWith("api + \"/echo\"", StringComparison.Ordinal));
        Assert.Contains("method: \"POST\"", post, StringComparison.Ordinal);
        Assert.Contains($"\"{header}\": csrfToken", post, StringComparison.Ordinal);

        foreach (string call in calls)
        {
            bool unsafeMethod = Regex.IsMatch(call, "method:\\s*\"(?!GET\"|HEAD\"|OPTIONS\")", RegexOptions.None, RegexTimeout.Value);
            Assert.Equal(unsafeMethod, call.Contains(header, StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void Each_shell_logs_out_by_form_post(string appId)
    {
        // "POST /bff/logout | Form navigation": a fetch would not follow the provider's logout redirect.
        SpaRelease release = SpaRelease.Of(appId);
        (string method, string logout) = ContractEndpoint("Form navigation");

        Assert.Contains($"<form method=\"{method.ToLowerInvariant()}\" action=\"{logout}\">", release.IndexHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(FetchCalls(release.Scripts), call => call.Contains(logout, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EveryApp))]
    public void No_script_puts_anything_where_F_05_looks_for_tokens(string appId)
    {
        // F-05 dumps cookies, web storage and the page and expects nothing token-shaped. The CSRF
        // token stays in a variable; the page is written with textContent only.
        string scripts = SpaRelease.Of(appId).Scripts;

        foreach (string sink in new[] { "localStorage", "sessionStorage", "document.cookie", "innerHTML", "outerHTML", "document.write" })
        {
            Assert.DoesNotContain(sink, scripts, StringComparison.Ordinal);
        }

        Assert.DoesNotMatch("show\\([^)]*csrfToken", scripts);
    }

    [Theory]
    [InlineData("bff/session")]
    [InlineData("api/orders/x.json")]
    [InlineData("healthz")]
    public void The_reserved_path_check_refuses_a_file_under_a_reserved_path(string file)
    {
        // The release check above runs only against releases that already pass it, so this feeds the
        // same comparison a file it must refuse (CODING_STANDARDS.md, "An assertion must be able to fail").
        Assert.True(IsReserved("/" + file, ReservedPaths()), $"{file} is not under a reserved path.");
    }

    [Fact]
    public void A_shell_release_refuses_a_file_under_fragments_exactly_when_the_contract_reserves_two_kinds()
    {
        // #982's second kind, "A path reserved from the shell's release", reserves /fragments/ from
        // every fixture, since each is a shell's release. A contract without #982's paragraph has one
        // kind and does not reserve it, so the expectation follows the paragraph (#1329).
        bool twoKinds = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"))
            .Contains("Two kinds of path are reserved.", StringComparison.Ordinal);

        Assert.Equal(twoKinds, IsReserved("/fragments/f1/entry.js", ReservedPaths()));
    }

    [Theory]
    [InlineData("/fragments/f1/api/x.json", "/fragments/{fragmentId}/api/", true)]
    [InlineData("/fragments/f1/entry.js", "/fragments/{fragmentId}/api/", false)]
    [InlineData("/signin-oidc/f1", "/signin-oidc/{fragmentId}", true)]
    [InlineData("/fragments/f1/entry.js", "/fragments/", true)]
    [InlineData("/bff/session", "/bff/", true)]
    [InlineData("/healthz", "/healthz", true)]
    [InlineData("/healthzz", "/healthz", false)]
    [InlineData("/assets/healthz", "/healthz", false)]
    public void A_reserved_path_covers_everything_under_it_and_a_braced_segment_stands_for_any_one(
        string file, string reserved, bool expected)
    {
        // #982 reserves paths with a template segment, /fragments/{fragmentId}/api/ and
        // /signin-oidc/{fragmentId}, which no first-segment comparison can ever match.
        Assert.Equal(expected, IsReserved(file, [reserved]));
    }

    // The checker has to be able to refuse, or every "fits" above is a label (CODING_STANDARDS.md,
    // "An assertion must be able to fail"). Each case below breaks the baseline one way.

    [Theory]
    [InlineData("<script>alert(1)</script>", "", "script-src: an inline <script>")]
    [InlineData("<button onclick=\"go()\">x</button>", "", "script-src: an inline event-handler attribute")]
    [InlineData("<p style=\"color:red\">x</p>", "", "style-src: inline style")]
    [InlineData("<style>p{}</style>", "", "style-src: inline style")]
    [InlineData("<img src=\"data:image/gif;base64,AA\">", "", "img-src: data:image/gif;base64,AA")]
    [InlineData("<script src=\"https://cdn.example/x.js\"></script>", "", "script-src: https://cdn.example/x.js")]
    [InlineData("<form method=\"post\" action=\"https://elsewhere.example/\">", "", "form-action: https://elsewhere.example/")]
    [InlineData("", "eval(\"1\");", "script-src: eval or the Function constructor")]
    [InlineData("", "fetch(\"https://api.example/x\");", "connect-src: https://api.example/x")]
    public void The_policy_check_refuses_what_the_baseline_refuses(string html, string scripts, string expected)
    {
        Assert.Contains(expected, ContentSecurityPolicyFit.Violations(html, scripts, ContentSecurityPolicyFit.Baseline));
    }

    [Fact]
    public void The_baseline_is_read_from_the_contract_with_its_ten_directives()
    {
        // registry.schema.json lists exactly the directives an entry may add to, which are the
        // baseline's ten. A parse that lost one would make a fixture's addition land nowhere.
        JsonNode schema = FixtureFiles.Read(FixtureFiles.Path("contracts", "registry.schema.json"));
        IEnumerable<string> addable = Directives(schema);

        Assert.Equal(addable.Order(StringComparer.Ordinal), ContentSecurityPolicyFit.Baseline.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["'self'"], ContentSecurityPolicyFit.Baseline["script-src"]);
    }

    private static IEnumerable<string> Directives(JsonNode schema)
    {
        // The csp subschema closes its property names with an enum of the directives it may extend.
        if (Walk(schema).OfType<JsonObject>().FirstOrDefault(node => node["title"]?.GetValue<string>() == "csp")
            is { } csp && csp["propertyNames"]?["enum"] is JsonArray directives)
        {
            return directives.Select(d => d!.GetValue<string>());
        }

        throw new InvalidOperationException("registry.schema.json has no csp properties.");
    }

    private static IEnumerable<JsonNode?> Walk(JsonNode? node)
    {
        yield return node;
        IEnumerable<JsonNode?> children = node switch
        {
            JsonObject obj => obj.Select(p => p.Value),
            JsonArray array => array,
            _ => [],
        };
        foreach (JsonNode? child in children)
        {
            foreach (JsonNode? descendant in Walk(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The argument text of each fetch call, up to the promise chain that follows it.</summary>
    private static List<string> FetchCalls(string scripts) =>
        [.. scripts.Split("fetch(").Skip(1).Select(call => call.Split(".then(")[0])];

    private static string CsrfHeaderName()
    {
        string contract = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));
        Match row = Regex.Match(contract, "^\\| `([A-Za-z-]+)` \\| Every unsafe method", RegexOptions.Multiline, RegexTimeout.Value);
        Assert.True(row.Success, "contracts/http-contract.md names no CSRF header.");
        return row.Groups[1].Value;
    }

    /// <summary>
    /// Every path the browser contract keeps out of a shell's release, as the contract writes it
    /// (<c>/bff/</c>, <c>/fragments/{fragmentId}/api/</c>, <c>/healthz</c>).
    /// </summary>
    private static IReadOnlyList<string> ReservedPaths()
    {
        string contract = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));

        // #982's paragraph, "Two kinds of path are reserved.", has one sentence per kind, each listing
        // its paths between a colon and its own full stop: "A path reserved outright is ...: ..." and
        // "A path reserved from the shell's release is ...: `/fragments/`, on every host." Each list
        // stops at its sentence's full stop, so neither picks up the 404 rules that follow it. A full
        // stop inside a code span is part of a path, not the sentence's end, so a dotted path cannot
        // cut the list short and drop every path after it.
        Match outright = Regex.Match(contract, "A path reserved outright is [^:]*:(?<list>(?:`[^`]*`|[^.`])*)\\.", RegexOptions.None, RegexTimeout.Value);
        IEnumerable<string> lists;
        if (outright.Success)
        {
            Match shell = Regex.Match(contract, "A path reserved from the shell's release is [^:]*:(?<list>(?:`[^`]*`|[^.`])*)\\.", RegexOptions.None, RegexTimeout.Value);
            Assert.True(shell.Success, "contracts/http-contract.md reserves paths outright but none from the shell's release.");
            lists = [outright.Groups["list"].Value, shell.Groups["list"].Value];
        }
        else
        {
            // The one-kind sentence #982 replaces. A contract without #982's paragraph still carries
            // it, and this file must hold on either form of the contract (#1329).
            Match sentence = Regex.Match(contract, "An SPA cannot serve content under (?<list>.*?)\\.\\s*\\n\\s*\\n", RegexOptions.Singleline, RegexTimeout.Value);
            Assert.True(sentence.Success, "contracts/http-contract.md lists no reserved paths.");
            lists = [sentence.Groups["list"].Value];
        }

        List<string> paths = [.. lists.SelectMany(list => Regex.Matches(list, "`(/[^`]+)`", RegexOptions.None, RegexTimeout.Value).Select(m => m.Groups[1].Value))];
        Assert.NotEmpty(paths);
        return paths;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of <paramref name="reserved"/> or under one, segment by
    /// segment, where a braced segment such as <c>{fragmentId}</c> stands for any one segment.
    /// </summary>
    private static bool IsReserved(string path, IEnumerable<string> reserved) =>
        reserved.Any(template => Regex.IsMatch(
            path,
            "^" + string.Join('/', template.TrimEnd('/').Split('/').Select(segment =>
                segment.StartsWith('{') && segment.EndsWith('}') ? "[^/]+" : Regex.Escape(segment))) + "(?:/|$)",
            RegexOptions.None,
            RegexTimeout.Value));

    /// <summary>
    /// The method and path of the shell's one <c>/bff</c> row in the contract's endpoint table made the
    /// given way ("Fetch", "Navigation" or "Form navigation"), with any query string dropped.
    /// </summary>
    private static (string Method, string Path) ContractEndpoint(string madeAs)
    {
        // The rule: a row whose request names a fragment, in its path (GET /bff/fragments) or its
        // query (GET /bff/login?fragment=...), belongs to the fragment surface, and the contract's
        // Fragments section says "An app with no fragments behaves as the rest of this contract
        // describes". Every fixture is an app with no fragments, so it calls the one row of each kind
        // whose request names no fragment. The filter reads the request cell only: the shell's own
        // rows cite "Fragments ->" in their Behaviour cell too.
        string contract = File.ReadAllText(FixtureFiles.Path("contracts", "http-contract.md"));
        Match row = Assert.Single(
            Regex.Matches(contract, "^\\| `(?<request>(?<method>[A-Z]+) (?<path>/bff/[^`?]*)[^`]*)` \\| (?<as>[^|]+?) \\|", RegexOptions.Multiline, RegexTimeout.Value),
            match => match.Groups["as"].Value == madeAs
                && !match.Groups["request"].Value.Contains("fragment", StringComparison.Ordinal));
        return (row.Groups["method"].Value, row.Groups["path"].Value);
    }

    private static string AppIdOf(string appId) =>
        FixtureFiles.Definition(appId)["appId"]!.GetValue<string>();

    public static TheoryData<string> EveryApp => ["app1", "app2", "app3"];
}
