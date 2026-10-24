using System.Reflection;
using System.Runtime.ExceptionServices;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Platform.Hosting;

/// <summary>
/// The Parameter Store client the key ring writes through: the client <c>AddPlatformRegistry</c>
/// registered, with the three resource tags (contracts/resource-tags.md) added to every
/// <see cref="PutParameterRequest"/> whose name is under the ring's prefix. #274 decided the ring's
/// parameters are tagged on create, and #332 confirmed this decorator as the way. The put as the ring
/// calls the package carries no tags (#216), but 4.0.3 can carry them: its <c>PersistOptions.Tags</c>
/// is copied onto the put (<c>SSMXmlRepository.StoreElementAsync</c> at tag <c>release_2026-04-17</c>,
/// https://github.com/aws/aws-ssm-data-protection-provider-for-aspnet/blob/release_2026-04-17/src/Amazon.AspNetCore.DataProtection.SSM/SSMXmlRepository.cs).
/// Whether to tag through that option instead is open on #340's review; the decorator is needed for
/// <see cref="IDisposable.Dispose"/> either way.
///
/// Every other call reaches the inner client unchanged: the same request object, the same answer.
/// <see cref="IDisposable.Dispose"/> is never forwarded, because the package's repository disposes the
/// client it is given (#216) and the inner client is the registry's secret source's too. The decorator
/// owns nothing: the container that registered the inner client disposes it (#332, decisions 7.5 and
/// 8.5).
///
/// A <see cref="DispatchProxy"/> rather than a class that forwards by hand, because the interface has
/// hundreds of operations and a hand-written forwarder is one place to forget one.
/// </summary>
internal class RingTaggingClient : DispatchProxy
{
    private IAmazonSimpleSystemsManagement inner = null!;
    private string prefix = null!;
    private (string Key, string Value)[] tags = [];

    /// <summary>For <see cref="DispatchProxy"/>, which needs a public constructor; use <see cref="Create"/>.</summary>
    public RingTaggingClient()
    {
    }

    /// <param name="prefix">
    /// The ring's prefix as configured. It is normalised as the package normalises it, one slash at each
    /// end (<c>'/' + prefix.Trim('/') + '/'</c> in <c>SSMXmlRepository</c>'s constructor, same file as
    /// above), so "under the prefix" means what the package's own parameter names mean.
    /// </param>
    public static IAmazonSimpleSystemsManagement Create(IAmazonSimpleSystemsManagement inner, string prefix, string cell)
    {
        ArgumentNullException.ThrowIfNull(inner);
        IAmazonSimpleSystemsManagement proxy = Create<IAmazonSimpleSystemsManagement, RingTaggingClient>();
        RingTaggingClient self = (RingTaggingClient)(object)proxy;
        self.inner = inner;
        self.prefix = NormalisePrefix(prefix);
        self.tags = [("Platform", KeyRing.PlatformTag), ("Cell", cell), ("ManagedBy", KeyRing.ManagedByTag)];
        return proxy;
    }

    internal static string NormalisePrefix(string prefix) => "/" + prefix.Trim('/') + "/";

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.DeclaringType == typeof(IDisposable))
        {
            return null;
        }

        if (args is [PutParameterRequest put, ..] && put.Name is { } name && name.StartsWith(prefix, StringComparison.Ordinal))
        {
            Tag(put);
        }

        try
        {
            return targetMethod.Invoke(inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    private void Tag(PutParameterRequest put)
    {
        // The SDK leaves collections null until set. A tag of the same key already on the request is
        // replaced, so the platform's three values are the ones the parameter carries.
        List<Tag> carried = put.Tags ?? [];
        foreach ((string key, string value) in tags)
        {
            carried.RemoveAll(t => string.Equals(t.Key, key, StringComparison.Ordinal));
            carried.Add(new Tag { Key = key, Value = value });
        }

        put.Tags = carried;
    }
}
