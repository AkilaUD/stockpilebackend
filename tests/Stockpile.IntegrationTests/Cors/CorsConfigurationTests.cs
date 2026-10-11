using Stockpile.Api.Configuration;

namespace Stockpile.IntegrationTests.Cors;

/// <summary>
/// Task 1.25 — security-matrix C7/X12: the policy always pairs the allowlist
/// with <c>AllowCredentials()</c>, so a configured wildcard would be
/// wildcard-with-credentials. That must fail startup, never configure.
/// </summary>
public sealed class CorsConfigurationTests
{
    [Fact]
    public void Wildcard_origin_is_rejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CorsConfiguration.Create(["*"]));

        Assert.Contains("C7/X12", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wildcard_is_rejected_even_alongside_valid_origins() =>
        Assert.Throws<InvalidOperationException>(
            () => CorsConfiguration.Create(["https://app.stockpile.lk", "*"]));

    [Fact]
    public void Empty_configuration_builds_an_empty_deny_all_allowlist() =>
        Assert.Empty(CorsConfiguration.Create([]));

    [Fact]
    public void Origins_compare_case_insensitively_and_drop_trailing_slashes()
    {
        var allowlist = CorsConfiguration.Create(["https://Allowed.test/"]);

        // HashSet<T>.Contains applies the OrdinalIgnoreCase comparer the set
        // was built with — the exact call the CORS policy uses.
        Assert.Contains("https://allowed.test", allowlist);
        Assert.DoesNotContain("https://allowed.test/", allowlist);
    }
}
