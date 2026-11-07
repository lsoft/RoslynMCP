using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace RoslynMcpServer.Tests.Infrastructure
{
    /// <summary>
    /// Builds an in-memory solution (no MSBuild involved). Documents get file paths under a
    /// unique temp root; <see cref="WriteFiles"/> puts them on disk for tests that need it.
    /// </summary>
    internal sealed class TestSolutionBuilder : IDisposable
    {
        private static readonly MetadataReference[] References = CreateReferences();

        private readonly AdhocWorkspace _workspace = new();
        private readonly Dictionary<string, ProjectId> _projects = new();
        private Solution _solution;

        public TestSolutionBuilder()
        {
            Root = Path.Combine(Path.GetTempPath(), "RoslynMcpServer.Tests", Guid.NewGuid().ToString("N"));
            _solution = _workspace.CurrentSolution;
        }

        public string Root { get; }

        public string SolutionPath => Path.Combine(Root, "Test.sln");

        public TestSolutionBuilder AddProject(string name, params (string FileName, string Code)[] files)
        {
            return AddProject(name, Array.Empty<string>(), files);
        }

        public TestSolutionBuilder AddProject(string name, string[] projectReferences, params (string FileName, string Code)[] files)
        {
            return AddProject(name, name, projectReferences, Array.Empty<string>(), files);
        }

        /// <summary>
        /// Mimics how MSBuildWorkspace loads a multi-targeted project: one project per target framework,
        /// named "Name(tfm)", all sharing the project file and the documents. Each copy gets its own
        /// preprocessor symbol (net48 -> NET48). Reference a copy as "Name(tfm)".
        /// </summary>
        public TestSolutionBuilder AddMultiTargetedProject(string name, string[] frameworks, params (string FileName, string Code)[] files)
        {
            foreach (var framework in frameworks)
            {
                var symbol = framework.ToUpperInvariant().Replace('.', '_');
                AddProject($"{name}({framework})", name, Array.Empty<string>(), new[] { symbol }, files);
            }

            return this;
        }

        private TestSolutionBuilder AddProject(
            string name, string fileName, string[] projectReferences, string[] preprocessorSymbols, (string FileName, string Code)[] files)
        {
            var projectId = ProjectId.CreateNewId(name);
            _projects[name] = projectId;

            var info = ProjectInfo.Create(
                projectId,
                VersionStamp.Create(),
                name,
                name,
                LanguageNames.CSharp,
                filePath: Path.Combine(Root, fileName, fileName + ".csproj"),
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                parseOptions: new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: preprocessorSymbols),
                projectReferences: projectReferences.Select(r => new ProjectReference(_projects[r])),
                metadataReferences: References);

            _solution = _solution.AddProject(info);

            foreach (var (documentName, code) in files)
            {
                var documentId = DocumentId.CreateNewId(projectId, documentName);
                _solution = _solution.AddDocument(
                    documentId, documentName, SourceText.From(code), filePath: Path.Combine(Root, fileName, documentName));
            }

            return this;
        }

        /// <summary>Adds an analyzer or source generator reference to a project added before.</summary>
        public TestSolutionBuilder AddAnalyzerReference(string projectName, Microsoft.CodeAnalysis.Diagnostics.AnalyzerReference reference)
        {
            _solution = _solution.AddAnalyzerReference(_projects[projectName], reference);
            return this;
        }

        public Solution Build() => _solution;

        /// <summary>Writes the .sln, the project files and all documents to disk.</summary>
        public TestSolutionBuilder WriteFiles()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(SolutionPath, string.Empty);

            foreach (var project in _solution.Projects)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(project.FilePath!)!);
                File.WriteAllText(project.FilePath!, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

                foreach (var document in project.Documents)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(document.FilePath!)!);
                    File.WriteAllText(document.FilePath!, document.GetTextAsync().Result.ToString());
                }
            }

            return this;
        }

        public void Dispose()
        {
            _workspace.Dispose();
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort, temp files are not worth failing a test over
            }
        }

        private static MetadataReference[] CreateReferences()
        {
            var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            return new[] { "System.Private.CoreLib.dll", "System.Runtime.dll", "System.Console.dll", "System.Collections.dll", "System.Linq.dll" }
                .Select(file => Path.Combine(runtimeDirectory, file))
                .Where(File.Exists)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();
        }
    }
}
