using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Models;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SymbolInfo = RoslynMcpServer.Models.SymbolInfo;

namespace RoslynMcpServer.Services
{
    public class SymbolSearchService
    {
        public static readonly string[] Relations = { "references", "implementations", "base", "callers" };

        private readonly CodeAnalysisService _codeAnalysis;
        private readonly ILogger<SymbolSearchService> _logger;
        private readonly IMemoryCache _cache;

        public SymbolSearchService(CodeAnalysisService codeAnalysis,
            ILogger<SymbolSearchService> logger, IMemoryCache cache)
        {
            _codeAnalysis = codeAnalysis;
            _logger = logger;
            _cache = cache;
        }

        public async Task<IEnumerable<SymbolSearchResult>> SearchSymbolsAsync(
            string pattern, string solutionPath, string symbolTypes, bool ignoreCase,
            CancellationToken cancellationToken = default)
        {
            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);
            var typeFilter = ParseSymbolTypes(symbolTypes);
            var regex = CreateWildcardRegex(pattern, ignoreCase);

            var results = new List<SymbolSearchResult>();

            // Search across all projects in parallel
            var searchTasks = solution.Projects
                .Where(p => p.SupportsCompilation)
                .Select(project => SearchProjectSymbolsAsync(project, solutionDirectory, regex, typeFilter, cancellationToken));

            var projectResults = await Task.WhenAll(searchTasks);

            foreach (var projectResult in projectResults)
                results.AddRange(projectResult);

            // Sort by relevance score; a multi-targeted project yields every symbol once per target framework
            return results
                .DistinctBy(r => (r.SymbolKind, r.FullName, r.FilePath, r.LineNumber))
                .OrderByDescending(r => CalculateRelevanceScore(r, pattern))
                .ThenBy(r => r.Name)
                .ToList();
        }

        private async Task<IEnumerable<SymbolSearchResult>> SearchProjectSymbolsAsync(
            Project project, string solutionDirectory, Regex pattern, HashSet<SymbolKind> typeFilter, CancellationToken cancellationToken)
        {
            var results = new List<SymbolSearchResult>();

            try
            {
                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) return results;

                var symbols = GetFilteredSymbols(compilation, typeFilter);

                foreach (var symbol in symbols)
                {
                    if (pattern.IsMatch(symbol.Name) || pattern.IsMatch(symbol.ToDisplayString()))
                    {
                        results.Add(CreateSearchResult(symbol, project, solutionDirectory));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error searching project: {ProjectName}", project.Name);
            }

            return results;
        }

        internal static Regex CreateWildcardRegex(string pattern, bool ignoreCase)
        {
            // Convert wildcard pattern to regex
            var regexPattern = Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".");

            // Not RegexOptions.Compiled: the regex is used once per request, compiling costs more than it saves
            var options = RegexOptions.CultureInvariant;
            if (ignoreCase) options |= RegexOptions.IgnoreCase;

            return new Regex($"^{regexPattern}$", options);
        }

        private HashSet<SymbolKind> ParseSymbolTypes(string symbolTypes)
        {
            var types = new HashSet<SymbolKind>();

            foreach (var type in symbolTypes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (type.Trim().ToLower())
                {
                    case "class":
                    case "interface":
                    case "struct":
                    case "enum":
                        types.Add(SymbolKind.NamedType);
                        break;
                    case "method": types.Add(SymbolKind.Method); break;
                    case "property": types.Add(SymbolKind.Property); break;
                    case "field": types.Add(SymbolKind.Field); break;
                    case "event": types.Add(SymbolKind.Event); break;
                    case "namespace": types.Add(SymbolKind.Namespace); break;
                }
            }

            return types;
        }

        private IEnumerable<ISymbol> GetFilteredSymbols(Compilation compilation, HashSet<SymbolKind> typeFilter)
        {
            return GetSourceSymbols(compilation)
                .Where(s => typeFilter.Contains(s.Kind));
        }

        // Symbols declared in the project's own source. compilation.GlobalNamespace would also walk
        // every referenced assembly (the whole BCL) and repeat symbols of referenced projects.
        public static IEnumerable<ISymbol> GetSourceSymbols(Compilation compilation)
        {
            return GetAllSymbolsRecursive(compilation.Assembly.GlobalNamespace)
                .Where(s => !s.IsImplicitlyDeclared && s.CanBeReferencedByName);
        }

        private static IEnumerable<ISymbol> GetAllSymbolsRecursive(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                yield return member;

                if (member is INamespaceOrTypeSymbol nested)
                {
                    foreach (var nestedMember in GetAllSymbolsRecursive(nested))
                        yield return nestedMember;
                }
            }
        }

        private SymbolSearchResult CreateSearchResult(ISymbol symbol, Project project, string solutionDirectory)
        {
            var location = symbol.Locations.FirstOrDefault();
            var lineNumber = location?.GetLineSpan().StartLinePosition.Line + 1 ?? 0;

            return new SymbolSearchResult
            {
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                Category = GetSymbolCategory(symbol),
                Location = SolutionFiles.FormatLocation(project.Solution, location, solutionDirectory, symbol),
                ProjectName = ProjectIdentity.GetDisplayName(project),
                FilePath = location?.SourceTree?.FilePath ?? "",
                LineNumber = lineNumber,
                Summary = GetSymbolSummary(symbol),
                Accessibility = symbol.DeclaredAccessibility.ToString().ToLower(),
                SymbolKind = symbol.Kind,
                Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? ""
            };
        }

        internal static string GetSymbolCategory(ISymbol symbol)
        {
            return symbol switch
            {
                INamedTypeSymbol namedType => namedType.TypeKind.ToString(),
                IMethodSymbol => "Method",
                IPropertySymbol => "Property",
                IFieldSymbol => "Field",
                IEventSymbol => "Event",
                INamespaceSymbol => "Namespace",
                _ => symbol.Kind.ToString()
            };
        }

        private static string TypeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        private static string GetSymbolSummary(ISymbol symbol)
        {
            return symbol switch
            {
                IMethodSymbol method => $"({string.Join(", ", method.Parameters.Select(p => $"{TypeName(p.Type)} {p.Name}"))})",
                IPropertySymbol property => $": {TypeName(property.Type)}",
                IFieldSymbol field => $": {TypeName(field.Type)}",
                INamedTypeSymbol type => $"{type.TypeKind} with {type.GetMembers().Length} members",
                _ => ""
            };
        }

        private double CalculateRelevanceScore(SymbolSearchResult result, string searchPattern)
        {
            double score = 0;

            // Exact match gets highest score
            if (result.Name.Equals(searchPattern.Replace("*", "").Replace("?", ""),
                StringComparison.OrdinalIgnoreCase))
                score += 100;

            // Prefix match
            if (result.Name.StartsWith(searchPattern.Replace("*", ""),
                StringComparison.OrdinalIgnoreCase))
                score += 50;

            // Length penalty (shorter names are more relevant)
            score -= result.Name.Length * 0.1;

            // Public accessibility bonus
            if (result.Accessibility == "public")
                score += 10;

            return score;
        }

        public async Task<IEnumerable<ReferenceResult>> FindReferencesAsync(
            string symbolName, string solutionPath, bool includeDefinition,
            CancellationToken cancellationToken = default)
        {
            var result = await FindRelatedAsync(solutionPath, new SymbolQuery(symbolName), "references", includeDefinition, cancellationToken);
            return result.References;
        }

        /// <summary>
        /// references: usages; implementations: derived types, overrides, interface implementations;
        /// base: base types, overridden and implemented interface members; callers: calling members.
        /// </summary>
        public async Task<RelationResult> FindRelatedAsync(
            string solutionPath, SymbolQuery query, string relation, bool includeDefinition,
            CancellationToken cancellationToken = default)
        {
            relation = string.IsNullOrWhiteSpace(relation) ? "references" : relation.Trim().ToLowerInvariant();
            if (!Relations.Contains(relation))
                return new RelationResult { Error = $"Unknown relation '{relation}', use one of: {string.Join(", ", Relations)}." };

            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);
            var resolution = await SymbolResolver.ResolveAsync(solution, solutionDirectory, query, cancellationToken);
            var result = new RelationResult { Relation = relation, Error = resolution.Error };
            if (resolution.Error != null)
                return result;

            result.Targets = resolution.Symbols.Select(s => s.ToDisplayString()).ToList();

            switch (relation)
            {
                case "references":
                    result.References = await CollectReferencesAsync(solution, resolution.Symbols, includeDefinition, cancellationToken);
                    break;

                case "callers":
                    result.Items = await CollectCallersAsync(solution, solutionDirectory, resolution.Symbols, cancellationToken);
                    break;

                default:
                    var related = new List<ISymbol>();
                    foreach (var symbol in resolution.Symbols)
                    {
                        related.AddRange(relation == "base"
                            ? await FindBaseAsync(symbol, solution, cancellationToken)
                            : await FindImplementationsAsync(symbol, solution, cancellationToken));
                    }

                    result.Items = related
                        .Select(s => s.OriginalDefinition)
                        .DistinctBy(ProjectIdentity.GetSymbolKey)
                        .Select(s => CreateRelatedItem(solution, solutionDirectory, s))
                        .ToList();
                    break;
            }

            return result;
        }

        private async Task<List<ReferenceResult>> CollectReferencesAsync(
            Solution solution, IReadOnlyList<ISymbol> targets, bool includeDefinition, CancellationToken cancellationToken)
        {
            var allReferences = new List<ReferenceResult>();

            foreach (var symbol in targets)
            {
                // ReferencedSymbol.Locations holds references only, definitions have to be added explicitly
                if (includeDefinition)
                {
                    foreach (var definition in symbol.Locations.Where(l => l.IsInSource))
                    {
                        var document = await SolutionFiles.FindDocumentAsync(solution, definition.SourceTree!, cancellationToken);
                        if (document == null) continue;

                        allReferences.Add(await CreateReferenceResultAsync(document, definition, symbol, isDefinition: true, cancellationToken));
                    }
                }

                var references = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken);

                foreach (var referencedSymbol in references)
                {
                    foreach (var location in referencedSymbol.Locations)
                    {
                        allReferences.Add(await CreateReferenceResultAsync(
                            location.Document, location.Location, symbol, isDefinition: false, cancellationToken));
                    }
                }
            }

            return allReferences
                .GroupBy(r => $"{r.DocumentPath}:{r.LineNumber}")
                .Select(g => g.First()) // Remove duplicates, definitions were added first and win
                .OrderBy(r => r.DocumentPath)
                .ThenBy(r => r.LineNumber)
                .ToList();
        }

        private static async Task<List<RelatedItem>> CollectCallersAsync(
            Solution solution, string solutionDirectory, IReadOnlyList<ISymbol> targets, CancellationToken cancellationToken)
        {
            var items = new Dictionary<string, RelatedItem>();

            foreach (var symbol in targets)
            {
                foreach (var caller in await SymbolFinder.FindCallersAsync(symbol, solution, cancellationToken))
                {
                    var key = ProjectIdentity.GetSymbolKey(caller.CallingSymbol);
                    if (!items.TryGetValue(key, out var item))
                    {
                        item = CreateRelatedItem(solution, solutionDirectory, caller.CallingSymbol);
                        item.Note = caller.IsDirect ? null : "through an override or interface";
                        items[key] = item;
                    }

                    foreach (var location in caller.Locations.Where(l => l.IsInSource))
                    {
                        var line = location.GetLineSpan().StartLinePosition.Line;
                        var text = location.SourceTree!.GetText(cancellationToken).Lines[line].ToString().Trim();
                        var entry = $"L{line + 1}: {text}";
                        if (!item.Lines.Contains(entry))
                            item.Lines.Add(entry);
                    }
                }
            }

            return items.Values.OrderBy(i => i.Location).ToList();
        }

        private static async Task<IEnumerable<ISymbol>> FindImplementationsAsync(
            ISymbol symbol, Solution solution, CancellationToken cancellationToken)
        {
            if (symbol is INamedTypeSymbol type)
            {
                if (type.TypeKind == TypeKind.Interface)
                {
                    return (await SymbolFinder.FindImplementationsAsync(type, solution, transitive: true, cancellationToken: cancellationToken))
                        .Concat<ISymbol>(await SymbolFinder.FindDerivedInterfacesAsync(type, solution, transitive: true, cancellationToken: cancellationToken));
                }

                return await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancellationToken);
            }

            // Overrides of virtual/abstract members and implementations of interface members
            return (await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken))
                .Concat(await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken));
        }

        private static async Task<IEnumerable<ISymbol>> FindBaseAsync(
            ISymbol symbol, Solution solution, CancellationToken cancellationToken)
        {
            var result = new List<ISymbol>();

            if (symbol is INamedTypeSymbol type)
            {
                for (var baseType = type.BaseType; baseType != null && !IsImplicitBase(baseType); baseType = baseType.BaseType)
                    result.Add(baseType);
                result.AddRange(type.AllInterfaces);
                return result;
            }

            for (var overridden = GetOverridden(symbol); overridden != null; overridden = GetOverridden(overridden))
                result.Add(overridden);
            result.AddRange(await SymbolFinder.FindImplementedInterfaceMembersAsync(symbol, solution, cancellationToken: cancellationToken));

            // Interface members are mapped to the member that first declared the implementation:
            // Square.Area overrides Shape.Area, which is what implements IShape.Area
            foreach (var member in result.Where(r => r is not INamedTypeSymbol).Prepend(symbol).ToList())
            {
                if (member.ContainingType is not { } containingType)
                    continue;

                result.AddRange(containingType.AllInterfaces
                    .SelectMany(i => i.GetMembers())
                    .Where(m => SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(m), member)));
            }
            return result;
        }

        private static bool IsImplicitBase(INamedTypeSymbol type)
        {
            return type.SpecialType is SpecialType.System_Object or SpecialType.System_ValueType or SpecialType.System_Enum;
        }

        private static ISymbol? GetOverridden(ISymbol symbol) => symbol switch
        {
            IMethodSymbol method => method.OverriddenMethod,
            IPropertySymbol property => property.OverriddenProperty,
            IEventSymbol @event => @event.OverriddenEvent,
            _ => null
        };

        private static RelatedItem CreateRelatedItem(Solution solution, string solutionDirectory, ISymbol symbol)
        {
            return new RelatedItem
            {
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                Kind = GetSymbolCategory(symbol),
                Location = SolutionFiles.FormatLocation(solution, symbol.Locations.FirstOrDefault(l => l.IsInSource), solutionDirectory, symbol)
            };
        }

        private async Task<ReferenceResult> CreateReferenceResultAsync(
            Document document, Location location, ISymbol symbol, bool isDefinition,
            CancellationToken cancellationToken)
        {
            var sourceText = await document.GetTextAsync(cancellationToken);
            var lineSpan = location.GetLineSpan();

            // Get surrounding context
            var lineNumber = lineSpan.StartLinePosition.Line;
            var line = sourceText.Lines[lineNumber];
            var contextStart = Math.Max(0, lineNumber - 2);
            var contextEnd = Math.Min(sourceText.Lines.Count - 1, lineNumber + 2);

            var context = sourceText.Lines
                .Skip(contextStart)
                .Take(contextEnd - contextStart + 1)
                .Select((l, i) => $"{contextStart + i + 1,4}: {l}")
                .ToList();

            return new ReferenceResult
            {
                SymbolName = symbol.Name,
                DocumentPath = document.FilePath ?? "",
                ProjectName = ProjectIdentity.GetDisplayName(document.Project),
                LineNumber = lineNumber + 1,
                ColumnNumber = lineSpan.StartLinePosition.Character + 1,
                LineText = line.ToString(),
                Context = context,
                IsDefinition = isDefinition,
                ReferenceKind = DetermineReferenceKind(location, symbol)
            };
        }

        private string DetermineReferenceKind(Location location, ISymbol symbol)
        {
            // This is a simplified implementation
            // A more sophisticated version would analyze the syntax context
            return symbol.Kind switch
            {
                SymbolKind.Method => "Method Call",
                SymbolKind.Property => "Property Access",
                SymbolKind.Field => "Field Access",
                SymbolKind.NamedType => "Type Reference",
                _ => "Reference"
            };
        }

        public async Task<SymbolInfo?> GetSymbolInfoAsync(
            string symbolName, string solutionPath, CancellationToken cancellationToken = default)
        {
            return (await DescribeSymbolAsync(solutionPath, new SymbolQuery(symbolName), cancellationToken)).Info;
        }

        /// <summary>
        /// Describes the symbol behind a name or a position. Positions also resolve symbols from
        /// referenced assemblies, which makes this "go to definition" for external APIs.
        /// </summary>
        public async Task<(SymbolInfo? Info, string? Error)> DescribeSymbolAsync(
            string solutionPath, SymbolQuery query, CancellationToken cancellationToken = default)
        {
            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);
            var resolution = await SymbolResolver.ResolveAsync(solution, solutionDirectory, query, cancellationToken);
            if (resolution.Error != null)
                return (null, resolution.Error);

            var symbol = resolution.Symbols[0];
            var info = new SymbolInfo
            {
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                Kind = symbol.Kind.ToString(),
                Accessibility = symbol.DeclaredAccessibility.ToString(),
                DeclaringType = symbol.ContainingType?.Name ?? "",
                Namespace = symbol.ContainingNamespace?.ToDisplayString() ?? "",
                Assembly = symbol.ContainingAssembly?.Name ?? "",
                Documentation = GetDocumentationSummary(symbol)
            };

            // Add method-specific information
            if (symbol is IMethodSymbol method)
            {
                info.Parameters = method.Parameters.Select(p => $"{TypeName(p.Type)} {p.Name}").ToList();
                info.ReturnType = TypeName(method.ReturnType);
            }

            // Add property-specific information
            if (symbol is IPropertySymbol property)
            {
                info.ReturnType = TypeName(property.Type);
            }

            // Add attributes
            info.Attributes = symbol.GetAttributes()
                .Select(attr => attr.AttributeClass?.Name ?? "")
                .Where(name => !string.IsNullOrEmpty(name))
                .ToList();

            // Every declaration (partial types have several), or the assembly for external symbols
            var sourceLocations = symbol.Locations.Where(l => l.IsInSource).ToList();
            info.SourceLocation = sourceLocations.Count > 0
                ? string.Join(", ", sourceLocations.Select(l => SolutionFiles.FormatLocation(solution, l, solutionDirectory, symbol)))
                : SolutionFiles.FormatLocation(solution, null, solutionDirectory, symbol);

            // A by-name lookup can be ambiguous; tell the agent which other symbols matched
            info.OtherMatches = resolution.Symbols.Skip(1)
                .Select(s => $"{s.ToDisplayString()} at {SolutionFiles.FormatLocation(solution, s.Locations.FirstOrDefault(l => l.IsInSource), solutionDirectory, s)}")
                .ToList();

            return (info, null);
        }

        // The <summary> text of the XML documentation, whitespace collapsed
        private static string GetDocumentationSummary(ISymbol symbol)
        {
            var xml = symbol.GetDocumentationCommentXml();
            if (string.IsNullOrWhiteSpace(xml))
                return "";

            try
            {
                var summary = XElement.Parse(xml).Element("summary");
                if (summary == null)
                    return "";

                // <see cref="T:System.String"/> -> String
                foreach (var reference in summary.Descendants().Where(e => e.Attribute("cref") != null).ToList())
                    reference.ReplaceWith(reference.Attribute("cref")!.Value.Split('.', ':').Last());

                return Regex.Replace(summary.Value, @"\s+", " ").Trim();
            }
            catch (System.Xml.XmlException)
            {
                return "";
            }
        }
    }
}
