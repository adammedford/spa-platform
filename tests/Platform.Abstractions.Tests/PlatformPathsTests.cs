using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// The path rules the gateway and <c>spactl</c> share (#988 part 8; the fragments spec, #961). The
/// literals are contracts/http-contract.md -> Endpoints and contracts/s3-layout.md.
/// </summary>
public sealed class PlatformPathsTests
{
    [Theory]
    [InlineData("/bff")]
    [InlineData("/BFF/login")]
    [InlineData("/api/orders")]
    [InlineData("/API")]
    [InlineData("/signin-oidc")]
    [InlineData("/SIGNIN-OIDC/frag1")]
    [InlineData("/signout-callback-oidc")]
    [InlineData("/Signout-Callback-Oidc/x")]
    [InlineData("/signout-oidc")]
    [InlineData("/SIGNOUT-OIDC")]
    [InlineData("/healthz")]
    [InlineData("/HEALTHZ")]
    [InlineData("/readyz")]
    [InlineData("/ReadyZ")]
    public void Each_reserved_prefix_is_reserved_outright_in_either_letter_case(string path)
    {
        // Mutation: match the prefixes with ordinal comparison, or drop one from Reserved.
        Assert.True(PlatformPaths.IsReservedOutright(new PathString(path)));
    }

    [Fact]
    public void Reserved_is_the_seven_prefixes_in_the_contracts_order()
    {
        Assert.Equal(
            ["/bff", "/api", "/signin-oidc", "/signout-callback-oidc", "/signout-oidc", "/healthz", "/readyz"],
            PlatformPaths.Reserved);
    }

    [Theory]
    [InlineData("/fragments/x/api", true)]
    [InlineData("/fragments/x/api/y", true)]
    [InlineData("/FRAGMENTS/x/API/y", true)]
    [InlineData("/fragments/x/apix", false)]
    [InlineData("/fragments/x/assets/api", false)]
    [InlineData("/fragments/x", false)]
    [InlineData("/apix", false)]
    [InlineData("/bffx", false)]
    [InlineData("/", false)]
    [InlineData("/index.html", false)]
    public void A_fragments_api_namespace_is_reserved_outright_and_its_neighbours_are_not(string path, bool reserved)
    {
        // Mutation: drop the fragment API clause, or match "/api" as a string prefix rather than a segment.
        Assert.Equal(reserved, PlatformPaths.IsReservedOutright(new PathString(path)));
    }

    [Theory]
    [InlineData("/fragments", true)]
    [InlineData("/fragments/", true)]
    [InlineData("/Fragments/a/b.js", true)]
    [InlineData("/fragmentsx", false)]
    [InlineData("/assets/fragments", false)]
    public void Everything_under_fragments_is_reserved_from_the_shell(string path, bool reserved)
    {
        Assert.Equal(reserved, PlatformPaths.IsReservedFromShell(new PathString(path)));
    }

    [Theory]
    [InlineData("/fragments")]
    [InlineData("/fragments/")]
    [InlineData("/fragments//x")]
    [InlineData("/fragmentsx/a")]
    [InlineData("/other/a")]
    public void FragmentOf_is_null_for_a_path_with_no_segment_under_fragments(string path)
    {
        // Mutation: return an empty segment rather than null.
        Assert.Null(PlatformPaths.FragmentOf(new PathString(path)));
    }

    [Theory]
    [InlineData("/fragments/a", "a", "")]
    [InlineData("/fragments/a/b", "a", "/b")]
    [InlineData("/fragments/orders-ui/assets/a.js", "orders-ui", "/assets/a.js")]
    [InlineData("/FRAGMENTS/orders-ui/", "orders-ui", "/")]
    public void FragmentOf_is_the_first_segment_and_the_path_after_it(string path, string fragmentId, string remainder)
    {
        // Mutation: keep the leading slash in the segment, or drop it from the remainder.
        (string FragmentId, PathString Remainder)? parsed = PlatformPaths.FragmentOf(new PathString(path));

        Assert.NotNull(parsed);
        Assert.Equal(fragmentId, parsed.Value.FragmentId);
        Assert.Equal(new PathString(remainder), parsed.Value.Remainder);
    }

    [Fact]
    public void The_fragment_paths_are_built_under_their_namespaces()
    {
        Assert.Equal(new PathString("/signin-oidc/orders-ui"), PlatformPaths.FragmentCallback("orders-ui"));
        Assert.Equal(new PathString("/fragments/orders-ui/api"), PlatformPaths.FragmentApi("orders-ui"));
        Assert.Equal("/fragments/orders-ui/entry.js", PlatformPaths.EntryUrl("orders-ui", PlatformPaths.DefaultEntry));
        Assert.True(PlatformPaths.IsReservedOutright(PlatformPaths.FragmentApi("orders-ui")));
        Assert.True(PlatformPaths.IsReservedOutright(PlatformPaths.FragmentCallback("orders-ui")));
    }

    [Theory]
    [InlineData("entry.js")]
    [InlineData("remoteEntry.js")]
    [InlineData("main.v2.js")]
    [InlineData("a_b-c.d-e.js")]
    [InlineData("9.js")]
    public void An_entry_name_is_a_release_root_js_file(string name)
    {
        Assert.True(PlatformPaths.IsEntryName(name));
    }

    [Theory]
    [InlineData("assets/entry.js")]
    [InlineData("api/entry.js")]
    [InlineData(".js")]
    [InlineData("entry.mjs")]
    [InlineData("-entry.js")]
    [InlineData("_entry.js")]
    [InlineData("entry..js")]
    [InlineData("entry.JS")]
    [InlineData("entry js.js")]
    [InlineData("")]
    public void A_name_outside_the_grammar_is_not_an_entry_name(string name)
    {
        // Mutation: drop the first-character rule, or accept an empty part between dots.
        Assert.False(PlatformPaths.IsEntryName(name));
    }

    [Fact]
    public void An_entry_name_is_at_most_sixty_four_characters()
    {
        // Mutation: compare with > 65, or drop the length check.
        string at64 = new string('a', PlatformPaths.EntryMaxLength - 3) + ".js";
        string at65 = new string('a', PlatformPaths.EntryMaxLength - 2) + ".js";

        Assert.Equal(64, at64.Length);
        Assert.True(PlatformPaths.IsEntryName(at64));
        Assert.False(PlatformPaths.IsEntryName(at65));
    }

    [Theory]
    [InlineData("api/x.js", false)]
    [InlineData("API/x.js", false)]
    [InlineData("Api", false)]
    [InlineData("apix/x.js", true)]
    [InlineData("assets/api/x.js", true)]
    [InlineData("entry.js", true)]
    public void A_fragment_release_publishes_nothing_under_a_top_level_api(string key, bool publishable)
    {
        // Mutation: compare "api" with ordinal case, or match it as a string prefix.
        Assert.Equal(publishable, PlatformPaths.IsPublishableInFragment(key));
    }
}
