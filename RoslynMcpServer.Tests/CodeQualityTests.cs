using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Tests.Infrastructure;
using RoslynMcpServer.Tools;

namespace RoslynMcpServer.Tests
{
    public class GetDiagnosticsTests : IDisposable
    {
        private readonly TestSolutionBuilder _builder = new();

        public void Dispose() => _builder.Dispose();

        private ServiceProvider Build(params (string, string)[] files)
        {
            return TestServices.Create(_builder.AddProject("Core", files).WriteFiles().Build());
        }

        private Task<string> Diagnostics(ServiceProvider services, string? file = null, string? project = null,
            string minSeverity = "warning", bool includeAnalyzers = false, int maxResults = 50) =>
            CodeQualityTools.GetDiagnostics(_builder.SolutionPath, file, project, minSeverity, includeAnalyzers, maxResults, serviceProvider: services);

        [Fact]
        public async Task ReportsCompilerErrorsAndWarnings()
        {
            using var services = Build(("Broken.cs", "class Broken\n{\n    int M()\n    {\n        int unused;\n        return missing;\n    }\n}"));

            var output = await Diagnostics(services, "Broken.cs");

            Assert.StartsWith("Diagnostics for Core/Broken.cs: 1 error, 1 warning (compiler only)", output);
            Assert.Contains("**Core/Broken.cs**", output);
            Assert.Contains("  error CS0103 L6:16 The name 'missing' does not exist in the current context", output);
            Assert.Contains("  warning CS0168 L5:13", output);
            Assert.True(output.IndexOf("CS0103", StringComparison.Ordinal) < output.IndexOf("CS0168", StringComparison.Ordinal), "errors come first");
        }

        [Fact]
        public async Task MinSeverityFilters()
        {
            using var services = Build(("Broken.cs", "class Broken { int M() { int unused; return missing; } }"));

            var output = await Diagnostics(services, "Broken.cs", minSeverity: "error");

            Assert.Contains("CS0103", output);
            Assert.DoesNotContain("CS0168", output);
        }

        [Fact]
        public async Task CleanFile()
        {
            using var services = Build(("Clean.cs", "class Clean { }"));

            Assert.Equal("No diagnostics for Core/Clean.cs (compiler only).\n", (await Diagnostics(services, "Clean.cs")).Replace("\r\n", "\n"));
        }

        [Fact]
        public async Task ProjectAndSolutionScopes()
        {
            using var services = Build(("A.cs", "class A { int M() => missing; }"), ("B.cs", "class B { int M() => alsoMissing; }"));

            var project = await Diagnostics(services, project: "Core");
            var solution = await Diagnostics(services);

            Assert.StartsWith("Diagnostics for Core: 2 errors", project);
            Assert.StartsWith("Diagnostics for Test.sln: 2 errors", solution);
            Assert.StartsWith("Error: Project 'Nope' not found.", await Diagnostics(services, project: "Nope"));
        }

        [Fact]
        public async Task InvalidSeverity()
        {
            using var services = Build(("A.cs", "class A { }"));
            Assert.Equal("Error: minSeverity must be error, warning or info.", await Diagnostics(services, minSeverity: "fatal"));
        }

        [Fact]
        public async Task TruncatesLongLists()
        {
            var body = string.Concat(Enumerable.Range(0, 10).Select(i => $"int M{i}() => missing{i}; "));
            using var services = Build(("Many.cs", $"class Many {{ {body} }}"));

            var output = await Diagnostics(services, "Many.cs", maxResults: 3);

            Assert.StartsWith("Diagnostics for Core/Many.cs: 10 errors (compiler only), showing 3", output);
            Assert.Contains("... and 7 more.", output);
        }

        [Fact]
        public async Task SeesEditsWithoutRebuildOrReload()
        {
            // The agent's edit-check loop: change a file on disk, ask again
            var loader = new FakeSolutionLoader(_builder.AddProject("Core", ("A.cs", "class A { }")).WriteFiles().Build());
            using var services = TestServices.Create(loader);
            Assert.StartsWith("No diagnostics", await Diagnostics(services, "A.cs"));

            var path = Path.Combine(_builder.Root, "Core", "A.cs");
            File.WriteAllText(path, "class A { int M() => missing; }");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

            Assert.Contains("error CS0103", await Diagnostics(services, "A.cs"));
            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task SharedFileOfAMultiTargetedProjectIsReportedOnce()
        {
            var solution = _builder.AddMultiTargetedProject("Lib", new[] { "net48", "net8.0" }, ("A.cs", "class A { int M() => missing; }")).WriteFiles().Build();
            using var services = TestServices.Create(solution);

            var output = await Diagnostics(services);

            Assert.StartsWith("Diagnostics for Test.sln: 1 error", output);
        }

        [Fact]
        public async Task ProjectAnalyzersRunOnlyWhenAsked()
        {
            var solution = _builder
                .AddProject("Core", ("Bad.cs", "class Bad { }"))
                .AddAnalyzerReference("Core", new InMemoryAnalyzerReference(new Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer[] { new BadTypeAnalyzer() }))
                .WriteFiles()
                .Build();
            using var services = TestServices.Create(solution);

            var withoutAnalyzers = await Diagnostics(services, "Bad.cs");
            var withAnalyzers = await Diagnostics(services, "Bad.cs", includeAnalyzers: true);
            var projectWide = await Diagnostics(services, includeAnalyzers: true);

            Assert.DoesNotContain("TEST001", withoutAnalyzers);
            Assert.Contains("(compiler + 1 analyzers)", withAnalyzers);
            Assert.Contains("warning TEST001 L1:7 Type 'Bad' is bad", withAnalyzers);
            Assert.Contains("TEST001", projectWide);
        }

        [Fact]
        public async Task CrashingAnalyzersAreSummarized()
        {
            var solution = _builder
                .AddProject("Core", ("A.cs", "class A { } class B { }"))
                .AddAnalyzerReference("Core", new InMemoryAnalyzerReference(new Microsoft.CodeAnalysis.Diagnostics.DiagnosticAnalyzer[] { new ThrowingAnalyzer() }))
                .WriteFiles()
                .Build();
            using var services = TestServices.Create(solution);

            var output = await Diagnostics(services, "A.cs", includeAnalyzers: true);

            Assert.DoesNotContain("AD0001", output);
            Assert.Contains("Analyzers that failed and were skipped:", output);
        }
    }

    public class FindUnusedSymbolsTests : IDisposable
    {
        private const string LibCode = """
            using System;

            namespace Lib
            {
                public interface IRunner { void Run(); }

                internal class Runner : IRunner, IDisposable
                {
                    private int _counter;
                    private int _neverUsed;

                    public void Run() { _counter++; Helper(); }
                    private void Helper() { }
                    private void Orphan() { }
                    private int Recursive(int n) => n <= 0 ? 0 : Recursive(n - 1);
                    [Obsolete] private void Attributed() { }
                    public void Dispose() { }
                    public void PublicButUnused() { }
                }

                internal sealed class UnusedType { public static UnusedType Create() => new UnusedType(); }

                public class PublicApi { public void Exposed() { } }

                internal record Point(int X, int Y);

                internal static class Program
                {
                    static void Main()
                    {
                        IRunner runner = new Runner();
                        runner.Run();
                        var point = new Point(1, 2);
                    }
                }
            }
            """;

        private readonly TestSolutionBuilder _builder = new();
        private readonly ServiceProvider _services;

        public FindUnusedSymbolsTests()
        {
            _services = TestServices.Create(_builder.AddProject("Core", ("Lib.cs", LibCode)).WriteFiles().Build());
        }

        public void Dispose()
        {
            _services.Dispose();
            _builder.Dispose();
        }

        [Fact]
        public async Task ReportsUnreferencedPrivateAndInternalSymbols()
        {
            var output = await CodeQualityTools.FindUnusedSymbols(_builder.SolutionPath, serviceProvider: _services);

            foreach (var unused in new[] { "Lib.Runner._neverUsed", "Lib.Runner.Orphan()", "Lib.Runner.Recursive(int)", "Lib.Runner.PublicButUnused()", "Lib.UnusedType", "Lib.UnusedType.Create()" })
                Assert.Contains($"`{unused}`", output);
            Assert.StartsWith("Found 6 unused symbols", output);
            Assert.Contains("**Core/Lib.cs**", output);
        }

        [Fact]
        public async Task SkipsWhatFrameworksOrTheCompilerMayUse()
        {
            var output = await CodeQualityTools.FindUnusedSymbols(_builder.SolutionPath, serviceProvider: _services);

            foreach (var used in new[] { "_counter", "Helper", "Attributed", "Dispose", "Runner.Run", "Main", "Point", "Exposed", "PublicApi", "IRunner" })
                Assert.DoesNotContain(used + "`", output.Replace("()", "").Replace("(int)", ""));
        }

        [Fact]
        public async Task IncludePublicAlsoChecksTheExternalSurface()
        {
            var output = await CodeQualityTools.FindUnusedSymbols(_builder.SolutionPath, includePublic: true, serviceProvider: _services);

            Assert.Contains("`Lib.PublicApi`", output);
            Assert.Contains("`Lib.PublicApi.Exposed()`", output);
            Assert.DoesNotContain("`Lib.IRunner`", output);
        }

        [Fact]
        public async Task LimitsOutput()
        {
            var output = await CodeQualityTools.FindUnusedSymbols(_builder.SolutionPath, maxResults: 2, serviceProvider: _services);

            Assert.Contains("showing 2", output);
            Assert.Contains("... and 4 more.", output);
        }

        [Fact]
        public async Task NothingToReport()
        {
            using var builder = new TestSolutionBuilder();
            using var services = TestServices.Create(builder.AddProject("Core", ("A.cs", "public class A { }")).WriteFiles().Build());

            var output = await CodeQualityTools.FindUnusedSymbols(builder.SolutionPath, serviceProvider: services);

            Assert.StartsWith("No unused symbols among 0 candidates", output);
        }
    }
}
