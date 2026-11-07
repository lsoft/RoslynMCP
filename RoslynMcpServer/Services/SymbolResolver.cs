using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;

namespace RoslynMcpServer.Services
{
    /// <summary>What the agent knows about a symbol: a name, a place in a file, or both.</summary>
    public sealed record SymbolQuery(string? Name, string? FilePath = null, int? Line = null, int? Column = null);

    public sealed record SymbolResolution(IReadOnlyList<ISymbol> Symbols, string? Error)
    {
        public static SymbolResolution Fail(string error) => new(Array.Empty<ISymbol>(), error);
    }

    public static class SymbolResolver
    {
        public static async Task<SymbolResolution> ResolveAsync(
            Solution solution, string solutionDirectory, SymbolQuery query, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(query.FilePath))
                return await ResolveAtPositionAsync(solution, solutionDirectory, query, cancellationToken);

            if (string.IsNullOrWhiteSpace(query.Name))
                return SymbolResolution.Fail("Provide symbolName, or filePath and line (plus column or symbolName).");

            var symbols = await FindByNameAsync(solution, query.Name, cancellationToken);
            return symbols.Count > 0
                ? new SymbolResolution(symbols, null)
                : SymbolResolution.Fail($"Symbol '{query.Name}' not found in the solution's source. Use SearchSymbols with a wildcard pattern to look it up.");
        }

        // Matches the simple name or the fully qualified display name, case-sensitively as C# does
        public static async Task<IReadOnlyList<ISymbol>> FindByNameAsync(
            Solution solution, string symbolName, CancellationToken cancellationToken)
        {
            var symbols = new List<ISymbol>();

            foreach (var project in solution.Projects.Where(p => p.SupportsCompilation))
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation != null)
                {
                    symbols.AddRange(SymbolSearchService.GetSourceSymbols(compilation)
                        .Where(s => s.Name.Equals(symbolName, StringComparison.Ordinal)
                            || s.ToDisplayString().Equals(symbolName, StringComparison.Ordinal)));
                }
            }

            // One copy per target framework is enough: Roslyn cascades a search to the symbol's
            // linked copies in the other target frameworks of the project
            return symbols.DistinctBy(ProjectIdentity.GetSymbolKey).ToList();
        }

        private static async Task<SymbolResolution> ResolveAtPositionAsync(
            Solution solution, string solutionDirectory, SymbolQuery query, CancellationToken cancellationToken)
        {
            if (query.Line is not int line)
                return SymbolResolution.Fail("line is required together with filePath.");

            var match = await SolutionFiles.FindDocumentsAsync(solution, solutionDirectory, query.FilePath!, cancellationToken);
            if (match.Documents.Count == 0)
            {
                return match.Candidates.Count > 0
                    ? SymbolResolution.Fail($"'{query.FilePath}' is ambiguous, candidates: {string.Join(", ", match.Candidates.Take(10))}")
                    : SymbolResolution.Fail($"'{query.FilePath}' is not part of the solution.");
            }

            var document = match.Documents[0];
            var text = await document.GetTextAsync(cancellationToken);
            if (line < 1 || line > text.Lines.Count)
                return SymbolResolution.Fail($"Line {line} is outside {SolutionFiles.Display(document.FilePath, solutionDirectory)} (1-{text.Lines.Count}).");

            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                return SymbolResolution.Fail("The document has no semantic information.");

            var lineSpan = text.Lines[line - 1].Span;
            SyntaxToken token;
            if (query.Column is int column)
            {
                token = root.FindToken(lineSpan.Start + Math.Clamp(column - 1, 0, Math.Max(0, lineSpan.Length - 1)));
            }
            else if (!string.IsNullOrWhiteSpace(query.Name))
            {
                // The agent usually knows the line and the name, not the column
                var simpleName = query.Name.Split('.').Last();
                token = root.DescendantTokens(lineSpan)
                    .FirstOrDefault(t => t.IsKind(SyntaxKind.IdentifierToken) && t.ValueText == simpleName);
                if (token == default)
                    return SymbolResolution.Fail($"'{simpleName}' does not occur on line {line}.");
            }
            else
            {
                return SymbolResolution.Fail("Provide column or symbolName together with filePath and line.");
            }

            var symbol = await SymbolFinder.FindSymbolAtPositionAsync(model, token.SpanStart, solution.Workspace, cancellationToken)
                ?? token.Parent?.AncestorsAndSelf().Select(n => model.GetDeclaredSymbol(n, cancellationToken)).FirstOrDefault(s => s != null);
            if (symbol == null)
                return SymbolResolution.Fail($"No symbol at line {line} near '{token.Text}'.");

            return new SymbolResolution(new[] { Normalize(symbol) }, null);
        }

        // List<int>.Add -> List<T>.Add, "x.Extension()" -> the declared extension method
        private static ISymbol Normalize(ISymbol symbol)
        {
            if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
                symbol = reduced;
            return symbol.OriginalDefinition;
        }
    }
}
