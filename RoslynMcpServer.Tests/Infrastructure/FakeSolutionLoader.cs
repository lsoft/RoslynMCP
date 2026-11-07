using Microsoft.CodeAnalysis;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests.Infrastructure
{
    internal sealed class FakeSolutionLoader : ISolutionLoader
    {
        private readonly Func<string, CancellationToken, Task<Solution>> _load;
        private int _loadCount;

        public FakeSolutionLoader(Solution solution)
            : this((_, _) => Task.FromResult(solution))
        {
        }

        public FakeSolutionLoader(Func<string, CancellationToken, Task<Solution>> load)
        {
            _load = load;
        }

        public int LoadCount => Volatile.Read(ref _loadCount);

        public Task<Solution> LoadAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _loadCount);
            return _load(path, cancellationToken);
        }
    }
}
