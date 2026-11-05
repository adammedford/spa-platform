using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using Gateway.TestHarness;
using Microsoft.Playwright;

namespace Gateway.E2ETests.Stack;

/// <summary>
/// A URL as a failure message may print it: scheme, authority and path, never the query or the
/// fragment. On the cell a login's URLs carry the provider's <c>state</c> and the callback's
/// authorization <c>code</c>, and a sitting posts its output on an issue; AGENTS.md has no code or
/// state value printed anywhere.
/// </summary>
internal static partial class Redacted
{
    public static string Url(Uri url) => url.GetLeftPart(UriPartial.Path);

    /// <summary>A page's URL as Playwright reports it; one that does not parse is not printed at all.</summary>
    public static string Url(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ? Url(parsed) : "(a URL that does not parse)";

    /// <summary>The URLs in order, each as <see cref="Url(Uri)"/> prints it, joined with " -> ".</summary>
    public static string Urls(IEnumerable<Uri> urls) => string.Join(" -> ", urls.Select(Url));

    /// <summary>What <see cref="Text"/> puts where it took something out.</summary>
    public const string Removed = "[redacted]";

    /// <summary>How much of a page's text <see cref="Text"/> keeps: enough for a login page's form and its error.</summary>
    public const int MaxText = 2000;

    /// <summary>A value shorter than this is not taken out of a text: a <c>lang=en</c> would take every "en" with it, and no code, state or cookie the cell issues is that short.</summary>
    public const int MinValue = 4;

    /// <summary>
    /// A page's visible text as an evidence file may hold it (#1003): each of <paramref name="values"/>
    /// (the page's own query and fragment values, the context's cookies) taken out, every URL in it
    /// printed as <see cref="Url(string)"/> prints one, every JWT-shaped value (<see cref="TokenShape.Jwt"/>)
    /// taken out, and the rest cut at <see cref="MaxText"/> characters.
    /// </summary>
    public static string Text(string text, IEnumerable<string> values)
    {
        foreach (string value in values.Where(value => value.Length >= MinValue).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length))
        {
            text = text.Replace(value, Removed, StringComparison.Ordinal);
        }

        text = AbsoluteUrl().Replace(text, url => Url(url.Value));
        text = TokenShape.Jwt().Replace(text, Removed);
        return text.Length <= MaxText ? text : string.Concat(text.AsSpan(0, MaxText), " (cut)");
    }

    /// <summary>The values of a URL's query and fragment parameters, each as written and decoded; none for a URL that does not parse.</summary>
    public static IEnumerable<string> ParameterValues(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            yield break;
        }

        foreach (string pair in $"{parsed.Query.TrimStart('?')}&{parsed.Fragment.TrimStart('#')}".Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string value = pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..];
            yield return value;
            yield return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
    }

    /// <summary>
    /// Runs a Playwright call and, when it fails, rethrows its failure with every URL in the message as
    /// <see cref="Url(string)"/> prints it. A Playwright timeout's message carries a call log of the URLs
    /// the page passed through (a login's callback with its code and state among them), and a failed
    /// navigation names its target, which a row may have built with a code in it (#253). A timeout
    /// stays a <see cref="System.TimeoutException"/>, which is what Microsoft.Playwright throws for one
    /// (https://playwright.dev/dotnet/docs/api/class-timeouterror catches it as <c>TimeoutException</c>,
    /// and 1.63.0 declares no timeout type of its own); any other failure comes back as a
    /// <see cref="PlaywrightException"/>, so a subclass Playwright keeps internal, such as its
    /// target-closed failure, loses its own type. The failure keeps its stack trace and loses its inner
    /// exception, whose message xUnit would print.
    /// </summary>
    public static async Task PlaywrightAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception failed) when (failed is PlaywrightException or System.TimeoutException)
        {
            throw Scrubbed(failed);
        }
    }

    /// <inheritdoc cref="PlaywrightAsync(Func{Task})"/>
    public static async Task<T> PlaywrightAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception failed) when (failed is PlaywrightException or System.TimeoutException)
        {
            throw Scrubbed(failed);
        }
    }

    private static Exception Scrubbed(Exception failed)
    {
        string message = AbsoluteUrl().Replace(failed.Message, url => Url(url.Value));
        Exception scrubbed = failed switch
        {
            // A timed-out hold keeps its kind and what the page showed, the evidence a continuity row writes (#1003).
            HoldTimeoutException held => new HoldTimeoutException(message, new PageShown(Url(held.Page.Url), Text(held.Page.Text, []))),
            System.TimeoutException => new System.TimeoutException(message),
            _ => new PlaywrightException(message),
        };
        return failed.StackTrace is { } trace ? ExceptionDispatchInfo.SetRemoteStackTrace(scrubbed, trace) : scrubbed;
    }

    /// <summary>A scheme, <c>://</c>, and everything up to whitespace, a quote or an angle bracket.</summary>
    [GeneratedRegex("""[A-Za-z][A-Za-z0-9+.-]*://[^\s"'<>]+""", RegexOptions.None, RegexTimeout.Milliseconds)]
    private static partial Regex AbsoluteUrl();
}
