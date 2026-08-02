using ModelValidator.Core;
using ModelValidator.Reporting;

namespace ModelValidator.IntegrationTests;

public sealed class LanguageAgnosticTests
{
    [Fact]
    public void MarkdownReportUsesObjectiveFields()
    {
        AttemptResult attempt = new(
            Protocol.Version,
            "run",
            "target",
            1,
            "fingerprint",
            false,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            "completed",
            [],
            new(1, 1, 100, 0, 1, 0, false),
            new(null, null, null, null, null, null, null),
            new(0, 1, 0, 0, 0, 10, null, null),
            new("Windows", "X64", 8, null),
            new Dictionary<string, string>());
        ComparisonReport report = new(Protocol.Version, "fingerprint", "compatible", [attempt], [], []);

        string markdown = MarkdownReport.Render(report);

        Assert.Contains("1/1", markdown, StringComparison.Ordinal);
        Assert.Contains("0/1", markdown, StringComparison.Ordinal);
    }
}
