using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Logging;

namespace RoslynMcpServer.Services
{
    /// <summary>Compiler and analyzer diagnostics without a build, and dead code candidates.</summary>
    public class CodeQualityService
    {
        // Methods the compiler binds by name (foreach, using, await, deconstruction, collection initializers)
        private static readonly HashSet<string> PatternMemberNames = new()
        {
            "GetEnumerator", "MoveNext", "Current", "Reset", "Dispose", "DisposeAsync", "GetAsyncEnumerator",
            "Deconstruct", "GetAwaiter", "GetResult", "IsCompleted", "OnCompleted", "UnsafeOnCompleted",
            "Add", "GetPinnableReference", "Slice", "Length", "Count", "Main", "$Main", "<Main>$"
        };

        private readonly CodeAnalysisService _codeAnalysis;
        private readonly ILogger<CodeQualityService> _logger;

        public CodeQualityService(CodeAnalysisService codeAnalysis, ILogger<CodeQualityService> logger)
        {
            _codeAnalysis = codeAnalysis;
            _logger = logger;
        }

        private sealed record Finding(DiagnosticSeverity Severity, string Id, string File, int Line, int Column, string Message);

        public async Task<string> GetDiagnosticsAsync(
            string solutionPath, string? filePath, string? projectName, string minSeverity, bool includeAnalyzers, int maxResults,
            CancellationToken cancellationToken)
        {
            if (!TryParseSeverity(minSeverity, out var threshold))
                return "Error: minSeverity must be error, warning or info.";

            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);

            // The scope: the projects to compile and, optionally, the one file to report on
            List<(Project Project, Document? Document)> scope;
            string title;
            if (!string.IsNullOrWhiteSpace(filePath))
            {
                var match = await SolutionFiles.FindDocumentsAsync(solution, solutionDirectory, filePath, cancellationToken);
                if (match.Documents.Count == 0)
                {
                    return match.Candidates.Count > 0
                        ? $"Error: '{filePath}' is ambiguous, candidates: {string.Join(", ", match.Candidates.Take(10))}"
                        : $"Error: '{filePath}' is not part of the solution.";
                }

                scope = match.Documents.Select(d => (d.Project, (Document?)d)).ToList();
                title = SolutionFiles.Display(match.Documents[0].FilePath, solutionDirectory);
            }
            else
            {
                scope = solution.Projects
                    .Where(p => p.SupportsCompilation)
                    .Where(p => projectName == null || string.Equals(ProjectIdentity.GetDisplayName(p), projectName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => (p, (Document?)null))
                    .ToList();
                if (scope.Count == 0)
                    return $"Error: Project '{projectName}' not found.";
                title = projectName ?? Path.GetFileName(solutionPath);
            }

            var findings = new List<Finding>();
            var analyzerCount = 0;
            var analyzerFailures = new HashSet<string>();

            foreach (var (project, document) in scope)
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                SyntaxTree? tree = document == null ? null : await document.GetSyntaxTreeAsync(cancellationToken);
                var diagnostics = new List<Diagnostic>(tree == null
                    ? compilation.GetDiagnostics(cancellationToken)
                    : compilation.GetSemanticModel(tree).GetDiagnostics(cancellationToken: cancellationToken));

                if (includeAnalyzers)
                {
                    var analyzers = LoadAnalyzers(project, analyzerFailures);
                    analyzerCount = Math.Max(analyzerCount, analyzers.Length);
                    if (analyzers.Length > 0)
                    {
                        // A crashing analyzer is skipped and named in the summary instead of failing the call
                        var options = new CompilationWithAnalyzersOptions(
                            project.AnalyzerOptions,
                            onAnalyzerException: (_, analyzer, _) => { lock (analyzerFailures) analyzerFailures.Add(analyzer.GetType().Name); },
                            concurrentAnalysis: true,
                            logAnalyzerExecutionTime: false);
                        var withAnalyzers = compilation.WithAnalyzers(analyzers, options);

                        if (tree == null)
                        {
                            diagnostics.AddRange(await withAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken));
                        }
                        else
                        {
                            diagnostics.AddRange(await withAnalyzers.GetAnalyzerSyntaxDiagnosticsAsync(tree, cancellationToken));
                            diagnostics.AddRange(await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(tree), null, cancellationToken));
                        }
                    }
                }

                foreach (var diagnostic in diagnostics)
                {
                    if (diagnostic.Severity < threshold || diagnostic.IsSuppressed)
                        continue;

                    // An analyzer crashed (AD0001): one line in the summary, not one per occurrence
                    if (diagnostic.Id == "AD0001")
                    {
                        analyzerFailures.Add(Message(diagnostic).Split('\'').ElementAtOrDefault(1) ?? "analyzer");
                        continue;
                    }

                    findings.Add(ToFinding(solution, project, diagnostic, solutionDirectory));
                }
            }

            return FormatFindings(title, findings, includeAnalyzers, analyzerCount, analyzerFailures, maxResults);
        }

        private ImmutableArray<DiagnosticAnalyzer> LoadAnalyzers(Project project, HashSet<string> failures)
        {
            var analyzers = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
            foreach (var reference in project.AnalyzerReferences)
            {
                try
                {
                    analyzers.AddRange(reference.GetAnalyzers(project.Language));
                }
                catch (Exception ex)
                {
                    // E.g. an analyzer built against a newer Roslyn than the server's
                    _logger.LogWarning(ex, "Could not load analyzers from {Reference}", reference.Display);
                    failures.Add(reference.Display ?? "analyzer");
                }
            }
            return analyzers.ToImmutable();
        }

        private static Finding ToFinding(Solution solution, Project project, Diagnostic diagnostic, string solutionDirectory)
        {
            var location = diagnostic.Location;
            if (!location.IsInSource || location.SourceTree == null)
                return new Finding(diagnostic.Severity, diagnostic.Id, $"({ProjectIdentity.GetDisplayName(project)})", 0, 0, Message(diagnostic));

            var span = location.GetLineSpan().StartLinePosition;
            var tree = location.SourceTree;
            var file = SolutionFiles.IsGenerated(solution, tree)
                ? SolutionFiles.GeneratedPrefix + Path.GetFileName(tree.FilePath)
                : SolutionFiles.Display(tree.FilePath, solutionDirectory);
            return new Finding(diagnostic.Severity, diagnostic.Id, file, span.Line + 1, span.Character + 1, Message(diagnostic));
        }

        // English regardless of the OS language: stable for the agent and searchable
        private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

        private static string FormatFindings(
            string title, List<Finding> findings, bool includeAnalyzers, int analyzerCount, HashSet<string> analyzerFailures, int maxResults)
        {
            // Target-framework copies report the same diagnostic once each
            var unique = findings
                .DistinctBy(f => (f.Id, f.File, f.Line, f.Column, f.Message))
                .OrderByDescending(f => f.Severity)
                .ThenBy(f => f.File)
                .ThenBy(f => f.Line)
                .ToList();
            var shown = unique.Take(Math.Max(1, maxResults)).ToList();

            var counts = string.Join(", ", unique.GroupBy(f => f.Severity).OrderByDescending(g => g.Key)
                .Select(g => $"{g.Count()} {SeverityName(g.Key)}{(g.Count() == 1 ? "" : "s")}"));
            var sources = includeAnalyzers ? $"compiler + {analyzerCount} analyzers" : "compiler only";

            var output = new StringBuilder();
            output.AppendLine(unique.Count == 0
                ? $"No diagnostics for {title} ({sources})."
                : $"Diagnostics for {title}: {counts} ({sources}){(shown.Count < unique.Count ? $", showing {shown.Count}" : "")}");

            foreach (var file in shown.GroupBy(f => f.File))
            {
                output.AppendLine($"**{file.Key}**");
                foreach (var finding in file)
                {
                    var position = finding.Line > 0 ? $" L{finding.Line}:{finding.Column}" : "";
                    output.AppendLine($"  {SeverityName(finding.Severity)} {finding.Id}{position} {finding.Message}");
                }
            }

            if (shown.Count < unique.Count)
                output.AppendLine($"... and {unique.Count - shown.Count} more. Narrow with filePath or minSeverity, or raise maxResults.");
            if (analyzerFailures.Count > 0)
                output.AppendLine($"Analyzers that failed and were skipped: {string.Join(", ", analyzerFailures.Take(5))}{(analyzerFailures.Count > 5 ? ", ..." : "")}");

            return output.ToString();
        }

        private static string SeverityName(DiagnosticSeverity severity) => severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            DiagnosticSeverity.Info => "info",
            _ => "hidden"
        };

        private static bool TryParseSeverity(string? text, out DiagnosticSeverity severity)
        {
            switch ((text ?? "warning").Trim().ToLowerInvariant())
            {
                case "error": severity = DiagnosticSeverity.Error; return true;
                case "warning": severity = DiagnosticSeverity.Warning; return true;
                case "info": severity = DiagnosticSeverity.Info; return true;
                default: severity = DiagnosticSeverity.Hidden; return false;
            }
        }

        /// <summary>
        /// Symbols with no references anywhere in the solution. Conservative: only what nothing outside
        /// the solution can see (unless includePublic), and nothing a framework may call by convention.
        /// </summary>
        public async Task<string> FindUnusedSymbolsAsync(
            string solutionPath, string? projectName, bool includePublic, int maxResults, CancellationToken cancellationToken)
        {
            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);

            var candidates = new List<ISymbol>();
            foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
            {
                if (projectName != null && !string.Equals(ProjectIdentity.GetDisplayName(project), projectName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                candidates.AddRange(SymbolSearchService.GetSourceSymbols(compilation)
                    .Where(s => IsCandidate(s, solution, includePublic)));
            }

            candidates = candidates.DistinctBy(ProjectIdentity.GetSymbolKey).ToList();

            // One reference search per candidate; bounded parallelism keeps large solutions responsible
            using var gate = new SemaphoreSlim(Environment.ProcessorCount);
            var checks = candidates.Select(async symbol =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    return await IsReferencedAsync(symbol, solution, cancellationToken) ? null : symbol;
                }
                finally
                {
                    gate.Release();
                }
            });
            var unused = (await Task.WhenAll(checks)).OfType<ISymbol>().ToList();

            return FormatUnused(solution, solutionDirectory, unused, candidates.Count, includePublic, maxResults);
        }

        private static bool IsCandidate(ISymbol symbol, Solution solution, bool includePublic)
        {
            var kindOk = symbol switch
            {
                INamedTypeSymbol type => type.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface or TypeKind.Enum or TypeKind.Delegate,
                IMethodSymbol method => method.MethodKind == MethodKind.Ordinary,
                IPropertySymbol property => !property.IsIndexer,
                IFieldSymbol field => field.ContainingType?.TypeKind != TypeKind.Enum,
                IEventSymbol => true,
                _ => false
            };
            if (!kindOk || symbol.IsImplicitlyDeclared || symbol.IsOverride || symbol.IsVirtual || symbol.IsAbstract || symbol.IsExtern)
                return false;

            if (!includePublic && IsVisibleOutsideAssembly(symbol))
                return false;

            // Frameworks find these by attribute ([Fact], [JsonPropertyName], [HttpGet], ...) or by name
            if (symbol.GetAttributes().Length > 0 || PatternMemberNames.Contains(symbol.Name))
                return false;
            if (symbol is INamedTypeSymbol namedType && namedType.GetMembers().Any(m => m.Name is "Main" or "<Main>$"))
                return false;

            // Polyfills such as IsExternalInit or NotNullWhenAttribute are used by the compiler, not by code
            if (symbol.ContainingNamespace?.ToDisplayString() is "System.Runtime.CompilerServices" or "System.Diagnostics.CodeAnalysis")
                return false;

            if (IsInterfaceImplementation(symbol))
                return false;

            // Record positional properties are used through the generated constructor and Deconstruct
            if (symbol is IPropertySymbol && symbol.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is ParameterSyntax))
                return false;

            // Generated code is not the user's to delete
            return symbol.Locations.Any(l => l.IsInSource && !SolutionFiles.IsGenerated(solution, l.SourceTree!));
        }

        private static bool IsVisibleOutsideAssembly(ISymbol symbol)
        {
            for (var current = symbol; current != null && current is not INamespaceSymbol; current = current.ContainingType)
            {
                if (current.DeclaredAccessibility is Accessibility.Private or Accessibility.Internal or Accessibility.ProtectedAndInternal)
                    return false;
            }
            return true;
        }

        private static bool IsInterfaceImplementation(ISymbol symbol)
        {
            if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 }
                or IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 }
                or IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
                return true;

            var type = symbol.ContainingType;
            return type != null && type.AllInterfaces
                .SelectMany(i => i.GetMembers())
                .Any(m => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(m), symbol));
        }

        private static async Task<bool> IsReferencedAsync(ISymbol symbol, Solution solution, CancellationToken cancellationToken)
        {
            // A private member can only be used inside its type's declarations
            IImmutableSet<Document>? documents = null;
            if (symbol.DeclaredAccessibility == Accessibility.Private && symbol.ContainingType != null)
            {
                documents = symbol.ContainingType.Locations
                    .Where(l => l.IsInSource)
                    .Select(l => solution.GetDocument(l.SourceTree))
                    .OfType<Document>()
                    .ToImmutableHashSet();
            }

            // Uses inside the symbol's own declaration (recursion, a factory returning its own type) do not count
            var ownSpans = symbol.DeclaringSyntaxReferences.Select(r => (r.SyntaxTree, r.Span)).ToList();

            var references = await SymbolFinder.FindReferencesAsync(symbol, solution, documents, cancellationToken);
            return references.SelectMany(r => r.Locations).Any(location =>
                !ownSpans.Any(own => own.SyntaxTree == location.Location.SourceTree && own.Span.Contains(location.Location.SourceSpan)));
        }

        private static string FormatUnused(
            Solution solution, string solutionDirectory, List<ISymbol> unused, int checkedCount, bool includePublic, int maxResults)
        {
            if (unused.Count == 0)
                return $"No unused symbols among {checkedCount} candidates ({(includePublic ? "all" : "private and internal")}).";

            var entries = unused
                .Select(s => (Symbol: s, Location: s.Locations.First(l => l.IsInSource)))
                .Select(e => (e.Symbol, File: SolutionFiles.Display(e.Location.SourceTree!.FilePath, solutionDirectory), Line: e.Location.GetLineSpan().StartLinePosition.Line + 1))
                .OrderBy(e => e.File)
                .ThenBy(e => e.Line)
                .ToList();
            var shown = entries.Take(Math.Max(1, maxResults)).ToList();

            var output = new StringBuilder();
            output.AppendLine($"Found {entries.Count} unused symbols out of {checkedCount} checked{(shown.Count < entries.Count ? $", showing {shown.Count}" : "")}.");
            output.AppendLine("No references in the solution; reflection, serialization and callers outside the solution are not visible.");
            foreach (var file in shown.GroupBy(e => e.File))
            {
                output.AppendLine($"**{file.Key}**");
                foreach (var entry in file)
                    output.AppendLine($"  {SymbolSearchService.GetSymbolCategory(entry.Symbol)} `{entry.Symbol.ToDisplayString()}` L{entry.Line}");
            }
            if (shown.Count < entries.Count)
                output.AppendLine($"... and {entries.Count - shown.Count} more. Raise maxResults or pass projectName.");

            return output.ToString();
        }
    }
}
