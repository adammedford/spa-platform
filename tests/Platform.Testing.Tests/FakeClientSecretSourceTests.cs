using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The secret source double. Two things it models deliberately: <c>Get</c> never does I/O, so a
/// reference that was not preloaded is not available however well known its value is; and a
/// reference whose value cannot be read is a single-app failure, not a batch failure, which is what
/// task 2.2 means by "one unreadable secret at start-up costs one app, not all".
///
/// Task 2.5: "no value appears in a log or exception message." The last test here is that rule.
/// </summary>
public sealed class FakeClientSecretSourceTests
{
    private const string App1Ref = "ssm:/spa-platform/poc/apps/app1/client-secret:1";
    private const string App2Ref = "ssm:/spa-platform/poc/apps/app2/client-secret:1";
    private const string Secret = "correct-horse-battery-staple";

    [Fact]
    public async Task A_preloaded_reference_returns_its_value()
    {
        FakeClientSecretSource source = new() { [App1Ref] = Secret };

        await source.PreloadAsync([App1Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
    }

    [Fact]
    public void A_reference_read_before_any_preload_throws()
    {
        FakeClientSecretSource source = new() { [App1Ref] = Secret };

        Assert.Throws<InvalidOperationException>(() => source.Get(App1Ref));
    }

    [Fact]
    public async Task A_reference_left_out_of_the_preload_throws_even_when_its_value_is_known()
    {
        // Get does no I/O. A caller that forgot to preload must fail here, not silently work.
        FakeClientSecretSource source = new() { [App1Ref] = Secret, [App2Ref] = Secret };

        await source.PreloadAsync([App1Ref], Ct);

        Assert.Throws<InvalidOperationException>(() => source.Get(App2Ref));
    }

    [Fact]
    public async Task An_unreadable_reference_throws_and_leaves_the_others_readable()
    {
        // The cold-start case: app 2's parameter cannot be read, so app 2 is left out and app 1
        // still goes live.
        FakeClientSecretSource source = new() { [App1Ref] = Secret };

        await source.PreloadAsync([App1Ref, App2Ref], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        Assert.Throws<InvalidOperationException>(() => source.Get(App2Ref));
    }

    [Fact]
    public async Task A_preload_records_what_it_was_asked_for()
    {
        // Task 2.2 preloads before it validates and swaps. A test asserts the order by asserting
        // this.
        FakeClientSecretSource source = new() { [App1Ref] = Secret };

        await source.PreloadAsync([App1Ref, App2Ref], Ct);

        Assert.Equal([App1Ref, App2Ref], source.Preloaded);
    }

    [Fact]
    public async Task A_later_preload_adds_to_what_is_available()
    {
        // A rotation raises the parameter version, so the entry names a new reference and the next
        // snapshot preloads it.
        FakeClientSecretSource source = new() { [App1Ref] = Secret };
        await source.PreloadAsync([App1Ref], Ct);

        source["ssm:/spa-platform/poc/apps/app1/client-secret:2"] = "rotated";
        await source.PreloadAsync(["ssm:/spa-platform/poc/apps/app1/client-secret:2"], Ct);

        Assert.Equal(Secret, source.Get(App1Ref));
        Assert.Equal("rotated", source.Get("ssm:/spa-platform/poc/apps/app1/client-secret:2"));
    }

    [Fact]
    public async Task The_failure_names_the_reference_and_never_a_value()
    {
        FakeClientSecretSource source = new() { [App1Ref] = Secret };
        await source.PreloadAsync([App1Ref], Ct);

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => source.Get(App2Ref));

        Assert.Contains(App2Ref, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, thrown.ToString(), StringComparison.Ordinal);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
