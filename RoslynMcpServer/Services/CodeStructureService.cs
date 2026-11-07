using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynMcpServer.Services
{
    /// <summary>
    /// Compact views of code: the outline of a file or type (signatures and lines instead of bodies)
    /// and source-generated files, which have no file on disk the agent could read.
    /// </summary>
    public class CodeStructureService
    {
        private const int MaxOutlineLines = 400;

        private static readonly SymbolDisplayFormat MemberFormat = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters | SymbolDisplayGenericsOptions.IncludeVariance,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType
                | SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers
                | SymbolDisplayMemberOptions.IncludeRef | SymbolDisplayMemberOptions.IncludeConstantValue,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName
                | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue
                | SymbolDisplayParameterOptions.IncludeExtensionThis,
            propertyStyle: SymbolDisplayPropertyStyle.ShowReadWriteDescriptor,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

        private static readonly SymbolDisplayFormat TypeNameFormat = new(
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

        private readonly CodeAnalysisService _codeAnalysis;

        public CodeStructureService(CodeAnalysisService codeAnalysis)
        {
            _codeAnalysis = codeAnalysis;
        }

        public sealed record OutlineOptions(bool IncludePrivate = true, bool IncludeDocs = true);

        /// <summary>Outline of a file (filePath) or of a type across all its partial declarations (typeName).</summary>
        public async Task<string> GetOutlineAsync(
            string solutionPath, string? filePath, string? typeName, OutlineOptions options, CancellationToken cancellationToken)
        {
            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);

            if (!string.IsNullOrWhiteSpace(filePath))
                return await GetFileOutlineAsync(solution, solutionDirectory, filePath, options, cancellationToken);

            if (!string.IsNullOrWhiteSpace(typeName))
                return await GetTypeOutlineAsync(solution, solutionDirectory, typeName, options, cancellationToken);

            return "Error: Provide filePath or typeName.";
        }

        private static async Task<string> GetFileOutlineAsync(
            Solution solution, string solutionDirectory, string filePath, OutlineOptions options, CancellationToken cancellationToken)
        {
            var match = await SolutionFiles.FindDocumentsAsync(solution, solutionDirectory, filePath, cancellationToken);
            if (match.Documents.Count == 0)
            {
                return match.Candidates.Count > 0
                    ? $"Error: '{filePath}' is ambiguous, candidates: {string.Join(", ", match.Candidates.Take(10))}"
                    : $"Error: '{filePath}' is not part of the solution.";
            }

            // Target-framework copies share the file; the first one is representative (#if aside)
            var document = match.Documents[0];
            var root = await document.GetSyntaxRootAsync(cancellationToken);
            var model = await document.GetSemanticModelAsync(cancellationToken);
            if (root == null || model == null)
                return "Error: The document has no semantic information.";

            var lines = new List<string>
            {
                $"{DisplayDocument(document, solutionDirectory)} ({ProjectIdentity.GetDisplayName(document.Project)}, {root.GetLocation().GetLineSpan().EndLinePosition.Line + 1} lines)"
            };
            AppendNodes(lines, root.ChildNodes(), model, 0, options, cancellationToken);
            return Finish(lines);
        }

        private static async Task<string> GetTypeOutlineAsync(
            Solution solution, string solutionDirectory, string typeName, OutlineOptions options, CancellationToken cancellationToken)
        {
            var types = (await SymbolResolver.FindByNameAsync(solution, typeName, cancellationToken))
                .OfType<INamedTypeSymbol>()
                .ToList();

            if (types.Count == 0)
                return $"Error: Type '{typeName}' not found in the solution's source.";

            if (types.Count > 1)
            {
                var candidates = types.Select(t => $"  • {t.ToDisplayString()} at {SolutionFiles.FormatLocation(solution, t.Locations.FirstOrDefault(), solutionDirectory, t)}");
                return $"'{typeName}' matches {types.Count} types, pass the fully qualified name:\n{string.Join("\n", candidates)}";
            }

            var type = types[0];
            var lines = new List<string>();
            var declarations = type.Locations.Where(l => l.IsInSource).ToList();
            lines.Add($"{FormatTypeHeader(type)}  // {string.Join(", ", declarations.Select(l => SolutionFiles.FormatLocation(solution, l, solutionDirectory, type)))}");
            var docs = options.IncludeDocs ? DocSummary(type) : "";
            if (docs != "")
                lines.Add($"  // {docs}");

            // Members of every partial declaration, each with its own location when the type spans files
            var multipleFiles = declarations.Select(l => l.SourceTree).Distinct().Count() > 1;
            foreach (var member in type.GetMembers().Where(m => IsOutlined(m, options)).OrderBy(m => m.Locations.FirstOrDefault()?.SourceTree?.FilePath).ThenBy(m => m.Locations.FirstOrDefault()?.SourceSpan.Start))
            {
                var location = member.Locations.FirstOrDefault(l => l.IsInSource);
                var where = multipleFiles
                    ? SolutionFiles.FormatLocation(solution, location, solutionDirectory, member)
                    : $"L{location?.GetLineSpan().StartLinePosition.Line + 1}";
                lines.Add($"  {FormatMember(member)}  {where}{DocSuffix(member, options)}");
            }

            return Finish(lines);
        }

        private static void AppendNodes(
            List<string> lines, IEnumerable<SyntaxNode> nodes, SemanticModel model, int depth, OutlineOptions options, CancellationToken cancellationToken)
        {
            var indent = new string(' ', depth * 2);
            var topLevelStatements = false;

            foreach (var node in nodes)
            {
                switch (node)
                {
                    case BaseNamespaceDeclarationSyntax ns:
                        lines.Add($"{indent}namespace {ns.Name}");
                        AppendNodes(lines, ns.Members, model, depth + 1, options, cancellationToken);
                        break;

                    case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                        if (model.GetDeclaredSymbol(node, cancellationToken) is not INamedTypeSymbol type || !IsVisible(type, options))
                            break;

                        lines.Add($"{indent}{FormatTypeHeader(type)}  {Range(node)}{DocSuffix(type, options)}");
                        if (node is TypeDeclarationSyntax typeDeclaration)
                            AppendNodes(lines, typeDeclaration.Members, model, depth + 1, options, cancellationToken);
                        else if (node is EnumDeclarationSyntax enumDeclaration)
                            AppendNodes(lines, enumDeclaration.Members, model, depth + 1, options, cancellationToken);
                        break;

                    case BaseFieldDeclarationSyntax field:
                        foreach (var variable in field.Declaration.Variables)
                            AppendMember(lines, indent, model.GetDeclaredSymbol(variable, cancellationToken), variable, options);
                        break;

                    case GlobalStatementSyntax:
                        if (!topLevelStatements)
                            lines.Add($"{indent}top-level statements  L{Line(node)}");
                        topLevelStatements = true;
                        break;

                    case MemberDeclarationSyntax or EnumMemberDeclarationSyntax:
                        AppendMember(lines, indent, model.GetDeclaredSymbol(node, cancellationToken), node, options);
                        break;
                }
            }
        }

        private static void AppendMember(List<string> lines, string indent, ISymbol? symbol, SyntaxNode node, OutlineOptions options)
        {
            if (symbol == null || !IsVisible(symbol, options))
                return;

            lines.Add($"{indent}{FormatMember(symbol)}  L{Line(node)}{DocSuffix(symbol, options)}");
        }

        private static bool IsOutlined(ISymbol member, OutlineOptions options)
        {
            if (member.IsImplicitlyDeclared || !IsVisible(member, options))
                return false;

            // Accessors are part of their property or event line
            return member is not IMethodSymbol { MethodKind: MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise };
        }

        private static bool IsVisible(ISymbol symbol, OutlineOptions options)
        {
            return options.IncludePrivate || symbol.DeclaredAccessibility is not (Accessibility.Private or Accessibility.NotApplicable)
                || symbol.ContainingType?.TypeKind is TypeKind.Interface or TypeKind.Enum;
        }

        public static string FormatTypeHeader(INamedTypeSymbol type)
        {
            if (type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod is { } invoke)
            {
                var parameters = string.Join(", ", invoke.Parameters.Select(p => p.ToDisplayString(MemberFormat)));
                return $"{AccessibilityPrefix(type)}delegate {invoke.ReturnType.ToDisplayString(TypeNameFormat)} {type.ToDisplayString(TypeNameFormat)}({parameters})";
            }

            var modifiers = new List<string>();
            if (type.IsStatic) modifiers.Add("static");
            else if (type.IsAbstract && type.TypeKind == TypeKind.Class) modifiers.Add("abstract");
            else if (type.IsSealed && type.TypeKind == TypeKind.Class) modifiers.Add("sealed");
            if (type.IsReadOnly && type.TypeKind == TypeKind.Struct) modifiers.Add("readonly");
            if (type.IsRefLikeType) modifiers.Add("ref");

            var kind = type switch
            {
                { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
                { IsRecord: true } => "record",
                { TypeKind: TypeKind.Interface } => "interface",
                { TypeKind: TypeKind.Struct } => "struct",
                { TypeKind: TypeKind.Enum } => "enum",
                _ => "class"
            };

            var bases = new List<ITypeSymbol>();
            if (type.TypeKind == TypeKind.Class && type.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
                bases.Add(baseType);
            if (type.TypeKind == TypeKind.Enum && type.EnumUnderlyingType is { SpecialType: not SpecialType.System_Int32 } underlying)
                bases.Add(underlying);
            // Records get IEquatable<Self> from the compiler, it is noise in an outline
            bases.AddRange(type.Interfaces.Where(i => !(type.IsRecord && i.Name == "IEquatable"
                && SymbolEqualityComparer.Default.Equals(i.TypeArguments.FirstOrDefault(), type))));

            var baseList = bases.Count > 0 ? " : " + string.Join(", ", bases.Select(b => b.ToDisplayString(TypeNameFormat))) : "";
            var modifierText = modifiers.Count > 0 ? string.Join(" ", modifiers) + " " : "";
            return $"{AccessibilityPrefix(type)}{modifierText}{kind} {type.ToDisplayString(TypeNameFormat)}{baseList}";
        }

        private static string FormatMember(ISymbol symbol)
        {
            return symbol switch
            {
                IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } enumMember => $"{enumMember.Name} = {enumMember.ConstantValue}",
                // The display format has no "event" keyword for field-like events
                IEventSymbol @event => $"{AccessibilityPrefix(@event)}{(@event.IsStatic ? "static " : "")}event {@event.Type.ToDisplayString(MemberFormat)} {@event.Name}",
                _ => symbol.ToDisplayString(MemberFormat)
            };
        }

        private static string AccessibilityPrefix(ISymbol symbol)
        {
            var text = SyntaxFacts.GetText(symbol.DeclaredAccessibility);
            return string.IsNullOrEmpty(text) ? "" : text + " ";
        }

        private static string DocSuffix(ISymbol symbol, OutlineOptions options)
        {
            if (!options.IncludeDocs)
                return "";
            var summary = DocSummary(symbol);
            return summary == "" ? "" : $"  // {summary}";
        }

        // First sentence of the XML <summary>, short enough to keep the outline compact
        private static string DocSummary(ISymbol symbol)
        {
            var xml = symbol.GetDocumentationCommentXml();
            if (string.IsNullOrWhiteSpace(xml))
                return "";

            try
            {
                var summary = XElement.Parse(xml).Element("summary");
                if (summary == null)
                    return "";

                foreach (var reference in summary.Descendants().Where(e => e.Attribute("cref") != null).ToList())
                    reference.ReplaceWith(reference.Attribute("cref")!.Value.Split('.', ':').Last());

                var text = Regex.Replace(summary.Value, @"\s+", " ").Trim();
                var sentenceEnd = text.IndexOf(". ", StringComparison.Ordinal);
                if (sentenceEnd > 0)
                    text = text[..(sentenceEnd + 1)];
                return text.Length <= 100 ? text : text[..100] + "...";
            }
            catch (System.Xml.XmlException)
            {
                return "";
            }
        }

        private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        private static string Range(SyntaxNode node)
        {
            var span = node.GetLocation().GetLineSpan();
            return $"L{span.StartLinePosition.Line + 1}-{span.EndLinePosition.Line + 1}";
        }

        private static string DisplayDocument(Document document, string solutionDirectory)
        {
            return document is SourceGeneratedDocument
                ? SolutionFiles.GeneratedPrefix + document.Name
                : SolutionFiles.Display(document.FilePath, solutionDirectory);
        }

        private static string Finish(List<string> lines)
        {
            if (lines.Count <= MaxOutlineLines)
                return string.Join("\n", lines) + "\n";

            return string.Join("\n", lines.Take(MaxOutlineLines))
                + $"\n... {lines.Count - MaxOutlineLines} more lines. Outline a single type with typeName to narrow it down.\n";
        }

        /// <summary>
        /// Without fileName: lists the source-generated files per project. With fileName: returns its
        /// text with line numbers, startLine..startLine+maxLines.
        /// </summary>
        public async Task<string> GetGeneratedCodeAsync(
            string solutionPath, string? fileName, string? projectName, int startLine, int maxLines, CancellationToken cancellationToken)
        {
            var solution = await _codeAnalysis.GetSolutionAsync(solutionPath, cancellationToken);
            var solutionDirectory = SolutionFiles.GetDirectory(solutionPath);

            var generated = new List<(Project Project, SourceGeneratedDocument Document)>();
            foreach (var project in solution.Projects)
            {
                if (projectName != null && !string.Equals(ProjectIdentity.GetDisplayName(project), projectName, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var document in await project.GetSourceGeneratedDocumentsAsync(cancellationToken))
                    generated.Add((project, document));
            }

            // Target-framework copies usually generate the same files
            var unique = generated
                .DistinctBy(g => (ProjectIdentity.GetKey(g.Project), GeneratorName(g.Document), g.Document.HintName))
                .ToList();

            if (string.IsNullOrWhiteSpace(fileName))
                return await ListGeneratedAsync(unique, cancellationToken);

            var matches = unique
                .Where(g => g.Document.HintName.Equals(fileName, StringComparison.OrdinalIgnoreCase)
                    || (g.Document.FilePath ?? "").Replace('\\', '/').EndsWith("/" + fileName.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                return $"Error: No generated file '{fileName}'. Call without fileName to list them.";
            if (matches.Count > 1)
                return $"'{fileName}' is ambiguous: {string.Join(", ", matches.Select(m => $"{ProjectIdentity.GetDisplayName(m.Project)}: {GeneratorName(m.Document)}/{m.Document.HintName}"))}. Pass projectName.";

            var (owner, file) = matches[0];
            var text = await file.GetTextAsync(cancellationToken);
            var first = Math.Clamp(startLine, 1, Math.Max(1, text.Lines.Count));
            var last = Math.Min(text.Lines.Count, first + Math.Max(1, maxLines) - 1);

            var output = new StringBuilder();
            output.AppendLine($"{SolutionFiles.GeneratedPrefix}{GeneratorName(file)}/{file.HintName} ({ProjectIdentity.GetDisplayName(owner)}, lines {first}-{last} of {text.Lines.Count})");
            for (var line = first; line <= last; line++)
                output.AppendLine($"{line,5}: {text.Lines[line - 1]}");
            if (last < text.Lines.Count)
                output.AppendLine($"... {text.Lines.Count - last} more lines (startLine={last + 1}).");

            return output.ToString();
        }

        private static async Task<string> ListGeneratedAsync(
            List<(Project Project, SourceGeneratedDocument Document)> generated, CancellationToken cancellationToken)
        {
            if (generated.Count == 0)
                return "No source-generated files in the solution.";

            var output = new StringBuilder();
            output.AppendLine($"{generated.Count} generated files:");
            foreach (var group in generated.GroupBy(g => ProjectIdentity.GetDisplayName(g.Project)).OrderBy(g => g.Key))
            {
                output.AppendLine($"**{group.Key}**:");
                foreach (var (_, document) in group.OrderBy(g => GeneratorName(g.Document)).ThenBy(g => g.Document.HintName))
                {
                    var text = await document.GetTextAsync(cancellationToken);
                    output.AppendLine($"  • {GeneratorName(document)}/{document.HintName} ({text.Lines.Count} lines)");
                }
            }

            return output.ToString();
        }

        // Generated documents live under ".../<generator assembly>/<generator type>/<hint name>"
        private static string GeneratorName(SourceGeneratedDocument document)
        {
            var directory = Path.GetDirectoryName(document.FilePath ?? "");
            var generatorType = string.IsNullOrEmpty(directory) ? "" : Path.GetFileName(directory);
            return string.IsNullOrEmpty(generatorType) ? "generator" : generatorType.Split('.').Last();
        }
    }
}
