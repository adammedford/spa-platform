using System.Reflection;
using System.Runtime.ExceptionServices;
using Amazon.SimpleSystemsManagement;

namespace Platform.Hosting;

/// <summary>
/// The Dispose shield over the Parameter Store client the key ring writes through, the client
/// <c>AddPlatformRegistry</c> registered. The package's repository disposes the client it is given
/// (#216) and the inner client is the registry's secret source's too, so <see cref="IDisposable.Dispose"/>
/// is never forwarded. The decorator owns nothing: the container that registered the inner client
/// disposes it (#332, decisions 7.5 and 8.5).
///
/// It does not tag. #274 decided the ring's parameters are tagged on create, and the package's 4.0.3
/// copies <c>PersistOptions.Tags</c> onto the put (<c>SSMXmlRepository.StoreElementAsync</c> at tag
/// <c>release_2026-04-17</c>,
/// https://github.com/aws/aws-ssm-data-protection-provider-for-aspnet/blob/release_2026-04-17/src/Amazon.AspNetCore.DataProtection.SSM/SSMXmlRepository.cs),
/// so the three tags are set there in <see cref="KeyRing.Register"/> (#349 Q47). Every call reaches
/// the inner client unchanged: the same request object, the same answer.
///
/// A <see cref="DispatchProxy"/> rather than a class that forwards by hand, because the interface has
/// hundreds of operations and a hand-written forwarder is one place to forget one.
/// </summary>
internal class RingTaggingClient : DispatchProxy
{
    private IAmazonSimpleSystemsManagement inner = null!;

    /// <summary>For <see cref="DispatchProxy"/>, which needs a public constructor; use <see cref="Create"/>.</summary>
    public RingTaggingClient()
    {
    }

    public static IAmazonSimpleSystemsManagement Create(IAmazonSimpleSystemsManagement inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        IAmazonSimpleSystemsManagement proxy = Create<IAmazonSimpleSystemsManagement, RingTaggingClient>();
        ((RingTaggingClient)(object)proxy).inner = inner;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.DeclaringType == typeof(IDisposable))
        {
            return null;
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
}
