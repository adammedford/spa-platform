using Gateway.E2ETests.Stack;

namespace Gateway.E2ETests.Deployed;

/// <summary>
/// F-05's web-storage check (<see cref="WebStorage"/>): on the cell what the page kept in storage may be
/// a real token, so its failure says that storage is not empty and never what is in it.
/// </summary>
public sealed class WebStorageTests
{
    private const string Canary = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJDQU5BUlkifQ.c2lnbmF0dXJl";

    [Fact]
    public void Storage_holding_a_value_fails_the_check_and_the_message_prints_neither_the_storage_nor_the_value()
    {
        string storage = $$$"""{"local":{"access_token":"{{{Canary}}}"},"session":{}}""";

        Exception failed = Assert.ThrowsAny<Exception>(() => WebStorage.AssertEmpty(storage));

        Assert.Contains("web storage", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Canary, failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", failed.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(storage, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_storage_holding_a_value_fails_the_check_too()
    {
        string storage = $$$"""{"local":{},"session":{"t":"{{{Canary}}}"}}""";

        Exception failed = Assert.ThrowsAny<Exception>(() => WebStorage.AssertEmpty(storage));

        Assert.DoesNotContain(Canary, failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_storage_passes_the_check() =>
        WebStorage.AssertEmpty("""{"local":{},"session":{}}""");
}
