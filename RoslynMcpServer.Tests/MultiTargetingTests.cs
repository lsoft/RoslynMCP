using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Infrastructure;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests
{
    /// <summary>
    /// A multi-targeted project is loaded once per target framework. Regression: every symbol,
    /// method and dependency used to be reported once per target framework, which wasted most
    /// of each tool's output on duplicates.
    /// </summary>
    public class MultiTargetingTests : IDisposable
    {
        private const string ParserCode = """
            namespace Lib;
            public class Parser
            {
                public int Parse(int x)
                {
                    if (x > 0) return 1;
                    if (x < 0) return -1;
            #if NET8_0
                    if (x == 5) return 5;
                    if (x == 6) return 6;
            #endif
                    return 0;
                }
            #if NET8_0
                public void ModernOnly() { }
            #endif
            }
            """;

        private readonly TestSolutionBuilder _builder = new();
        private readonly ServiceProvider _services;
        private readonly SymbolSearchService _search;

        public MultiTargetingTests()
        {
            var solution = _builder
                .AddMultiTargetedProject("Lib", new[] { "net48", "net8.0" }, ("Parser.cs", ParserCode))
                .AddProject("App", new[] { "Lib(net8.0)" },
                    ("Program.cs", "using Lib; namespace App; public static class Program { public static int Run() => new Parser().Parse(1) + new Parser().Parse(2); }"))
                .AddProject("Legacy", new[] { "Lib(net48)" },
                    ("Old.cs", "using Lib; namespace Legacy; public static class Old { public static int Run() => new Parser().Parse(3); }"))
                .WriteFiles()
                .Build();
            _services = TestServices.Create(solution);
            _search = _services.GetRequiredService<SymbolSearchService>();
        }

        public void Dispose()
        {
            _services.Dispose();
            _builder.Dispose();
        }

        [Fact]
        public async Task SearchSymbols_ReportsSymbolOncePerProjectFile()
        {
            var results = (await _search.SearchSymbolsAsync("Parser", _builder.SolutionPath, "class", ignoreCase: false)).ToList();

            var parser = Assert.Single(results);
            Assert.Equal("Lib", parser.ProjectName);
            Assert.Equal("Lib/Parser.cs:2", parser.Location);
        }

        [Fact]
        public async Task SearchSymbols_FindsSymbolsOfSingleTargetFramework()
        {
            var results = (await _search.SearchSymbolsAsync("ModernOnly", _builder.SolutionPath, "method", ignoreCase: false)).ToList();

            Assert.Single(results);
        }

        [Fact]
        public async Task SearchSymbols_KeepsDistinctSymbolsWithSameName()
        {
            var results = (await _search.SearchSymbolsAsync("*", _builder.SolutionPath, "method", ignoreCase: false)).ToList();

            Assert.Equal(new[] { "ModernOnly", "Parse", "Run", "Run" }, results.Select(r => r.Name).OrderBy(n => n));
        }

        [Fact]
        public async Task FindReferences_CoversEveryTargetFrameworkOnce()
        {
            // App compiles against the net8.0 copy, Legacy against net48: both must be found
            // although the search starts from a single copy of the symbol
            var results = (await _search.FindReferencesAsync("Parse", _builder.SolutionPath, includeDefinition: true)).ToList();

            var definition = Assert.Single(results, r => r.IsDefinition);
            Assert.Equal("Lib", definition.ProjectName);
            Assert.Equal(new[] { "App", "Legacy" }, results.Where(r => !r.IsDefinition).Select(r => r.ProjectName).OrderBy(n => n));
        }

        [Fact]
        public async Task AnalyzeCodeComplexity_ReportsMethodOnceWithHighestComplexity()
        {
            var output = await CodeNavigationTools.AnalyzeCodeComplexity(_builder.SolutionPath, threshold: 5, serviceProvider: _services);

            // net48 sees 3 decision points less than net8.0; the net8.0 value wins
            Assert.Contains("Found 1 methods with high complexity", output);
            Assert.Contains("**Parser.Parse** (Complexity: 5)", output);
        }

        [Fact]
        public async Task AnalyzeCodeComplexity_ListsEveryMethodOnce()
        {
            var output = await CodeNavigationTools.AnalyzeCodeComplexity(_builder.SolutionPath, threshold: 1, serviceProvider: _services);

            // Parse, ModernOnly, Program.Run, Old.Run
            Assert.Contains("Found 4 methods with high complexity", output);
        }

        [Fact]
        public async Task AnalyzeDependencies_CountsPerProjectFile()
        {
            var solution = await _services.GetRequiredService<CodeAnalysisService>().GetSolutionAsync(_builder.SolutionPath);

            var analysis = await CodeAnalysisService.AnalyzeDependenciesAsync(solution, "Test", maxDepth: 3);

            // Parser, Parse, ModernOnly, Program, Program.Run, Old, Old.Run
            Assert.Equal(7, analysis.TotalSymbols);

            var lib = Assert.Single(analysis.Dependencies, d => d.Type == "ProjectReference");
            Assert.Equal("Lib", lib.Name);
            Assert.Equal(2, lib.UsageCount);

            Assert.Equal(2, Assert.Single(analysis.NamespaceUsages, u => u.Namespace == "Lib").UsageCount);
        }

        [Theory]
        [InlineData("Lib(net8.0)", "Lib")]
        [InlineData("Lib(netstandard2.1)", "Lib")]
        [InlineData("Lib", "Lib")]
        [InlineData("(odd)", "(odd)")]
        [InlineData("Name(with)Suffix", "Name(with)Suffix")]
        public void DisplayName_StripsTargetFrameworkSuffix(string projectName, string expected)
        {
            using var workspace = new Microsoft.CodeAnalysis.AdhocWorkspace();
            var project = workspace.AddProject(projectName, Microsoft.CodeAnalysis.LanguageNames.CSharp);

            Assert.Equal(expected, ProjectIdentity.GetDisplayName(project));
        }
    }
}
