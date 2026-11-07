using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using System.Collections.Concurrent;

namespace RoslynMcpServer.Services
{
    public interface ISolutionLoader
    {
        Task<Solution> LoadAsync(string path, CancellationToken cancellationToken);
    }

    public class MSBuildSolutionLoader : ISolutionLoader
    {
        private readonly ILogger<MSBuildSolutionLoader> _logger;

        public MSBuildSolutionLoader(ILogger<MSBuildSolutionLoader> logger)
        {
            _logger = logger;
        }

        public async Task<Solution> LoadAsync(string path, CancellationToken cancellationToken)
        {
            // Each load gets its own workspace; the returned Solution is an immutable snapshot
            // and stays usable after the workspace is gone.
            using var workspace = MSBuildWorkspace.Create();
            using var failedHandler = workspace.RegisterWorkspaceFailedHandler(e =>
                _logger.LogWarning("Workspace diagnostic ({Kind}): {Message}", e.Diagnostic.Kind, e.Diagnostic.Message));

            var solution = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(path, cancellationToken: cancellationToken)).Solution
                : await workspace.OpenSolutionAsync(path, cancellationToken: cancellationToken);

            // A project the SDK could not evaluate (e.g. a target framework newer than the SDK) still
            // loads its documents, but without references every type is an error type
            foreach (var project in solution.Projects.Where(p => !p.MetadataReferences.Any()))
            {
                _logger.LogWarning(
                    "Project {Project} loaded without any references, semantic results will be incomplete; " +
                    "check that an installed .NET SDK supports its target framework", project.Name);
            }

            return solution;
        }
    }

    public class CodeAnalysisService
    {
        private readonly ISolutionLoader _loader;
        private readonly ILogger<CodeAnalysisService> _logger;
        private readonly ConcurrentDictionary<string, Lazy<Task<TrackedSolution>>> _solutions =
            new(StringComparer.OrdinalIgnoreCase);

        public CodeAnalysisService(ISolutionLoader loader, ILogger<CodeAnalysisService> logger)
        {
            _loader = loader;
            _logger = logger;
        }

        public async Task<Solution> GetSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
        {
            var key = Path.GetFullPath(solutionPath);

            var (entry, tracked) = await GetOrLoadAsync(key, cancellationToken);
            var result = await tracked.RefreshAsync(cancellationToken);
            if (result == TrackedSolution.RefreshResult.Updated)
                _logger.LogInformation("Applied source changes to {SolutionPath}", key);
            if (result != TrackedSolution.RefreshResult.ReloadRequired)
                return tracked.Solution;

            // Project files changed: MSBuild has to evaluate again. Reload once; a file stamped in
            // the future must not trigger a reload on every call.
            _logger.LogInformation("Project files of {SolutionPath} changed, reloading", key);
            _solutions.TryRemove(new KeyValuePair<string, Lazy<Task<TrackedSolution>>>(key, entry));
            (_, tracked) = await GetOrLoadAsync(key, cancellationToken);
            return tracked.Solution;
        }

        private async Task<(Lazy<Task<TrackedSolution>> Entry, TrackedSolution Solution)> GetOrLoadAsync(
            string key, CancellationToken cancellationToken)
        {
            // The load is shared between callers, so it must not be cancelled by any single one of them
            var entry = _solutions.GetOrAdd(key, k => new Lazy<Task<TrackedSolution>>(() => LoadAsync(k)));
            try
            {
                return (entry, await entry.Value.WaitAsync(cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || entry.Value.IsCanceled)
            {
                // Do not keep failed loads around, the next call should retry
                _solutions.TryRemove(new KeyValuePair<string, Lazy<Task<TrackedSolution>>>(key, entry));
                throw;
            }
        }

        public void InvalidateSolution(string solutionPath)
        {
            _solutions.TryRemove(Path.GetFullPath(solutionPath), out _);
        }

        private async Task<TrackedSolution> LoadAsync(string path)
        {
            // Take the timestamp before loading so edits made during the load are seen as changes
            var loadStarted = DateTime.UtcNow;
            _logger.LogInformation("Loading solution {SolutionPath}", path);
            var solution = await _loader.LoadAsync(path, CancellationToken.None);
            return TrackedSolution.Create(path, solution, loadStarted);
        }

        public async Task<DependencyAnalysis> AnalyzeDependenciesAsync(
            string solutionPath, int maxDepth, CancellationToken cancellationToken = default)
        {
            var solution = await GetSolutionAsync(solutionPath, cancellationToken);
            return await AnalyzeDependenciesAsync(solution, Path.GetFileNameWithoutExtension(solutionPath), maxDepth, cancellationToken);
        }

        public static async Task<DependencyAnalysis> AnalyzeDependenciesAsync(
            Solution solution, string name, int maxDepth, CancellationToken cancellationToken = default)
        {
            var analysis = new DependencyAnalysis { ProjectName = name };
            var projectDependencies = new Dictionary<string, ProjectDependency>();
            var assemblyDependencies = new Dictionary<string, ProjectDependency>(StringComparer.OrdinalIgnoreCase);
            var namespaceUsages = new Dictionary<string, NamespaceUsage>();

            // A multi-targeted project appears once per target framework; count everything per project file
            var countedProjectReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var countedAssemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var countedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var countedSymbols = new HashSet<string>();

            foreach (var project in solution.Projects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var projectKey = ProjectIdentity.GetKey(project);

                foreach (var dependency in GetProjectReferences(solution, project, maxDepth))
                {
                    var dependencyKey = ProjectIdentity.GetKey(dependency);
                    if (!countedProjectReferences.Add($"{projectKey}|{dependencyKey}"))
                        continue;

                    if (!projectDependencies.TryGetValue(dependencyKey, out var entry))
                    {
                        entry = new ProjectDependency { Name = ProjectIdentity.GetDisplayName(dependency), Type = "ProjectReference" };
                        projectDependencies[dependencyKey] = entry;
                    }
                    entry.UsageCount++;
                }

                foreach (var reference in project.MetadataReferences.OfType<PortableExecutableReference>())
                {
                    if (reference.FilePath == null || IsFrameworkReference(reference.FilePath))
                        continue;

                    var assemblyName = Path.GetFileNameWithoutExtension(reference.FilePath);
                    if (!countedAssemblyReferences.Add($"{projectKey}|{assemblyName}"))
                        continue;

                    if (!assemblyDependencies.TryGetValue(assemblyName, out var entry))
                    {
                        entry = new ProjectDependency { Name = assemblyName, Type = "AssemblyReference" };
                        assemblyDependencies[assemblyName] = entry;
                    }
                    entry.UsageCount++;
                }

                foreach (var document in project.Documents)
                {
                    if (document.FilePath != null && !countedDocuments.Add(document.FilePath))
                        continue;

                    var root = await document.GetSyntaxRootAsync(cancellationToken);
                    if (root == null) continue;

                    foreach (var usingDirective in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
                    {
                        if (usingDirective.Alias != null || usingDirective.Name == null)
                            continue;

                        var ns = usingDirective.Name.ToString();
                        if (!namespaceUsages.TryGetValue(ns, out var usage))
                        {
                            usage = new NamespaceUsage { Namespace = ns };
                            namespaceUsages[ns] = usage;
                        }
                        usage.UsageCount++;
                    }
                }

                if (!project.SupportsCompilation) continue;

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                foreach (var symbol in SymbolSearchService.GetSourceSymbols(compilation))
                {
                    if (symbol is INamespaceSymbol || !countedSymbols.Add(ProjectIdentity.GetSymbolKey(symbol)))
                        continue;

                    analysis.TotalSymbols++;
                    if (symbol.DeclaredAccessibility == Accessibility.Public)
                        analysis.PublicSymbols++;
                    else if (symbol.DeclaredAccessibility == Accessibility.Internal)
                        analysis.InternalSymbols++;
                }
            }

            analysis.Dependencies = projectDependencies.Values
                .Concat(assemblyDependencies.Values)
                .OrderByDescending(d => d.UsageCount)
                .ThenBy(d => d.Name)
                .ToList();
            analysis.NamespaceUsages = namespaceUsages.Values.ToList();

            return analysis;
        }

        // Project references reachable from the project, transitive up to maxDepth levels
        private static IEnumerable<Project> GetProjectReferences(Solution solution, Project project, int maxDepth)
        {
            var visited = new HashSet<ProjectId> { project.Id };
            var current = new List<Project> { project };

            for (var depth = 0; depth < maxDepth && current.Count > 0; depth++)
            {
                var next = new List<Project>();
                foreach (var reference in current.SelectMany(p => p.ProjectReferences))
                {
                    var referenced = solution.GetProject(reference.ProjectId);
                    if (referenced != null && visited.Add(referenced.Id))
                        next.Add(referenced);
                }

                foreach (var referenced in next)
                    yield return referenced;

                current = next;
            }
        }

        private static bool IsFrameworkReference(string path)
        {
            return path.Contains("Microsoft.NETCore.App.Ref", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Microsoft.AspNetCore.App.Ref", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Microsoft.WindowsDesktop.App.Ref", StringComparison.OrdinalIgnoreCase)
                || path.Contains("NETStandard.Library.Ref", StringComparison.OrdinalIgnoreCase)
                || path.Contains("microsoft.netframework.referenceassemblies", StringComparison.OrdinalIgnoreCase)
                || path.Contains("Reference Assemblies", StringComparison.OrdinalIgnoreCase);
        }
    }
}
