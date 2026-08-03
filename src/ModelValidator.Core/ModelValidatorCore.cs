using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ModelValidator.Core;

public static class Protocol
{
    public const string Version = "1.0";
}

public sealed record ValidationIssue(string Path, string Message);

public sealed record ValidationResult(bool IsValid, IReadOnlyList<ValidationIssue> Issues)
{
    public static ValidationResult Success { get; } = new(true, Array.Empty<ValidationIssue>());
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    long Timestamp { get; }
    TimeSpan ElapsedSince(long timestamp);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long Timestamp => Stopwatch.GetTimestamp();
    public TimeSpan ElapsedSince(long timestamp) => Stopwatch.GetElapsedTime(timestamp);
}

public sealed record ProcessResult(int ExitCode, bool TimedOut, TimeSpan Duration, string StandardOutput, string StandardError);

public sealed record ChallengeManifest(
    string SchemaVersion,
    string ChallengeId,
    string ChallengeVersion,
    string Title,
    PromptSpec Prompt,
    WorkspaceSpec Workspace,
    ValidationSpec Validation,
    ChallengeLimits Limits,
    IReadOnlyList<SelfCheckSpec>? SelfChecks = null);

public sealed record PromptSpec(string Path, string Sha256);
public sealed record WorkspaceSpec(string Type, string Path, string Sha256, string BaseCommit);
public sealed record ValidationSpec(ValidatorImageSpec Image, string WorkspacePath, IReadOnlyList<AssertionSpec> Assertions);
public sealed record ValidatorImageSpec(string Type, string? ContextPath, string? ContainerfilePath, string? ContextSha256, string? Reference);
public sealed record AssertionSpec(string Id, string Title, string Classification, bool Required, IReadOnlyList<string> Command, string WorkingDirectory, int TimeoutSeconds);
public sealed record SelfCheckSpec(string Id, string Title, IReadOnlyList<string> Command, string WorkingDirectory, int TimeoutSeconds);
public sealed record ChallengeLimits(int TargetTimeoutSeconds, int ValidatorTimeoutSeconds);

public sealed record TargetConfiguration(
    string SchemaVersion,
    string TargetId,
    string DisplayName,
    AgentSpec Agent,
    ModelSpec Model,
    AdapterSpec Adapter,
    EnvironmentSpec Environment,
    ResourceLimits Limits,
    HardwareSpec? Hardware = null);

public sealed record AgentSpec(string Name, string Version);
public sealed record ModelSpec(string Name, string Version, string Provider);
public sealed record AdapterSpec(string Mode, string? Image, IReadOnlyList<string> Command, string WorkspacePath, string PromptPath, string OutputPath);
public sealed record EnvironmentSpec(IReadOnlyList<string> AllowedVariables, IReadOnlyList<string> SecretVariables);
public sealed record ResourceLimits(int CpuCount, long MemoryBytes, int Pids);
public sealed record HardwareSpec(string Os, string Architecture, int ProcessorCount, long? TotalMemoryBytes);

public sealed record BenchmarkPlan(
    string SchemaVersion,
    string PlanId,
    string ChallengePath,
    IReadOnlyList<string> Targets,
    int AttemptsPerTarget,
    string OutputPath,
    ExecutionPlan Execution);

public sealed record ExecutionPlan(int MaximumParallelTargets, bool RetainWorkspaces);

public enum AssertionStatus { Passed, Failed, TimedOut, InfrastructureError }

public sealed record AssertionRunResult(
    string Id,
    string Classification,
    bool Required,
    AssertionStatus Status,
    int ExitCode,
    TimeSpan Duration,
    string StdoutPath,
    string StderrPath);

public sealed record CoverageResult(
    int RequirementsPassed,
    int RequirementsTotal,
    decimal RequirementCoveragePercent,
    int RegressionsPassed,
    int RegressionsTotal,
    decimal RegressionCoveragePercent,
    bool Resolved);

public sealed record UsageMetrics(
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? Requests,
    decimal? ProviderReportedCost,
    string? Currency);

public sealed record ChangedFile(string Path, string Status, bool IsBinary, long? LinesAdded, long? LinesDeleted);
public sealed record CandidateChangeMetrics(int Added, int Modified, int Deleted, int Renamed, int Binary, long PatchBytes, long? LinesAdded, long? LinesDeleted);
public sealed record CounterexampleManifest(IReadOnlyList<CounterexampleSpec> Counterexamples);
public sealed record CounterexampleSpec(string Id, string Path, IReadOnlyList<string> ExpectedFailedAssertions);
public sealed record ChallengeVerificationResult(
    bool StarterFailedRequiredAssertion,
    bool OraclePassedAllAssertions,
    bool OracleStableAcrossThreeRuns,
    IReadOnlyDictionary<string, IReadOnlyList<string>> CounterexampleFailedAssertions,
    IReadOnlyList<string> Diagnostics);

public sealed record AttemptResult(
    string ProtocolVersion,
    string RunId,
    string TargetId,
    int AttemptIndex,
    string ComparisonFingerprint,
    bool Authoritative,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    TimeSpan TargetDuration,
    TimeSpan CandidateCaptureDuration,
    TimeSpan ValidationDuration,
    TimeSpan TotalDuration,
    string TerminationReason,
    IReadOnlyList<AssertionRunResult> Assertions,
    CoverageResult Coverage,
    UsageMetrics Usage,
    CandidateChangeMetrics CandidateMetrics,
    HardwareSpec Hardware,
    IReadOnlyDictionary<string, string> ArtifactHashes);

public sealed record PairwiseComparison(string LeftTargetId, string RightTargetId, string Classification, IReadOnlyList<string> Warnings);

public sealed record ComparisonReport(
    string ProtocolVersion,
    string ChallengeFingerprint,
    string CompatibilityStatus,
    IReadOnlyList<AttemptResult> Attempts,
    IReadOnlyList<PairwiseComparison> PairwiseComparisons,
    IReadOnlyList<string> Diagnostics);

public static class JsonIO
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false
    };

    public static T Load<T>(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            T? value = JsonSerializer.Deserialize<T>(stream, Options);
            return value ?? throw new InvalidOperationException($"'{path}' contained JSON null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in '{path}': {ex.Message}", ex);
        }
    }

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Options));
    }

    public static string Canonicalize(string json)
    {
        JsonNode? node = JsonNode.Parse(json);
        if (node is null)
        {
            throw new InvalidOperationException("JSON document is null.");
        }

        ArrayBufferWriter<byte> buffer = new();
        using Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = false });
        WriteCanonical(node, writer);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string CanonicalDigest<T>(T value)
    {
        string json = JsonSerializer.Serialize(value, Options);
        return Hashing.Sha256String(Canonicalize(json));
    }

    private static void WriteCanonical(JsonNode node, Utf8JsonWriter writer)
    {
        switch (node)
        {
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, JsonNode?> property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    if (property.Value is null) writer.WriteNullValue(); else WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (JsonNode? item in array)
                {
                    if (item is null) writer.WriteNullValue(); else WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;
            case JsonValue value:
                value.WriteTo(writer);
                break;
        }
    }
}

public static class ConfigurationLoader
{
    public static ChallengeManifest LoadChallenge(string path)
    {
        ChallengeManifest manifest = JsonIO.Load<ChallengeManifest>(path);
        ValidationResult result = ContractValidator.Validate(manifest);
        ThrowIfInvalid(path, result);
        return manifest;
    }

    public static TargetConfiguration LoadTarget(string path)
    {
        TargetConfiguration target = JsonIO.Load<TargetConfiguration>(path);
        ValidationResult result = ContractValidator.Validate(target);
        ThrowIfInvalid(path, result);
        return target;
    }

    public static BenchmarkPlan LoadPlan(string path)
    {
        BenchmarkPlan plan = JsonIO.Load<BenchmarkPlan>(path);
        ValidationResult result = ContractValidator.Validate(plan);
        ThrowIfInvalid(path, result);
        return plan;
    }

    private static void ThrowIfInvalid(string path, ValidationResult result)
    {
        if (result.IsValid)
        {
            return;
        }

        string message = string.Join("; ", result.Issues.Select(i => $"{i.Path}: {i.Message}"));
        throw new InvalidOperationException($"Configuration '{path}' is invalid: {message}");
    }
}

public static class Hashing
{
    public static string Sha256File(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256String(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string Sha256Directory(string root)
    {
        IEnumerable<string> files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => Path.GetRelativePath(root, p), StringComparer.Ordinal);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public static class ContractValidator
{
    public static ValidationResult Validate(ChallengeManifest manifest)
    {
        List<ValidationIssue> issues = new();
        Require(manifest.SchemaVersion == "1.0", "schemaVersion", "must be '1.0'");
        Require(!string.IsNullOrWhiteSpace(manifest.ChallengeId), "challengeId", "is required");
        Require(IsSha256(manifest.Prompt.Sha256), "prompt.sha256", "must be a SHA-256 hex digest");
        Require(manifest.Workspace.Type == "git-bundle", "workspace.type", "only 'git-bundle' is supported");
        Require(IsSha256(manifest.Workspace.Sha256), "workspace.sha256", "must be a SHA-256 hex digest");
        Require(IsGitSha(manifest.Workspace.BaseCommit), "workspace.baseCommit", "must be a Git commit SHA");
        Require(manifest.Limits.TargetTimeoutSeconds > 0, "limits.targetTimeoutSeconds", "must be positive");
        Require(manifest.Limits.ValidatorTimeoutSeconds > 0, "limits.validatorTimeoutSeconds", "must be positive");
        Require(manifest.Validation.Assertions.Count > 0, "validation.assertions", "must contain at least one assertion");
        foreach (IGrouping<string, AssertionSpec> duplicate in manifest.Validation.Assertions.GroupBy(a => a.Id).Where(g => g.Count() > 1))
        {
            issues.Add(new("validation.assertions", $"duplicate assertion id '{duplicate.Key}'"));
        }

        for (int i = 0; i < manifest.Validation.Assertions.Count; i++)
        {
            AssertionSpec assertion = manifest.Validation.Assertions[i];
            string path = $"validation.assertions[{i}]";
            Require(!string.IsNullOrWhiteSpace(assertion.Id), $"{path}.id", "is required");
            Require(assertion.Classification is "requirement" or "regression", $"{path}.classification", "must be 'requirement' or 'regression'");
            Require(assertion.Command.Count > 0 && assertion.Command.All(c => !string.IsNullOrWhiteSpace(c)), $"{path}.command", "must be a non-empty argument array");
            Require(assertion.TimeoutSeconds > 0, $"{path}.timeoutSeconds", "must be positive");
        }

        IReadOnlyList<SelfCheckSpec> selfChecks = manifest.SelfChecks ?? [];
        foreach (IGrouping<string, SelfCheckSpec> duplicate in selfChecks.GroupBy(c => c.Id).Where(g => g.Count() > 1))
        {
            issues.Add(new("selfChecks", $"duplicate self-check id '{duplicate.Key}'"));
        }

        for (int i = 0; i < selfChecks.Count; i++)
        {
            SelfCheckSpec check = selfChecks[i];
            string path = $"selfChecks[{i}]";
            Require(!string.IsNullOrWhiteSpace(check.Id), $"{path}.id", "is required");
            Require(!string.IsNullOrWhiteSpace(check.Title), $"{path}.title", "is required");
            Require(check.Command.Count > 0 && check.Command.All(c => !string.IsNullOrWhiteSpace(c)), $"{path}.command", "must be a non-empty argument array");
            Require(!string.IsNullOrWhiteSpace(check.WorkingDirectory), $"{path}.workingDirectory", "is required");
            Require(check.TimeoutSeconds > 0, $"{path}.timeoutSeconds", "must be positive");
        }

        if (manifest.Validation.Image.Type == "build")
        {
            Require(!string.IsNullOrWhiteSpace(manifest.Validation.Image.ContextPath), "validation.image.contextPath", "is required for build images");
            Require(!string.IsNullOrWhiteSpace(manifest.Validation.Image.ContainerfilePath), "validation.image.containerfilePath", "is required for build images");
            Require(IsSha256(manifest.Validation.Image.ContextSha256 ?? ""), "validation.image.contextSha256", "must be a SHA-256 hex digest");
        }
        else if (manifest.Validation.Image.Type == "existing")
        {
            Require(manifest.Validation.Image.Reference?.Contains("@sha256:", StringComparison.Ordinal) == true, "validation.image.reference", "must be digest pinned");
        }
        else
        {
            issues.Add(new("validation.image.type", "must be 'build' or 'existing'"));
        }

        return new(issues.Count == 0, issues);

        void Require(bool condition, string path, string message)
        {
            if (!condition) issues.Add(new(path, message));
        }
    }

    public static ValidationResult Validate(TargetConfiguration target)
    {
        List<ValidationIssue> issues = new();
        Require(target.SchemaVersion == "1.0", "schemaVersion", "must be '1.0'");
        Require(!string.IsNullOrWhiteSpace(target.TargetId), "targetId", "is required");
        Require(target.Adapter.Mode is "container" or "process", "adapter.mode", "must be 'container' or 'process'");
        Require(target.Adapter.Command.Count > 0 && target.Adapter.Command.All(c => !string.IsNullOrWhiteSpace(c)), "adapter.command", "must be a non-empty argument array");
        if (target.Adapter.Mode == "container")
        {
            Require(target.Adapter.Image?.Contains("@sha256:", StringComparison.Ordinal) == true, "adapter.image", "container image must be digest pinned");
        }

        Require(target.Limits.CpuCount > 0, "limits.cpuCount", "must be positive");
        Require(target.Limits.MemoryBytes > 0, "limits.memoryBytes", "must be positive");
        Require(target.Limits.Pids > 0, "limits.pids", "must be positive");
        foreach (string name in target.Environment.AllowedVariables.Concat(target.Environment.SecretVariables))
        {
            Require(IsEnvironmentName(name), "environment", $"'{name}' is not a valid environment variable name");
        }

        return new(issues.Count == 0, issues);

        void Require(bool condition, string path, string message)
        {
            if (!condition) issues.Add(new(path, message));
        }
    }

    public static ValidationResult Validate(BenchmarkPlan plan)
    {
        List<ValidationIssue> issues = new();
        if (plan.SchemaVersion != "1.0") issues.Add(new("schemaVersion", "must be '1.0'"));
        if (string.IsNullOrWhiteSpace(plan.PlanId)) issues.Add(new("planId", "is required"));
        if (string.IsNullOrWhiteSpace(plan.ChallengePath)) issues.Add(new("challengePath", "is required"));
        if (plan.Targets.Count == 0) issues.Add(new("targets", "must contain at least one target"));
        if (plan.AttemptsPerTarget < 1) issues.Add(new("attemptsPerTarget", "must be at least 1"));
        if (plan.Execution.MaximumParallelTargets < 1) issues.Add(new("execution.maximumParallelTargets", "must be at least 1"));
        return new(issues.Count == 0, issues);
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsGitSha(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
    private static bool IsEnvironmentName(string value) => !string.IsNullOrWhiteSpace(value) && (char.IsLetter(value[0]) || value[0] == '_') && value.All(c => char.IsLetterOrDigit(c) || c == '_');
}

public static class Metrics
{
    public static CoverageResult CalculateCoverage(IEnumerable<AssertionRunResult> assertions)
    {
        AssertionRunResult[] values = assertions.ToArray();
        int reqTotal = values.Count(a => a.Classification == "requirement" && a.Required);
        int reqPass = values.Count(a => a.Classification == "requirement" && a.Required && a.Status == AssertionStatus.Passed);
        int regTotal = values.Count(a => a.Classification == "regression" && a.Required);
        int regPass = values.Count(a => a.Classification == "regression" && a.Required && a.Status == AssertionStatus.Passed);
        return new(reqPass, reqTotal, Percent(reqPass, reqTotal), regPass, regTotal, Percent(regPass, regTotal), reqPass == reqTotal && regPass == regTotal);
    }

    private static decimal Percent(int passed, int total) => total == 0 ? 100m : decimal.Round(passed * 100m / total, 2);
}

public static class Fingerprints
{
    public static string Challenge(ChallengeManifest manifest, string manifestDigest, string validatorImageId, ExecutionPlan execution)
    {
        var payload = new
        {
            manifest.ChallengeId,
            manifest.ChallengeVersion,
            ManifestDigest = manifestDigest,
            BundleDigest = manifest.Workspace.Sha256,
            manifest.Workspace.BaseCommit,
            PromptDigest = manifest.Prompt.Sha256,
            Validator = new
            {
                manifest.Validation.Image.Type,
                manifest.Validation.Image.Reference,
                manifest.Validation.Image.ContextSha256,
                ImageId = validatorImageId
            },
            Assertions = manifest.Validation.Assertions.Select(a => new { a.Id, a.Classification, a.Required, a.Command, a.WorkingDirectory, a.TimeoutSeconds }).ToArray(),
            SelfChecks = (manifest.SelfChecks ?? []).Select(c => new { c.Id, c.Command, c.WorkingDirectory, c.TimeoutSeconds }).ToArray(),
            manifest.Limits.TargetTimeoutSeconds,
            manifest.Limits.ValidatorTimeoutSeconds,
            execution.MaximumParallelTargets,
            ProtocolVersion = Protocol.Version
        };
        return JsonIO.CanonicalDigest(payload);
    }
}

public static class ComparisonClassifier
{
    public static PairwiseComparison Classify(TargetConfiguration left, TargetConfiguration right)
    {
        int dimensions = 0;
        bool modelDiff = left.Model != right.Model;
        bool agentDiff = left.Agent != right.Agent;
        bool adapterDiff = left.Adapter != right.Adapter;
        bool limitsDiff = left.Limits != right.Limits;
        bool hardwareDiff = left.Hardware != right.Hardware;
        if (modelDiff) dimensions++;
        if (agentDiff || adapterDiff) dimensions++;
        if (limitsDiff) dimensions++;
        if (hardwareDiff) dimensions++;

        string classification;
        if (modelDiff && !agentDiff && !adapterDiff && !limitsDiff && !hardwareDiff)
        {
            classification = "model-only";
        }
        else if (hardwareDiff && !modelDiff && !agentDiff && !adapterDiff && !limitsDiff)
        {
            classification = "hardware";
        }
        else if ((agentDiff || adapterDiff || limitsDiff) && dimensions == 1)
        {
            classification = "agent-system";
        }
        else
        {
            classification = "mixed";
        }

        List<string> warnings = new();
        if (hardwareDiff || left.Adapter.Mode != right.Adapter.Mode)
        {
            warnings.Add("elapsed time is not directly controlled");
        }

        return new(left.TargetId, right.TargetId, classification, warnings);
    }
}

public static class SecretRedactor
{
    public static string Redact(string text, IEnumerable<string> secretValues)
    {
        string result = text;
        foreach (string value in secretValues.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal))
        {
            result = result.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        }

        return result;
    }
}
