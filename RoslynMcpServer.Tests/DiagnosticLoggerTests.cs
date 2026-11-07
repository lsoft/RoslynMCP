using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests
{
    public class DiagnosticLoggerTests
    {
        private readonly DiagnosticLogger _logger = new(NullLogger<DiagnosticLogger>.Instance);

        private sealed class Node
        {
            public Node? Self { get; set; }
        }

        [Fact]
        public async Task ReturnsOperationResult()
        {
            var result = await _logger.LoggedExecutionAsync("op", () => Task.FromResult(42), new { Path = "x" });
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task RethrowsOperationException()
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _logger.LoggedExecutionAsync<int>("op", () => throw new InvalidOperationException()));
        }

        [Fact]
        public async Task UnserializableParametersDoNotBreakTheOperation()
        {
            // Regression: a serialization failure while logging was thrown before the operation ran
            var cyclic = new Node();
            cyclic.Self = cyclic;

            var result = await _logger.LoggedExecutionAsync("op", () => Task.FromResult("ran"), cyclic);

            Assert.Equal("ran", result);
        }
    }
}
