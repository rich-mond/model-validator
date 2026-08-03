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

            if (args is ["results", "--run", var resultsRunPath])
            {
                ComparisonReport report = JsonIO.Load<ComparisonReport>(Path.Combine(resultsRunPath, "comparison.json"));
                PrintConsoleSummary(report, Path.GetFullPath(resultsRunPath));
                return 0;
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

            if (args.Length > 1 && args[0] == "adapter" && args[1] == "manual")
            {
                return await RunManualAdapterAsync(args.Skip(2).ToArray()).ConfigureAwait(false);
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
        string challengeRoot = Path.GetFullPath(challengePath);
        string manifestPath = Path.Combine(challengeRoot, "challenge.json");
        Console.WriteLine($"Verifying challenge pack: {challengeRoot}");
        Console.WriteLine("Checking manifest, prompt digest and starter bundle...");
        ChallengeManifest manifest = ConfigurationLoader.LoadChallenge(manifestPath);
        if (Hashing.Sha256File(Path.Combine(challengeRoot, manifest.Prompt.Path)) != manifest.Prompt.Sha256) throw new InvalidOperationException("Prompt digest mismatch.");
        GitWorkspaceManager git = new();
        await git.VerifyBundleAsync(Path.Combine(challengeRoot, manifest.Workspace.Path), manifest.Workspace.Sha256, manifest.Workspace.BaseCommit).ConfigureAwait(false);
        Console.WriteLine("Building validator image and running calibration checks...");
        ChallengeVerificationResult result = await new ChallengePackVerifier(git).VerifyAsync(challengeRoot, new ConsoleChallengeVerificationProgress()).ConfigureAwait(false);
        JsonIO.Save(Path.Combine(challengeRoot, "verification", "pack-verification.json"), result);
        if (result.Diagnostics.Count > 0)
        {
            foreach (string diagnostic in result.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }

            return 1;
        }

        Console.WriteLine("Challenge pack verified.");
        Console.WriteLine($"Starter fails required assertion: {result.StarterFailedRequiredAssertion}");
        Console.WriteLine($"Oracle passes all assertions: {result.OraclePassedAllAssertions}");
        Console.WriteLine($"Oracle stable across three runs: {result.OracleStableAcrossThreeRuns}");
        Console.WriteLine($"Counterexamples checked: {result.CounterexampleFailedAssertions.Count}");
        return 0;
    }

    private sealed class ConsoleChallengeVerificationProgress : IChallengeVerificationProgress
    {
        public void ValidatorImageResolved(string image) => Console.WriteLine($"Validator image ready: {Shorten(image)}");

        public void WorkRootCreated(string path) => Console.WriteLine($"Verification evidence root: {path}");

        public void StageStarted(string id, string? patchPath)
        {
            string patch = string.IsNullOrWhiteSpace(patchPath) ? "starter workspace" : Path.GetFileName(patchPath);
            Console.WriteLine($"Calibration stage: {id} ({patch})");
        }

        public void PatchApplied(string id, string patchPath) => Console.WriteLine($"  patch applied: {Path.GetFileName(patchPath)}");

        public void AssertionStarted(string stageId, AssertionSpec assertion) => Console.WriteLine($"  running {assertion.Id}...");

        public void AssertionCompleted(string stageId, AssertionRunResult result)
        {
            string status = result.Status == AssertionStatus.Passed ? "passed" : result.Status.ToString();
            Console.WriteLine($"  {result.Id}: {status}; exit {result.ExitCode}; {result.Duration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}s");
        }

        public void CounterexampleCompleted(string id, IReadOnlyList<string> failedAssertions, IReadOnlyList<string> expectedFailedAssertions)
        {
            string failed = failedAssertions.Count == 0 ? "none" : string.Join(", ", failedAssertions);
            string expected = expectedFailedAssertions.Count == 0 ? "none" : string.Join(", ", expectedFailedAssertions);
            Console.WriteLine($"  counterexample {id}: failed [{failed}], expected [{expected}]");
        }

        private static string Shorten(string value) => value.Length <= 24 ? value : value[..24] + "...";
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
                await git.MaterializeAsync(Path.Combine(challengeRoot, challenge.Workspace.Path), challenge.Workspace.Sha256, challenge.Workspace.BaseCommit, workspace, DisposableBranchName(plan.PlanId, target.TargetId, attemptIndex)).ConfigureAwait(false);
                string workspaceTaskPath = await WriteInteractiveWorkspaceInstructionsAsync(workspace, promptPath, target.DisplayName, challenge.SelfChecks ?? []).ConfigureAwait(false);
                AdapterExecutionRequest adapterRequest = new(target, workspace, workspaceTaskPath, adapterOutput, challenge.Limits.TargetTimeoutSeconds);
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
        string markdown = MarkdownReport.Render(report);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "comparison.md"), markdown).ConfigureAwait(false);
        Console.WriteLine($"Run complete: {outputRoot}");
        PrintConsoleSummary(report, outputRoot);
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
        string agent = options.Value("--agent") ?? "interactive";
        string model = options.Value("--model") ?? "selected-in-agent-ui";
        string outputPath = options.Required("--output");
        string provider = options.Value("--provider") ?? DefaultProvider(agent);
        int attempts = int.TryParse(options.Value("--attempts"), out int parsedAttempts) && parsedAttempts > 0 ? parsedAttempts : 1;
        string agentVersion = options.Value("--agent-version") ?? "unknown";
        string targetId = options.Value("--target-id") ?? Slug($"{agent}-{model}");
        string displayName = options.Value("--display-name") ?? $"{agent} {model}";
        if (options.Trailing.Count == 0)
        {
            return await RunInteractiveBenchmarkAsync(challengePath, outputPath, targetId, displayName, agent, agentVersion, model, provider, attempts, options.Value("--open")).ConfigureAwait(false);
        }

        string[] command = options.Trailing.ToArray();

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

    private static async Task<int> RunInteractiveBenchmarkAsync(string challengePath, string outputPath, string targetId, string displayName, string agent, string agentVersion, string model, string provider, int attempts, string? openTool)
    {
        string challengeRoot = Path.GetFullPath(challengePath);
        string outputRoot = Path.GetFullPath(outputPath);
        string generatedRoot = Path.Combine(outputRoot, "_generated");
        Directory.CreateDirectory(generatedRoot);
        string targetPath = Path.Combine(generatedRoot, $"{targetId}.target.json");
        string planPath = Path.Combine(generatedRoot, "benchmark-plan.json");
        TargetConfiguration target = new("1.0", targetId, displayName, new(agent, agentVersion), new(model, model, provider), new("process", null, ["interactive-ui"], "/workspace", "/input/prompt.md", "/output"), new(DefaultAllowedVariables([]), DefaultSecretVariables(provider, [])), new(2, 2147483648, 128));
        BenchmarkPlan plan = new("1.0", Slug($"{Path.GetFileName(challengeRoot)}-{targetId}"), challengeRoot, [targetPath], attempts, outputRoot, new(1, true));
        JsonIO.Save(targetPath, target);
        JsonIO.Save(planPath, plan);
        Console.WriteLine($"Generated target: {targetPath}");
        Console.WriteLine($"Generated plan: {planPath}");

        ChallengeManifest challenge = ConfigurationLoader.LoadChallenge(Path.Combine(challengeRoot, "challenge.json"));
        JsonIO.Save(Path.Combine(outputRoot, "plan.snapshot.json"), plan);
        JsonIO.Save(Path.Combine(outputRoot, "challenge.snapshot.json"), challenge);
        string promptPath = Path.Combine(challengeRoot, challenge.Prompt.Path);
        GitWorkspaceManager git = new();
        ValidatorRunner validators = new();
        SystemClock clock = new();
        string validatorImage = await validators.ResolveImageAsync(challengeRoot, challenge.Validation.Image).ConfigureAwait(false);
        string fingerprint = Fingerprints.Challenge(challenge, Hashing.Sha256File(Path.Combine(challengeRoot, "challenge.json")), validatorImage, plan.Execution);
        List<AttemptResult> results = new();
        TargetConfiguration recordedTarget = target;

        for (int attemptIndex = 1; attemptIndex <= attempts; attemptIndex++)
        {
            string attemptRoot = Path.Combine(outputRoot, "targets", targetId, $"attempt-{attemptIndex:000}");
            string workspace = Path.Combine(attemptRoot, "workspace");
            string adapterOutput = Path.Combine(attemptRoot, "adapter-output");
            Directory.CreateDirectory(adapterOutput);
            await git.MaterializeAsync(Path.Combine(challengeRoot, challenge.Workspace.Path), challenge.Workspace.Sha256, challenge.Workspace.BaseCommit, workspace, DisposableBranchName(plan.PlanId, targetId, attemptIndex)).ConfigureAwait(false);
            string promptCopy = Path.Combine(adapterOutput, "prompt.md");
            File.Copy(promptPath, promptCopy, overwrite: true);
            string workspaceTaskPath = await WriteInteractiveWorkspaceInstructionsAsync(workspace, promptPath, target.DisplayName, challenge.SelfChecks ?? []).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Attempt {attemptIndex}/{attempts} is ready.");
            Console.WriteLine($"Workspace: {workspace}");
            Console.WriteLine($"Task file: {workspaceTaskPath}");
            if (openTool is not null)
            {
                await TryOpenWorkspaceAsync(openTool, workspace, workspaceTaskPath).ConfigureAwait(false);
            }

            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("1. Use your chosen model or coding-agent UI in the workspace above.");
            Console.WriteLine("2. Send exactly: read AGENTS.md and follow it");
            Console.WriteLine("3. Do not press Enter until the agent reports the required self-check command results.");
            Console.WriteLine("4. Return here and press Enter only after the agent has finished.");
            DateTimeOffset startedUtc = clock.UtcNow;
            long started = clock.Timestamp;
            _ = Console.ReadLine();
            if (attemptIndex == 1)
            {
                recordedTarget = ReadInteractiveTargetMetadata(target);
                JsonIO.Save(targetPath, recordedTarget);
            }

            JsonIO.Save(Path.Combine(adapterOutput, "interactive-metadata.json"), new
            {
                recordedTarget.Agent,
                recordedTarget.Model
            });
            Console.WriteLine("Validating candidate changes...");
            DateTimeOffset finishedUtc = clock.UtcNow;
            TimeSpan targetDuration = clock.ElapsedSince(started);
            await File.WriteAllTextAsync(Path.Combine(attemptRoot, "target.stdout.log"), "Interactive target execution completed by user.").ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(attemptRoot, "target.stderr.log"), string.Empty).ConfigureAwait(false);
            JsonIO.Save(Path.Combine(adapterOutput, "usage.json"), new UsageMetrics(null, null, null, null, null, null, null));

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
            AttemptResult attempt = new(Protocol.Version, plan.PlanId, targetId, attemptIndex, fingerprint, false, startedUtc, finishedUtc, targetDuration, captureDuration, validationDuration, clock.ElapsedSince(started), "completed", assertionResults, coverage, new(null, null, null, null, null, null, null), capture.Metrics, recordedTarget.Hardware ?? new(Environment.OSVersion.Platform.ToString(), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Environment.ProcessorCount, null), new Dictionary<string, string> { ["candidate.patch"] = Hashing.Sha256File(Path.Combine(attemptRoot, "candidate.patch")) });
            JsonIO.Save(Path.Combine(attemptRoot, "result.json"), attempt);
            results.Add(attempt);
        }

        ComparisonReport report = new(Protocol.Version, fingerprint, "compatible", results, [], []);
        JsonIO.Save(Path.Combine(outputRoot, "comparison.json"), report);
        string markdown = MarkdownReport.Render(report);
        await File.WriteAllTextAsync(Path.Combine(outputRoot, "comparison.md"), markdown).ConfigureAwait(false);
        Console.WriteLine($"Run complete: {outputRoot}");
        PrintConsoleSummary(report, outputRoot);
        return 0;
    }

    private static TargetConfiguration ReadInteractiveTargetMetadata(TargetConfiguration target)
    {
        Console.WriteLine();
        Console.WriteLine("Record the model used. Leave blank to keep the value in brackets.");
        string modelName = PromptWithDefault("Model", target.Model.Name);
        string modelVersion = PromptWithDefault("Model version", target.Model.Version == target.Model.Name ? modelName : target.Model.Version);
        return target with
        {
            DisplayName = $"{target.Agent.Name} {modelName}",
            Model = new(modelName, modelVersion, target.Model.Provider)
        };
    }

    private static string PromptWithDefault(string label, string defaultValue)
    {
        Console.Write($"{label} [{defaultValue}]: ");
        string? value = Console.ReadLine();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static async Task<string> WriteInteractiveWorkspaceInstructionsAsync(string workspace, string promptPath, string displayName, IReadOnlyList<SelfCheckSpec> selfChecks)
    {
        string prompt = await File.ReadAllTextAsync(promptPath).ConfigureAwait(false);
        string taskPath = Path.Combine(workspace, "MODEL_VALIDATOR_TASK.md");
        string agentsPath = Path.Combine(workspace, "AGENTS.md");
        string selfCheckContent = BuildSelfCheckMarkdown(selfChecks);
        string taskContent = $"""
            # Model Validator Task

            Target: {displayName}

            You are working inside a prepared benchmark workspace. Implement the task below by editing files in this workspace only.

            Do not edit `AGENTS.md` or `MODEL_VALIDATOR_TASK.md`; they are local benchmark instructions and are excluded from candidate scoring.

            You must run every command under `Required Self-Checks` after your edits. Do not skip, replace or reinterpret those commands. Do not finish until each required self-check exits with code 0, unless a command is impossible to run in this workspace; if that happens, report the exact command, exit code and error.

            ## Task

            {prompt}

            ## Required Self-Checks

            {selfCheckContent}
            """;
        string agentsContent = $"""
            # Model Validator Workspace Instructions

            This is a prepared benchmark workspace.

            - Read `MODEL_VALIDATOR_TASK.md`.
            - Implement the requested task by editing this workspace only.
            - Do not edit `AGENTS.md` or `MODEL_VALIDATOR_TASK.md`.
            - The task is incomplete until every required self-check command has been run after the final code edit.
            - Run every command listed under `Required Self-Checks` in `MODEL_VALIDATOR_TASK.md` after editing.
            - Do not skip, replace or reinterpret any required self-check command.
            - Do not finish until every required self-check exits with code 0.
            - If a required self-check cannot be run, report the exact command, exit code and error.
            - In your final response, report each required self-check command and its exit code.
            - Do not add repository remotes or credentials.
            - When finished, stop. The human operator will return to the benchmark terminal and press Enter to validate.
            """;

        await File.WriteAllTextAsync(taskPath, taskContent).ConfigureAwait(false);
        await File.WriteAllTextAsync(agentsPath, agentsContent).ConfigureAwait(false);
        string excludePath = Path.Combine(workspace, ".git", "info", "exclude");
        string existingExclude = File.Exists(excludePath) ? await File.ReadAllTextAsync(excludePath).ConfigureAwait(false) : string.Empty;
        string[] requiredExcludes = ["AGENTS.md", "MODEL_VALIDATOR_TASK.md"];
        List<string> additions = requiredExcludes
            .Where(entry => !existingExclude.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(entry, StringComparer.Ordinal))
            .ToList();
        if (additions.Count > 0)
        {
            await File.AppendAllTextAsync(excludePath, Environment.NewLine + string.Join(Environment.NewLine, additions) + Environment.NewLine).ConfigureAwait(false);
        }

        return taskPath;
    }

    private static string BuildSelfCheckMarkdown(IReadOnlyList<SelfCheckSpec> selfChecks)
    {
        if (selfChecks.Count == 0)
        {
            return "No challenge-supplied self-check commands are declared for this challenge.";
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            selfChecks.Select(check =>
            {
                string command = string.Join(" ", check.Command.Select(QuoteArgumentForDisplay));
                return $"""
                    - {check.Title}
                      - Working directory: `{check.WorkingDirectory}`
                      - Command: `{command}`
                    """;
            }));
    }

    private static string QuoteArgumentForDisplay(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"" : argument;
    }

    private static async Task TryOpenWorkspaceAsync(string openTool, string workspace, string promptPath)
    {
        string executableName = openTool.ToLowerInvariant() switch
        {
            "vscode" or "code" => "code",
            _ => throw new InvalidOperationException($"Unsupported --open value '{openTool}'. Supported value: vscode.")
        };

        string? executable = ResolveExecutable(executableName);
        if (executable is null)
        {
            Console.Error.WriteLine("Could not find VS Code's 'code' launcher on PATH.");
            Console.Error.WriteLine("Open the workspace manually, or install the VS Code shell command, then press Enter here when the agent has finished.");
            return;
        }

        Console.WriteLine($"Opening workspace in VS Code: {workspace}");
        Dictionary<string, string?> environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        ProcessResult result = await new ProcessRunner().RunAsync(new(executable, [workspace, promptPath], workspace, environment, TimeSpan.FromSeconds(30))).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            Console.Error.WriteLine($"Could not open VS Code automatically. Exit code: {result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrWhiteSpace(result.StandardError)) Console.Error.WriteLine(result.StandardError.Trim());
            if (!string.IsNullOrWhiteSpace(result.StandardOutput)) Console.Error.WriteLine(result.StandardOutput.Trim());
            Console.Error.WriteLine("Open the workspace manually, then press Enter here when the agent has finished.");
        }
    }

    private static string? ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) && File.Exists(executable)) return executable;

        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];
        bool hasExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(executable));
        IEnumerable<string> candidates = OperatingSystem.IsWindows() && !hasExtension
            ? extensions.Select(extension => executable + extension.ToLowerInvariant()).Concat(extensions.Select(extension => executable + extension.ToUpperInvariant()))
            : [executable];

        foreach (string pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string candidate in candidates)
            {
                string fullPath = Path.Combine(pathEntry, candidate);
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        return null;
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

    private static async Task<int> RunManualAdapterAsync(IReadOnlyList<string> args)
    {
        ParsedOptions options = ParsedOptions.Parse(args);
        string workspace = options.Required("--workspace");
        string prompt = options.Required("--prompt");
        string output = options.Required("--output");
        string? openTool = options.Value("--open");
        Directory.CreateDirectory(output);
        string promptCopy = Path.Combine(output, "prompt.md");
        File.Copy(prompt, promptCopy, overwrite: true);
        string workspaceTaskPath = await WriteInteractiveWorkspaceInstructionsAsync(workspace, prompt, "manual target", []).ConfigureAwait(false);

        if (openTool is not null)
        {
            await TryOpenWorkspaceAsync(openTool, workspace, workspaceTaskPath).ConfigureAwait(false);
        }

        Console.WriteLine("Manual benchmark workspace is ready.");
        Console.WriteLine($"Workspace: {workspace}");
        Console.WriteLine($"Task file: {workspaceTaskPath}");
        Console.WriteLine("Run the agent/model of your choice in the workspace, then press Enter here to continue.");
        _ = Console.ReadLine();
        JsonIO.Save(Path.Combine(output, "usage.json"), new UsageMetrics(null, null, null, null, null, null, null));
        return 0;
    }

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

    private static string DisposableBranchName(string planId, string targetId, int attemptIndex) => $"mv-{Hashing.Sha256String($"{planId}:{targetId}:{attemptIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}")[..16]}";

    private static void PrintConsoleSummary(ComparisonReport report, string runPath)
    {
        Console.WriteLine();
        Console.WriteLine("Result summary");
        Console.WriteLine($"Run: {runPath}");
        Console.WriteLine($"Protocol: {report.ProtocolVersion}");
        Console.WriteLine($"Compatibility: {report.CompatibilityStatus}");

        foreach (AttemptResult attempt in report.Attempts.OrderBy(a => a.TargetId, StringComparer.Ordinal).ThenBy(a => a.AttemptIndex))
        {
            string outcome = attempt.Coverage.Resolved ? "RESOLVED" : "UNRESOLVED";
            Console.WriteLine();
            Console.WriteLine($"{attempt.TargetId} attempt {attempt.AttemptIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)}: {outcome}");
            Console.WriteLine($"  Requirements: {attempt.Coverage.RequirementsPassed.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{attempt.Coverage.RequirementsTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  Regressions: {attempt.Coverage.RegressionsPassed.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{attempt.Coverage.RegressionsTotal.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  Target time: {attempt.TargetDuration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s");
            Console.WriteLine($"  Validation time: {attempt.ValidationDuration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}s");
            Console.WriteLine($"  Termination: {attempt.TerminationReason}");
            Console.WriteLine($"  Authoritative: {attempt.Authoritative.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            AssertionRunResult[] failed = attempt.Assertions.Where(a => a.Status != AssertionStatus.Passed).ToArray();
            if (failed.Length == 0)
            {
                Console.WriteLine("  Failed assertions: none");
            }
            else
            {
                Console.WriteLine("  Failed assertions:");
                foreach (AssertionRunResult assertion in failed)
                {
                    Console.WriteLine($"    - {assertion.Id} ({assertion.Classification}) {assertion.Status}; exit {assertion.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
            }
        }

        if (report.PairwiseComparisons.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Pairwise classifications");
            foreach (PairwiseComparison pair in report.PairwiseComparisons)
            {
                string warnings = pair.Warnings.Count == 0 ? "none" : string.Join("; ", pair.Warnings);
                Console.WriteLine($"{pair.LeftTargetId} vs {pair.RightTargetId}: {pair.Classification}; warnings: {warnings}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Detailed files: comparison.md, comparison.json and targets/<target>/attempt-*/");
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
