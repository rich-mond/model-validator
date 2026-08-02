using ModelValidator.Core;

namespace ModelValidator.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void CanonicalJsonOrdersProperties()
    {
        string digestA = Hashing.Sha256String(JsonIO.Canonicalize("""{"b":2,"a":1}"""));
        string digestB = Hashing.Sha256String(JsonIO.Canonicalize("""{"a":1,"b":2}"""));
        Assert.Equal(digestA, digestB);
    }

    [Fact]
    public void ChallengeValidationRejectsShellStringStyleEmptyCommands()
    {
        ChallengeManifest manifest = ValidChallenge() with
        {
            Validation = ValidChallenge().Validation with
            {
                Assertions = [ValidChallenge().Validation.Assertions[0] with { Command = [] }]
            }
        };

        ValidationResult result = ContractValidator.Validate(manifest);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, i => i.Path.EndsWith(".command", StringComparison.Ordinal));
    }

    [Fact]
    public void CoverageRequiresAllRequiredAssertions()
    {
        AssertionRunResult[] assertions =
        [
            new("req", "requirement", true, AssertionStatus.Passed, 0, TimeSpan.Zero, "", ""),
            new("reg", "regression", true, AssertionStatus.Failed, 1, TimeSpan.Zero, "", "")
        ];

        CoverageResult coverage = Metrics.CalculateCoverage(assertions);

        Assert.Equal(1, coverage.RequirementsPassed);
        Assert.Equal(0, coverage.RegressionsPassed);
        Assert.False(coverage.Resolved);
    }

    [Fact]
    public void ComparisonClassifiesModelOnly()
    {
        TargetConfiguration left = ValidTarget();
        TargetConfiguration right = left with { TargetId = "right", Model = new("model-b", "2", "provider") };

        PairwiseComparison pair = ComparisonClassifier.Classify(left, right);

        Assert.Equal("model-only", pair.Classification);
    }

    [Fact]
    public void ComparisonClassifiesMixed()
    {
        TargetConfiguration left = ValidTarget();
        TargetConfiguration right = left with
        {
            TargetId = "right",
            Model = new("model-b", "2", "provider"),
            Agent = new("agent-b", "1")
        };

        PairwiseComparison pair = ComparisonClassifier.Classify(left, right);

        Assert.Equal("mixed", pair.Classification);
    }

    [Fact]
    public void ComparisonClassifiesAgentSystem()
    {
        TargetConfiguration left = ValidTarget();
        TargetConfiguration right = left with { TargetId = "right", Agent = new("other-agent", "1") };

        PairwiseComparison pair = ComparisonClassifier.Classify(left, right);

        Assert.Equal("agent-system", pair.Classification);
    }

    [Fact]
    public void ComparisonClassifiesHardware()
    {
        TargetConfiguration left = ValidTarget() with { Hardware = new("os", "arch", 8, 1000) };
        TargetConfiguration right = left with { TargetId = "right", Hardware = new("os", "arch", 16, 1000) };

        PairwiseComparison pair = ComparisonClassifier.Classify(left, right);

        Assert.Equal("hardware", pair.Classification);
        Assert.Contains("elapsed time is not directly controlled", pair.Warnings);
    }

    [Fact]
    public void ExampleConfigurationsLoadThroughValidatedLoaders()
    {
        string root = FindRepositoryRoot();

        ChallengeManifest challenge = ConfigurationLoader.LoadChallenge(Path.Combine(root, "examples", "challenge.example.json"));
        TargetConfiguration target = ConfigurationLoader.LoadTarget(Path.Combine(root, "examples", "target.example.json"));
        BenchmarkPlan plan = ConfigurationLoader.LoadPlan(Path.Combine(root, "examples", "benchmark-plan.example.json"));

        Assert.Equal("sample-language-agnostic-challenge", challenge.ChallengeId);
        Assert.Equal("sample-process-target", target.TargetId);
        Assert.Equal("sample-comparison", plan.PlanId);
    }

    [Fact]
    public void InvalidTargetFailsForExpectedReason()
    {
        string path = Path.Combine(Path.GetTempPath(), "model-validator-invalid-target-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
            {
              "schemaVersion": "1.0",
              "targetId": "bad",
              "displayName": "Bad",
              "agent": { "name": "agent", "version": "1" },
              "model": { "name": "model", "version": "1", "provider": "provider" },
              "adapter": {
                "mode": "container",
                "image": "not-pinned",
                "command": [],
                "workspacePath": "/workspace",
                "promptPath": "/prompt",
                "outputPath": "/output"
              },
              "environment": { "allowedVariables": [ "1BAD" ], "secretVariables": [] },
              "limits": { "cpuCount": 0, "memoryBytes": 0, "pids": 0 }
            }
            """);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ConfigurationLoader.LoadTarget(path));

        Assert.Contains("adapter.image", ex.Message, StringComparison.Ordinal);
        Assert.Contains("adapter.command", ex.Message, StringComparison.Ordinal);
        Assert.Contains("limits.cpuCount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaFilesExistAndAreJsonDocuments()
    {
        string schemaRoot = Path.Combine(FindRepositoryRoot(), "schemas");
        Assert.Equal(3, Directory.EnumerateFiles(schemaRoot, "*.schema.json").Count());
        foreach (string schema in Directory.EnumerateFiles(schemaRoot, "*.schema.json"))
        {
            string canonical = JsonIO.Canonicalize(File.ReadAllText(schema));
            Assert.StartsWith("{", canonical, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ExecutionCodeContainsNoTargetLanguageBuildAssumptions()
    {
        string executionRoot = Path.Combine(FindRepositoryRoot(), "src", "ModelValidator.Execution");
        string allCode = string.Join('\n', Directory.EnumerateFiles(executionRoot, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        string[] forbidden =
        [
            ".sln", ".slnx", ".csproj", "dotnet build", "dotnet test", "pytest", "npm test", "mvn test", "cargo test"
        ];

        foreach (string value in forbidden)
        {
            Assert.DoesNotContain(value, allCode, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ChallengeManifest ValidChallenge() => new(
        "1.0",
        "challenge",
        "1.0.0",
        "Challenge",
        new("prompt.md", new string('0', 64)),
        new("git-bundle", "workspace/starter.bundle", new string('1', 64), new string('2', 40)),
        new(new("existing", null, null, null, "example.invalid/validator@sha256:" + new string('3', 64)), "/candidate", [new("assertion", "Assertion", "requirement", true, ["/validator/run"], "/candidate", 120)]),
        new(1800, 600));

    private static TargetConfiguration ValidTarget() => new(
        "1.0",
        "left",
        "Left",
        new("agent", "1"),
        new("model", "1", "provider"),
        new("process", null, ["tool"], "/workspace", "/prompt.md", "/output"),
        new([], []),
        new(2, 1024, 32));

    private static string FindRepositoryRoot()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "ModelValidator.slnx")))
        {
            directory = Directory.GetParent(directory)?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }

        return directory;
    }
}
