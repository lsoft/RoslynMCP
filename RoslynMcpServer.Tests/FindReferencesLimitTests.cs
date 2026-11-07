using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Tests.Infrastructure;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests
{
    public class FindReferencesLimitTests : IDisposable
    {
        private readonly TestSolutionBuilder _builder = new();
        private readonly ServiceProvider _services;

        public FindReferencesLimitTests()
        {
            // Target.Hit() is defined once and called on 30 separate lines spread over 3 files (5, 10, 15 calls)
            var files = new List<(string, string)> { ("Target.cs", "public static class Target { public static void Hit() { } }") };
            foreach (var (file, calls) in new[] { ("A.cs", 5), ("B.cs", 10), ("C.cs", 15) })
            {
                var body = string.Concat(Enumerable.Repeat("\n        Target.Hit();", calls));
                files.Add((file, $"class {Path.GetFileNameWithoutExtension(file)}Caller\n{{\n    void Call()\n    {{{body}\n    }}\n}}"));
            }

            _builder.AddProject("Core", files.ToArray()).WriteFiles();
            _services = TestServices.Create(_builder.Build());
        }

        public void Dispose()
        {
            _services.Dispose();
            _builder.Dispose();
        }

        private Task<string> FindHit(int maxResults)
        {
            return CodeNavigationTools.FindReferences(_builder.SolutionPath, "Hit", maxResults: maxResults, serviceProvider: _services);
        }

        private static int ListedReferences(string output) => output.Split('\n').Count(l => l.TrimStart().StartsWith("• Line "));

        [Fact]
        public async Task ListsEverythingWithinTheLimit()
        {
            var output = await CodeNavigationTools.FindReferences(_builder.SolutionPath, "Hit", serviceProvider: _services);

            Assert.StartsWith("Found 31 references:", output);
            Assert.Equal(31, ListedReferences(output));
            Assert.DoesNotContain("more in", output);
        }

        [Fact]
        public async Task TruncatesAndSummarizesTheRest()
        {
            var output = await FindHit(maxResults: 6);

            // The definition plus all 5 calls in A.cs are listed; B.cs and C.cs are summarized
            Assert.StartsWith("Found 31 references, showing 6:", output);
            Assert.Equal(6, ListedReferences(output));
            Assert.Contains("... and 25 more in 2 files:", output);
            // Remaining files are ordered by how many references they hold
            Assert.Matches(@"• Core/C\.cs: 15\s+• Core/B\.cs: 10", output);
            Assert.Contains("Increase maxResults", output);
        }

        [Fact]
        public async Task DefinitionIsListedFirst()
        {
            var output = await FindHit(maxResults: 1);

            Assert.Contains("**Core/Target.cs** (1 references):", output);
            Assert.Contains("Definition", output);
            Assert.Contains("... and 30 more in 3 files:", output);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task NonPositiveLimitStillListsOne(int maxResults)
        {
            var output = await FindHit(maxResults);

            Assert.Equal(1, ListedReferences(output));
        }

        [Fact]
        public async Task OutputSizeIsBoundedByTheLimit()
        {
            var limited = await FindHit(maxResults: 5);
            var full = await FindHit(maxResults: 100);

            Assert.True(limited.Length < full.Length / 2, $"limited={limited.Length}, full={full.Length}");
        }
    }
}
