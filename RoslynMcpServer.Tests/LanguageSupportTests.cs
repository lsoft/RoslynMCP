using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Services;
using RoslynMcpServer.Tests.Infrastructure;

namespace RoslynMcpServer.Tests
{
    /// <summary>
    /// Regression: with Roslyn 4.8 the server could not parse C# 13/14, and MSBuildLocator in a .NET 8
    /// process only found SDK 8, so net9.0+ projects loaded silently without any references.
    /// </summary>
    public class LanguageSupportTests : IDisposable
    {
        private const string ModernCode = """
            namespace Modern;

            public partial class Settings
            {
                public partial int Size { get; }

                public string Name
                {
                    get => field;
                    set => field = value.Trim();
                } = "";

                public int Use() => Name.Shout().Length + Size;
            }

            public partial class Settings
            {
                public partial int Size => 3;
            }

            public static class TextExtensions
            {
                extension(string text)
                {
                    public string Shout() => text.ToUpperInvariant();
                }
            }
            """;

        private readonly TestSolutionBuilder _builder = new();

        public void Dispose() => _builder.Dispose();

        [Fact]
        public async Task CSharp14_ExtensionMembersAreSearchableAndReferenced()
        {
            var service = TestServices.CreateSearchService(_builder.AddProject("Core", ("Settings.cs", ModernCode)).Build());

            var shout = (await service.SearchSymbolsAsync("Shout", _builder.SolutionPath, "method", ignoreCase: false)).ToList();
            var references = (await service.FindReferencesAsync("Shout", _builder.SolutionPath, includeDefinition: false)).ToList();

            Assert.Single(shout);
            Assert.Contains("Name.Shout()", Assert.Single(references).LineText);
        }

        [Fact]
        public async Task CSharp13_PartialPropertyIsOneSymbol()
        {
            var service = TestServices.CreateSearchService(_builder.AddProject("Core", ("Settings.cs", ModernCode)).Build());

            var size = (await service.SearchSymbolsAsync("Size", _builder.SolutionPath, "property", ignoreCase: false)).ToList();

            Assert.Single(size);
        }

        [Fact]
        public async Task CSharp14_CompilesWithoutErrors()
        {
            var compilation = await _builder.AddProject("Core", ("Settings.cs", ModernCode)).Build().Projects.Single().GetCompilationAsync();

            Assert.Empty(compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        /// <summary>
        /// Loads a real project through MSBuildWorkspace's BuildHost. Needs an installed .NET 10+ SDK.
        /// </summary>
        [Fact]
        [Trait("Category", "Integration")]
        public async Task MSBuildLoader_LoadsNet10ProjectWithReferences()
        {
            var directory = Path.Combine(_builder.Root, "Net10Lib");
            Directory.CreateDirectory(directory);
            var projectPath = Path.Combine(directory, "Net10Lib.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Settings.cs"), ModernCode);
            await RestoreAsync(projectPath);

            var loader = new MSBuildSolutionLoader(NullLogger<MSBuildSolutionLoader>.Instance);
            var solution = await loader.LoadAsync(projectPath, CancellationToken.None);

            var project = Assert.Single(solution.Projects);
            Assert.NotEmpty(project.MetadataReferences);
            Assert.Contains(project.Documents, d => d.Name == "Settings.cs");

            var compilation = await project.GetCompilationAsync();
            Assert.Empty(compilation!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        }

        private static async Task RestoreAsync(string projectPath)
        {
            using var process = Process.Start(new ProcessStartInfo("dotnet", $"restore \"{projectPath}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"dotnet restore failed:\n{output}");
        }
    }
}
