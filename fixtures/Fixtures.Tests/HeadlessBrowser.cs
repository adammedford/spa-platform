using Microsoft.Playwright;

namespace Fixtures.Tests;

/// <summary>
/// One headless Chromium for the test classes that share it (task 5.2's "unit tests in a headless
/// browser"). The browser comes with Microsoft.Playwright, and the suite installs it itself by
/// calling the installer the package's <c>playwright.ps1</c> wraps, so PowerShell is not a
/// dependency (docs/dependencies.md -> "Playwright's browser installer"). The installer is a no-op
/// when the build the package pins is already in the cache.
/// </summary>
public sealed class HeadlessBrowser : IAsyncLifetime
{
    private static readonly SemaphoreSlim InstallOnce = new(1, 1);

    private static bool installed;

    private IPlaywright? playwright;

    private IBrowser? browser;

    public IBrowser Browser => browser ?? throw new InvalidOperationException("The browser has not started.");

    public async ValueTask InitializeAsync()
    {
        await InstallOnce.WaitAsync();
        try
        {
            if (!installed)
            {
                int exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Playwright's browser installer exited with {exitCode}.");
                }

                installed = true;
            }
        }
        finally
        {
            InstallOnce.Release();
        }

        playwright = await Playwright.CreateAsync();
        browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (browser is not null)
        {
            await browser.DisposeAsync();
        }

        playwright?.Dispose();
    }
}
