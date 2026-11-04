using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using YamlDotNet.RepresentationModel;

namespace Whiskers.Tests;

/// <summary>
/// R1-52 (F-10, F-19): the inputs of a build are fixed and every one of them has an update path.
///
/// <list type="bullet">
/// <item>Locked restore: every project in <c>Whiskers.slnx</c> has a <c>packages.lock.json</c>
/// (<c>Directory.Build.props</c> turns them on), CI restores with <c>--locked-mode</c> and builds without a second
/// restore, and so does the Dockerfile — a lock file that does not match its project fails the build.</item>
/// <item>Complete update coverage: Dependabot covers every project's NuGet directory, the SDK in <c>global.json</c>,
/// the Dockerfile's base images and the workflows' actions. A new project without coverage fails here.</item>
/// <item>Pinned bytes: every base image is <c>image:tag@sha256:…</c>, and the SDK image is the exact SDK of
/// <c>global.json</c> (the SDK writes its own framework packages into the lock files).</item>
/// </list>
///
/// The action SHA pins themselves are checked by <see cref="ReleaseWorkflowTests"/>. Every check here also runs
/// against a mutated copy of the real file, so none of them can pass by finding nothing.
/// </summary>
public class BuildReproducibilityTests
{
    // ---- the real files ------------------------------------------------------------------------------------

    [Fact]
    public void Every_project_under_src_is_in_the_solution()
    {
        var inSolution = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx")).ToHashSet(StringComparer.Ordinal);
        var onDisk = Directory.EnumerateFiles(DeploymentScripts.RepoFile("src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(p => Path.GetRelativePath(DeploymentScripts.RepoRoot(), p).Replace('\\', '/'))
            .ToList();
        Assert.NotEmpty(onDisk);
        Assert.All(onDisk, p => Assert.Contains(p, inSolution));
    }

    [Fact]
    public void Every_project_has_a_lock_file_and_none_opts_out()
    {
        var projects = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"));
        Assert.Equal(5, projects.Count); // the check must see the projects; raise with a new project
        Assert.Empty(BuildPolicy.LockFileViolations(Repo("Directory.Build.props"), projects,
            p => File.Exists(DeploymentScripts.RepoFile(p)) ? DeploymentScripts.ReadRepoFile(p) : null));
    }

    [Fact]
    public void Ci_restores_locked_and_never_restores_again()
    {
        var restores = BuildPolicy.WorkflowRestoreViolations(Repo(".github", "workflows", "ci.yml"), out var lockedRestores);
        Assert.True(lockedRestores > 0, "ci.yml has no locked `dotnet restore`");
        Assert.Empty(restores);
    }

    /// <summary>Every workflow file, also one added later: none may restore unlocked or set up another SDK.</summary>
    public static TheoryData<string> Workflows()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(DeploymentScripts.RepoFile(".github", "workflows"), "*.yml").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(file));
        return data;
    }

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_workflow_restores_unlocked(string workflow)
        => Assert.Empty(BuildPolicy.WorkflowRestoreViolations(Repo(".github", "workflows", workflow), out _));

    [Theory]
    [MemberData(nameof(Workflows))]
    public void Every_workflow_sets_up_the_sdk_of_global_json(string workflow)
        => Assert.Empty(BuildPolicy.CiSdkViolations(Repo(".github", "workflows", workflow)));

    [Fact]
    public void Ci_sets_up_the_sdk_at_all()
        => Assert.Contains("global-json-file: global.json", Repo(".github", "workflows", "ci.yml"));

    [Fact]
    public void Dockerfile_restores_locked_with_every_lock_file()
        => Assert.Empty(BuildPolicy.DockerfileRestoreViolations(Repo("Dockerfile"), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));

    [Fact]
    public void Every_base_image_is_pinned_by_digest()
    {
        var dockerfile = Repo("Dockerfile");
        Assert.True(BuildPolicy.BaseImages(dockerfile).Count >= 3, "the Dockerfile's FROM lines were not found");
        Assert.Empty(BuildPolicy.UnpinnedBaseImages(dockerfile));
    }

    [Fact]
    public void Sdk_image_is_the_exact_sdk_of_global_json()
        => Assert.Empty(BuildPolicy.SdkPinViolations(Repo("global.json"), Repo("Dockerfile")));

    [Fact]
    public void Dependabot_covers_every_project_the_sdk_the_images_and_the_actions()
        => Assert.Empty(BuildPolicy.DependabotViolations(Repo(".github", "dependabot.yml"), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));

    // ---- each check bites: the real file, mutated -----------------------------------------------------------

    [Theory]
    [InlineData("      - \"/src/Whiskers.Data\"\n", "")]                                   // a project without NuGet coverage
    [InlineData("package-ecosystem: \"dotnet-sdk\"", "package-ecosystem: \"bundler\"")]   // the SDK uncovered
    [InlineData("package-ecosystem: \"docker\"", "package-ecosystem: \"npm\"")]           // the base images uncovered
    [InlineData("package-ecosystem: \"github-actions\"", "package-ecosystem: \"pip\"")]   // the actions uncovered
    public void Dependabot_check_finds_a_gap(string original, string mutation)
    {
        var real = Repo(".github", "dependabot.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.DependabotViolations(real.Replace(original, mutation), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));
    }

    [Fact]
    public void Dependabot_check_finds_a_new_project_without_coverage()
    {
        var projects = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx")).Append("src/Whiskers.New/Whiskers.New.csproj").ToList();
        Assert.Contains(BuildPolicy.DependabotViolations(Repo(".github", "dependabot.yml"), projects), v => v.Contains("Whiskers.New"));
    }

    [Fact]
    public void Lock_file_check_finds_a_missing_lock_file_and_an_opt_out()
    {
        var projects = BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"));
        string? Read(string p) => File.Exists(DeploymentScripts.RepoFile(p)) ? DeploymentScripts.ReadRepoFile(p) : null;

        Assert.Contains(BuildPolicy.LockFileViolations(Repo("Directory.Build.props"), projects,
                p => p == "src/Whiskers.Data/packages.lock.json" ? null : Read(p)),
            v => v.Contains("Whiskers.Data"));
        Assert.NotEmpty(BuildPolicy.LockFileViolations(
            Repo("Directory.Build.props").Replace("<RestorePackagesWithLockFile>true<", "<RestorePackagesWithLockFile>false<"), projects, Read));
        Assert.Contains(BuildPolicy.LockFileViolations(Repo("Directory.Build.props"), projects,
                p => p == "src/Whiskers.Tests/Whiskers.Tests.csproj"
                    ? Read(p)!.Replace("<IsPackable>false</IsPackable>", "<IsPackable>false</IsPackable><RestorePackagesWithLockFile>false</RestorePackagesWithLockFile>")
                    : Read(p)),
            v => v.Contains("Whiskers.Tests"));
    }

    [Theory]
    [InlineData("dotnet restore Whiskers.slnx --locked-mode", "dotnet restore Whiskers.slnx")]                       // unlocked restore
    [InlineData("dotnet build Whiskers.slnx --no-restore --configuration Release", "dotnet build Whiskers.slnx --configuration Release")] // implicit restore
    public void Workflow_restore_check_finds_an_unlocked_restore(string original, string mutation)
    {
        var real = Repo(".github", "workflows", "ci.yml");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.WorkflowRestoreViolations(real.Replace(original, mutation), out _));
    }

    [Fact]
    public void Ci_sdk_check_finds_a_floating_sdk()
    {
        var real = Repo(".github", "workflows", "ci.yml");
        Assert.Contains("global-json-file: global.json", real);
        Assert.NotEmpty(BuildPolicy.CiSdkViolations(real.Replace("global-json-file: global.json", "dotnet-version: \"10.0.x\"")));
    }

    [Theory]
    [InlineData("RUN dotnet restore ./Whiskers/Whiskers.csproj --locked-mode", "RUN dotnet restore ./Whiskers/Whiskers.csproj")]
    [InlineData("--no-restore -o /app/publish", "-o /app/publish")]
    [InlineData(" src/Whiskers.Data/packages.lock.json ./Whiskers.Data/", " ./Whiskers.Data/")]
    [InlineData("COPY Directory.Build.props ./\n", "")]
    public void Dockerfile_restore_check_finds_an_unlocked_build(string original, string mutation)
    {
        var real = Repo("Dockerfile");
        Assert.Contains(original, real);
        Assert.NotEmpty(BuildPolicy.DockerfileRestoreViolations(real.Replace(original, mutation), BuildPolicy.SolutionProjects(Repo("Whiskers.slnx"))));
    }

    [Theory]
    [InlineData("aspnet:10.0.14@sha256:")]
    [InlineData("sdk:10.0.402@sha256:")]
    [InlineData("docker:29.9.3-cli@sha256:")]
    public void Digest_check_finds_an_unpinned_image(string pinned)
    {
        var real = Repo("Dockerfile");
        var line = real.Split('\n').Single(l => l.Contains(pinned, StringComparison.Ordinal));
        var unpinned = Regex.Replace(line, "@sha256:[0-9a-f]{64}", "");
        Assert.NotEmpty(BuildPolicy.UnpinnedBaseImages(real.Replace(line, unpinned)));
        // A shortened or tag-less digest is no pin either.
        Assert.NotEmpty(BuildPolicy.UnpinnedBaseImages(real.Replace(line, Regex.Replace(line, "(@sha256:[0-9a-f]{12})[0-9a-f]+", "$1"))));
    }

    [Fact]
    public void Sdk_pin_check_finds_a_drifting_sdk()
    {
        var global = Repo("global.json");
        var dockerfile = Repo("Dockerfile");
        Assert.NotEmpty(BuildPolicy.SdkPinViolations(global.Replace("\"10.0.402\"", "\"10.0.403\""), dockerfile));
        Assert.NotEmpty(BuildPolicy.SdkPinViolations(global.Replace("\"disable\"", "\"latestFeature\""), dockerfile));
    }

    private static string Repo(params string[] parts) => DeploymentScripts.ReadRepoFile(parts);
}

/// <summary>The checks behind <see cref="BuildReproducibilityTests"/>; each returns the violations it found.</summary>
internal static class BuildPolicy
{
    private static readonly Regex FromLine = new(@"^FROM\s+(?:--platform=\S+\s+)?(?<image>\S+)(?:\s+AS\s+(?<stage>\S+))?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex PinnedImage = new(@"^[a-z0-9][a-z0-9._/-]*(?::[A-Za-z0-9._-]+)@sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex DotnetCommand = new(@"\bdotnet\s+(?<verb>restore|build|publish|test|run|pack)\b(?<rest>[^\n]*)", RegexOptions.CultureInvariant);

    /// <summary>The project paths of <c>Whiskers.slnx</c>, repository-relative with forward slashes.</summary>
    public static IReadOnlyList<string> SolutionProjects(string slnx) =>
        XDocument.Parse(slnx).Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .OfType<string>()
            .Select(p => p.Replace('\\', '/'))
            .ToList();

    private static string ProjectDirectory(string project) => project[..project.LastIndexOf('/')];

    // ---- lock files ----

    public static IReadOnlyList<string> LockFileViolations(string directoryBuildProps, IReadOnlyList<string> projects, Func<string, string?> read)
    {
        var violations = new List<string>();
        var props = XDocument.Parse(directoryBuildProps);
        if (!props.Descendants("RestorePackagesWithLockFile").Any(e => e.Value.Trim() == "true" && e.Attribute("Condition") is null))
            violations.Add("Directory.Build.props does not set RestorePackagesWithLockFile=true unconditionally");

        foreach (var project in projects)
        {
            var csproj = read(project);
            if (csproj is null) { violations.Add($"{project} does not exist"); continue; }
            if (XDocument.Parse(csproj).Descendants("RestorePackagesWithLockFile").Any(e => e.Value.Trim() != "true"))
                violations.Add($"{project} switches its lock file off");

            var lockPath = ProjectDirectory(project) + "/packages.lock.json";
            var lockFile = read(lockPath);
            if (lockFile is null) { violations.Add($"{lockPath} is missing"); continue; }
            using var json = JsonDocument.Parse(lockFile);
            if (!json.RootElement.TryGetProperty("version", out _) || !json.RootElement.TryGetProperty("dependencies", out var deps)
                || !deps.EnumerateObject().Any())
                violations.Add($"{lockPath} is not a NuGet lock file with dependencies");
        }
        return violations;
    }

    // ---- restores in workflows and the Dockerfile ----

    /// <summary>Every <c>dotnet restore</c> is locked; every other dotnet verb that would restore implicitly says
    /// <c>--no-restore</c> or <c>--no-build</c>.</summary>
    public static IReadOnlyList<string> WorkflowRestoreViolations(string text, out int lockedRestores)
    {
        var violations = new List<string>();
        lockedRestores = 0;
        foreach (var line in JoinContinuations(text))
        {
            if (line.TrimStart().StartsWith('#')) continue;
            foreach (Match m in DotnetCommand.Matches(line))
            {
                var verb = m.Groups["verb"].Value;
                var rest = m.Groups["rest"].Value;
                if (verb == "restore")
                {
                    if (rest.Contains("--locked-mode", StringComparison.Ordinal)) lockedRestores++;
                    else violations.Add($"unlocked restore: '{m.Value.Trim()}'");
                }
                else if (!rest.Contains("--no-restore", StringComparison.Ordinal) && !rest.Contains("--no-build", StringComparison.Ordinal))
                {
                    violations.Add($"'{m.Value.Trim()}' restores implicitly (unlocked); add --no-restore or --no-build");
                }
            }
        }
        return violations;
    }

    public static IReadOnlyList<string> CiSdkViolations(string ciYaml)
    {
        var violations = new List<string>();
        var root = (YamlMappingNode)Load(ciYaml);
        foreach (var (job, steps) in JobSteps(root))
        {
            foreach (var step in steps)
            {
                if (Scalar(step, "uses")?.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal) != true) continue;
                var with = step.Children.TryGetValue(new YamlScalarNode("with"), out var w) ? w as YamlMappingNode : null;
                if (with is null || Scalar(with, "global-json-file") != "global.json")
                    violations.Add($"job '{job}' sets up .NET without global-json-file: global.json");
                if (with is not null && with.Children.ContainsKey(new YamlScalarNode("dotnet-version")))
                    violations.Add($"job '{job}' names a dotnet-version next to global.json");
            }
        }
        return violations;
    }

    public static IReadOnlyList<string> DockerfileRestoreViolations(string dockerfile, IReadOnlyList<string> projects)
    {
        var violations = new List<string>();
        var lines = JoinContinuations(dockerfile).Where(l => !l.TrimStart().StartsWith('#')).ToList();
        var restores = 0;
        foreach (var line in lines.Where(l => l.TrimStart().StartsWith("RUN ", StringComparison.Ordinal)))
        {
            foreach (Match m in DotnetCommand.Matches(line))
            {
                var verb = m.Groups["verb"].Value;
                var rest = m.Groups["rest"].Value;
                if (verb == "restore")
                {
                    restores++;
                    if (!rest.Contains("--locked-mode", StringComparison.Ordinal)) violations.Add($"unlocked restore: '{m.Value.Trim()}'");
                }
                else if (!rest.Contains("--no-restore", StringComparison.Ordinal) && !rest.Contains("--no-build", StringComparison.Ordinal))
                {
                    violations.Add($"'{m.Value.Trim()}' restores implicitly (unlocked); add --no-restore");
                }
            }
        }
        if (restores == 0) violations.Add("the Dockerfile has no dotnet restore");

        // What the restore layer sees: the COPY sources before the first restore.
        var copies = lines.TakeWhile(l => !(l.TrimStart().StartsWith("RUN ", StringComparison.Ordinal) && l.Contains("dotnet restore", StringComparison.Ordinal)))
            .Where(l => l.TrimStart().StartsWith("COPY ", StringComparison.Ordinal) && !l.Contains("--from=", StringComparison.Ordinal))
            .SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)).ToHashSet(StringComparer.Ordinal);
        if (!copies.Contains("Directory.Build.props"))
            violations.Add("the Dockerfile does not copy Directory.Build.props before the restore (no lock files without it)");
        // Every project the image builds (all but the test project) brings its lock file into the restore layer.
        foreach (var project in projects.Where(p => !p.Contains(".Tests/", StringComparison.Ordinal)))
        {
            var lockFile = ProjectDirectory(project) + "/packages.lock.json";
            if (!copies.Contains(lockFile)) violations.Add($"the Dockerfile does not copy {lockFile} into the restore layer");
        }
        return violations;
    }

    // ---- base images ----

    /// <summary>The FROM references that are images (a FROM naming an earlier stage is not one).</summary>
    public static IReadOnlyList<string> BaseImages(string dockerfile)
    {
        var stages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<string>();
        foreach (Match m in FromLine.Matches(dockerfile))
        {
            var image = m.Groups["image"].Value;
            if (!stages.Contains(image)) images.Add(image);
            if (m.Groups["stage"].Success) stages.Add(m.Groups["stage"].Value);
        }
        return images;
    }

    public static IReadOnlyList<string> UnpinnedBaseImages(string dockerfile) =>
        BaseImages(dockerfile).Where(i => !PinnedImage.IsMatch(i))
            .Select(i => $"base image '{i}' is not pinned as image:tag@sha256:<64 hex>")
            .ToList();

    public static IReadOnlyList<string> SdkPinViolations(string globalJson, string dockerfile)
    {
        var violations = new List<string>();
        using var json = JsonDocument.Parse(globalJson);
        var sdk = json.RootElement.GetProperty("sdk");
        var version = sdk.GetProperty("version").GetString();
        if (!sdk.TryGetProperty("rollForward", out var rf) || rf.GetString() != "disable")
            violations.Add("global.json must not roll forward: another SDK writes other framework packages into the lock files");
        var sdkImages = BaseImages(dockerfile).Where(i => i.StartsWith("mcr.microsoft.com/dotnet/sdk:", StringComparison.Ordinal)).ToList();
        if (sdkImages.Count == 0) violations.Add("the Dockerfile has no mcr.microsoft.com/dotnet/sdk stage");
        foreach (var image in sdkImages)
        {
            var tag = image["mcr.microsoft.com/dotnet/sdk:".Length..].Split('@')[0];
            if (tag != version) violations.Add($"the SDK image tag '{tag}' is not global.json's SDK {version}");
        }
        return violations;
    }

    // ---- Dependabot ----

    public static IReadOnlyList<string> DependabotViolations(string dependabotYaml, IReadOnlyList<string> projects)
    {
        var violations = new List<string>();
        var root = (YamlMappingNode)Load(dependabotYaml);
        var updates = (YamlSequenceNode)root.Children[new YamlScalarNode("updates")];
        var covered = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var update in updates.Children.OfType<YamlMappingNode>())
        {
            var ecosystem = Scalar(update, "package-ecosystem") ?? "";
            if (!covered.TryGetValue(ecosystem, out var dirs)) covered[ecosystem] = dirs = new HashSet<string>(StringComparer.Ordinal);
            if (Scalar(update, "directory") is { } dir) dirs.Add(dir.TrimEnd('/') is "" ? "/" : dir.TrimEnd('/'));
            if (update.Children.TryGetValue(new YamlScalarNode("directories"), out var list) && list is YamlSequenceNode seq)
                foreach (var d in seq.Children.OfType<YamlScalarNode>())
                    dirs.Add(d.Value!.TrimEnd('/') is "" ? "/" : d.Value!.TrimEnd('/'));
            if (!update.Children.ContainsKey(new YamlScalarNode("schedule")))
                violations.Add($"the {ecosystem} entry has no schedule");
        }

        bool Covers(string ecosystem, string dir) => covered.TryGetValue(ecosystem, out var d) && d.Contains(dir);
        foreach (var project in projects)
        {
            var dir = "/" + ProjectDirectory(project);
            if (!Covers("nuget", dir)) violations.Add($"nuget does not cover {dir} ({project})");
        }
        if (!Covers("dotnet-sdk", "/")) violations.Add("dotnet-sdk does not cover / (global.json)");
        if (!Covers("docker", "/")) violations.Add("docker does not cover / (the Dockerfile's base images)");
        if (!Covers("github-actions", "/")) violations.Add("github-actions does not cover / (.github/workflows)");
        return violations;
    }

    // ---- helpers ----

    /// <summary>The text as logical lines: a trailing backslash continues the line (shell and Dockerfile).</summary>
    private static IEnumerable<string> JoinContinuations(string text)
    {
        var current = new System.Text.StringBuilder();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.EndsWith('\\')) { current.Append(line[..^1]).Append(' '); continue; }
            current.Append(line);
            yield return current.ToString();
            current.Clear();
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static YamlNode Load(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return stream.Documents[0].RootNode;
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;

    private static IEnumerable<(string Job, IReadOnlyList<YamlMappingNode> Steps)> JobSteps(YamlMappingNode root)
    {
        if (!root.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) || jobs is not YamlMappingNode map) yield break;
        foreach (var (key, job) in map.Children)
        {
            if (job is not YamlMappingNode jobMap) continue;
            var steps = jobMap.Children.TryGetValue(new YamlScalarNode("steps"), out var s) && s is YamlSequenceNode seq
                ? seq.Children.OfType<YamlMappingNode>().ToList()
                : new List<YamlMappingNode>();
            yield return (((YamlScalarNode)key).Value!, steps);
        }
    }
}
