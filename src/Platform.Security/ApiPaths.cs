using Microsoft.AspNetCore.Http;
using Platform.Abstractions;

namespace Platform.Security;

/// <summary>
/// Which paths are an API's: <c>/api/**</c>, and a fragment's <c>/fragments/{seg}/api/**</c> for any
/// one segment (contracts/request-gate.md -> Rules by path; #996). The request gate and
/// <c>UsePlatformAntiforgery</c> both read it, so the gate's <c>/api</c> row and the token check cannot
/// disagree on a path. Classified by shape, before anything knows whether the segment names a fragment.
/// </summary>
internal static class ApiPaths
{
    // PathString.StartsWithSegments compares without regard to case, as endpoint routing does, so
    // /API/... and /FRAGMENTS/x/API/... are API paths too.
    // https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.http.pathstring.startswithsegments
    private static readonly PathString Api = "/api";

    internal static bool IsApi(PathString path) =>
        path.StartsWithSegments(Api)
        || (PlatformPaths.FragmentOf(path) is { } fragment && fragment.Remainder.StartsWithSegments(Api));
}
