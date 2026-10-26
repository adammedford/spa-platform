using System.Reflection;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <see cref="ObjectReadRefusedException"/> is what <see cref="IObjectStore.GetAsync"/> throws when the
/// store refused a read rather than confirming the key is absent (change request
/// contracts/changes/issue-381-object-read-refusal.md, item 1). Its callers decide what a refusal
/// means for their key, so the two things they read off it, the key and the store's own exception,
/// are what this holds.
/// </summary>
public sealed class ObjectReadRefusedExceptionTests
{
    [Fact]
    public void It_carries_the_key_and_the_store_s_exception()
    {
        // Mutation: drop the inner exception from the base constructor call, and the inner is null.
        // Mutation: assign Key from anything but the argument, and the key comes back wrong.
        InvalidOperationException fromTheStore = new("403 AccessDenied");

        ObjectReadRefusedException refused = new("apps/app1/releases/r1/index.html", fromTheStore);

        Assert.Equal("apps/app1/releases/r1/index.html", refused.Key);
        Assert.Same(fromTheStore, refused.InnerException);
    }

    [Fact]
    public void A_store_with_no_exception_of_its_own_refuses_with_none()
    {
        // The in-memory store's refusal mode has no SDK exception to wrap. The inner is then null,
        // not an invented one a caller might read as a cause.
        ObjectReadRefusedException refused = new("registry/registry.json", innerException: null);

        Assert.Equal("registry/registry.json", refused.Key);
        Assert.Null(refused.InnerException);
    }

    [Fact]
    public void The_message_names_the_key_and_says_the_read_was_ambiguous()
    {
        // spactl's diagnostic names the key and says the read was refused, which means missing or
        // not readable (the change request's caller table). A key is a bucket path, never a
        // credential, so naming it is allowed (contracts/telemetry.md -> Never logged).
        // Mutation: a message without the key, or one that says "not found", fails this.
        ObjectReadRefusedException refused = new("registry/registry.json", innerException: null);

        Assert.Contains("'registry/registry.json'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("missing or not readable", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_is_required()
    {
        // A refusal with no key leaves its caller unable to say which key it was, and the asset
        // route chooses its answer by key.
        Assert.Throws<ArgumentNullException>(() => new ObjectReadRefusedException(null!, innerException: null));
    }

    [Fact]
    public void It_is_an_exception_no_store_failure_already_matches()
    {
        // A caller that catches it must not also catch a transport failure or a cancellation, which
        // the change request says propagate unchanged. So it derives from Exception directly, not
        // from IOException, OperationCanceledException or InvalidOperationException.
        // Mutation: derive from IOException, and the base type is wrong.
        Assert.Equal(typeof(Exception), typeof(ObjectReadRefusedException).BaseType);
        Assert.True(typeof(ObjectReadRefusedException).IsSealed);
    }

    [Fact]
    public void IObjectStore_GetAsync_still_has_its_frozen_signature()
    {
        // The change request keeps every IObjectStore signature, so the store wrappers in the test
        // projects compile unchanged. The refusal is an exception, not a new return shape.
        MethodInfo get = typeof(IObjectStore).GetMethod(nameof(IObjectStore.GetAsync))!;

        Assert.Equal(typeof(Task<StoredObject?>), get.ReturnType);
        Assert.Equal<Type[]>(
            [typeof(string), typeof(string), typeof(CancellationToken)],
            [.. get.GetParameters().Select(parameter => parameter.ParameterType)]);
    }
}
