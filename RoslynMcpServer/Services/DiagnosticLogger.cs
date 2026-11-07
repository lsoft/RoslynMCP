using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace RoslynMcpServer.Services
{
    public class DiagnosticLogger
    {
        private readonly ILogger _logger;
        
        public DiagnosticLogger(ILogger<DiagnosticLogger> logger)
        {
            _logger = logger;
        }
        
        public async Task<T> LoggedExecutionAsync<T>(
            string operationName, 
            Func<Task<T>> operation,
            object? parameters = null)
        {
            var stopwatch = Stopwatch.StartNew();
            var operationId = Guid.NewGuid().ToString("N")[..8];
            
            _logger.LogInformation(
                "Starting {OperationName} [{OperationId}] with parameters: {Parameters}",
                operationName, operationId, SerializeParameters(parameters));
            
            try
            {
                var result = await operation();
                
                _logger.LogInformation(
                    "Completed {OperationName} [{OperationId}] in {ElapsedMs}ms",
                    operationName, operationId, stopwatch.ElapsedMilliseconds);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed {OperationName} [{OperationId}] after {ElapsedMs}ms",
                    operationName, operationId, stopwatch.ElapsedMilliseconds);
                throw;
            }
        }

        // Logging must never break the operation itself (cycles, unsupported types, ...)
        private static string SerializeParameters(object? parameters)
        {
            try
            {
                return JsonSerializer.Serialize(parameters);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
            {
                return parameters?.ToString() ?? "null";
            }
        }
    }
}