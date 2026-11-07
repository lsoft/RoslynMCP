using Microsoft.CodeAnalysis;

namespace RoslynMcpServer.Services
{
    /// <summary>
    /// Maps between paths the agent sees (relative to the solution directory) and solution documents,
    /// including source-generated ones that have no file on disk.
    /// </summary>
    public static class SolutionFiles
    {
        public const string GeneratedPrefix = "[generated] ";

        public static string GetDirectory(string solutionPath)
        {
            return Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        }

        /// <summary>Path relative to the solution directory with '/' separators, absolute when outside it.</summary>
        public static string Display(string? path, string solutionDirectory)
        {
            if (string.IsNullOrEmpty(path))
                return "";

            var relative = Path.GetRelativePath(solutionDirectory, path);
            if (relative.StartsWith("..") || Path.IsPathRooted(relative))
                return path.Replace('\\', '/');
            return relative.Replace('\\', '/');
        }

        /// <summary>"path:line" for source, "[generated] Hint.g.cs:line" for generated code, "metadata Assembly" otherwise.</summary>
        public static string FormatLocation(Solution solution, Location? location, string solutionDirectory, ISymbol? symbol = null)
        {
            if (location == null || !location.IsInSource || location.SourceTree == null)
                return symbol?.ContainingAssembly != null ? $"metadata {symbol.ContainingAssembly.Name}" : "metadata";

            var line = location.GetLineSpan().StartLinePosition.Line + 1;
            var tree = location.SourceTree;
            return IsGenerated(solution, tree)
                ? $"{GeneratedPrefix}{Path.GetFileName(tree.FilePath)}:{line}"
                : $"{Display(tree.FilePath, solutionDirectory)}:{line}";
        }

        /// <summary>True for source-generator output (or a tree the solution does not know).</summary>
        public static bool IsGenerated(Solution solution, SyntaxTree tree)
        {
            // GetDocument also finds already generated documents, so check the document type
            var document = solution.GetDocument(tree);
            return document == null || document is SourceGeneratedDocument;
        }

        /// <summary>Finds a document (including source-generated ones) behind a syntax tree.</summary>
        public static async Task<Document?> FindDocumentAsync(Solution solution, SyntaxTree tree, CancellationToken cancellationToken)
        {
            var document = solution.GetDocument(tree);
            if (document != null)
                return document;

            foreach (var project in solution.Projects)
            {
                foreach (var generated in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
                {
                    if (string.Equals(generated.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase))
                        return generated;
                }
            }

            return null;
        }

        public sealed record DocumentMatch(IReadOnlyList<Document> Documents, IReadOnlyList<string> Candidates);

        /// <summary>
        /// Finds the documents for a path given as absolute, relative to the solution directory, or as a
        /// unique path suffix ("Customer.cs", "Models/Customer.cs", a generated file's hint name).
        /// All target-framework copies of the file are returned. When the suffix is ambiguous,
        /// Documents is empty and Candidates lists the matching paths.
        /// </summary>
        public static async Task<DocumentMatch> FindDocumentsAsync(
            Solution solution, string solutionDirectory, string filePath, CancellationToken cancellationToken)
        {
            var absolute = Path.GetFullPath(Path.IsPathRooted(filePath) ? filePath : Path.Combine(solutionDirectory, filePath));
            var exact = solution.GetDocumentIdsWithFilePath(absolute)
                .Select(solution.GetDocument)
                .OfType<Document>()
                .ToList();
            if (exact.Count > 0)
                return new DocumentMatch(exact, Array.Empty<string>());

            var suffix = filePath.Replace('\\', '/').TrimStart('.', '/');
            var all = solution.Projects.SelectMany(p => p.Documents).Cast<Document>().ToList();
            foreach (var project in solution.Projects)
                all.AddRange(await project.GetSourceGeneratedDocumentsAsync(cancellationToken));

            var matches = all
                .Where(d => d.FilePath != null && EndsWithPath(d.FilePath, suffix))
                .ToList();
            var paths = matches.Select(d => d.FilePath!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            return paths.Count == 1
                ? new DocumentMatch(matches, Array.Empty<string>())
                : new DocumentMatch(Array.Empty<Document>(), paths.Select(p => Display(p, solutionDirectory)).ToList());
        }

        private static bool EndsWithPath(string path, string suffix)
        {
            var normalized = path.Replace('\\', '/');
            return normalized.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
