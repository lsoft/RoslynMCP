using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Services
{
    /// <summary>
    /// A loaded solution plus the file timestamps it was built from. Source edits are applied to
    /// the immutable Solution in place (cheap, compilations are reused); only changes that affect
    /// MSBuild evaluation (.sln, project files, Directory.Build.*, global.json) need a full reload.
    /// </summary>
    public sealed class TrackedSolution
    {
        private static readonly string[] EvaluationFileNames =
            { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "global.json" };

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, DateTime> _evaluationStamps;
        private readonly List<GlobbedProject> _globbedProjects;
        // .cs files that were on disk at load time but not compiled (e.g. <Compile Remove>): never "new"
        private readonly HashSet<string> _excludedFiles;
        private Dictionary<string, DateTime> _documentStamps;

        // A project whose Compile items come from the SDK's default **/*.cs glob
        private sealed record GlobbedProject(string Directory, string ProjectFilePath);

        public enum RefreshResult { UpToDate, Updated, ReloadRequired }

        private TrackedSolution(
            Solution solution,
            Dictionary<string, DateTime> evaluationStamps,
            Dictionary<string, DateTime> documentStamps,
            List<GlobbedProject> globbedProjects)
        {
            Solution = solution;
            _evaluationStamps = evaluationStamps;
            _documentStamps = documentStamps;
            _globbedProjects = globbedProjects;

            var compiled = new HashSet<string>(
                solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>(),
                StringComparer.OrdinalIgnoreCase);
            _excludedFiles = new HashSet<string>(
                globbedProjects.SelectMany(p => EnumerateGlobbedSources(p.Directory, p.Directory)).Where(f => !compiled.Contains(f)),
                StringComparer.OrdinalIgnoreCase);
        }

        public Solution Solution { get; private set; }

        public static TrackedSolution Create(string solutionPath, Solution solution, DateTime loadStartedUtc)
        {
            var evaluationStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in GetEvaluationFiles(solutionPath, solution))
                evaluationStamps[file] = GetStamp(file);

            var documentStamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in solution.Projects.SelectMany(p => p.Documents).Select(d => d.FilePath).OfType<string>())
            {
                var stamp = GetStamp(path);
                if (stamp == DateTime.MinValue)
                    continue; // Not on disk (in-memory or virtual document): nothing to track

                // Changed while the load was running: force a refresh on the next call
                documentStamps[path] = stamp > loadStartedUtc ? DateTime.MinValue : stamp;
            }

            var globbedProjects = solution.Projects
                .Select(p => p.FilePath)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(UsesDefaultCompileGlob)
                .Select(p => new GlobbedProject(Path.GetDirectoryName(p)!, p))
                .ToList();

            return new TrackedSolution(solution, evaluationStamps, documentStamps, globbedProjects);
        }

        /// <summary>Brings the solution up to date with the files on disk.</summary>
        public async Task<RefreshResult> RefreshAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_evaluationStamps.Any(e => GetStamp(e.Key) != e.Value))
                    return RefreshResult.ReloadRequired;

                // Work on copies and commit at the end, so a cancelled refresh leaves no half-applied state
                var solution = Solution;
                var stamps = new Dictionary<string, DateTime>(_documentStamps, StringComparer.OrdinalIgnoreCase);
                var changed = false;

                foreach (var (path, recorded) in _documentStamps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = GetStamp(path);
                    if (current == recorded)
                        continue;

                    var ids = solution.GetDocumentIdsWithFilePath(path);
                    if (current == DateTime.MinValue)
                    {
                        // Build output (obj/bin) is recreated by the next build; keep the last known text
                        if (IsBuildOutput(path))
                            continue;

                        solution = solution.RemoveDocuments(ids);
                        stamps.Remove(path);
                    }
                    else
                    {
                        solution = solution.WithDocumentText(ids, await ReadTextAsync(path, cancellationToken));
                        stamps[path] = current;
                    }
                    changed = true;
                }

                foreach (var path in FindNewSourceFiles(stamps))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var stamp = GetStamp(path);
                    var text = await ReadTextAsync(path, cancellationToken);
                    var owner = _globbedProjects
                        .Where(p => IsUnder(path, p.Directory))
                        .OrderByDescending(p => p.Directory.Length)
                        .First();

                    // A multi-targeted project gets the file once per target framework
                    foreach (var project in solution.Projects.Where(p => string.Equals(p.FilePath, owner.ProjectFilePath, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        var folders = Path.GetRelativePath(owner.Directory, Path.GetDirectoryName(path)!)
                            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                            .Where(f => f != ".");
                        solution = solution.AddDocument(
                            DocumentId.CreateNewId(project.Id, path), Path.GetFileName(path), text, folders, path);
                    }

                    stamps[path] = stamp;
                    changed = true;
                }

                if (!changed)
                    return RefreshResult.UpToDate;

                Solution = solution;
                _documentStamps = stamps;
                return RefreshResult.Updated;
            }
            finally
            {
                _gate.Release();
            }
        }

        private IEnumerable<string> FindNewSourceFiles(Dictionary<string, DateTime> known)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in _globbedProjects)
            {
                foreach (var file in EnumerateGlobbedSources(project.Directory, project.Directory))
                {
                    if (!known.ContainsKey(file) && !_excludedFiles.Contains(file) && found.Add(file))
                        yield return file;
                }
            }
        }

        // Mirrors the SDK's default Compile glob: **/*.cs without bin, obj, hidden folders and nested projects
        private static IEnumerable<string> EnumerateGlobbedSources(string directory, string projectRoot)
        {
            string[] files;
            string[] subdirectories;
            try
            {
                if (!string.Equals(directory, projectRoot, StringComparison.OrdinalIgnoreCase)
                    && Directory.EnumerateFiles(directory, "*.*proj").Any())
                    yield break;

                files = Directory.GetFiles(directory, "*.cs");
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            foreach (var file in files)
                yield return Path.GetFullPath(file);

            foreach (var subdirectory in subdirectories)
            {
                var name = Path.GetFileName(subdirectory);
                if (name.StartsWith('.') || name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var file in EnumerateGlobbedSources(subdirectory, projectRoot))
                    yield return file;
            }
        }

        private static IEnumerable<string> GetEvaluationFiles(string solutionPath, Solution solution)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { solutionPath };
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var projectPath in solution.Projects.Select(p => p.FilePath).OfType<string>())
            {
                files.Add(projectPath);

                // MSBuild looks for these files in every directory up from the project
                for (var directory = Path.GetDirectoryName(projectPath); directory != null; directory = Path.GetDirectoryName(directory))
                {
                    if (!directories.Add(directory))
                        break; // the rest of the chain is already covered
                }
            }

            foreach (var directory in directories)
            {
                foreach (var name in EvaluationFileNames)
                    files.Add(Path.Combine(directory, name));
            }

            return files;
        }

        private static bool UsesDefaultCompileGlob(string projectPath)
        {
            try
            {
                var content = File.ReadAllText(projectPath);
                var sdkStyle = content.Contains("Sdk=\"", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("<Sdk ", StringComparison.OrdinalIgnoreCase);
                var globDisabled = content.Contains("<EnableDefaultCompileItems>false", StringComparison.OrdinalIgnoreCase)
                    || content.Contains("<EnableDefaultItems>false", StringComparison.OrdinalIgnoreCase);
                return sdkStyle && !globDisabled;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsBuildOutput(string path)
        {
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return segments.Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) || s.Equals("bin", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUnder(string path, string directory)
        {
            var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // MinValue stands for "does not exist"
        private static DateTime GetStamp(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return DateTime.MinValue;
            }
        }

        private static async Task<SourceText> ReadTextAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken);
            memory.Position = 0;
            // Detects the encoding from the BOM, UTF-8 otherwise, like the compiler does
            return SourceText.From(memory);
        }
    }
}
