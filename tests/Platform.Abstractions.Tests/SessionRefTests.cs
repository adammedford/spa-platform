using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Platform.Abstractions;

namespace Platform.Abstractions.Tests;

/// <summary>
/// <c>session_ref</c> is defined in a contract, not in this code: contracts/telemetry.md says "the
/// first 12 hex characters of a SHA-256 of the session key", and GLOSSARY.md repeats it. Two writers
/// will exist, workstream 3 and workstream 6's tests, so every part of the formula that either could
/// get differently has to be pinned here.
///
/// The first test reads the contract rather than restating it, because a guard that repeats the value
/// it protects protects nothing (CODING_STANDARDS.md). The rest pin what the contract leaves open.
/// </summary>
public sealed class SessionRefTests
{
    [Fact]
    public void The_contract_still_says_a_SHA_256_and_twelve_hex_characters()
    {
        // If someone edits the contract to say 16 characters, or a different hash, this fails and
        // the code below is the thing to change. Without it, Length is a number agreeing with itself.
        // Whitespace is collapsed first, so rewrapping the paragraph does not fail this while a
        // changed number or a changed hash still does.
        string contract = string.Join(
            ' ',
            File.ReadAllText(Path.Combine(ContractsDirectory(), "telemetry.md"))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains($"first {SessionRef.Length} hex characters of a SHA-256 of the session key", contract);
    }

    [Fact]
    public void A_reference_is_the_first_twelve_characters_of_the_lowercase_hex_SHA_256()
    {
        // Computed by a different route from the one under test: hash, format, truncate, by hand.
        const string key = "a-session-key";
        string expected = string.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(key)).Select(b => b.ToString("x2")))
            .Substring(0, SessionRef.Length);

        Assert.Equal(expected, SessionRef.Of(key));
    }

    [Fact]
    public void The_formula_is_pinned_by_a_known_answer()
    {
        // SHA-256 of the five bytes "hello" is 2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e7304
        // 3362938b9824. Twelve lowercase hex characters of that is the reference below. A literal, so
        // that a change to the encoding, the case or the truncation fails here and not in a log
        // somewhere.
        Assert.Equal("2cf24dba5fb0", SessionRef.Of("hello"));
    }

    [Fact]
    public void A_reference_is_twelve_lowercase_hex_characters()
    {
        string reference = SessionRef.Of("another-session-key");

        Assert.Equal(SessionRef.Length, reference.Length);
        Assert.Matches("^[0-9a-f]{12}$", reference);
    }

    [Fact]
    public void A_key_is_hashed_as_UTF_8()
    {
        // The contract fixes the hash and the length, not the encoding, so this is where the choice
        // lives. A non-ASCII key is the only input that can tell UTF-8 from the alternatives.
        const string key = "sesión-clave-éü中";
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..SessionRef.Length];

        Assert.Equal(expected, SessionRef.Of(key));
        Assert.NotEqual(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(key)))[..SessionRef.Length],
            SessionRef.Of(key));
    }

    [Fact]
    public void One_key_gives_one_reference_and_two_keys_give_two()
    {
        // What the field is for: correlating a session's requests. Both halves can fail -- a
        // truncation to nothing would break the second, a random salt the first.
        Assert.Equal(SessionRef.Of("key-one"), SessionRef.Of("key-one"));
        Assert.NotEqual(SessionRef.Of("key-one"), SessionRef.Of("key-two"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_missing_key_is_refused_by_this_method_rather_than_hashed(string? key)
    {
        // An empty key hashes perfectly well, to a reference every empty-keyed request would share.
        //
        // ParamName is asserted, not just the exception type. Without the guard, a null key still
        // throws ArgumentNullException -- from Encoding.UTF8.GetBytes, naming its own parameter -- so
        // the type alone cannot tell a guarded method from an unguarded one. The name can.
        ArgumentException thrown =
            Assert.ThrowsAny<ArgumentException>(() => SessionRef.Of(key!));

        Assert.Equal("sessionKey", thrown.ParamName);
    }

    [Fact]
    public void The_reference_is_not_a_prefix_of_the_key()
    {
        // The field exists so a log can correlate without carrying a credential
        // (contracts/telemetry.md, "Never logged": session keys). The plausible bug is a helper that
        // truncates instead of hashing: sessionKey[..12] would pass every other test in this file
        // except the two that compute an expected hash, and it fails here.
        const string key = "6f3b2a1c-the-whole-session-key";

        Assert.DoesNotContain(SessionRef.Of(key), key);
    }

    /// <summary>
    /// The source tree first, then the output directory's ancestors, as FreezeMarkersTests does.
    /// </summary>
    private static string ContractsDirectory([CallerFilePath] string? thisFile = null)
    {
        if (thisFile is not null)
        {
            string fromSource =
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "contracts"));

            if (Directory.Exists(fromSource))
            {
                return fromSource;
            }
        }

        for (DirectoryInfo? candidate = new(AppContext.BaseDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "SpaPlatform.slnx")))
            {
                return Path.Combine(candidate.FullName, "contracts");
            }
        }

        throw new InvalidOperationException("contracts/ is neither beside the source nor above the output.");
    }
}
