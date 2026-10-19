using System.Globalization;
using System.Reflection;
using Yarp.ReverseProxy.Forwarder;

namespace Platform.Proxy.Tests;

/// <summary>
/// What YARP does with a <see cref="ForwarderRequestConfig.ActivityTimeout"/>, asked of YARP.
///
/// <c>HttpForwarder.SendAsync</c> is the only caller of
/// <c>ActivityCancellationTokenSource.Rent</c>, which stores the activity timeout as
/// <c>(int)activityTimeout.TotalMilliseconds</c> and passes that to <c>CancelAfter</c>
/// (https://github.com/dotnet/yarp/blob/v2.3.0/src/ReverseProxy/Utilities/ActivityCancellationTokenSource.cs).
/// Both the type and the field are internal to YARP, and nothing public reports the limit a forward
/// will really be given, so this reaches them by reflection. The alternative — waiting out a
/// 24-day idle limit to see whether it fires — is not a test.
///
/// Every lookup fails with a message naming what moved and what to do about it, because the point
/// of <see cref="ProxyTimeoutCeilingTests"/> is to notice a package upgrade rather than to pass
/// through one.
/// </summary>
internal static class YarpActivityTimeout
{
    /// <summary>The versions the ceiling was first measured against, named in every failure.</summary>
    internal const string MeasuredAgainst = ".NET 10.0.12 with YARP 2.3.0";

    private const string SourceTypeName = "Yarp.ReverseProxy.Utilities.ActivityCancellationTokenSource";
    private const string StoredFieldName = "_activityTimeoutMs";

    private static readonly Type SourceType = ResolveSourceType();
    private static readonly MethodInfo RentMethod = ResolveRentMethod(SourceType);
    private static readonly FieldInfo StoredField = ResolveStoredField(SourceType);

    /// <summary>
    /// The idle limit a forward configured with <paramref name="activityTimeout"/> would actually
    /// be given, in the units YARP keeps it in.
    /// </summary>
    internal static int AppliedMilliseconds(TimeSpan activityTimeout)
    {
        object rented =
            RentMethod.Invoke(null, [activityTimeout, CancellationToken.None, CancellationToken.None])
            ?? throw Moved($"{SourceTypeName}.Rent returned null");

        try
        {
            object? stored = StoredField.GetValue(rented);

            return stored is int milliseconds
                ? milliseconds
                : throw Moved(
                    $"{SourceTypeName}.{StoredFieldName} read back as "
                    + $"{stored?.GetType().FullName ?? "null"}, not an Int32");
        }
        finally
        {
            // Rent takes one from a shared pool and starts its timer; this one is never returned,
            // so disposing it is what stops the timer and releases the linked registrations.
            (rented as IDisposable)?.Dispose();
        }
    }

    /// <summary>Whether YARP applies a whole number of seconds as the registry stated it.</summary>
    internal static bool IsAppliedExactly(long seconds) =>
        AppliedMilliseconds(TimeSpan.FromSeconds(seconds)) == seconds * 1000;

    /// <summary>
    /// The largest whole second YARP applies exactly, found by asking YARP across the range rather
    /// than by reading <see cref="ProxyTimeout.MaximumTimeoutSeconds"/>. Exactness is monotone — a
    /// saturating conversion gives up at one point and never recovers — so a bisection finds the
    /// boundary wherever YARP has put it, which is the whole point: a guard that read the number it
    /// guards would agree with itself after an upgrade moved it.
    /// </summary>
    internal static long LargestSecondsAppliedExactly()
    {
        long exact = 1;
        long clamped = int.MaxValue;

        if (!IsAppliedExactly(exact))
        {
            throw Moved($"an activity timeout of {exact} second is not applied exactly");
        }

        if (IsAppliedExactly(clamped))
        {
            throw Moved(
                $"{clamped.ToString("N0", CultureInfo.InvariantCulture)} seconds is applied exactly, "
                + "so the ceiling is above the range this search covers");
        }

        while (clamped - exact > 1)
        {
            long midpoint = exact + ((clamped - exact) / 2);

            if (IsAppliedExactly(midpoint))
            {
                exact = midpoint;
            }
            else
            {
                clamped = midpoint;
            }
        }

        return exact;
    }

    private static Type ResolveSourceType()
    {
        Assembly yarp = typeof(ForwarderRequestConfig).Assembly;

        return yarp.GetType(SourceTypeName, throwOnError: false)
            ?? throw Moved($"{SourceTypeName} is not in {yarp.GetName().Name} {yarp.GetName().Version}");
    }

    private static MethodInfo ResolveRentMethod(Type source) =>
        source.GetMethod(
            "Rent",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            [typeof(TimeSpan), typeof(CancellationToken), typeof(CancellationToken)],
            modifiers: null)
        ?? throw Moved($"{SourceTypeName}.Rent(TimeSpan, CancellationToken, CancellationToken) is gone");

    private static FieldInfo ResolveStoredField(Type source) =>
        source.GetField(StoredFieldName, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw Moved($"{SourceTypeName}.{StoredFieldName} is gone");

    private static InvalidOperationException Moved(string what) => new(
        $"{what}. The proxy's timeout ceiling, {nameof(ProxyTimeout)}."
        + $"{nameof(ProxyTimeout.MaximumTimeoutSeconds)}, was measured against {MeasuredAgainst} by "
        + "reading YARP's own internals. An upgrade has moved them, so this test can no longer see "
        + "what the proxy applies: re-measure the ceiling, and if it has moved, update the constant "
        + "and file a change request for the maximum contracts/registry.schema.json states for "
        + "apis[].timeoutSeconds, which is set to that ceiling (ADR-0019 decision 3).");
}
