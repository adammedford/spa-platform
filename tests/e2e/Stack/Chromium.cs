using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// Headless Chromium, as Microsoft.Playwright ships it. The suite installs the browser by calling the
/// installer the package's <c>playwright.ps1</c> wraps, so PowerShell is not a dependency
/// (docs/dependencies.md -> "Playwright's browser installer"); it is a no-op when the build the
/// package pins is already cached.
/// </summary>
public static class Chromium
{
    private static readonly SemaphoreSlim Once = new(1, 1);
    private static IPlaywright? playwright;

    /// <summary>A new Chromium process with these command-line switches, and no proxy whatever the machine is set to use.</summary>
    public static async Task<IBrowser> LaunchAsync(params string[] args)
    {
        await Once.WaitAsync();
        try
        {
            if (playwright is null)
            {
                int exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Playwright's browser installer exited with {exitCode}.");
                }

                playwright = await Playwright.CreateAsync();
            }
        }
        finally
        {
            Once.Release();
        }

        return await playwright.Chromium.LaunchAsync(new() { Headless = true, Args = ["--no-proxy-server", .. args] });
    }
}
