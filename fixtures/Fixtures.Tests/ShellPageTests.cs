using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Playwright;

namespace Fixtures.Tests;

/// <summary>
/// The fixture shell in headless Chromium (#998): its release served from <c>fixtures/spa/shell/</c>,
/// each <c>/fragments/{fragmentId}/**</c> from that fragment's fixture directory, and the
/// <c>/bff</c> endpoints as stubs in the shapes contracts/http-contract.md gives them, with the
/// merged content security policy the gateway would send (the baseline, then the shell's csp, then
/// each fragment's in registry order). The seam is the page's network and its document: which entry
/// URLs and chunks it requests, and what it mounts.
/// </summary>
public sealed class ShellPageTests(HeadlessBrowser headless) : IClassFixture<HeadlessBrowser>
{
    [Fact]
    public async Task The_shell_imports_each_fragments_entry_and_its_chunk_resolves_under_that_fragments_assets()
    {
        // Mutations: the shell importing a fixed "/fragments/{id}/entry.js" instead of entryUrl turns
        // this red on reports, whose entry is main.js; a fragment entry importing "/assets/..." by an
        // absolute path turns it red on that fragment (the shell's assets/ has no such chunk).
        await using ShellPage shell = await ShellPage.OpenAsync(headless.Browser, _ => "signedIn");

        await shell.ComposedAsync();

        foreach (JsonNode fragment in shell.Fragments)
        {
            string id = fragment["fragmentId"]!.GetValue<string>();
            Assert.Single(shell.RequestsTo(ShellPage.EntryUrlOf(fragment)));
            IRequest chunk = Assert.Single(shell.Requests, request =>
                new Uri(request.Url).AbsolutePath.StartsWith($"/fragments/{id}/assets/", StringComparison.Ordinal));
            Assert.Equal("true", await shell.SlotAttributeAsync(id, "data-mounted"));
            Assert.Equal(new Uri(chunk.Url).AbsolutePath, await shell.Page.GetAttributeAsync($"[data-fragment=\"{id}\"]", "data-chunk"));
        }
    }

    [Fact]
    public async Task A_fragment_that_is_not_signed_in_gets_a_control_and_is_not_imported()
    {
        // "The shell mounts a signedIn fragment; for loginRequired or unavailable it renders a control
        // instead of mounting it", and the control is the only thing that starts the fragment login.
        // Mutation: mounting every fragment whatever its state turns this red.
        await using ShellPage shell = await ShellPage.OpenAsync(headless.Browser, id => id switch
        {
            "reports" => "loginRequired",
            "alerts" => "unavailable",
            _ => "signedIn",
        });

        await shell.ComposedAsync();

        JsonNode reports = shell.Fragments.Single(f => f["fragmentId"]!.GetValue<string>() == "reports");
        Assert.Empty(shell.RequestsTo(ShellPage.EntryUrlOf(reports)));
        Assert.Null(await shell.SlotAttributeAsync("reports", "data-mounted"));
        Assert.Equal("Sign in to reports", await shell.Page.TextContentAsync("[data-fragment-login=\"reports\"]"));
        Assert.Equal("Unavailable; try again", await shell.Page.TextContentAsync("[data-fragment-login=\"alerts\"]"));
        Assert.Equal("true", await shell.SlotAttributeAsync("orders", "data-mounted"));
        Assert.Empty(shell.RequestsTo("/bff/login"));

        Task<IRequest> navigation = shell.Page.WaitForRequestAsync(request =>
            request.IsNavigationRequest && new Uri(request.Url).AbsolutePath == "/bff/login");
        await shell.Page.ClickAsync("[data-fragment-login=\"reports\"]");
        Uri login = new((await navigation).Url);

        Assert.Equal("reports", QueryHelpers.ParseQuery(login.Query)["fragment"].ToString());
        Assert.Equal(ShellPage.Path, QueryHelpers.ParseQuery(login.Query)["returnUrl"].ToString());
    }

    [Fact]
    public async Task A_policy_without_the_maps_hash_composes_nothing()
    {
        // The map is exercised, not decorative: with the shell's hash left out of script-src the
        // browser refuses the map, the shell's module cannot resolve "fixture-ui", and no fragment
        // is imported. This is the failing input for the first test's policy. Mutation: the shell
        // importing its shared module by path instead of through the map turns this red.
        await using ShellPage shell = await ShellPage.OpenAsync(headless.Browser, _ => "signedIn", withShellPolicy: false);

        await shell.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.Null(await shell.Page.GetAttributeAsync("#app", "data-composed"));
        Assert.DoesNotContain(shell.Requests, request => new Uri(request.Url).AbsolutePath.StartsWith("/fragments/", StringComparison.Ordinal));
    }

    /// <summary>The shell on a made-up origin, every request answered by <c>page.RouteAsync</c>.</summary>
    private sealed class ShellPage : IAsyncDisposable
    {
        internal const string Origin = "https://shell.spa-poc.test";

        internal const string Path = "/";

        private readonly IBrowserContext context;

        private readonly ConcurrentQueue<IRequest> requests = new();

        private readonly Func<string, string> stateOf;

        private readonly string policy;

        private ShellPage(IBrowserContext context, IPage page, Func<string, string> stateOf, string policy)
        {
            this.context = context;
            Page = page;
            this.stateOf = stateOf;
            this.policy = policy;
        }

        public IPage Page { get; }

        public IReadOnlyList<IRequest> Requests => [.. requests];

        public IReadOnlyList<JsonNode> Fragments => [.. FixtureFiles.Fragments(Definition)];

        private static JsonNode Definition => FixtureFiles.Definition("shell");

        private static string AppId => Definition["appId"]!.GetValue<string>();

        /// <summary><c>/fragments/{fragmentId}/{entry}</c>, entry.js unless the definition names another.</summary>
        public static string EntryUrlOf(JsonNode fragment) =>
            $"/fragments/{fragment["fragmentId"]!.GetValue<string>()}/" +
            (fragment["entry"]?.GetValue<string>()
             ?? FixtureFiles.Read(FixtureFiles.Path("contracts", "registry.schema.json"))["$defs"]!["entry"]!["default"]!.GetValue<string>());

        public static async Task<ShellPage> OpenAsync(IBrowser browser, Func<string, string> stateOf, bool withShellPolicy = true)
        {
            IBrowserContext context = await browser.NewContextAsync();
            IPage page = await context.NewPageAsync();
            ShellPage shell = new(context, page, stateOf, Policy(withShellPolicy));
            page.Request += (_, request) => shell.requests.Enqueue(request);
            await page.RouteAsync("**/*", shell.AnswerAsync);
            await page.GotoAsync(Origin + Path);
            return shell;
        }

        public async Task ComposedAsync() =>
            await Page.WaitForSelectorAsync("#app[data-composed=\"true\"]", new() { Timeout = 10000 });

        public Task<string?> SlotAttributeAsync(string fragmentId, string name) =>
            Page.GetAttributeAsync($"[data-slot=\"{fragmentId}\"]", name);

        public IReadOnlyList<IRequest> RequestsTo(string path) =>
            [.. requests.Where(request => new Uri(request.Url).AbsolutePath == path)];

        public async ValueTask DisposeAsync() => await context.DisposeAsync();

        /// <summary>
        /// The policy the gateway sends on the shell's host: per directive, the baseline's sources,
        /// then the shell's, then each fragment's in registry order (Response headers).
        /// </summary>
        private static string Policy(bool withShellPolicy)
        {
            Dictionary<string, List<string>> merged = ContentSecurityPolicyFit.Baseline
                .ToDictionary(directive => directive.Key, directive => directive.Value.ToList(), StringComparer.Ordinal);
            IEnumerable<JsonNode?> additions = [withShellPolicy ? Definition["csp"] : null, .. FixtureFiles.Fragments(Definition).Select(f => f["csp"])];
            foreach (JsonObject csp in additions.OfType<JsonObject>())
            {
                foreach ((string directive, JsonNode? sources) in csp)
                {
                    merged[directive].AddRange(sources!.AsArray().Select(s => s!.GetValue<string>()).Where(s => !merged[directive].Contains(s)));
                }
            }

            return string.Join("; ", merged.Select(directive => directive.Key + " " + string.Join(' ', directive.Value)));
        }

        private Task AnswerAsync(IRoute route)
        {
            string path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/bff/session")
            {
                return Json(route, new Dictionary<string, object>
                {
                    ["sub"] = "user-1",
                    ["name"] = "Ada",
                    ["email"] = "ada@example.test",
                    ["expiresAt"] = "2026-11-20T12:00:00Z",
                    ["csrfToken"] = "csrf-canary-shell",
                    ["fragments"] = Fragments.ToDictionary(f => f["fragmentId"]!.GetValue<string>(), f => stateOf(f["fragmentId"]!.GetValue<string>())),
                });
            }

            if (path == "/bff/fragments")
            {
                return Json(route, new
                {
                    fragments = Fragments.Select(f => new { fragmentId = f["fragmentId"]!.GetValue<string>(), entryUrl = EntryUrlOf(f) }),
                });
            }

            if (path.StartsWith("/bff/", StringComparison.Ordinal))
            {
                return route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = "<!doctype html><title>provider</title>" });
            }

            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments is ["fragments", _, "api", ..])
            {
                return Json(route, new { ok = true });
            }

            string? file = segments is ["fragments", string id, .. string[] rest] && rest.Length > 0
                ? FixtureFiles.Path(["fixtures", "fragments", AppId, id, .. rest])
                : path == Path
                    ? FixtureFiles.Path("fixtures", "spa", AppId, "index.html")
                    : FixtureFiles.Path(["fixtures", "spa", AppId, .. segments]);

            if (!File.Exists(file))
            {
                return route.FulfillAsync(new() { Status = 404, ContentType = "text/plain", Body = "not found" });
            }

            return route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = file.EndsWith(".js", StringComparison.Ordinal) ? "text/javascript" : "text/html",
                Path = file,
                Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = policy },
            });
        }

        private Task Json(IRoute route, object body) =>
            route.FulfillAsync(new()
            {
                Status = 200,
                ContentType = "application/json",
                Body = JsonSerializer.Serialize(body),
                Headers = new Dictionary<string, string> { ["Content-Security-Policy"] = policy },
            });
    }
}
