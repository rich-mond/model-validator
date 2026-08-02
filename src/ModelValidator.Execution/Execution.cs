using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ModelValidator.Core;

namespace ModelValidator.Execution;

public sealed record ProcessRequest(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment, TimeSpan Timeout);
public sealed record AdapterExecutionRequest(TargetConfiguration Target, string WorkspacePath, string PromptPath, string OutputPath, int TimeoutSeconds);
public sealed record AdapterExecutionResult(ProcessResult ProcessResult, bool Authoritative, UsageMetrics Usage, string TerminationReason);

public sealed class ProcessRunner
{
    private readonly IClock clock;

    public ProcessRunner(IClock? clock = null) => this.clock = clock ?? new SystemClock();

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in request.Arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment.Clear();
        foreach (KeyValuePair<string, string?> entry in request.Environment)
        {
            if (entry.Value is not null)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }

        long started = clock.Timestamp;
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start '{request.FileName}'.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        int exitCode = timedOut ? -1 : process.ExitCode;
        return new(exitCode, timedOut, clock.ElapsedSince(started), stdout, stderr);
    }
}

public sealed class GitWorkspaceManager
{
    private readonly ProcessRunner runner;

    public GitWorkspaceManager(ProcessRunner? runner = null) => this.runner = runner ?? new ProcessRunner();

    public async Task VerifyBundleAsync(string bundlePath, string expectedSha256, string baseCommit, CancellationToken cancellationToken = default)
    {
        string actual = Hashing.Sha256File(bundlePath);
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Bundle digest mismatch for '{bundlePath}'. Expected {expectedSha256}, got {actual}.");
        }

        string verifyRoot = Path.Combine(Path.GetTempPath(), "model-validator-bundle-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(verifyRoot);
        ProcessResult init = await GitAsync(verifyRoot, "init", cancellationToken).ConfigureAwait(false);
        if (init.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not create temporary bundle verification repository: {init.StandardError}{init.StandardOutput}");
        }

        ProcessResult verify = await GitAsync(verifyRoot, ["bundle", "verify", Path.GetFullPath(bundlePath)], cancellationToken).ConfigureAwait(false);
        try { Directory.Delete(verifyRoot, recursive: true); } catch (IOException) { }
        if (verify.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git bundle verification failed: {verify.StandardError}{verify.StandardOutput}");
        }

        _ = baseCommit;
    }

    public async Task<string> MaterializeAsync(string bundlePath, string expectedSha256, string baseCommit, string destination, string branchName, CancellationToken cancellationToken = default)
    {
        await VerifyBundleAsync(bundlePath, expectedSha256, baseCommit, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException($"Workspace destination already exists: {destination}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        ProcessResult clone = await GitAsync(Environment.CurrentDirectory, "clone", bundlePath, destination, cancellationToken).ConfigureAwait(false);
        if (clone.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git clone failed: {clone.StandardError}{clone.StandardOutput}");
        }

        string head = (await GitAsync(destination, ["rev-parse", "HEAD"], cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (!string.Equals(head, baseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Materialised HEAD mismatch. Expected {baseCommit}, got {head}.");
        }

        ProcessResult remotes = await GitAsync(destination, "remote", cancellationToken).ConfigureAwait(false);
        foreach (string remote in remotes.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            ProcessResult remove = await GitAsync(destination, ["remote", "remove", remote], cancellationToken).ConfigureAwait(false);
            if (remove.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to remove remote '{remote}': {remove.StandardError}");
            }
        }

        ProcessResult branch = await GitAsync(destination, "switch", "-c", branchName, cancellationToken).ConfigureAwait(false);
        if (branch.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to create disposable branch: {branch.StandardError}");
        }

        ProcessResult status = await GitAsync(destination, ["status", "--porcelain=v1"], cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new InvalidOperationException($"Materialised workspace is not clean: {status.StandardOutput}");
        }

        return destination;
    }

    public async Task<(IReadOnlyList<ChangedFile> Files, CandidateChangeMetrics Metrics)> CaptureCandidateAsync(string workspace, string patchPath, string changedFilesPath, CancellationToken cancellationToken = default)
    {
        long startedPatchBytes = 0;
        ProcessResult add = await GitAsync(workspace, ["add", "-A"], cancellationToken).ConfigureAwait(false);
        if (add.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to stage candidate changes: {add.StandardError}");
        }

        ProcessResult diff = await GitAsync(workspace, ["diff", "HEAD", "--binary"], cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(diff.StandardOutput))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            add = await GitAsync(workspace, ["add", "-A"], cancellationToken).ConfigureAwait(false);
            if (add.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to stage candidate changes after retry: {add.StandardError}");
            }

            diff = await GitAsync(workspace, ["diff", "HEAD", "--binary"], cancellationToken).ConfigureAwait(false);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(patchPath))!);
        await File.WriteAllTextAsync(patchPath, diff.StandardOutput, cancellationToken).ConfigureAwait(false);
        startedPatchBytes = new FileInfo(patchPath).Length;

        ProcessResult nameStatus = await GitAsync(workspace, ["diff", "HEAD", "--numstat", "--summary"], cancellationToken).ConfigureAwait(false);
        ProcessResult status = await GitAsync(workspace, ["diff", "HEAD", "--name-status"], cancellationToken).ConfigureAwait(false);
        Dictionary<string, (bool IsBinary, long? Added, long? Deleted)> numstat = ParseNumstat(nameStatus.StandardOutput);
        List<ChangedFile> changed = ParseNameStatus(status.StandardOutput, numstat);
        JsonIO.Save(changedFilesPath, changed);

        long? linesAdded = numstat.Values.Any(v => v.Added.HasValue) ? numstat.Values.Sum(v => v.Added ?? 0) : null;
        long? linesDeleted = numstat.Values.Any(v => v.Deleted.HasValue) ? numstat.Values.Sum(v => v.Deleted ?? 0) : null;

        CandidateChangeMetrics metrics = new(
            changed.Count(f => f.Status.StartsWith("A", StringComparison.Ordinal)),
            changed.Count(f => f.Status.StartsWith("M", StringComparison.Ordinal)),
            changed.Count(f => f.Status.StartsWith("D", StringComparison.Ordinal)),
            changed.Count(f => f.Status.StartsWith("R", StringComparison.Ordinal)),
            changed.Count(f => f.IsBinary),
            startedPatchBytes,
            linesAdded,
            linesDeleted);
        return (changed, metrics);
    }

    private static Dictionary<string, (bool IsBinary, long? Added, long? Deleted)> ParseNumstat(string output)
    {
        Dictionary<string, (bool IsBinary, long? Added, long? Deleted)> values = new(StringComparer.Ordinal);
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3)
            {
                continue;
            }

            bool binary = parts[0] == "-" || parts[1] == "-";
            long? added = long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedAdded) ? parsedAdded : null;
            long? deleted = long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedDeleted) ? parsedDeleted : null;
            string path = parts[^1].Replace('\\', '/');
            values[path] = (binary, added, deleted);
        }

        return values;
    }

    private static List<ChangedFile> ParseNameStatus(string output, IReadOnlyDictionary<string, (bool IsBinary, long? Added, long? Deleted)> numstat)
    {
        List<ChangedFile> files = new();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 2) continue;
            string status = parts[0];
            string path = parts[^1];
            string normalizedPath = path.Replace('\\', '/');
            numstat.TryGetValue(normalizedPath, out (bool IsBinary, long? Added, long? Deleted) stat);
            files.Add(new(normalizedPath, status.Length > 1 && status[0] == 'R' ? "R" : status, stat.IsBinary, stat.Added, stat.Deleted));
        }

        return files;
    }

    private Task<ProcessResult> GitAsync(string cwd, params string[] args) => GitAsync(cwd, args, CancellationToken.None);

    private Task<ProcessResult> GitAsync(string cwd, string arg0, string arg1, string arg2, CancellationToken ct) => GitAsync(cwd, [arg0, arg1, arg2], ct);
    private Task<ProcessResult> GitAsync(string cwd, string arg0, string arg1, string arg2, string arg3, CancellationToken ct) => GitAsync(cwd, [arg0, arg1, arg2, arg3], ct);
    private Task<ProcessResult> GitAsync(string cwd, string arg0, CancellationToken ct) => GitAsync(cwd, [arg0], ct);

    private Task<ProcessResult> GitAsync(string cwd, IReadOnlyList<string> args, CancellationToken ct) =>
        runner.RunAsync(new("git", args, cwd, BaseEnvironment(), TimeSpan.FromMinutes(2)), ct);

    public static IReadOnlyDictionary<string, string?> BaseEnvironment() => new Dictionary<string, string?>
    {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH"),
        ["Path"] = Environment.GetEnvironmentVariable("Path"),
        ["SYSTEMROOT"] = Environment.GetEnvironmentVariable("SYSTEMROOT"),
        ["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot"),
        ["SYSTEMDRIVE"] = Environment.GetEnvironmentVariable("SYSTEMDRIVE"),
        ["SystemDrive"] = Environment.GetEnvironmentVariable("SystemDrive"),
        ["TEMP"] = Environment.GetEnvironmentVariable("TEMP"),
        ["TMP"] = Environment.GetEnvironmentVariable("TMP"),
        ["COMSPEC"] = Environment.GetEnvironmentVariable("COMSPEC"),
        ["HOME"] = null,
        ["GIT_TERMINAL_PROMPT"] = "0"
    };
}

public sealed class AdapterRunner
{
    private readonly ProcessRunner runner;

    public AdapterRunner(ProcessRunner? runner = null) => this.runner = runner ?? new ProcessRunner();

    public async Task<AdapterExecutionResult> RunProcessAdapterAsync(AdapterExecutionRequest request, CancellationToken cancellationToken = default)
    {
        TargetConfiguration target = request.Target;
        string workspace = request.WorkspacePath;
        string promptPath = request.PromptPath;
        string outputPath = request.OutputPath;
        Directory.CreateDirectory(outputPath);
        Dictionary<string, string?> environment = GitWorkspaceManager.BaseEnvironment().ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);
        foreach (string variable in target.Environment.AllowedVariables.Concat(target.Environment.SecretVariables))
        {
            environment[variable] = Environment.GetEnvironmentVariable(variable);
        }

        List<string> args = target.Adapter.Command.Skip(1)
            .Concat(["--workspace", workspace, "--prompt", promptPath, "--output", outputPath])
            .ToList();
        ProcessResult result = await runner.RunAsync(new(target.Adapter.Command[0], args, workspace, environment, TimeSpan.FromSeconds(request.TimeoutSeconds)), cancellationToken).ConfigureAwait(false);
        string[] secretValues = target.Environment.SecretVariables.Select(Environment.GetEnvironmentVariable).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray();
        ProcessResult redacted = result with
        {
            StandardOutput = SecretRedactor.Redact(result.StandardOutput, secretValues),
            StandardError = SecretRedactor.Redact(result.StandardError, secretValues)
        };
        return new(redacted, false, ParseUsage(Path.Combine(outputPath, "usage.json")), redacted.TimedOut ? "timed-out" : "completed");
    }

    public async Task<AdapterExecutionResult> RunContainerAdapterAsync(AdapterExecutionRequest request, CancellationToken cancellationToken = default)
    {
        TargetConfiguration target = request.Target;
        if (target.Adapter.Image is null)
        {
            throw new InvalidOperationException("Container adapter image is required.");
        }

        Directory.CreateDirectory(request.OutputPath);
        List<string> args = BuildContainerArguments(request);
        ProcessResult result = await runner.RunAsync(new("docker", args, Environment.CurrentDirectory, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(request.TimeoutSeconds)), cancellationToken).ConfigureAwait(false);
        string[] secretValues = target.Environment.SecretVariables.Select(Environment.GetEnvironmentVariable).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!).ToArray();
        ProcessResult redacted = result with { StandardOutput = SecretRedactor.Redact(result.StandardOutput, secretValues), StandardError = SecretRedactor.Redact(result.StandardError, secretValues) };
        return new(redacted, true, ParseUsage(Path.Combine(request.OutputPath, "usage.json")), redacted.TimedOut ? "timed-out" : "completed");
    }

    public static List<string> BuildContainerArguments(AdapterExecutionRequest request)
    {
        TargetConfiguration target = request.Target;
        if (target.Adapter.Image is null)
        {
            throw new InvalidOperationException("Container adapter image is required.");
        }

        List<string> args =
        [
            "run", "--rm", "--network", "none", "--cpus", target.Limits.CpuCount.ToString(CultureInfo.InvariantCulture),
            "--memory", target.Limits.MemoryBytes.ToString(CultureInfo.InvariantCulture), "--pids-limit", target.Limits.Pids.ToString(CultureInfo.InvariantCulture),
            "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "-v", $"{Path.GetFullPath(request.WorkspacePath)}:{target.Adapter.WorkspacePath}",
            "-v", $"{Path.GetFullPath(request.PromptPath)}:{target.Adapter.PromptPath}:ro",
            "-v", $"{Path.GetFullPath(request.OutputPath)}:{target.Adapter.OutputPath}",
        ];
        foreach (string variable in target.Environment.AllowedVariables.Concat(target.Environment.SecretVariables))
        {
            if (Environment.GetEnvironmentVariable(variable) is not null)
            {
                args.Add("-e");
                args.Add(variable);
            }
        }

        args.Add(target.Adapter.Image);
        args.AddRange(target.Adapter.Command);
        return args;
    }

    public static UsageMetrics ParseUsage(string path)
    {
        if (!File.Exists(path))
        {
            return new(null, null, null, null, null, null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            long? input = ReadLong(root, "inputTokens");
            long? cached = ReadLong(root, "cachedInputTokens");
            long? output = ReadLong(root, "outputTokens");
            return new(input, cached, output, input.HasValue || output.HasValue ? (input ?? 0) + (output ?? 0) : null, ReadLong(root, "requests"), ReadDecimal(root, "providerReportedCost"), ReadString(root, "currency"));
        }
        catch (JsonException)
        {
            return new(null, null, null, null, null, null, null);
        }
    }

    private static long? ReadLong(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result) ? result : null;
    private static decimal? ReadDecimal(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal result) ? result : null;
    private static string? ReadString(JsonElement element, string name) => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class ValidatorRunner
{
    private readonly ProcessRunner runner;

    public ValidatorRunner(ProcessRunner? runner = null) => this.runner = runner ?? new ProcessRunner();

    public async Task<string> ResolveImageAsync(string challengeRoot, ValidatorImageSpec image, CancellationToken cancellationToken = default)
    {
        if (image.Type == "existing")
        {
            return image.Reference ?? throw new InvalidOperationException("Existing validator image reference is required.");
        }

        string context = Path.Combine(challengeRoot, image.ContextPath ?? "");
        string expected = image.ContextSha256 ?? "";
        string actual = Hashing.Sha256Directory(context);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Validator context digest mismatch. Expected {expected}, got {actual}.");
        }

        string tag = $"model-validator-local-{Hashing.Sha256String(context)[..12]}";
        ProcessResult build = await runner.RunAsync(new("docker", ["build", "-f", Path.Combine(challengeRoot, image.ContainerfilePath ?? ""), "-t", tag, context], challengeRoot, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromMinutes(20)), cancellationToken).ConfigureAwait(false);
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException($"Validator image build failed: {build.StandardError}{build.StandardOutput}");
        }

        ProcessResult inspect = await runner.RunAsync(new("docker", ["image", "inspect", tag, "--format", "{{.Id}}"], challengeRoot, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromMinutes(1)), cancellationToken).ConfigureAwait(false);
        return inspect.StandardOutput.Trim().Length == 0 ? tag : inspect.StandardOutput.Trim();
    }

    public async Task<AssertionRunResult> RunAssertionAsync(string image, string candidateWorkspace, string workspacePath, AssertionSpec assertion, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        List<string> args =
        [
            "run", "--rm", "--network", "none", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
            "-v", $"{Path.GetFullPath(candidateWorkspace)}:{workspacePath}:ro",
            "-w", assertion.WorkingDirectory,
            image
        ];
        args.AddRange(assertion.Command);
        ProcessResult result = await runner.RunAsync(new("docker", args, Environment.CurrentDirectory, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromSeconds(assertion.TimeoutSeconds)), cancellationToken).ConfigureAwait(false);
        string stdoutPath = Path.Combine(outputDirectory, "stdout.log");
        string stderrPath = Path.Combine(outputDirectory, "stderr.log");
        await File.WriteAllTextAsync(stdoutPath, result.StandardOutput, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(stderrPath, result.StandardError, cancellationToken).ConfigureAwait(false);
        AssertionStatus status = result.TimedOut ? AssertionStatus.TimedOut : result.ExitCode == 0 ? AssertionStatus.Passed : AssertionStatus.Failed;
        AssertionRunResult assertionResult = new(assertion.Id, assertion.Classification, assertion.Required, status, result.ExitCode, result.Duration, stdoutPath, stderrPath);
        JsonIO.Save(Path.Combine(outputDirectory, "result.json"), assertionResult);
        return assertionResult;
    }
}

public sealed class ChallengePackVerifier
{
    private readonly GitWorkspaceManager git;
    private readonly ValidatorRunner validator;
    private readonly ProcessRunner processRunner;

    public ChallengePackVerifier(GitWorkspaceManager? git = null, ValidatorRunner? validator = null, ProcessRunner? processRunner = null)
    {
        this.processRunner = processRunner ?? new ProcessRunner();
        this.git = git ?? new GitWorkspaceManager(this.processRunner);
        this.validator = validator ?? new ValidatorRunner(this.processRunner);
    }

    public async Task<ChallengeVerificationResult> VerifyAsync(string challengeRoot, CancellationToken cancellationToken = default)
    {
        ChallengeManifest manifest = ConfigurationLoader.LoadChallenge(Path.Combine(challengeRoot, "challenge.json"));
        if (Hashing.Sha256File(Path.Combine(challengeRoot, manifest.Prompt.Path)) != manifest.Prompt.Sha256)
        {
            throw new InvalidOperationException("Prompt digest mismatch.");
        }

        string bundlePath = Path.Combine(challengeRoot, manifest.Workspace.Path);
        await git.VerifyBundleAsync(bundlePath, manifest.Workspace.Sha256, manifest.Workspace.BaseCommit, cancellationToken).ConfigureAwait(false);
        string image = await validator.ResolveImageAsync(challengeRoot, manifest.Validation.Image, cancellationToken).ConfigureAwait(false);
        string workRoot = Path.Combine(Path.GetTempPath(), "model-validator-challenge-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        IReadOnlyList<AssertionRunResult> starter = await MaterializePatchAndValidateAsync("starter", null).ConfigureAwait(false);
        IReadOnlyList<AssertionRunResult> oracle1 = await MaterializePatchAndValidateAsync("oracle-1", Path.Combine(challengeRoot, "oracle", "solution.patch")).ConfigureAwait(false);
        IReadOnlyList<AssertionRunResult> oracle2 = await MaterializePatchAndValidateAsync("oracle-2", Path.Combine(challengeRoot, "oracle", "solution.patch")).ConfigureAwait(false);
        IReadOnlyList<AssertionRunResult> oracle3 = await MaterializePatchAndValidateAsync("oracle-3", Path.Combine(challengeRoot, "oracle", "solution.patch")).ConfigureAwait(false);

        Dictionary<string, IReadOnlyList<string>> counterexampleFailures = new(StringComparer.Ordinal);
        string counterexampleManifestPath = Path.Combine(challengeRoot, "counterexamples", "manifest.json");
        if (File.Exists(counterexampleManifestPath))
        {
            CounterexampleManifest counterexamples = JsonIO.Load<CounterexampleManifest>(counterexampleManifestPath);
            foreach (CounterexampleSpec counterexample in counterexamples.Counterexamples)
            {
                IReadOnlyList<AssertionRunResult> results = await MaterializePatchAndValidateAsync("counterexample-" + counterexample.Id, Path.Combine(challengeRoot, "counterexamples", counterexample.Path)).ConfigureAwait(false);
                counterexampleFailures[counterexample.Id] = results.Where(r => r.Status != AssertionStatus.Passed).Select(r => r.Id).ToArray();
            }
        }

        bool starterFailed = starter.Any(a => a.Required && a.Classification == "requirement" && a.Status != AssertionStatus.Passed);
        bool oraclePassed = oracle1.All(a => !a.Required || a.Status == AssertionStatus.Passed);
        bool oracleStable = StatusSignature(oracle1) == StatusSignature(oracle2) && StatusSignature(oracle2) == StatusSignature(oracle3);
        List<string> diagnostics = new();
        if (!starterFailed) diagnostics.Add("starter did not fail any required assertion");
        if (!oraclePassed) diagnostics.Add("oracle did not pass every required assertion");
        if (!oracleStable) diagnostics.Add("oracle results were not stable across three runs");
        return new(starterFailed, oraclePassed, oracleStable, counterexampleFailures, diagnostics);

        async Task<IReadOnlyList<AssertionRunResult>> MaterializePatchAndValidateAsync(string id, string? patchPath)
        {
            string workspace = Path.Combine(workRoot, id, "workspace");
            await git.MaterializeAsync(bundlePath, manifest.Workspace.Sha256, manifest.Workspace.BaseCommit, workspace, $"model-validator/verify/{id}", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(patchPath) && !string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(patchPath, cancellationToken).ConfigureAwait(false)))
            {
                ProcessResult apply = await processRunner.RunAsync(new("git", ["apply", "--whitespace=nowarn", patchPath], workspace, GitWorkspaceManager.BaseEnvironment(), TimeSpan.FromMinutes(2)), cancellationToken).ConfigureAwait(false);
                if (apply.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Patch '{patchPath}' failed to apply: {apply.StandardError}{apply.StandardOutput}");
                }
            }

            List<AssertionRunResult> results = new();
            foreach (AssertionSpec assertion in manifest.Validation.Assertions)
            {
                string output = Path.Combine(workRoot, id, "validators", assertion.Id);
                results.Add(await validator.RunAssertionAsync(image, workspace, manifest.Validation.WorkspacePath, assertion, output, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }
    }

    private static string StatusSignature(IEnumerable<AssertionRunResult> results) =>
        string.Join('|', results.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id}:{r.Status}"));
}
