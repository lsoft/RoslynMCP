using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tools
{
    /// <summary>Shared guard for tools: validates the solution path, turns exceptions into safe messages.</summary>
    internal static class ToolRunner
    {
        public const string InvalidPath = "Error: Invalid solution path provided.";

        public static async Task<string> RunAsync(
            IServiceProvider? serviceProvider, string solutionPath, string operation, Func<IServiceProvider, Task<string>> action)
        {
            try
            {
                var validator = serviceProvider?.GetService<SecurityValidator>();
                if (serviceProvider == null || validator == null || !validator.ValidateSolutionPath(solutionPath))
                    return InvalidPath;

                return await action(serviceProvider);
            }
            catch (OperationCanceledException)
            {
                return $"Error: {operation} was cancelled or timed out.";
            }
            catch (Exception ex)
            {
                // Log full error details but return a safe message
                serviceProvider?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ToolRunner)).LogError(ex, "Error during {Operation}", operation);
                return $"Error: An unexpected error occurred during {operation}.";
            }
        }
    }
}
