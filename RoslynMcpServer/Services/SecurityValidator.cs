using System.Text.RegularExpressions;

namespace RoslynMcpServer.Services
{
    public class SecurityValidator
    {
        private readonly HashSet<string> _allowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".sln", ".csproj" };
        private static readonly char[] _forbiddenChars = { '<', '>', '|', '?', '*', '"', '\0' };

        public bool ValidateSolutionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            // Only absolute paths, on Windows that means a drive letter
            if (!Path.IsPathFullyQualified(path))
                return false;

            // UNC and device paths (\\server\share, \\?\, \\.\) would let the server reach out to
            // network hosts, which on Windows also leaks the user's NTLM credentials
            if (path.StartsWith(@"\\") || path.StartsWith("//"))
                return false;

            if (path.IndexOfAny(_forbiddenChars) >= 0)
                return false;

            // A colon is only valid right after the drive letter; elsewhere it names an NTFS stream
            if (OperatingSystem.IsWindows() && path.IndexOf(':', 2) >= 0)
                return false;

            // Check for path traversal attempts; ".." inside a name like "my..app" is fine
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Contains(".."))
                return false;

            // Check file extension
            var extension = Path.GetExtension(path);
            if (!_allowedExtensions.Contains(extension))
                return false;

            // Verify file exists and is accessible
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public string SanitizeSearchPattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return "*";

            // Remove potentially dangerous characters
            return Regex.Replace(pattern, @"[^\w*?.]", "");
        }
    }
}
