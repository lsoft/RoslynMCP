using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using RoslynMcpServer.Models;
using RoslynMcpServer.Services;
using System.ComponentModel;
using System.Text;

namespace RoslynMcpServer.Tools
{
    [McpServerToolType]
    public class CodeNavigationTools
    {
        [McpServerTool, Description("Search for symbols in C# code using wildcard patterns (* and ?)")]
        public static async Task<string> SearchSymbols(
            [Description("Wildcard pattern to search for (e.g., 'User*', '*Service', 'Get*User')")] string pattern,
            [Description(".sln or .csproj path")] string solutionPath,
            [Description("Comma-separated: class,interface,struct,enum,method,property,field,event,namespace")] string symbolTypes = "class,interface,method,property",
            [Description("Whether to ignore case in search")] bool ignoreCase = true,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var validator = serviceProvider?.GetService<SecurityValidator>();

                // Validate inputs
                if (validator == null || !validator.ValidateSolutionPath(solutionPath))
                {
                    return "Error: Invalid solution path provided.";
                }
                
                var sanitizedPattern = validator?.SanitizeSearchPattern(pattern) ?? pattern;
                
                // Perform search with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromMinutes(5));
                var searchService = serviceProvider?.GetService<SymbolSearchService>();
                if (searchService == null)
                {
                    return "Error: Symbol search service not available.";
                }

                var results = await searchService.SearchSymbolsAsync(
                    sanitizedPattern, solutionPath, symbolTypes, ignoreCase, cts.Token);
                
                return FormatSearchResults(results);
            }
            catch (OperationCanceledException)
            {
                return "Error: Search operation timed out. The codebase may be too large or complex.";
            }
            catch (FileNotFoundException)
            {
                return "Error: Solution file not found. Please check the path and try again.";
            }
            catch (UnauthorizedAccessException)
            {
                return "Error: Access denied. Please check file permissions.";
            }
            catch (Exception ex)
            {
                // Log full error details but return safe message
                var logger = serviceProvider?.GetService<ILogger<CodeNavigationTools>>();
                logger?.LogError(ex, "Unexpected error during symbol search");
                
                return "Error: An unexpected error occurred during the search operation.";
            }
        }

        [McpServerTool, Description("References, implementations (subtypes, overrides), base members or callers of a symbol given by name or exactly by filePath+line")]
        public static Task<string> FindReferences(
            [Description(".sln or .csproj path")] string solutionPath,
            [Description("Name or full name; with filePath+line, the name on that line")] string? symbolName = null,
            [Description("Absolute, solution-relative or unique file name")] string? filePath = null,
            [Description("1-based")] int? line = null,
            [Description("1-based, optional")] int? column = null,
            [Description("references | implementations | base | callers")] string relation = "references",
            [Description("For references")] bool includeDefinition = true,
            int maxResults = 50,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            return ToolRunner.RunAsync(serviceProvider, solutionPath, "finding references", async services =>
            {
                var searchService = services.GetRequiredService<SymbolSearchService>();
                var result = await searchService.FindRelatedAsync(
                    solutionPath, new SymbolQuery(symbolName, filePath, line, column), relation, includeDefinition, cancellationToken);

                if (result.Error != null)
                    return $"Error: {result.Error}";

                var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);
                var header = FormatTargets(result.Targets);
                return result.Relation == "references"
                    ? header + FormatReferenceResults(result.References, maxResults, solutionDirectory)
                    : header + FormatRelatedItems(result, maxResults);
            });
        }

        [McpServerTool, Description("Signature, docs and location of a symbol. With filePath+line it resolves any symbol used there, including library APIs (go to definition)")]
        public static Task<string> GetSymbolInfo(
            [Description(".sln or .csproj path")] string solutionPath,
            [Description("Name or full name; with filePath+line, the name on that line")] string? symbolName = null,
            [Description("Absolute, solution-relative or unique file name")] string? filePath = null,
            [Description("1-based")] int? line = null,
            [Description("1-based, optional")] int? column = null,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            return ToolRunner.RunAsync(serviceProvider, solutionPath, "getting symbol information", async services =>
            {
                var searchService = services.GetRequiredService<SymbolSearchService>();
                var (info, error) = await searchService.DescribeSymbolAsync(
                    solutionPath, new SymbolQuery(symbolName, filePath, line, column), cancellationToken);
                return error != null ? $"Error: {error}" : FormatSymbolInfo(info);
            });
        }

        // Name lookups can match several symbols; say which ones the results cover
        private static string FormatTargets(List<string> targets)
        {
            return targets.Count > 1
                ? $"Matched {targets.Count} symbols: {string.Join(", ", targets.Take(5))}{(targets.Count > 5 ? ", ..." : "")}. Pass filePath + line to pick one.\n\n"
                : "";
        }

        private static string FormatRelatedItems(RelationResult result, int maxResults)
        {
            var output = new StringBuilder();
            var what = result.Relation switch
            {
                "implementations" => "implementations",
                "base" => "base types or members",
                _ => "callers"
            };
            var target = result.Targets.Count == 1 ? $" of `{result.Targets[0]}`" : "";
            var shown = result.Items.Take(Math.Max(1, maxResults)).ToList();

            output.AppendLine($"Found {result.Items.Count} {what}{target}:");
            foreach (var item in shown)
            {
                var note = item.Note != null ? $" ({item.Note})" : "";
                output.AppendLine($"  • {item.Kind} `{item.FullName}` at {item.Location}{note}");
                foreach (var callSite in item.Lines.Take(5))
                    output.AppendLine($"      {callSite}");
                if (item.Lines.Count > 5)
                    output.AppendLine($"      ... and {item.Lines.Count - 5} more calls");
            }

            if (result.Items.Count > shown.Count)
                output.AppendLine($"... and {result.Items.Count - shown.Count} more. Increase maxResults to list them.");

            return output.ToString();
        }

        [McpServerTool, Description("Analyze project dependencies and symbol usage patterns")]
        public static async Task<string> AnalyzeDependencies(
            [Description("Path to solution file (.sln)")] string solutionPath,
            [Description("Maximum depth for dependency analysis")] int maxDepth = 3,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var validator = serviceProvider?.GetService<SecurityValidator>();
                if (validator == null || !validator.ValidateSolutionPath(solutionPath))
                {
                    return "Error: Invalid solution path provided.";
                }
                
                var analysisService = serviceProvider?.GetService<CodeAnalysisService>();
                if (analysisService == null)
                {
                    return "Error: Code analysis service not available.";
                }
                
                var dependencies = await analysisService.AnalyzeDependenciesAsync(solutionPath, maxDepth, cancellationToken);
                return FormatDependencyAnalysis(dependencies);
            }
            catch (Exception ex)
            {
                var logger = serviceProvider?.GetService<ILogger<CodeNavigationTools>>();
                logger?.LogError(ex, "Error analyzing dependencies");
                return "Error: An unexpected error occurred during dependency analysis.";
            }
        }

        [McpServerTool, Description("Analyze code complexity and identify high-complexity methods")]
        public static async Task<string> AnalyzeCodeComplexity(
            [Description("Path to solution file")] string solutionPath,
            [Description("Complexity threshold (1-10)")] int threshold = 5,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var validator = serviceProvider?.GetService<SecurityValidator>();
                if (validator == null || !validator.ValidateSolutionPath(solutionPath))
                {
                    return "Error: Invalid solution path provided.";
                }
                
                var analysisService = serviceProvider?.GetService<CodeAnalysisService>();
                if (analysisService == null)
                {
                    return "Error: Code analysis service not available.";
                }
                
                var solution = await analysisService.GetSolutionAsync(solutionPath, cancellationToken);
                var complexityResults = new List<ComplexityResult>();

                // Syntax trees are enough here, no need to build compilations. A multi-targeted project
                // holds the same file once per target framework; #if may make the copies differ, so
                // each method is reported once with its highest complexity.
                var documentsByFile = solution.Projects
                    .SelectMany(p => p.Documents)
                    .GroupBy(d => d.FilePath ?? d.Id.Id.ToString());

                foreach (var copies in documentsByFile)
                {
                    var fileResults = new List<ComplexityResult>();
                    foreach (var document in copies)
                    {
                        var root = await document.GetSyntaxRootAsync(cancellationToken);
                        if (root == null) continue;

                        fileResults.AddRange(ComplexityCalculator.Analyze(root, document.FilePath ?? document.Name, threshold));
                    }

                    complexityResults.AddRange(fileResults
                        .GroupBy(r => (r.LineNumber, r.MethodName))
                        .Select(g => g.MaxBy(r => r.Complexity)!));
                }

                return FormatComplexityResults(complexityResults);
            }
            catch (Exception ex)
            {
                var logger = serviceProvider?.GetService<ILogger<CodeNavigationTools>>();
                logger?.LogError(ex, "Error analyzing code complexity");
                return "Error: An unexpected error occurred during complexity analysis.";
            }
        }

        private static string FormatSearchResults(IEnumerable<SymbolSearchResult> results)
        {
            var materialized = results.ToList();
            var grouped = materialized.GroupBy(r => r.Category);
            var output = new StringBuilder();

            output.AppendLine($"Found {materialized.Count} symbols:\n");
            
            foreach (var group in grouped.OrderBy(g => g.Key))
            {
                output.AppendLine($"**{group.Key}** ({group.Count()}):");
                foreach (var result in group.Take(20)) // Limit results
                {
                    output.AppendLine($"  • `{result.Name}` in {result.Location}");
                    if (!string.IsNullOrEmpty(result.Summary))
                        output.AppendLine($"    {result.Summary}");
                }
                if (group.Count() > 20)
                    output.AppendLine($"    ... and {group.Count() - 20} more");
                output.AppendLine();
            }
            
            return output.ToString();
        }

        internal static string FormatReferenceResults(IEnumerable<ReferenceResult> results, int maxResults, string solutionDirectory)
        {
            var materialized = results.ToList();

            // A popular symbol can have thousands of references; list a bounded number, definitions first,
            // and summarize the rest per file
            var shown = materialized
                .OrderByDescending(r => r.IsDefinition)
                .ThenBy(r => r.DocumentPath)
                .ThenBy(r => r.LineNumber)
                .Take(Math.Max(1, maxResults))
                .ToList();

            var output = new StringBuilder();
            output.AppendLine(shown.Count < materialized.Count
                ? $"Found {materialized.Count} references, showing {shown.Count}:\n"
                : $"Found {materialized.Count} references:\n");

            var groupedByFile = shown.GroupBy(r => r.DocumentPath);
            
            foreach (var fileGroup in groupedByFile.OrderBy(g => g.Key))
            {
                output.AppendLine($"**{SolutionFiles.Display(fileGroup.Key, solutionDirectory)}** ({fileGroup.Count()} references):");
                
                foreach (var reference in fileGroup.OrderBy(r => r.LineNumber))
                {
                    var refType = reference.IsDefinition ? "Definition" : reference.ReferenceKind;
                    output.AppendLine($"  • Line {reference.LineNumber}: {refType}");
                    output.AppendLine($"    `{reference.LineText.Trim()}`");
                }
                output.AppendLine();
            }
            
            var hidden = materialized.Except(shown).ToList();
            if (hidden.Count > 0)
            {
                var hiddenByFile = hidden
                    .GroupBy(r => r.DocumentPath)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key)
                    .ToList();

                output.AppendLine($"... and {hidden.Count} more in {hiddenByFile.Count} files:");
                foreach (var fileGroup in hiddenByFile.Take(10))
                    output.AppendLine($"  • {SolutionFiles.Display(fileGroup.Key, solutionDirectory)}: {fileGroup.Count()}");
                if (hiddenByFile.Count > 10)
                    output.AppendLine($"  ... and {hiddenByFile.Count - 10} more files");
                output.AppendLine("Increase maxResults to list them.");
            }

            return output.ToString();
        }

        private static string FormatSymbolInfo(RoslynMcpServer.Models.SymbolInfo? info)
        {
            if (info == null)
                return "Symbol not found.";
            
            var output = new StringBuilder();
            output.AppendLine($"**{info.Name}** ({info.Kind})");
            output.AppendLine($"Full Name: `{info.FullName}`");
            output.AppendLine($"Accessibility: {info.Accessibility}");
            
            if (!string.IsNullOrEmpty(info.Namespace))
                output.AppendLine($"Namespace: {info.Namespace}");
            
            if (!string.IsNullOrEmpty(info.DeclaringType))
                output.AppendLine($"Declaring Type: {info.DeclaringType}");
            
            if (!string.IsNullOrEmpty(info.ReturnType))
                output.AppendLine($"Return Type: {info.ReturnType}");
            
            if (info.Parameters.Any())
            {
                output.AppendLine("Parameters:");
                foreach (var param in info.Parameters)
                    output.AppendLine($"  • {param}");
            }
            
            if (info.Attributes.Any())
            {
                output.AppendLine("Attributes:");
                foreach (var attr in info.Attributes)
                    output.AppendLine($"  • {attr}");
            }
            
            if (!string.IsNullOrEmpty(info.SourceLocation))
                output.AppendLine($"Location: {info.SourceLocation}");

            if (!string.IsNullOrEmpty(info.Documentation))
                output.AppendLine($"Summary: {Truncate(info.Documentation, 400)}");

            if (info.OtherMatches.Any())
            {
                output.AppendLine($"Also matches ({info.OtherMatches.Count}, pass filePath + line to pick one):");
                foreach (var match in info.OtherMatches.Take(10))
                    output.AppendLine($"  • {match}");
            }

            return output.ToString();
        }

        private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";

        private static string FormatDependencyAnalysis(DependencyAnalysis analysis)
        {
            var output = new StringBuilder();
            output.AppendLine($"**Dependency Analysis for {analysis.ProjectName}**\n");
            
            output.AppendLine($"Symbol Summary:");
            output.AppendLine($"  • Total Symbols: {analysis.TotalSymbols}");
            output.AppendLine($"  • Public Symbols: {analysis.PublicSymbols}");
            output.AppendLine($"  • Internal Symbols: {analysis.InternalSymbols}");
            output.AppendLine();
            
            if (analysis.Dependencies.Any())
            {
                output.AppendLine("Dependencies:");
                var groupedDeps = analysis.Dependencies.GroupBy(d => d.Type);
                foreach (var group in groupedDeps)
                {
                    output.AppendLine($"  **{group.Key}** ({group.Count()}):");
                    foreach (var dep in group.Take(10))
                        output.AppendLine($"    • {dep.Name}");
                    if (group.Count() > 10)
                        output.AppendLine($"    ... and {group.Count() - 10} more");
                }
                output.AppendLine();
            }
            
            if (analysis.NamespaceUsages.Any())
            {
                output.AppendLine("Top Namespace Usages:");
                foreach (var usage in analysis.NamespaceUsages.OrderByDescending(n => n.UsageCount).Take(10))
                    output.AppendLine($"  • {usage.Namespace}: {usage.UsageCount} usages");
            }
            
            return output.ToString();
        }

        private static string FormatComplexityResults(List<ComplexityResult> results)
        {
            var output = new StringBuilder();
            output.AppendLine($"**Code Complexity Analysis**\n");
            output.AppendLine($"Found {results.Count} methods with high complexity:\n");
            
            foreach (var result in results.OrderByDescending(r => r.Complexity).Take(20))
            {
                output.AppendLine($"**{result.ClassName}.{result.MethodName}** (Complexity: {result.Complexity})");
                output.AppendLine($"  Location: {result.FileName}:{result.LineNumber}");
                if (!string.IsNullOrEmpty(result.Namespace))
                    output.AppendLine($"  Namespace: {result.Namespace}");
                output.AppendLine();
            }
            
            if (results.Count > 20)
                output.AppendLine($"... and {results.Count - 20} more methods with high complexity");
            
            return output.ToString();
        }
    }
}
