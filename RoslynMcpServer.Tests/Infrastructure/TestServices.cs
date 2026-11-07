using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests.Infrastructure
{
    internal static class TestServices
    {
        /// <summary>The same registrations as Program.cs, with the MSBuild loader swapped for a fake.</summary>
        public static ServiceProvider Create(Solution solution)
        {
            return Create(new FakeSolutionLoader(solution));
        }

        public static ServiceProvider Create(ISolutionLoader loader)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.AddSingleton(loader);
            services.AddSingleton<CodeAnalysisService>();
            services.AddSingleton<SymbolSearchService>();
            services.AddSingleton<CodeStructureService>();
            services.AddSingleton<CodeQualityService>();
            services.AddSingleton<SecurityValidator>();
            return services.BuildServiceProvider();
        }

        public static SymbolSearchService CreateSearchService(Solution solution)
        {
            return Create(solution).GetRequiredService<SymbolSearchService>();
        }
    }
}
