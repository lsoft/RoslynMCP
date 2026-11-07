using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoslynMcpServer.Services;
using System.ComponentModel;

namespace RoslynMcpServer.Tools
{
    [McpServerToolType]
    public class CodeQualityTools
    {
        [McpServerTool, Description("Compiler errors and warnings without a build, reflecting edits on disk immediately")]
        public static Task<string> GetDiagnostics(
            [Description(".sln or .csproj path")] string solutionPath,
            [Description("Absolute, solution-relative or unique file name")] string? filePath = null,
            string? projectName = null,
            [Description("error | warning | info")] string minSeverity = "warning",
            [Description("Also run the project's analyzers and .editorconfig rules; slower, prefer with filePath")] bool includeAnalyzers = false,
            int maxResults = 50,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            return ToolRunner.RunAsync(serviceProvider, solutionPath, "collecting diagnostics", services =>
                services.GetRequiredService<CodeQualityService>().GetDiagnosticsAsync(
                    solutionPath, filePath, projectName, minSeverity, includeAnalyzers, maxResults, cancellationToken));
        }

        [McpServerTool, Description("Dead code: types and members without references in the solution (overrides, interface implementations and attributed members are skipped)")]
        public static Task<string> FindUnusedSymbols(
            [Description(".sln or .csproj path")] string solutionPath,
            string? projectName = null,
            [Description("Also check public/protected API")] bool includePublic = false,
            int maxResults = 50,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            return ToolRunner.RunAsync(serviceProvider, solutionPath, "finding unused symbols", services =>
                services.GetRequiredService<CodeQualityService>().FindUnusedSymbolsAsync(
                    solutionPath, projectName, includePublic, maxResults, cancellationToken));
        }
    }
}
