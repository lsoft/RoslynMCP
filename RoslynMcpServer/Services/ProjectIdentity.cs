using Microsoft.CodeAnalysis;

namespace RoslynMcpServer.Services
{
    /// <summary>
    /// MSBuildWorkspace loads a multi-targeted project once per target framework, as "Name(tfm)",
    /// all sharing one project file and the same documents. Results are reported per project file,
    /// otherwise every symbol, reference and method shows up once per target framework.
    /// </summary>
    public static class ProjectIdentity
    {
        public static string GetKey(Project project)
        {
            return project.FilePath ?? project.Name;
        }

        public static string GetDisplayName(Project project)
        {
            var name = project.Name;
            var open = name.LastIndexOf('(');
            return open > 0 && name.EndsWith(')') ? name[..open] : name;
        }

        // Identifies a source symbol independently of the target framework it was compiled for
        public static string GetSymbolKey(ISymbol symbol)
        {
            var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
            return $"{symbol.Kind}|{symbol.ToDisplayString()}|{location?.SourceTree?.FilePath}|{location?.SourceSpan.Start}";
        }
    }
}
