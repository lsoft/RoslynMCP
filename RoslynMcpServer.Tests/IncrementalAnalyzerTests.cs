using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Infrastructure;

namespace RoslynMcpServer.Tests
{
    public class IncrementalAnalyzerTests : IDisposable
    {
        private const string ComplexMethod = """
            namespace Shop;
            public class Orders
            {
                public int Classify(int x)
                {
                    if (x > 0 && x < 10) return 1;
                    if (x > 100 || x < -100) return 2;
                    return 0;
                }
            }
            """;

        private readonly TestSolutionBuilder _builder = new();

        public void Dispose() => _builder.Dispose();

        [Fact]
        public async Task Analyze_ExtractsSymbolsAndComplexity()
        {
            var solution = _builder.AddProject("Core", ("Orders.cs", ComplexMethod)).Build();

            var result = await new IncrementalAnalyzer().AnalyzeIncrementallyAsync(solution);

            Assert.Equal(1, result.ProcessedDocuments);
            Assert.Contains(result.Symbols, s => s.Name == "Orders" && s.Category == "Class");
            Assert.Contains(result.Symbols, s => s.Name == "Classify" && s.Namespace == "Shop");
            Assert.Contains(result.Symbols, s => s.Name == "x" && s.SymbolKind == SymbolKind.Parameter);

            var complex = Assert.Single(result.ComplexityIssues);
            Assert.Equal("Orders", complex.ClassName);
            Assert.Equal("Shop", complex.Namespace);
            Assert.True(result.AnalysisEndTime >= result.AnalysisStartTime);
        }

        [Fact]
        public async Task Analyze_SecondRunServesUnchangedDocumentsFromCache()
        {
            var solution = _builder.AddProject("Core", ("Orders.cs", ComplexMethod)).Build();
            var analyzer = new IncrementalAnalyzer();

            var first = await analyzer.AnalyzeIncrementallyAsync(solution);
            var second = await analyzer.AnalyzeIncrementallyAsync(solution);

            Assert.Equal(0, second.ProcessedDocuments);
            Assert.Equal(first.Symbols.Count, second.Symbols.Count);
            Assert.Equal(first.ComplexityIssues.Count, second.ComplexityIssues.Count);
        }

        [Fact]
        public async Task Analyze_ReanalyzesDocumentsEditedInMemory()
        {
            // Regression: staleness was decided by the file's mtime on disk, so an edit in the
            // solution snapshot (the whole point of changedDocuments) returned stale cached results
            var solution = _builder.AddProject("Core", ("Orders.cs", ComplexMethod)).Build();
            var analyzer = new IncrementalAnalyzer();
            await analyzer.AnalyzeIncrementallyAsync(solution);

            var document = solution.Projects.Single().Documents.Single();
            var edited = solution.WithDocumentText(document.Id, SourceText.From("namespace Shop; public class Invoices { }"));
            var result = await analyzer.AnalyzeIncrementallyAsync(edited, new[] { document.Id });

            Assert.Equal(1, result.ProcessedDocuments);
            Assert.Contains(result.Symbols, s => s.Name == "Invoices");
            Assert.DoesNotContain(result.Symbols, s => s.Name == "Orders");
            Assert.Empty(result.ComplexityIssues);
        }

        [Fact]
        public async Task Analyze_OnlyRequestedDocumentsAreAnalyzed()
        {
            var solution = _builder.AddProject("Core", ("A.cs", "class A { }"), ("B.cs", "class B { }")).Build();
            var documentB = solution.Projects.Single().Documents.Single(d => d.Name == "B.cs");

            var result = await new IncrementalAnalyzer().AnalyzeIncrementallyAsync(solution, new[] { documentB.Id });

            Assert.Equal(1, result.ProcessedDocuments);
            Assert.Contains(result.Symbols, s => s.Name == "B");
            Assert.DoesNotContain(result.Symbols, s => s.Name == "A");
        }

        [Fact]
        public async Task Analyze_ManyDocumentsInParallelLoseNothing()
        {
            // Regression: documents of a batch wrote to shared List/Dictionary instances concurrently
            const int count = 200;
            var files = Enumerable.Range(0, count)
                .Select(i => ($"C{i}.cs", $"namespace N; public class C{i} {{ public int M{i}(int a) => a > 0 && a < 5 || a == 7 ? (a == 1 ? 1 : 2) : 0; }}"))
                .ToArray();
            var solution = _builder.AddProject("Core", files).Build();

            for (var run = 0; run < 3; run++)
            {
                var result = await new IncrementalAnalyzer().AnalyzeIncrementallyAsync(solution);

                Assert.Equal(count, result.ProcessedDocuments);
                Assert.Equal(count, result.Symbols.Count(s => s.SymbolKind == SymbolKind.NamedType));
                Assert.Equal(count, result.ComplexityIssues.Count);
            }
        }

        [Fact]
        public async Task Analyze_SkipsDocumentsWithoutFilePath()
        {
            var solution = _builder.AddProject("Core", ("A.cs", "class A { }")).Build();
            var projectId = solution.ProjectIds.Single();
            solution = solution.AddDocument(DocumentId.CreateNewId(projectId), "NoPath.cs", SourceText.From("class NoPath { }"));

            var result = await new IncrementalAnalyzer().AnalyzeIncrementallyAsync(solution);

            Assert.Equal(1, result.ProcessedDocuments);
            Assert.DoesNotContain(result.Symbols, s => s.Name == "NoPath");
        }

        [Theory]
        [InlineData(0, 3, new int[0])]
        [InlineData(5, 5, new[] { 5 })]
        [InlineData(7, 3, new[] { 3, 3, 1 })]
        [InlineData(2, 10, new[] { 2 })]
        public void Batch_SplitsIntoChunks(int itemCount, int batchSize, int[] expectedSizes)
        {
            var batches = Enumerable.Range(0, itemCount).Batch(batchSize).Select(b => b.ToList()).ToList();

            Assert.Equal(expectedSizes, batches.Select(b => b.Count));
            Assert.Equal(Enumerable.Range(0, itemCount), batches.SelectMany(b => b));
        }
    }
}
