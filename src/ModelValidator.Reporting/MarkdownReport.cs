using System.Globalization;
using System.Text;
using ModelValidator.Core;

namespace ModelValidator.Reporting;

public static class MarkdownReport
{
    public static string Render(ComparisonReport report)
    {
        StringBuilder builder = new();
        builder.AppendLine("# Model Validator Comparison");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Protocol: `{report.ProtocolVersion}`");
        builder.AppendLine(CultureInfo.InvariantCulture, $"Compatibility: `{report.CompatibilityStatus}`");
        builder.AppendLine();
        builder.AppendLine("| Target | Attempt | Resolved | Requirements | Regressions | Target time | Validation time | Termination | Authoritative |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|---:|---|---:|");
        foreach (AttemptResult attempt in report.Attempts)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {attempt.TargetId} | {attempt.AttemptIndex} | {attempt.Coverage.Resolved} | {attempt.Coverage.RequirementsPassed}/{attempt.Coverage.RequirementsTotal} | {attempt.Coverage.RegressionsPassed}/{attempt.Coverage.RegressionsTotal} | {attempt.TargetDuration.TotalSeconds:F2}s | {attempt.ValidationDuration.TotalSeconds:F2}s | {attempt.TerminationReason} | {attempt.Authoritative} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Pairwise Classifications");
        builder.AppendLine();
        builder.AppendLine("| Left | Right | Classification | Warnings |");
        builder.AppendLine("|---|---|---|---|");
        foreach (PairwiseComparison pair in report.PairwiseComparisons)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {pair.LeftTargetId} | {pair.RightTargetId} | {pair.Classification} | {string.Join("; ", pair.Warnings)} |");
        }

        if (report.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Diagnostics");
            foreach (string diagnostic in report.Diagnostics)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {diagnostic}");
            }
        }

        return builder.ToString();
    }
}
