using Platform.Abstractions;
using Platform.Testing;

namespace Platform.Testing.Tests;

/// <summary>
/// The in-memory double against the shared <see cref="ObjectStoreContract"/>, in its default mode:
/// a key never written reads as null. The S3 store in task 2.2 subclasses the same suite, so the two
/// cannot drift.
/// </summary>
public sealed class InMemoryObjectStoreTests : ObjectStoreContract
{
    protected override IObjectStore CreateStore() => new InMemoryObjectStore();
}

/// <summary>
/// The in-memory double in its refusal mode, against the same suite: a key never written is refused,
/// as S3 refuses it to the gateway's GetObject-only role, and every other case holds unchanged
/// (change request contracts/changes/issue-381-object-read-refusal.md, item 2).
/// Mutation (#427): make the refusal mode answer null, and both missing-key cases here fail.
/// </summary>
public sealed class RefusingInMemoryObjectStoreTests : ObjectStoreContract
{
    protected override MissingKeyAnswer MissingKeys => MissingKeyAnswer.AmbiguousRefusal;

    protected override IObjectStore CreateStore() => new InMemoryObjectStore(MissingKeyAnswer.AmbiguousRefusal);
}

/// <summary>
/// The suite asserts exactly the declared answer, never either: a store declared in one mode that
/// answers in the other fails the missing-key cases. Without this the two-mode suite could accept
/// both answers and pass every store, which is the rule the change request removed.
/// </summary>
public sealed class ObjectStoreContractModeTests
{
    [Theory]
    [InlineData(MissingKeyAnswer.ConfirmedAbsence, MissingKeyAnswer.AmbiguousRefusal)]
    [InlineData(MissingKeyAnswer.AmbiguousRefusal, MissingKeyAnswer.ConfirmedAbsence)]
    public async Task A_store_that_answers_in_the_other_mode_fails_the_missing_key_cases(
        MissingKeyAnswer declared, MissingKeyAnswer answers)
    {
        // Mutation (#427): let the contract's missing-key assertion accept null or a refusal alike,
        // and both rows fail.
        Mismatched suite = new(declared, answers);

        await Assert.ThrowsAnyAsync<Exception>(suite.A_key_that_was_never_written_reads_as_the_store_declares);
        await Assert.ThrowsAnyAsync<Exception>(suite.A_conditional_read_of_a_key_never_written_reads_as_the_store_declares);
    }

    [Theory]
    [InlineData(MissingKeyAnswer.ConfirmedAbsence)]
    [InlineData(MissingKeyAnswer.AmbiguousRefusal)]
    public async Task A_store_that_answers_as_it_declares_passes_them(MissingKeyAnswer mode)
    {
        // The other half: the same harness, matched, passes, so the failures above are the
        // mismatch and not the harness.
        // Mutation (#427 review): make the refusal mode answer null, or answer a conditional read of
        // a missing key with null, and the AmbiguousRefusal row fails.
        Mismatched suite = new(mode, mode);

        await suite.A_key_that_was_never_written_reads_as_the_store_declares();
        await suite.A_conditional_read_of_a_key_never_written_reads_as_the_store_declares();
    }

    [Fact]
    public async Task A_declaration_the_suite_does_not_know_fails()
    {
        // Fails closed: an unknown mode asserts nothing, so it must not pass.
        // Mutation (#427): make the switch's default case return, and this fails.
        Mismatched suite = new((MissingKeyAnswer)99, MissingKeyAnswer.ConfirmedAbsence);

        await Assert.ThrowsAnyAsync<Exception>(suite.A_key_that_was_never_written_reads_as_the_store_declares);
    }

    [Fact]
    public async Task The_refusal_mode_refuses_only_keys_it_does_not_hold_and_names_the_key()
    {
        // The refusal is for a missing key; a key that is there reads normally, as it does under the
        // deployed role. The store's own exception is absent, since memory has none to wrap.
        // Mutation (#427 review): refuse every key in this mode, held ones included; refuse with a
        // key other than the one read; or give the refusal an invented inner exception. Each fails this.
        InMemoryObjectStore store = new(MissingKeyAnswer.AmbiguousRefusal);
        using MemoryStream body = new("<!doctype html>"u8.ToArray());
        await store.PutAsync("apps/app1/releases/r1/index.html", body, ifMatch: null, TestContext.Current.CancellationToken);

        StoredObject? present = await store.GetAsync(
            "apps/app1/releases/r1/index.html", ifNoneMatch: null, TestContext.Current.CancellationToken);
        ObjectReadRefusedException refused = await Assert.ThrowsAsync<ObjectReadRefusedException>(
            () => store.GetAsync("apps/app1/releases/r1/assets/missing.js", ifNoneMatch: null, TestContext.Current.CancellationToken));

        Assert.NotNull(present);
        present.Body!.Dispose();
        Assert.Equal("apps/app1/releases/r1/assets/missing.js", refused.Key);
        Assert.Null(refused.InnerException);
    }

    [Fact]
    public async Task A_cancelled_read_is_cancelled_in_either_mode_before_it_is_refused()
    {
        // The change request: cancellation propagates unchanged, never as a refusal.
        // Mutation (#427): check the key before the token, and the refusal mode throws the refusal.
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        InMemoryObjectStore store = new(MissingKeyAnswer.AmbiguousRefusal);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetAsync("registry/registry.json", ifNoneMatch: null, cancelled.Token));
    }

    [Fact]
    public void A_mode_that_is_not_one_of_the_two_is_refused_at_construction()
    {
        // Without the guard an undefined mode falls through to the null answer, so a store built
        // with a cast value would claim a mode it does not have and pass the confirmed-absence suite.
        // Mutation (#427 review): drop the constructor's Enum.IsDefined guard, and this fails.
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryObjectStore((MissingKeyAnswer)99));
    }

    /// <summary>
    /// The contract suite, declared in one mode, over a store that answers in another. Not public,
    /// so the test runner does not run it as a suite of its own.
    /// </summary>
    private sealed class Mismatched(MissingKeyAnswer declared, MissingKeyAnswer answers) : ObjectStoreContract
    {
        protected override MissingKeyAnswer MissingKeys => declared;

        protected override IObjectStore CreateStore() => new InMemoryObjectStore(answers);
    }
}
