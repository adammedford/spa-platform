using System.Text.Json.Nodes;

namespace Spactl.Tests;

/// <summary>
/// The import-map scan every registry write makes of each shell's <c>index.html</c> (the fragments
/// spec, #961, "The import map", and its traces' C9 scanner; ticket #993). Each case writes the
/// contract's registry example with the shell's document seeded as given and runs
/// <c>registry validate</c> against the bucket, which makes every check a write makes.
/// </summary>
public sealed class ImportMapTests : IDisposable
{
    private static readonly string Map = FragmentWorld.Map("m1");

    private readonly FragmentWorld world = new();

    private Harness Spactl => world.Spactl;

    public void Dispose() => world.Dispose();

    /// <summary>Seeds the example's releases, the shell's <c>index.html</c> as given, and validates the example with the shell's script-src set.</summary>
    private async Task<int> Validate(string index, params string[] scriptSrc)
    {
        JsonObject document = Harness.ExampleJson("registry.example.json");
        JsonObject shell = document["apps"]![0]!.AsObject();
        FragmentWorld.ScriptSrc(shell, scriptSrc);
        foreach (JsonNode? fragment in shell["fragments"]!.AsArray())
        {
            await Spactl.Store.SeedAsync(
                $"apps/app1/fragments/{fragment!["fragmentId"]}/releases/{fragment["release"]}/{fragment["entry"]}", "export {};");
        }

        await Spactl.Store.SeedAsync($"apps/app1/releases/{shell["release"]}/index.html", index);
        return await Spactl.Run("registry", "validate", Spactl.WriteFile("registry.json", document.ToJsonString()));
    }

    private static string Html(string head) => "<!doctype html><html><head>" + head + "</head><body></body></html>";

    private string Error => Spactl.Error.ToString();

    [Fact]
    public async Task A_shell_with_no_map_passes()
    {
        Assert.Equal(0, await Validate(Html("<script type=\"module\" src=\"/assets/app.js\"></script>")));
    }

    [Fact]
    public async Task A_map_admitted_by_its_sha256_passes()
    {
        Assert.Equal(0, await Validate(Html(FragmentWorld.ImportMapTag(Map)), FragmentWorld.HashSource(Map)));
    }

    [Fact]
    public async Task A_map_no_source_admits_is_refused_naming_the_padded_sha256_source_to_add()
    {
        string map = FragmentWorld.Map("m3");

        Assert.Equal(1, await Validate(Html(FragmentWorld.ImportMapTag(map)), FragmentWorld.HashSource(Map)));

        string source = FragmentWorld.HashSource(map);
        Assert.EndsWith("='", source, StringComparison.Ordinal);
        Assert.Contains($"Add {source} to", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sha384")]
    [InlineData("sha512")]
    public async Task A_sha384_or_sha512_source_admits_a_map(string algorithm)
    {
        Assert.Equal(0, await Validate(Html(FragmentWorld.ImportMapTag(Map)), FragmentWorld.HashSource(Map, algorithm)));
    }

    [Fact]
    public async Task A_source_is_matched_on_its_decoded_bytes_so_unpadded_and_base64url_spellings_admit()
    {
        // A map whose sha256 carries '+' or '/' and padding, found by name, so the respelling changes the text.
        string map = Enumerable.Range(0, 200).Select(i => FragmentWorld.Map("m" + i))
            .First(candidate => FragmentWorld.HashSource(candidate).AsSpan(8).IndexOfAny('+', '/') >= 0);
        string source = FragmentWorld.HashSource(map);
        string respelt = source.Replace("=", "", StringComparison.Ordinal).Replace('+', '-').Replace('/', '_');
        Assert.NotEqual(source, respelt);

        Assert.Equal(0, await Validate(Html(FragmentWorld.ImportMapTag(map)), respelt));
    }

    private const string Crlf = "{\r\n  \"imports\": {\"lib\": \"/assets/lib.js\"}\r\n}";

    [Fact]
    public async Task A_crlf_authored_map_passes_with_its_lf_hash()
    {
        Assert.Equal(0, await Validate(Html(FragmentWorld.ImportMapTag(Crlf)), FragmentWorld.HashSource(Crlf.Replace("\r\n", "\n", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_crlf_authored_map_is_refused_with_its_crlf_hash_naming_the_lf_one()
    {
        Assert.Equal(1, await Validate(Html(FragmentWorld.ImportMapTag(Crlf)), FragmentWorld.HashSource(Crlf)));
        Assert.Contains(FragmentWorld.HashSource(Crlf.Replace("\r\n", "\n", StringComparison.Ordinal)), Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lone_cr_is_read_as_lf()
    {
        string cr = "{\r  \"imports\": {}\r}";

        Assert.Equal(0, await Validate(Html(FragmentWorld.ImportMapTag(cr)), FragmentWorld.HashSource(cr.Replace('\r', '\n'))));
    }

    [Fact]
    public async Task A_second_map_is_refused()
    {
        string twice = FragmentWorld.ImportMapTag(Map) + FragmentWorld.ImportMapTag(Map);

        Assert.Equal(1, await Validate(Html(twice), FragmentWorld.HashSource(Map)));
        Assert.Contains("more than one import map", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_src_on_a_map_is_refused()
    {
        Assert.Equal(1, await Validate(Html("<script type=\"importmap\" src=\"/assets/map.json\"></script>")));
        Assert.Contains("names a src", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<script type='importmap'>")]
    [InlineData("<script type=importmap>")]
    [InlineData("<SCRIPT type=\"importmap\">")]
    [InlineData("<script type=\"importmap\" >")]
    [InlineData("<script type=\"IMPORTMAP\">")]
    [InlineData("<script\ttype=\"importmap\">")]
    [InlineData("<script nonce=\"n\" type=\"importmap\">")]
    public async Task A_map_whose_start_tag_is_not_exact_is_refused(string tag)
    {
        Assert.Equal(1, await Validate(Html(tag + Map + "</script>"), FragmentWorld.HashSource(Map)));
        Assert.Contains("not exactly", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_tag_carrying_an_ampersand_is_refused()
    {
        Assert.Equal(1, await Validate(Html("<script type=\"&#105;mportmap\">" + Map + "</script>"), FragmentWorld.HashSource(Map)));
        Assert.Contains("'&'", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"imports\":{\"é\":\"/assets/a.js\"}}", "outside ASCII")]
    [InlineData("{\"imports\":{\"a\":\"/assets/a.js\"}}\0", "NUL")]
    [InlineData("{\"imports\":{}}<!--", "'<!--'")]
    [InlineData("{\"imports\":{\"<script\":\"/assets/a.js\"}}", "'<script'")]
    [InlineData("{\"imports\":{\"<SCRIPT\":\"/assets/a.js\"}}", "'<script'")]
    public async Task A_map_body_the_scan_will_not_hash_is_refused(string body, string reason)
    {
        Assert.Equal(1, await Validate(Html(FragmentWorld.ImportMapTag(body))));
        Assert.Contains(reason, Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_inside_another_script_is_not_read_as_a_tag()
    {
        // A classic script's content is script data, so a string in it that looks like a map's start
        // tag is not one (https://html.spec.whatwg.org/multipage/parsing.html#script-data-state).
        Assert.Equal(0, await Validate(Html("<script src=\"/assets/a.js\">var t = '<script type=importmap>';</script>")));
    }

    [Fact]
    public async Task A_greater_than_inside_a_quoted_attribute_value_does_not_end_the_tag()
    {
        // The tokenizer ends a start tag at a '>' outside a quoted attribute value
        // (https://html.spec.whatwg.org/multipage/parsing.html#attribute-value-(double-quoted)-state), so
        // this tag names importmap; ending it at the first '>' would read the map as script data.
        Assert.Equal(1, await Validate(Html("<script data-x=\">\" type=\"importmap\">" + Map + "</script>"), FragmentWorld.HashSource(Map)));
        Assert.Contains("not exactly", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-- <script> -->")]
    [InlineData("<!-- <script> --!>")]
    public async Task A_script_tag_inside_a_comment_does_not_hide_the_map_after_it(string comment)
    {
        // A comment's text is never a tag (https://html.spec.whatwg.org/multipage/parsing.html#comment-state),
        // so the map after it is the document's, and it is checked. A comment ended late would reach the
        // map and be refused as a comment naming importmap instead.
        string map = FragmentWorld.Map("m3");

        Assert.Equal(1, await Validate(Html(comment + FragmentWorld.ImportMapTag(map)), FragmentWorld.HashSource(Map)));
        Assert.Contains($"Add {FragmentWorld.HashSource(map)} to", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-- {0} -->")]
    public async Task Text_the_scan_skips_as_a_comment_is_refused_when_it_names_importmap(string around)
    {
        // Text the scan skips as a comment must hold no map a browser could run, so it refuses rather
        // than skip one. A "<!--" inside raw text is no comment: see the test after this one.
        string html = Html(string.Format(System.Globalization.CultureInfo.InvariantCulture, around, FragmentWorld.ImportMapTag(Map)));

        Assert.Equal(1, await Validate(html, FragmentWorld.HashSource(Map)));
        Assert.Contains("a comment names importmap", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_comment_open_inside_raw_text_does_not_hide_the_map_after_it()
    {
        // A browser reads no "<!--" inside a <style> as a comment, so the map after it is live and checked.
        string map = FragmentWorld.Map("m3");

        Assert.Equal(1, await Validate(Html("<style>a::before { content: \"<!--\" }</style>" + FragmentWorld.ImportMapTag(map) + "-->"), FragmentWorld.HashSource(Map)));
        Assert.Contains($"Add {FragmentWorld.HashSource(map)} to", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!-->")]
    [InlineData("<!--->")]
    public async Task An_abruptly_closed_comment_ends_where_a_browser_ends_it(string comment)
    {
        string map = FragmentWorld.Map("m3");

        Assert.Equal(1, await Validate(Html(comment + FragmentWorld.ImportMapTag(map) + "-->"), FragmentWorld.HashSource(Map)));
        Assert.Contains($"Add {FragmentWorld.HashSource(map)} to", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<title><script></title>{0}")]
    [InlineData("<textarea><script></textarea>{0}")]
    [InlineData("<style><script></style>{0}")]
    [InlineData("<noscript><script></noscript>{0}")]
    public async Task A_script_tag_inside_raw_text_is_refused_rather_than_read_as_script_data(string around)
    {
        // A browser reads a <script inside a raw-text element as text, so the map after it is live
        // (https://html.spec.whatwg.org/multipage/parsing.html#rawtext-state). Read as a classic script,
        // its data would run to the map's end tag and hide the map; inside <svg> the same text is markup.
        // The scan refuses it, as the review's probe of this input (#1486) asked.
        string map = FragmentWorld.Map("m9");
        string html = Html(string.Format(System.Globalization.CultureInfo.InvariantCulture, around, FragmentWorld.ImportMapTag(map)));

        Assert.Equal(1, await Validate(html, FragmentWorld.HashSource(Map)));
        Assert.Contains("holds '<script' or names importmap", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_raw_text_element_naming_importmap_is_refused_even_before_an_admitted_map()
    {
        // The second half of the raw-text refusal: text skipped as raw must not name a map either,
        // since inside <svg> or <math> it is markup (#1486's review).
        Assert.Equal(1, await Validate(Html("<title>importmap</title>" + FragmentWorld.ImportMapTag(Map)), FragmentWorld.HashSource(Map)));
        Assert.Contains("<title> holds '<script' or names importmap", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plaintext_runs_to_the_end_of_the_document_so_a_map_after_it_is_refused()
    {
        // Nothing ends <plaintext>, not even </plaintext>
        // (https://html.spec.whatwg.org/multipage/parsing.html#plaintext-state), so the map after it is
        // text a browser never runs, and the scan, skipping it as raw text, refuses it.
        Assert.Equal(1, await Validate(Html("<plaintext></plaintext>" + FragmentWorld.ImportMapTag(Map)), FragmentWorld.HashSource(Map)));
        Assert.Contains("<plaintext> holds '<script' or names importmap", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<div title=\"<script>\"></div>{0}", "<div>")]
    [InlineData("<div title='<script>'></div>{0}", "<div>")]
    [InlineData("<div title=<script>></div>{0}", "<div>")]
    [InlineData("<div <script>></div>{0}", "<div>")]
    [InlineData("</div title=\"<script>\">{0}", "</div>")]
    [InlineData("<title></title x=\"<script>\">{0}", "</title>")]
    [InlineData("<script></script x=\"<script>\">{0}", "</script>")]
    public async Task A_script_tag_inside_another_tag_is_refused_rather_than_read_as_script_data(string around, string tag)
    {
        // A browser reads a '<script' inside another start or end tag as part of that tag, so the map
        // after it is live (https://html.spec.whatwg.org/multipage/parsing.html#before-attribute-value-state).
        // Read as a classic script, its data would run to the map's end tag and hide the map; the
        // review's probe of the first input (#1486) exited 0.
        string map = FragmentWorld.Map("m9");
        string html = Html(string.Format(System.Globalization.CultureInfo.InvariantCulture, around, FragmentWorld.ImportMapTag(map)));

        Assert.Equal(1, await Validate(html, FragmentWorld.HashSource(Map)));
        Assert.Contains($"a {tag} tag holds '<script'", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<!x <script>>{0}")]
    [InlineData("<?<script>>{0}")]
    [InlineData("<!doctype <script>>{0}")]
    [InlineData("</ <script>>{0}")]
    [InlineData("<!x importmap>{0}")]
    public async Task A_script_tag_inside_a_bogus_comment_or_doctype_is_refused_rather_than_read_as_script_data(string around)
    {
        // A bogus comment or a doctype ends at the first '>'
        // (https://html.spec.whatwg.org/multipage/parsing.html#bogus-comment-state), so a '<script' in
        // one is text and the map after it is live.
        string map = FragmentWorld.Map("m9");
        string html = Html(string.Format(System.Globalization.CultureInfo.InvariantCulture, around, FragmentWorld.ImportMapTag(map)));

        Assert.Equal(1, await Validate(html, FragmentWorld.HashSource(Map)));
        Assert.Contains("construct holds '<script' or names importmap", Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("</scriptx")]
    [InlineData("</SCRIPT-")]
    public async Task A_map_ends_at_an_end_tag_not_at_a_longer_name(string notAnEndTag)
    {
        // Script data ends at "</script" followed by whitespace, '/' or '>' only
        // (https://html.spec.whatwg.org/multipage/parsing.html#script-data-end-tag-name-state), so
        // the whole body is the map a browser runs, and its hash is the one that admits it.
        string map = "{\"imports\":{\"lib\":\"/assets/" + notAnEndTag + ".js\"}}";

        Assert.Equal(0, await Validate(Html(FragmentWorld.ImportMapTag(map)), FragmentWorld.HashSource(map)));
    }

    [Fact]
    public async Task A_csp_meta_warns_and_does_not_refuse()
    {
        string head = "<meta http-equiv=\"Content-Security-Policy\" content=\"script-src 'self'\">" + FragmentWorld.ImportMapTag(Map);

        Assert.Equal(0, await Validate(Html(head), FragmentWorld.HashSource(Map)));
        Assert.Contains("warning:", Error, StringComparison.Ordinal);
        Assert.Contains("Content-Security-Policy <meta>", Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_without_a_csp_meta_does_not_warn_of_one()
    {
        Assert.Equal(0, await Validate(Html("<meta charset=\"utf-8\">" + FragmentWorld.ImportMapTag(Map)), FragmentWorld.HashSource(Map)));
        Assert.DoesNotContain("<meta>", Error, StringComparison.Ordinal);
    }
}
