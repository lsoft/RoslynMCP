using RoslynMcpServer.Services;

namespace RoslynMcpServer.Tests
{
    public class SecurityValidatorTests : IDisposable
    {
        private readonly SecurityValidator _validator = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "RoslynMcpServer.Tests", Guid.NewGuid().ToString("N"));

        public SecurityValidatorTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        private string CreateFile(string relativePath)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
            return path;
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ValidateSolutionPath_RejectsEmpty(string? path)
        {
            Assert.False(_validator.ValidateSolutionPath(path!));
        }

        [Theory]
        [InlineData("App.sln")]
        [InlineData("App.csproj")]
        public void ValidateSolutionPath_AcceptsExistingSolutionOrProject(string fileName)
        {
            Assert.True(_validator.ValidateSolutionPath(CreateFile(fileName)));
        }

        [Fact]
        public void ValidateSolutionPath_ExtensionIsCaseInsensitive()
        {
            Assert.True(_validator.ValidateSolutionPath(CreateFile("App.SLN")));
        }

        [Theory]
        [InlineData("App.txt")]
        [InlineData("App.sln.exe")]
        [InlineData("App")]
        public void ValidateSolutionPath_RejectsOtherExtensions(string fileName)
        {
            Assert.False(_validator.ValidateSolutionPath(CreateFile(fileName)));
        }

        [Fact]
        public void ValidateSolutionPath_RejectsMissingFile()
        {
            Assert.False(_validator.ValidateSolutionPath(Path.Combine(_root, "Missing.sln")));
        }

        [Fact]
        public void ValidateSolutionPath_RejectsRelativePath()
        {
            CreateFile("App.sln");
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, Path.Combine(_root, "App.sln"));

            Assert.False(_validator.ValidateSolutionPath(relative));
            Assert.False(_validator.ValidateSolutionPath("App.sln"));
        }

        [Fact]
        public void ValidateSolutionPath_RejectsTraversalSegments()
        {
            CreateFile("App.sln");
            var traversal = Path.Combine(_root, "sub", "..", "App.sln");

            Assert.False(_validator.ValidateSolutionPath(traversal));
        }

        [Fact]
        public void ValidateSolutionPath_AllowsDotsInsideNames()
        {
            // Regression: any ".." substring used to be rejected
            Assert.True(_validator.ValidateSolutionPath(CreateFile(Path.Combine("my..app", "App.sln"))));
        }

        [Fact]
        public void ValidateSolutionPath_AllowsTildeInsideNames()
        {
            // Regression: "~" was rejected, which broke Windows 8.3 short names like PROGRA~1
            Assert.True(_validator.ValidateSolutionPath(CreateFile(Path.Combine("REPO~1", "App.sln"))));
        }

        [Theory]
        [InlineData(@"\\server\share\App.sln")]
        [InlineData("//server/share/App.sln")]
        [InlineData(@"\\?\C:\App.sln")]
        [InlineData(@"\\.\C:\App.sln")]
        public void ValidateSolutionPath_RejectsNetworkAndDevicePaths(string path)
        {
            Assert.False(_validator.ValidateSolutionPath(path));
        }

        [Fact]
        public void ValidateSolutionPath_RejectsWildcards()
        {
            CreateFile("App.sln");
            Assert.False(_validator.ValidateSolutionPath(Path.Combine(_root, "*.sln")));
        }

        [Fact]
        public void ValidateSolutionPath_RejectsAlternateDataStreams()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var path = CreateFile("App.sln");
            Assert.False(_validator.ValidateSolutionPath(path + ":stream.sln"));
        }

        [Fact]
        public void ValidateSolutionPath_AcceptsUnixAbsolutePaths()
        {
            // Regression: the old regex demanded a drive letter, so nothing passed on Linux/macOS/Docker
            if (OperatingSystem.IsWindows())
                return;

            Assert.True(_validator.ValidateSolutionPath(CreateFile("App.sln")));
        }

        [Theory]
        [InlineData(null, "*")]
        [InlineData("", "*")]
        [InlineData("  ", "*")]
        [InlineData("User*", "User*")]
        [InlineData("Get?User", "Get?User")]
        [InlineData("My.Namespace.Type", "My.Namespace.Type")]
        [InlineData("Name_With_Underscores", "Name_With_Underscores")]
        [InlineData("a;b|c", "abc")]
        [InlineData("(.*)+", ".*")]
        [InlineData("<script>", "script")]
        public void SanitizeSearchPattern(string? pattern, string expected)
        {
            Assert.Equal(expected, _validator.SanitizeSearchPattern(pattern!));
        }
    }
}
