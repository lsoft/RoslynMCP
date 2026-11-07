using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RoslynMcpServer.Tests.Infrastructure
{
    /// <summary>Wraps in-memory generators and analyzers the way a NuGet analyzer package reaches a project.</summary>
    internal sealed class InMemoryAnalyzerReference : AnalyzerReference
    {
        private readonly ImmutableArray<DiagnosticAnalyzer> _analyzers;
        private readonly ImmutableArray<ISourceGenerator> _generators;

        public InMemoryAnalyzerReference(DiagnosticAnalyzer[]? analyzers = null, IIncrementalGenerator[]? generators = null)
        {
            _analyzers = (analyzers ?? Array.Empty<DiagnosticAnalyzer>()).ToImmutableArray();
            _generators = (generators ?? Array.Empty<IIncrementalGenerator>()).Select(g => g.AsSourceGenerator()).ToImmutableArray();
        }

        public override string? FullPath => null;
        public override object Id { get; } = Guid.NewGuid();
        public override string Display => "InMemory";

        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzers(string language) => _analyzers;
        public override ImmutableArray<DiagnosticAnalyzer> GetAnalyzersForAllLanguages() => _analyzers;
        public override ImmutableArray<ISourceGenerator> GetGenerators(string language) => _generators;
        public override ImmutableArray<ISourceGenerator> GetGeneratorsForAllLanguages() => _generators;
    }

    /// <summary>
    /// The test assembly as a real analyzer file reference, the way MSBuild hands generators to a project.
    /// FindReferences needs this: Roslyn can only checksum file-based analyzer references.
    /// </summary>
    internal static class TestAssemblyAnalyzers
    {
        public static AnalyzerFileReference Create() =>
            new(typeof(HelloGenerator).Assembly.Location, new LoadedAssemblyLoader());

        private sealed class LoadedAssemblyLoader : IAnalyzerAssemblyLoader
        {
            public void AddDependencyLocation(string fullPath) { }

            public System.Reflection.Assembly LoadFromPath(string fullPath) => typeof(HelloGenerator).Assembly;
        }
    }

    /// <summary>Generates Gen.Hello.World() into "Hello.g.cs".</summary>
    [Generator]
    public sealed class HelloGenerator : IIncrementalGenerator
    {
        public const string Source = """
            namespace Gen
            {
                public static class Hello
                {
                    /// <summary>Says hi.</summary>
                    public static string World() => "hi";
                }
            }
            """;

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            context.RegisterPostInitializationOutput(c => c.AddSource("Hello.g.cs", Source));
        }
    }

    /// <summary>Reports TEST001 on every type named "Bad".</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal sealed class BadTypeAnalyzer : DiagnosticAnalyzer
    {
        public static readonly DiagnosticDescriptor Rule = new(
            "TEST001", "Bad type", "Type '{0}' is bad", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterSymbolAction(c =>
            {
                if (c.Symbol.Name == "Bad")
                    c.ReportDiagnostic(Diagnostic.Create(Rule, c.Symbol.Locations[0], c.Symbol.Name));
            }, SymbolKind.NamedType);
        }
    }

    /// <summary>Crashes on every type, like a buggy third-party analyzer.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    internal sealed class ThrowingAnalyzer : DiagnosticAnalyzer
    {
        private static readonly DiagnosticDescriptor Rule = new(
            "TEST002", "Never reported", "Never reported", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.RegisterSymbolAction(_ => throw new InvalidOperationException("analyzer bug"), SymbolKind.NamedType);
        }
    }
}
