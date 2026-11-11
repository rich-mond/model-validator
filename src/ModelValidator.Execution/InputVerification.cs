using ModelValidator.Core;

namespace ModelValidator.Execution;

public static class InputVerification
{
    public static async Task<ChallengeManifest> ChallengeAsync(string root, CancellationToken cancellationToken = default)
    {
        ChallengeManifest manifest = ConfigurationLoader.LoadChallenge(Path.Combine(root, "challenge.json"));
        if (!string.Equals(Hashing.Sha256File(Path.Combine(root, manifest.Prompt.Path)), manifest.Prompt.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prompt digest mismatch.");
        await new GitWorkspaceManager().VerifyBundleAsync(Path.Combine(root, manifest.Workspace.Path), manifest.Workspace.Sha256, manifest.Workspace.BaseCommit, cancellationToken).ConfigureAwait(false);
        ValidatorImageSpec image = manifest.Validation.Image;
        if (image.Type == "build")
        {
            string context = Path.Combine(root, image.ContextPath!);
            if (!Directory.Exists(context) || !File.Exists(Path.Combine(root, image.ContainerfilePath!)))
                throw new InvalidOperationException("Validator context or Containerfile is missing.");
            if (!string.Equals(Hashing.Sha256Directory(context), image.ContextSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Validator context digest mismatch.");
        }
        return manifest;
    }

    public static async Task<BenchmarkPlan> PlanAsync(string path)
    {
        BenchmarkPlan plan = ConfigurationLoader.LoadPlan(path);
        string root = Path.GetDirectoryName(Path.GetFullPath(path))!;
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (string reference in plan.Targets)
        {
            TargetConfiguration target = ConfigurationLoader.LoadTarget(Path.GetFullPath(Path.Combine(root, reference)));
            if (!ids.Add(target.TargetId)) throw new InvalidOperationException($"Duplicate target id '{target.TargetId}'.");
        }
        await ChallengeAsync(Path.GetFullPath(Path.Combine(root, plan.ChallengePath))).ConfigureAwait(false);
        return plan;
    }

    public static string AttemptTermination(string targetTermination, IReadOnlyList<AssertionRunResult> assertions)
    {
        if (targetTermination != "completed") return targetTermination;
        if (assertions.Any(a => a.Status == AssertionStatus.InfrastructureError)) return "validation-infrastructure-error";
        if (assertions.Any(a => a.Status == AssertionStatus.TimedOut)) return "validator-timeout";
        return assertions.Any(a => a.Status != AssertionStatus.Passed) ? "assertion-failed" : "completed";
    }

    public static void RequireUnusedOutput(string root)
    {
        if (File.Exists(root) || Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException($"Output directory must be empty or absent: {root}");
    }

    public static void DeleteWorkspace(string attemptRoot)
    {
        // The caller supplies the attempt directory, never a candidate-controlled path.
        string workspace = Path.GetFullPath(Path.Combine(attemptRoot, "workspace"));
        if (!Directory.Exists(workspace)) return;
        if ((File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing to clean a linked workspace.");
        DeleteTree(workspace);

        static void DeleteTree(string directory)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(entry);
                    else DeleteTree(entry);
                }
                else
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                    File.Delete(entry);
                }
            }
            Directory.Delete(directory);
        }
    }
}
