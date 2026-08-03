using ModelValidator.Core;
using ModelValidator.Execution;
using ModelValidator.Reporting;

namespace ModelValidator.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] == "doctor")
            {
                Console.WriteLine("modelval doctor: .NET, Git and Docker are expected on PATH.");
                return 0;
            }

            if (args is ["challenge", "verify", "--path", var challengePath])
            {
                return await VerifyChallengeAsync(challengePath).ConfigureAwait(false);
            }

            if (args is ["plan", "validate", "--path", var planPath])
            {
                _ = ConfigurationLoader.LoadPlan(planPath);
                Console.WriteLine("Benchmark plan is valid.");
                return 0;
            }

            if (args is ["report", "--run", var runPath, "--format", var format])
            {
                string json = Path.Combine(runPath, "comparison.json");
                if (format == "json")
                {
                    Console.WriteLine(await File.ReadAllTextAsync(json).ConfigureAwait(false));
                    return 0;
                }

                if (format == "markdown")
                {
                    string markdown = Path.Combine(runPath, "comparison.md");
                    Console.WriteLine(await File.ReadAllTextAsync(markdown).ConfigureAwait(false));
                    return 0;
                }
            }

            if (args is ["run", "--plan", var runPlanPath])
            {
                return await RunPlanAsync(runPlanPath).ConfigureAwait(false);
            }

            if (args.Length > 0 && args[0] == "benchmark")
            {
                return await RunQuickBenchmarkAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
            }

            if (args.Length > 1 && args[0] == "adapter" && args[1] == "local-command")
            {
                return await RunLocalCommandAdapterAsync(args.Skip(2).ToArray()).ConfigureAwait(false);
            }

            if (args.Length >= 3 && args[0] == "compare" && args[1] == "--runs")
            {
                return CompareRuns(args.Skip(2).ToArray());
            }

            Console.Error.WriteLine("Unsupported command.");
            return 2;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> VerifyChallengeAsync(string challengePath)
    {
        string manifestPath = Path.Combine(challengePath, "challenge.json");
        ChallengeManifest manifest = ConfigurationLoader.LoadChallenge(manifestPath);
        if (Hashing.Sha256File(Path.Combine(challengePath, manifest.Prompt.Path)) != manifest.Prompt.Sha256) throw new InvalidOperationException("Prompt digest mismatch.");
        GitWorkspaceManager git = new();
        await git.VerifyBundleAsync(Path.Combine(challengePath, manifest.Workspace.Path), manifest.Workspace.Sha256, manifest.Workspace.BaseCommit).ConfigureAwait(false);
        ChallengeVerificationResult result = await new ChallengePackVerifier(git).VerifyAsync(challengePath).ConfigureAwait(false);
        JsonIO.Save(Path.Combine(challengePath, "verification", "pack-verification.json"), result);
        if (result.Diagnostics.Count > 0)
        {
            foreach (string diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }

            return 1;
        }

        Console.WriteLine("Challenge pack verified.");
        return 0;
    }

    private static async Task<int> RunPlanAsync(string planPath)
    {
        BenchmarkPlan plan = ConfigurationLoader.LoadPlan(planPath);

        string planRoot = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        string challengeRoot = Path.GetFullPath(Path.Combine(planRoot, plan.ChallengePath));
        ChallengeManifest challenge = ConfigurationLoader.LoadChallenge(Path.Combine(challengeRoot, "challenge.json"));
        string outputRoot = Path.GetFullPath(Path.Combine(planRoot, plan.OutputPath));
        Directory.CreateDirectory(outputRoot);
        JsonIO.Save(Path.Combine(outputRoot, "plan.snapshot.json"), plan);
        JsonIO.Save(Path.Combine(outputRoot, "challenge.snapshot.json"), challenge);

        string promptPath = Path.Combine(challengeRoot, challenge.Prompt.Path);
        string imageId = "process-validation-unresolved";
        string fingerprint = Fingerprints.Challenge(challenge, Hashing.Sha256File(Path.Combine(challengeRoot, "challenge.json")), imageId, plan.Execution);
        List<AttemptResult> attempts = new();
        Dictionary<string, TargetConfiguration> targets = new(StringComparer.Ordinal);
        GitWorkspaceManager git = new();
        AdapterRunner adapters = new();
        ValidatorRunner validators = new();
        SystemClock clock = new();
        string validatorImage = await validators.ResolveImageAsync(challengeRoot, challenge.Validation.Image).ConfigureAwait(false);
        fingerprint = Fingerprints.Challenge(challenge, Hashing.Sha256File(Path.Combine(challengeRoot, "challenge.json")), validatorImage, plan.Execution);

        foreach (string targetPath in plan.Targets)
        {
            TargetConfiguration target = ConfigurationLoader.LoadTarget(Path.GetFullPath(Path.Combine(planRoot, targetPath)));
            targets[target.TargetId] = target;
            for (int attemptIndex = 1; attemptIndex <= plan.AttemptsPerTarget; attemptIndex++)
            {
                string attemptRoot = Path.Combine(outputRoot, "targets", target.TargetId, $"attempt-{attemptIndex:000}");
                string workspace = Path.Combine(attemptRoot, "workspace");
                string adapterOutput = Path.Combine(attemptRoot, "adapter-output");
                Directory.CreateDirectory(attemptRoot);
                long started = clock.Timestamp;
                DateTimeOffset startedUtc = clock.UtcNow;
                await git.MaterializeAsync(Path.Combine(challengeRoot, challenge.Workspace.Path), challenge.Workspace.Sha256, challenge.Workspace.BaseCommit, workspace, $"model-validator/{plan.PlanId}/{target.TargetId}").ConfigureAwait(false);
                AdapterExecutionRequest adapterRequest = new(target, workspace, promptPath, adapterOutput, challenge.Limits.TargetTimeoutSeconds);
                AdapterExecutionResult adapterResult = target.Adapter.Mode == "process"
                    ? await adapters.RunProcessAdapterAsync(adapterRequest).ConfigureAwait(false)
                    : await adapters.RunContainerAdapterAsync(adapterRequest).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(attemptRoot, "target.stdout.log"), adapterResult.ProcessResult.StandardOutput).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(attemptRoot, "target.stderr.log"), adapterResult.ProcessResult.StandardError).ConfigureAwait(false);
                long captureStart = clock.Timestamp;
                var capture = await git.CaptureCandidateAsync(workspace, Path.Combine(attemptRoot, "candidate.patch"), Path.Combine(attemptRoot, "changed-files.json")).ConfigureAwait(false);
                TimeSpan captureDuration = clock.ElapsedSince(captureStart);

                long validationStart = clock.Timestamp;
                List<AssertionRunResult> assertionResults = new();
                foreach (AssertionSpec assertion in challenge.Validation.Assertions)
                {
                    string assertionDir = Path.Combine(attemptRoot, "validators", assertion.Id);
                    assertionResults.Add(await validators.RunAssertionAsync(validatorImage, workspace, challenge.Validation.WorkspacePath, assertion, assertionDir).ConfigureAwait(false));
                }
                TimeSpan validationDuration = clock.ElapsedSince(validationStart);

                CoverageResult coverage = Metrics.CalculateCoverage(assertionResults);
                DateTimeOffset finishedUtc = clock.UtcNow;
                AttemptResult attempt = new(Protocol.Version, plan.PlanId, target.TargetId, attemptIndex, fingerprint, adapterResult.Authoritative, startedUtc, finishedUtc, adapterResult.ProcessResult.Duration, captureDuration, validationDuration, clock.ElapsedSince(started), adapterResult.TerminationReason, assertionResults, coverage, adapterResult.Usage, capture.Metrics, target.Hardware ?? new(Environment.OSVersion.Platform.ToString(), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount, null), new Dictionary<string, string> { ["candidate.patch"] = Hashing.Sha256File(Path.Combine(attemptRoot, "candidate.patch")) });
                JsonIO.Save(Path.Combine(attemptRoot, "result.json"), attempt);
                attempts.Add(attempt);
            }
        }

        List<PairwiseComparison> pairs = new();
        TargetConfiguration[] targetValues = targets.Values.ToArray();
        for (int i = 0; i < targetValues.Length; i++)
        {
            for (int j = i + 1; j < targetValues.Length; j++)
            {
                pairs.Add(ComparisonClassifier.Classify(targetValues[i], targetValues[j]));
            }
        }

        ComparisonReport report = new(Protocol.Version, fingerprint, "compatible", attempts, pairs, Array.Empty<string>());
        JsonIO.Save(Path.Combine(outputRoot, "comparison.json"), report);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "comparison.md"), MarkdownReport.Render(report)).ConfigureAwait(false);
        Console.WriteLine($"Run complete: {outputRoot}");
        return 0;
    }

    private static int CompareRuns(IReadOnlyList<string> runDirectories)
    {
        if (runDirectories.Count < 2)
        {
            Console.Error.WriteLine("compare --runs requires at least two run directories.");
            return 2;
        }

        ComparisonReport[] reports = runDirectories
            .Select(path => JsonIO.Load<ComparisonReport>(Path.Combine(path, "comparison.json")))
            .ToArray();
        string firstFingerprint = reports[0].ChallengeFingerprint;
        bool compatible = reports.All(r => r.ChallengeFingerprint == firstFingerprint);
        Console.WriteLine(compatible ? "Runs are directly comparable for correctness." : "Runs have different comparison fingerprints; direct ranking is refused.");
        foreach (ComparisonReport report in reports)
        {
            foreach (AttemptResult attempt in report.Attempts)
            {
                Console.WriteLine($"{attempt.RunId}\t{attempt.TargetId}\tresolved={attempt.Coverage.Resolved}\trequirements={attempt.Coverage.RequirementsPassed}/{attempt.Coverage.RequirementsTotal}\tregressions={attempt.Coverage.RegressionsPassed}/{attempt.Coverage.RegressionsTotal}");
            }
        }

        return compatible ? 0 : 1;
    }

    private static async Task<int> RunQuickBenchmarkAsync(IReadOnlyList<string> args)
    {
        ParsedOptions options = ParsedOptions.Parse(args);
        string challengePath = options.Required("--challenge");
        string agent = options.Required("--agent");
        string model = options.Required("--model");
        string outputPath = options.Required("--output");
        string provider = options.Value("--provider") ?? DefaultProvider(agent);
        int attempts = int.TryParse(options.Value("--attempts"), out int parsedAttempts) && parsedAttempts > 0 ? parsedAttempts : 1;
        string agentVersion = options.Value("--agent-version") ?? "unknown";
        string targetId = options.Value("--target-id") ?? Slug($"{agent}-{model}");
        string displayName = options.Value("--display-name") ?? $"{agent} {model}";
        string[] command = options.Trailing.Count > 0 ? options.Trailing.ToArray() : PresetCommand(agent, model);

        string runRoot = Path.GetFullPath(outputPath);
        string generatedRoot = Path.Combine(runRoot, "_generated");
        Directory.CreateDirectory(generatedRoot);
        string targetPath = Path.Combine(generatedRoot, $"{targetId}.target.json");
        string planPath = Path.Combine(generatedRoot, "benchmark-plan.json");
        string adapterAssembly = typeof(Program).Assembly.Location;
        string[] adapterCommand = ["dotnet", adapterAssembly, "adapter", "local-command", "--agent", agent, "--model", model, .. command.SelectMany(value => new[] { "--command", value })];

        TargetConfiguration target = new(
            "1.0",
            targetId,
            displayName,
            new(agent, agentVersion),
            new(model, model, provider),
            new("process", null, adapterCommand, "/workspace", "/input/prompt.md", "/output"),
            new(DefaultAllowedVariables(options.Values("--allow-env")), DefaultSecretVariables(provider, options.Values("--secret-env"))),
            new(2, 2147483648, 128));
        BenchmarkPlan plan = new(
            "1.0",
            Slug($"{Path.GetFileName(Path.GetFullPath(challengePath))}-{targetId}"),
            Path.GetFullPath(challengePath),
            [targetPath],
            attempts,
            runRoot,
            new(1, false));

        JsonIO.Save(targetPath, target);
        JsonIO.Save(planPath, plan);
        Console.WriteLine($"Generated target: {targetPath}");
        Console.WriteLine($"Generated plan: {planPath}");
        return await RunPlanAsync(planPath).ConfigureAwait(false);
    }

    private static async Task<int> RunLocalCommandAdapterAsync(IReadOnlyList<string> args)
    {
        ParsedOptions options = ParsedOptions.Parse(args);
        string workspace = options.Required("--workspace");
        string prompt = options.Required("--prompt");
        string output = options.Required("--output");
        string model = options.Value("--model") ?? "unknown";
        IReadOnlyList<string> command = options.Values("--command");
        if (command.Count == 0)
        {
            throw new InvalidOperationException("No agent command was supplied after '--'.");
        }

        Directory.CreateDirectory(output);
        string promptText = await File.ReadAllTextAsync(prompt).ConfigureAwait(false);
        string executable = ExpandToken(command[0], workspace, prompt, output, promptText, model);
        string[] commandArgs = command.Skip(1).Select(value => ExpandToken(value, workspace, prompt, output, promptText, model)).ToArray();
        Dictionary<string, string?> environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        ProcessResult result = await new ProcessRunner().RunAsync(new(executable, commandArgs, workspace, environment, TimeSpan.FromDays(7))).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.Combine(output, "agent.stdout.log"), result.StandardOutput).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "agent.stderr.log"), result.StandardError).ConfigureAwait(false);
        if (!File.Exists(Path.Combine(output, "usage.json")))
        {
            JsonIO.Save(Path.Combine(output, "usage.json"), new UsageMetrics(null, null, null, null, null, null, null));
        }

        Console.Write(result.StandardOutput);
        Console.Error.Write(result.StandardError);
        return result.TimedOut ? 124 : result.ExitCode;
    }

    private static string[] PresetCommand(string agent, string model) => agent.ToLowerInvariant() switch
    {
        "codex" => ["codex", "exec", "--model", model, "--sandbox", "workspace-write", "--ask-for-approval", "never", "{prompt}"],
        "claude" or "claude-code" => ["claude", "-p", "{prompt}"],
        _ => throw new InvalidOperationException($"No built-in command preset exists for agent '{agent}'. Use '-- <command> <args>' after the benchmark options.")
    };

    private static string DefaultProvider(string agent) => agent.ToLowerInvariant() switch
    {
        "codex" => "openai",
        "claude" or "claude-code" => "anthropic",
        _ => "custom"
    };

    private static IReadOnlyList<string> DefaultSecretVariables(string provider, IReadOnlyList<string> requested)
    {
        SortedSet<string> values = new(requested, StringComparer.Ordinal);
        if (provider.Equals("openai", StringComparison.OrdinalIgnoreCase)) values.Add("OPENAI_API_KEY");
        if (provider.Equals("anthropic", StringComparison.OrdinalIgnoreCase)) values.Add("ANTHROPIC_API_KEY");
        return values.ToArray();
    }

    private static IReadOnlyList<string> DefaultAllowedVariables(IReadOnlyList<string> requested)
    {
        SortedSet<string> values = new(requested, StringComparer.Ordinal)
        {
            "APPDATA",
            "HOME",
            "LOCALAPPDATA",
            "USERPROFILE"
        };
        return values.ToArray();
    }

    private static string ExpandToken(string value, string workspace, string promptPath, string outputPath, string promptText, string model) =>
        value.Replace("{workspace}", workspace, StringComparison.Ordinal)
            .Replace("{promptPath}", promptPath, StringComparison.Ordinal)
            .Replace("{output}", outputPath, StringComparison.Ordinal)
            .Replace("{prompt}", promptText, StringComparison.Ordinal)
            .Replace("{model}", model, StringComparison.Ordinal);

    private static string Slug(string value)
    {
        char[] chars = value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void PrintIssues(ValidationResult result)
    {
        foreach (ValidationIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"{issue.Path}: {issue.Message}");
        }
    }

    private sealed class ParsedOptions
    {
        private readonly Dictionary<string, List<string>> values;

        private ParsedOptions(Dictionary<string, List<string>> values, IReadOnlyList<string> trailing)
        {
            this.values = values;
            Trailing = trailing;
        }

        public IReadOnlyList<string> Trailing { get; }

        public static ParsedOptions Parse(IReadOnlyList<string> args)
        {
            Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
            List<string> trailing = new();
            bool inTrailing = false;
            for (int i = 0; i < args.Count; i++)
            {
                string arg = args[i];
                if (inTrailing)
                {
                    trailing.Add(arg);
                    continue;
                }

                if (arg == "--")
                {
                    inTrailing = true;
                    continue;
                }

                if (!arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unexpected argument '{arg}'.");
                }

                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Missing value for '{arg}'.");
                }

                if (!values.TryGetValue(arg, out List<string>? list))
                {
                    list = new();
                    values[arg] = list;
                }

                list.Add(args[++i]);
            }

            return new(values, trailing);
        }

        public string Required(string name) => Value(name) ?? throw new InvalidOperationException($"Missing required option '{name}'.");

        public string? Value(string name) => values.TryGetValue(name, out List<string>? list) && list.Count > 0 ? list[^1] : null;

        public IReadOnlyList<string> Values(string name) => values.TryGetValue(name, out List<string>? list) ? list : [];
    }
}
