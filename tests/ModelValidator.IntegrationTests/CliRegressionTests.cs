using ModelValidator.Core;
using ModelValidator.Execution;

namespace ModelValidator.IntegrationTests;

public sealed class CliRegressionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task LocalCommandPreservesArgumentsAndFrameworkPaths(int exitCode)
    {
        string root = Path.Combine(Path.GetTempPath(), "model-validator-cli-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace with spaces");
        Directory.CreateDirectory(workspace);
        string prompt = Path.Combine(root, "prompt with spaces.md");
        File.WriteAllText(prompt, "task text\nsecond line");
        string output = Path.Combine(root, "output with spaces");
        Directory.CreateDirectory(output);
        string script = Path.Combine(root, "record.ps1");
        File.WriteAllText(script, "$args | ConvertTo-Json -AsArray -Compress | Set-Content -LiteralPath (Join-Path $args[8] 'arguments.json'); exit " + exitCode);
        string cli = FindCli();
        TargetConfiguration target = new("1.0", "probe", "Probe", new("agent", "unknown"), new("example-model", "unknown", "custom"),
            new("process", null, ["dotnet", cli, "adapter", "local-command", "--model", "example-model", "--", "pwsh", "-NoProfile", "-File", script,
                "--model", "{model}", "--sandbox", "workspace-write", "--ask-for-approval", "never", "{workspace}", "{promptPath}", "{output}", "two words", "", "{prompt}"], "/workspace", "/prompt", "/output"),
            new([], []), new(1, 268435456, 64));

        AdapterExecutionResult result = await new AdapterRunner().RunProcessAdapterAsync(new(target, workspace, prompt, output, 30));

        Assert.Equal(exitCode, result.ProcessResult.ExitCode);
        Assert.Equal(exitCode == 0 ? "completed" : "target-failed", result.TerminationReason);
        string[] received = JsonIO.Load<string[]>(Path.Combine(output, "arguments.json"));
        Assert.Equal(new[] { "--model", "example-model", "--sandbox", "workspace-write", "--ask-for-approval", "never", workspace, prompt, output, "two words", "", "task text\nsecond line" }, received);
        Assert.Null(result.Usage.TotalTokens);
    }

    [Fact]
    public async Task EmptyCommandsAreRejected()
    {
        ProcessResult result = await new ProcessRunner().RunAsync(new("dotnet", [FindCli(), "adapter", "local-command", "--workspace", ".", "--prompt", "missing", "--output", ".", "--"],
            Environment.CurrentDirectory, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(20)));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No agent command", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualAdapterRejectsInterruptedInput()
    {
        string root = Path.Combine(Path.GetTempPath(), "model-validator-manual-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        ProcessResult init = await new ProcessRunner().RunAsync(new("git", ["init"], workspace, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(20)));
        Assert.Equal(0, init.ExitCode);
        string prompt = Path.Combine(root, "prompt.md");
        File.WriteAllText(prompt, "task");
        System.Diagnostics.ProcessStartInfo info = new("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { FindCli(), "adapter", "manual", "--workspace", workspace, "--prompt", prompt, "--output", Path.Combine(root, "output") })
            info.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await stdout;
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("interrupted", await stderr, StringComparison.Ordinal);
    }

    private static string FindCli()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "ModelValidator.slnx"))) root = Directory.GetParent(root)!.FullName;
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(root, "src", "ModelValidator.Cli", "bin", configuration, "net10.0", "ModelValidator.Cli.dll");
    }
}
