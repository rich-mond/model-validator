using ModelValidator.Execution;
using ModelValidator.Core;

namespace ModelValidator.Execution.Tests;

public sealed class ExecutionTests
{
    [Fact]
    public async Task ProcessRunnerTimesOutProcessTree()
    {
        ProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(new("git", ["--version"], Environment.CurrentDirectory, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(10)));
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("git version", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunnerReportsTimeoutAndTerminates()
    {
        ProcessRunner runner = new();
        ProcessResult result = await runner.RunAsync(new("pwsh", ["-NoProfile", "-Command", "Start-Sleep -Seconds 5"], Environment.CurrentDirectory, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromMilliseconds(250)));

        Assert.True(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public void MissingUsageProducesNullMetrics()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "usage.json");
        UsageMetrics usage = AdapterRunner.ParseUsage(path);
        Assert.Null(usage.InputTokens);
        Assert.Null(usage.ProviderReportedCost);
    }

    [Fact]
    public void MalformedUsageProducesNullMetrics()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "usage.json");
        File.WriteAllText(path, "{not-json");

        UsageMetrics usage = AdapterRunner.ParseUsage(path);

        Assert.Null(usage.TotalTokens);
        Assert.Null(usage.Requests);
    }

    [Fact]
    public async Task ProcessAdapterRedactsSecretsAndMarksNonAuthoritative()
    {
        string root = Path.Combine(Path.GetTempPath(), "model-validator-adapter-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        string output = Path.Combine(root, "output");
        Directory.CreateDirectory(workspace);
        string prompt = Path.Combine(root, "prompt.md");
        await File.WriteAllTextAsync(prompt, "task");
        Environment.SetEnvironmentVariable("MODEL_VALIDATOR_TEST_SECRET", "super-secret-value");
        TargetConfiguration target = Target("secret-probe", ["pwsh", "-NoProfile", "-Command", "Write-Output $env:MODEL_VALIDATOR_TEST_SECRET"], ["MODEL_VALIDATOR_TEST_SECRET"]);

        AdapterExecutionResult result = await new AdapterRunner().RunProcessAdapterAsync(new(target, workspace, prompt, output, 30));

        Assert.False(result.Authoritative);
        Assert.DoesNotContain("super-secret-value", result.ProcessResult.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", result.ProcessResult.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainerArgumentsMountOnlyWorkspacePromptAndOutput()
    {
        TargetConfiguration target = Target("container-probe", ["/adapter/run"], []) with
        {
            Adapter = new("container", "example.invalid/adapter@sha256:" + new string('a', 64), ["/adapter/run"], "/workspace", "/input/prompt.md", "/output")
        };
        AdapterExecutionRequest request = new(target, "C:\\candidate-workspace", "C:\\prompt.md", "C:\\adapter-output", 30);

        List<string> args = AdapterRunner.BuildContainerArguments(request);
        string joined = string.Join('\n', args);

        Assert.Contains("--network\nnone", joined, StringComparison.Ordinal);
        Assert.Contains("C:\\candidate-workspace:/workspace", joined, StringComparison.Ordinal);
        Assert.Contains("C:\\prompt.md:/input/prompt.md:ro", joined, StringComparison.Ordinal);
        Assert.Contains("C:\\adapter-output:/output", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("validator", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oracle", joined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("counterexamples", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GitMaterialisationRemovesRemotesAndCapturesCompleteCandidate()
    {
        string root = Path.Combine(Path.GetTempPath(), "model-validator-test-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string bundle = Path.Combine(root, "starter.bundle");
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(source);
        ProcessRunner runner = new();
        await runner.RunAsync(new("git", ["init"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        await File.WriteAllTextAsync(Path.Combine(source, "opaque.data"), "starter");
        await File.WriteAllTextAsync(Path.Combine(source, "staged.data"), "before");
        await File.WriteAllTextAsync(Path.Combine(source, "unstaged.data"), "before");
        await File.WriteAllTextAsync(Path.Combine(source, "deleted.data"), "delete me");
        await File.WriteAllTextAsync(Path.Combine(source, "renamed.data"), "rename me");
        await runner.RunAsync(new("git", ["add", "opaque.data"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        await runner.RunAsync(new("git", ["add", "staged.data", "unstaged.data", "deleted.data", "renamed.data"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        await runner.RunAsync(new("git", ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "starter"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        string commit = (await runner.RunAsync(new("git", ["rev-parse", "HEAD"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)))).StandardOutput.Trim();
        await runner.RunAsync(new("git", ["remote", "add", "origin", "https://example.invalid/repo.git"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        await runner.RunAsync(new("git", ["bundle", "create", bundle, "HEAD"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));

        GitWorkspaceManager manager = new(runner);
        await manager.MaterializeAsync(bundle, ModelValidator.Core.Hashing.Sha256File(bundle), commit, workspace, "model-validator/test/target");
        string remotes = (await runner.RunAsync(new("git", ["remote"], workspace, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)))).StandardOutput;
        await File.WriteAllTextAsync(Path.Combine(workspace, "staged.data"), "after staged");
        await runner.RunAsync(new("git", ["add", "staged.data"], workspace, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        await File.WriteAllTextAsync(Path.Combine(workspace, "unstaged.data"), "after unstaged");
        File.Delete(Path.Combine(workspace, "deleted.data"));
        await runner.RunAsync(new("git", ["mv", "renamed.data", "renamed-new.data"], workspace, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)));
        await File.WriteAllTextAsync(Path.Combine(workspace, "untracked.data"), "new text");
        await File.WriteAllBytesAsync(Path.Combine(workspace, "new.bin"), [0, 1, 2, 3, 4, 0, 255]);
        var capture = await manager.CaptureCandidateAsync(workspace, Path.Combine(root, "candidate.patch"), Path.Combine(root, "changed-files.json"));
        string sourceStatus = (await runner.RunAsync(new("git", ["status", "--porcelain=v1"], source, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(30)))).StandardOutput;

        Assert.True(string.IsNullOrWhiteSpace(remotes));
        Assert.Contains(capture.Files, f => f.Path == "staged.data" && f.Status == "M");
        Assert.Contains(capture.Files, f => f.Path == "unstaged.data" && f.Status == "M");
        Assert.Contains(capture.Files, f => f.Path == "deleted.data" && f.Status == "D");
        Assert.Contains(capture.Files, f => f.Path == "renamed-new.data" && f.Status == "R");
        Assert.Contains(capture.Files, f => f.Path == "untracked.data" && f.Status == "A");
        Assert.Contains(capture.Files, f => f.Path == "new.bin" && f.Status == "A" && f.IsBinary);
        Assert.Contains("GIT binary patch", await File.ReadAllTextAsync(Path.Combine(root, "candidate.patch")), StringComparison.Ordinal);
        Assert.DoesNotContain("opaque.data", sourceStatus, StringComparison.Ordinal);
    }

    private static TargetConfiguration Target(string id, IReadOnlyList<string> command, IReadOnlyList<string> secrets) => new(
        "1.0",
        id,
        id,
        new("agent", "1"),
        new("model", "1", "provider"),
        new("process", null, command, "/workspace", "/prompt", "/output"),
        new([], secrets),
        new(1, 268435456, 64));
}
