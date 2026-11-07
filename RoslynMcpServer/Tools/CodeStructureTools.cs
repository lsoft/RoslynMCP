using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using RoslynMcpServer.Services;
using System.ComponentModel;

namespace RoslynMcpServer.Tools
{
    [McpServerToolType]
    public class CodeStructureTools
    {
        [McpServerTool, Description("Member signatures with line numbers of a file or type, without bodies; far cheaper than reading the file")]
        public static Task<string> GetOutline(
            [Description(".sln or .csproj path")] string solutionPath,
            [Description("Absolute, solution-relative, unique or generated file name")] string? filePath = null,
            [Description("Instead of a file: type name or full name, all partial parts")] string? typeName = null,
            bool includePrivate = true,
            [Description("First sentence of XML docs")] bool includeDocs = true,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            return ToolRunner.RunAsync(serviceProvider, solutionPath, "building the outline", services =>
                services.GetRequiredService<CodeStructureService>().GetOutlineAsync(
                    solutionPath, filePath, typeName, new CodeStructureService.OutlineOptions(includePrivate, includeDocs), cancellationToken));
        }

        [McpServerTool, Description("Source-generator output, which has no file on disk: lists generated files, or returns one's text")]
        public static Task<string> GetGeneratedCode(
            [Description(".sln or .csproj path")] string solutionPath,
            [Description("Generated file name, e.g. RegexGenerator.g.cs; omit to list")] string? fileName = null,
            string? projectName = null,
            int startLine = 1,
            int maxLines = 200,
            IServiceProvider? serviceProvider = null,
            CancellationToken cancellationToken = default)
        {
            return ToolRunner.RunAsync(serviceProvider, solutionPath, "reading generated code", services =>
                services.GetRequiredService<CodeStructureService>().GetGeneratedCodeAsync(
                    solutionPath, fileName, projectName, startLine, maxLines, cancellationToken));
        }
    }
}
