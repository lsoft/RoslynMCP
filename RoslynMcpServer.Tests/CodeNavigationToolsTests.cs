using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Infrastructure;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests
{
    public class CodeNavigationToolsTests : IDisposable
    {
        private const string OrdersCode = """
            namespace Shop.Orders;

            public class OrderService
            {
                public int Classify(int x)
                {
                    if (x > 0 && x < 10) return 1;
                    if (x > 100 || x < -100) return 2;
                    return 0;
                }

                public int Total() => Classify(1) + Classify(2);
            }
            """;

        private readonly TestSolutionBuilder _builder = new();
        private readonly ServiceProvider _services;

        public CodeNavigationToolsTests()
        {
            var solution = _builder
                .AddProject("Shop", ("OrderService.cs", OrdersCode))
                .WriteFiles()
                .Build();
            _services = TestServices.Create(solution);
        }

        public void Dispose()
        {
            _services.Dispose();
            _builder.Dispose();
        }

        [Fact]
        public async Task SearchSymbols_FormatsResults()
        {
            var output = await CodeNavigationTools.SearchSymbols("Order*", _builder.SolutionPath, "class", serviceProvider: _services);

            Assert.StartsWith("Found 1 symbols:", output);
            Assert.Contains("**Class** (1):", output);
            Assert.Contains("`OrderService` in Shop/OrderService.cs:3", output);
        }

        [Fact]
        public async Task SearchSymbols_SanitizesPattern()
        {
            var output = await CodeNavigationTools.SearchSymbols("Order(*)", _builder.SolutionPath, "class", serviceProvider: _services);

            Assert.StartsWith("Found 1 symbols:", output);
        }

        [Fact]
        public async Task SearchSymbols_TruncatesLargeGroups()
        {
            var files = Enumerable.Range(0, 25).Select(i => ($"T{i}.cs", $"class Type{i} {{ }}")).ToArray();
            using var builder = new TestSolutionBuilder();
            builder.AddProject("Many", files).WriteFiles();
            using var services = TestServices.Create(builder.Build());

            var output = await CodeNavigationTools.SearchSymbols("Type*", builder.SolutionPath, "class", serviceProvider: services);

            Assert.StartsWith("Found 25 symbols:", output);
            Assert.Contains("... and 5 more", output);
        }

        [Fact]
        public async Task SearchSymbols_CancellationIsReported()
        {
            // Regression: the 5 minute timeout token was created but never passed to the search
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var output = await CodeNavigationTools.SearchSymbols("*", _builder.SolutionPath, serviceProvider: _services, cancellationToken: cts.Token);

            Assert.StartsWith("Error: Search operation timed out", output);
        }

        [Fact]
        public async Task FindReferences_FormatsResults()
        {
            var output = await CodeNavigationTools.FindReferences(_builder.SolutionPath, "Classify", serviceProvider: _services);

            Assert.StartsWith("Found 2 references:", output);
            Assert.Contains("**Shop/OrderService.cs** (2 references):", output);
            Assert.Contains("Line 5: Definition", output);
            Assert.Contains("Line 12: Method Call", output);
        }

        [Fact]
        public async Task GetSymbolInfo_FormatsResult()
        {
            var output = await CodeNavigationTools.GetSymbolInfo(_builder.SolutionPath, "Classify", serviceProvider: _services);

            Assert.Contains("**Classify** (Method)", output);
            Assert.Contains("Return Type: int", output);
            Assert.Contains("int x", output);
            Assert.Contains("Location: Shop/OrderService.cs:5", output);
        }

        [Fact]
        public async Task GetSymbolInfo_UnknownSymbol()
        {
            var output = await CodeNavigationTools.GetSymbolInfo(_builder.SolutionPath, "Nope", serviceProvider: _services);
            Assert.StartsWith("Error: Symbol 'Nope' not found", output);
        }

        [Fact]
        public async Task AnalyzeCodeComplexity_ReportsQualifiedMethod()
        {
            var output = await CodeNavigationTools.AnalyzeCodeComplexity(_builder.SolutionPath, threshold: 5, serviceProvider: _services);

            Assert.Contains("Found 1 methods with high complexity", output);
            Assert.Contains("**OrderService.Classify** (Complexity: 5)", output);
            Assert.Contains("Location: OrderService.cs:5", output);
            Assert.Contains("Namespace: Shop.Orders", output);
        }

        [Fact]
        public async Task AnalyzeDependencies_FormatsResult()
        {
            var output = await CodeNavigationTools.AnalyzeDependencies(_builder.SolutionPath, serviceProvider: _services);

            Assert.Contains("**Dependency Analysis for Test**", output);
            Assert.Contains("Total Symbols: 3", output);
            Assert.Contains("Public Symbols: 3", output);
        }

        [Fact]
        public async Task AllTools_RejectInvalidPath()
        {
            var missing = Path.Combine(_builder.Root, "Missing.sln");
            const string error = "Error: Invalid solution path provided.";

            Assert.Equal(error, await CodeNavigationTools.SearchSymbols("*", missing, serviceProvider: _services));
            Assert.Equal(error, await CodeNavigationTools.FindReferences(missing, "X", serviceProvider: _services));
            Assert.Equal(error, await CodeNavigationTools.GetSymbolInfo(missing, "X", serviceProvider: _services));
            Assert.Equal(error, await CodeStructureTools.GetOutline(missing, "A.cs", serviceProvider: _services));
            Assert.Equal(error, await CodeStructureTools.GetGeneratedCode(missing, serviceProvider: _services));
            Assert.Equal(error, await CodeQualityTools.GetDiagnostics(missing, serviceProvider: _services));
            Assert.Equal(error, await CodeQualityTools.FindUnusedSymbols(missing, serviceProvider: _services));
            Assert.Equal(error, await CodeNavigationTools.AnalyzeDependencies(missing, serviceProvider: _services));
            Assert.Equal(error, await CodeNavigationTools.AnalyzeCodeComplexity(missing, serviceProvider: _services));
        }

        [Fact]
        public async Task AllTools_FailClosedWithoutValidator()
        {
            // Regression: "!validator?.Validate(...) ?? false" let every path through when the validator was missing
            using var services = new ServiceCollection().BuildServiceProvider();
            const string error = "Error: Invalid solution path provided.";

            Assert.Equal(error, await CodeNavigationTools.SearchSymbols("*", _builder.SolutionPath, serviceProvider: services));
            Assert.Equal(error, await CodeNavigationTools.FindReferences(_builder.SolutionPath, "X", serviceProvider: services));
            Assert.Equal(error, await CodeNavigationTools.GetSymbolInfo(_builder.SolutionPath, "X", serviceProvider: services));
            Assert.Equal(error, await CodeStructureTools.GetOutline(_builder.SolutionPath, "A.cs", serviceProvider: services));
            Assert.Equal(error, await CodeQualityTools.GetDiagnostics(_builder.SolutionPath, serviceProvider: services));
            Assert.Equal(error, await CodeNavigationTools.AnalyzeDependencies(_builder.SolutionPath, serviceProvider: services));
            Assert.Equal(error, await CodeNavigationTools.AnalyzeCodeComplexity(_builder.SolutionPath, serviceProvider: services));
            Assert.Equal(error, await CodeNavigationTools.SearchSymbols("*", _builder.SolutionPath));
        }

        [Fact]
        public async Task LoaderFailure_ReturnsSafeMessage()
        {
            var loader = new FakeSolutionLoader((_, _) => Task.FromException<Solution>(new InvalidOperationException("secret details")));
            using var services = TestServices.Create(loader);

            var output = await CodeNavigationTools.FindReferences(_builder.SolutionPath, "X", serviceProvider: services);

            Assert.Equal("Error: An unexpected error occurred during finding references.", output);
            Assert.DoesNotContain("secret", output);
        }
    }
}
