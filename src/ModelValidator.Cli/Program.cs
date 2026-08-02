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

    private static void PrintIssues(ValidationResult result)
    {
        foreach (ValidationIssue issue in result.Issues)
        {
            Console.Error.WriteLine($"{issue.Path}: {issue.Message}");
        }
    }
}
