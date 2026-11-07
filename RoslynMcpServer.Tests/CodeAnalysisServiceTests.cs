using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Infrastructure;

namespace RoslynMcpServer.Tests
{
    public class CodeAnalysisServiceTests : IDisposable
    {
        private readonly TestSolutionBuilder _builder = new();

        public void Dispose() => _builder.Dispose();

        private static CodeAnalysisService CreateService(ISolutionLoader loader)
        {
            return new CodeAnalysisService(loader, NullLogger<CodeAnalysisService>.Instance);
        }

        private Solution BuildOnDisk()
        {
            return _builder.AddProject("Core", ("A.cs", "class A { }")).WriteFiles().Build();
        }

        [Fact]
        public async Task GetSolution_LoadsOnceWhileFilesAreUnchanged()
        {
            var loader = new FakeSolutionLoader(BuildOnDisk());
            var service = CreateService(loader);

            var first = await service.GetSolutionAsync(_builder.SolutionPath);
            var second = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Same(first, second);
            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task GetSolution_ConcurrentCallersShareOneLoad()
        {
            var gate = new TaskCompletionSource<Solution>();
            var loader = new FakeSolutionLoader((_, _) => gate.Task);
            var service = CreateService(loader);
            var solution = BuildOnDisk();

            var calls = Enumerable.Range(0, 5).Select(_ => service.GetSolutionAsync(_builder.SolutionPath)).ToList();
            gate.SetResult(solution);
            await Task.WhenAll(calls);

            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task GetSolution_PathsAreNormalized()
        {
            var loader = new FakeSolutionLoader(BuildOnDisk());
            var service = CreateService(loader);

            await service.GetSolutionAsync(_builder.SolutionPath);
            await service.GetSolutionAsync(Path.Combine(_builder.Root, ".", "Test.sln"));

            Assert.Equal(1, loader.LoadCount);
        }

        private static void Touch(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        private static async Task<string> TextOf(Solution solution, string fileName)
        {
            var document = solution.Projects.SelectMany(p => p.Documents).Single(d => d.Name == fileName);
            return (await document.GetTextAsync()).ToString();
        }

        [Fact]
        public async Task GetSolution_AppliesSourceEditWithoutReload()
        {
            // Regression: any edit used to reload the whole solution through MSBuild
            var solution = BuildOnDisk();
            var loader = new FakeSolutionLoader(solution);
            var service = CreateService(loader);
            await service.GetSolutionAsync(_builder.SolutionPath);

            var path = solution.Projects.Single().Documents.Single().FilePath!;
            File.WriteAllText(path, "class Edited { }");
            Touch(path);
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Equal(1, loader.LoadCount);
            Assert.Equal("class Edited { }", await TextOf(updated, "A.cs"));
        }

        [Fact]
        public async Task GetSolution_FutureTimestampIsAppliedOnce()
        {
            var solution = BuildOnDisk();
            var service = CreateService(new FakeSolutionLoader(solution));
            await service.GetSolutionAsync(_builder.SolutionPath);

            Touch(solution.Projects.Single().Documents.Single().FilePath!);
            var first = await service.GetSolutionAsync(_builder.SolutionPath);
            var second = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Same(first, second);
        }

        [Fact]
        public async Task GetSolution_EditsReachEveryTargetFrameworkCopy()
        {
            var solution = _builder.AddMultiTargetedProject("Lib", new[] { "net48", "net8.0" }, ("A.cs", "class A { }")).WriteFiles().Build();
            var service = CreateService(new FakeSolutionLoader(solution));
            await service.GetSolutionAsync(_builder.SolutionPath);

            var path = solution.Projects.First().Documents.Single().FilePath!;
            File.WriteAllText(path, "class Edited { }");
            Touch(path);
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            foreach (var document in updated.Projects.SelectMany(p => p.Documents))
                Assert.Equal("class Edited { }", (await document.GetTextAsync()).ToString());
        }

        [Fact]
        public async Task GetSolution_RemovesDeletedSourceFile()
        {
            var solution = BuildOnDisk();
            var loader = new FakeSolutionLoader(solution);
            var service = CreateService(loader);
            await service.GetSolutionAsync(_builder.SolutionPath);

            File.Delete(solution.Projects.Single().Documents.Single().FilePath!);
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Empty(updated.Projects.Single().Documents);
            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task GetSolution_KeepsDeletedBuildOutput()
        {
            // "dotnet clean" deletes obj/*.cs (global usings, assembly info); the next build recreates them
            var solution = _builder.AddProject("Core", ("A.cs", "class A { }"), (Path.Combine("obj", "GlobalUsings.g.cs"), "global using System;")).WriteFiles().Build();
            var service = CreateService(new FakeSolutionLoader(solution));
            await service.GetSolutionAsync(_builder.SolutionPath);

            File.Delete(Path.Combine(_builder.Root, "Core", "obj", "GlobalUsings.g.cs"));
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Equal(2, updated.Projects.Single().Documents.Count());
        }

        [Fact]
        public async Task GetSolution_AddsNewSourceFiles()
        {
            var solution = BuildOnDisk();
            var loader = new FakeSolutionLoader(solution);
            var service = CreateService(loader);
            await service.GetSolutionAsync(_builder.SolutionPath);

            var projectDirectory = Path.Combine(_builder.Root, "Core");
            Directory.CreateDirectory(Path.Combine(projectDirectory, "Models"));
            File.WriteAllText(Path.Combine(projectDirectory, "Models", "Order.cs"), "class Order { }");
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Equal(1, loader.LoadCount);
            var added = Assert.Single(updated.Projects.Single().Documents, d => d.Name == "Order.cs");
            Assert.Equal(new[] { "Models" }, added.Folders);
            var compilation = await updated.Projects.Single().GetCompilationAsync();
            Assert.NotNull(compilation!.GetTypeByMetadataName("Order"));
        }

        [Theory]
        [InlineData("bin")]
        [InlineData("obj")]
        [InlineData(".hidden")]
        [InlineData("Nested")]
        public async Task GetSolution_IgnoresFilesOutsideTheDefaultGlob(string folder)
        {
            var solution = BuildOnDisk();
            var service = CreateService(new FakeSolutionLoader(solution));
            await service.GetSolutionAsync(_builder.SolutionPath);

            var directory = Path.Combine(_builder.Root, "Core", folder);
            Directory.CreateDirectory(directory);
            if (folder == "Nested")
                File.WriteAllText(Path.Combine(directory, "Nested.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(directory, "Stray.cs"), "class Stray { }");
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.DoesNotContain(updated.Projects.Single().Documents, d => d.Name == "Stray.cs");
        }

        [Fact]
        public async Task GetSolution_DoesNotAddFilesExcludedAtLoadTime()
        {
            // A .cs file on disk that MSBuild did not compile (<Compile Remove>) stays out, even when edited
            _builder.AddProject("Core", ("A.cs", "class A { }")).WriteFiles();
            var excluded = Path.Combine(_builder.Root, "Core", "Excluded.cs");
            File.WriteAllText(excluded, "class Excluded { }");
            var service = CreateService(new FakeSolutionLoader(_builder.Build()));
            await service.GetSolutionAsync(_builder.SolutionPath);

            Touch(excluded);
            var updated = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.DoesNotContain(updated.Projects.Single().Documents, d => d.Name == "Excluded.cs");
        }

        [Fact]
        public async Task GetSolution_ReloadsAfterProjectFileChanged()
        {
            var solution = BuildOnDisk();
            var loader = new FakeSolutionLoader(solution);
            var service = CreateService(loader);
            await service.GetSolutionAsync(_builder.SolutionPath);

            Touch(solution.Projects.Single().FilePath!);
            await service.GetSolutionAsync(_builder.SolutionPath);
            await service.GetSolutionAsync(_builder.SolutionPath);

            // One reload, not one per call, although the project file stays stamped in the future
            Assert.Equal(2, loader.LoadCount);
        }

        [Theory]
        [InlineData("Directory.Build.props")]
        [InlineData("Directory.Packages.props")]
        [InlineData("global.json")]
        public async Task GetSolution_ReloadsWhenEvaluationFileAppears(string fileName)
        {
            var loader = new FakeSolutionLoader(BuildOnDisk());
            var service = CreateService(loader);
            await service.GetSolutionAsync(_builder.SolutionPath);

            File.WriteAllText(Path.Combine(_builder.Root, fileName), "<Project />");
            await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Equal(2, loader.LoadCount);
        }

        [Fact]
        public async Task InvalidateSolution_ForcesReload()
        {
            var loader = new FakeSolutionLoader(BuildOnDisk());
            var service = CreateService(loader);

            await service.GetSolutionAsync(_builder.SolutionPath);
            service.InvalidateSolution(_builder.SolutionPath);
            await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Equal(2, loader.LoadCount);
        }

        [Fact]
        public async Task GetSolution_FailedLoadIsNotCached()
        {
            var solution = BuildOnDisk();
            var attempt = 0;
            var loader = new FakeSolutionLoader((_, _) =>
                ++attempt == 1 ? Task.FromException<Solution>(new IOException("locked")) : Task.FromResult(solution));
            var service = CreateService(loader);

            await Assert.ThrowsAsync<IOException>(() => service.GetSolutionAsync(_builder.SolutionPath));
            var loaded = await service.GetSolutionAsync(_builder.SolutionPath);

            Assert.Same(solution, loaded);
            Assert.Equal(2, loader.LoadCount);
        }

        [Fact]
        public async Task GetSolution_CancelledCallerDoesNotCancelSharedLoad()
        {
            var solution = BuildOnDisk();
            var gate = new TaskCompletionSource<Solution>();
            var loader = new FakeSolutionLoader((_, _) => gate.Task);
            var service = CreateService(loader);

            using var cts = new CancellationTokenSource();
            var cancelled = service.GetSolutionAsync(_builder.SolutionPath, cts.Token);
            var waiting = service.GetSolutionAsync(_builder.SolutionPath);

            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            gate.SetResult(solution);
            Assert.Same(solution, await waiting);
            Assert.Equal(1, loader.LoadCount);
        }

        [Fact]
        public async Task AnalyzeDependencies_FollowsProjectReferencesUpToMaxDepth()
        {
            var solution = _builder
                .AddProject("Data", ("D.cs", "public class D { }"))
                .AddProject("Domain", new[] { "Data" }, ("M.cs", "public class M { }"))
                .AddProject("Web", new[] { "Domain" }, ("W.cs", "public class W { }"))
                .Build();

            var shallow = await CodeAnalysisService.AnalyzeDependenciesAsync(solution, "Shop", maxDepth: 1);
            var deep = await CodeAnalysisService.AnalyzeDependenciesAsync(solution, "Shop", maxDepth: 2);
            var none = await CodeAnalysisService.AnalyzeDependenciesAsync(solution, "Shop", maxDepth: 0);

            Assert.Equal(new Dictionary<string, int> { ["Data"] = 1, ["Domain"] = 1 }, ProjectDependencyCounts(shallow));
            // Web reaches Data transitively at depth 2
            Assert.Equal(new Dictionary<string, int> { ["Data"] = 2, ["Domain"] = 1 }, ProjectDependencyCounts(deep));
            Assert.Empty(ProjectDependencyCounts(none));
            Assert.Equal("Shop", deep.ProjectName);
        }

        [Fact]
        public async Task AnalyzeDependencies_IgnoresFrameworkReferenceAssemblies()
        {
            var solution = _builder.AddProject("Core", ("A.cs", "class A { }")).Build();

            var analysis = await CodeAnalysisService.AnalyzeDependenciesAsync(solution, "Shop", maxDepth: 3);

            // The test references come from the shared runtime directory, not from a reference pack
            Assert.All(analysis.Dependencies.Where(d => d.Type == "AssemblyReference"),
                d => Assert.StartsWith("System", d.Name));
            Assert.DoesNotContain(analysis.Dependencies, d => d.Type == "ProjectReference");
        }

        [Fact]
        public async Task AnalyzeDependencies_CountsUsingsAndSymbols()
        {
            var solution = _builder
                .AddProject("Core",
                    ("A.cs", "using System; using System.Linq; using Alias = System.Text; public class A { public int P { get; set; } internal void M() { } private int f; }"),
                    ("B.cs", "using System; internal class B { }"))
                .Build();

            var analysis = await CodeAnalysisService.AnalyzeDependenciesAsync(solution, "Shop", maxDepth: 3);

            var usages = analysis.NamespaceUsages.ToDictionary(u => u.Namespace, u => u.UsageCount);
            Assert.Equal(new Dictionary<string, int> { ["System"] = 2, ["System.Linq"] = 1 }, usages);

            // A, P, M, f, B; implicit constructors and accessors are not counted
            Assert.Equal(5, analysis.TotalSymbols);
            Assert.Equal(2, analysis.PublicSymbols);
            Assert.Equal(2, analysis.InternalSymbols);
        }

        private static Dictionary<string, int> ProjectDependencyCounts(Models.DependencyAnalysis analysis)
        {
            return analysis.Dependencies
                .Where(d => d.Type == "ProjectReference")
                .ToDictionary(d => d.Name, d => d.UsageCount);
        }
    }
}
