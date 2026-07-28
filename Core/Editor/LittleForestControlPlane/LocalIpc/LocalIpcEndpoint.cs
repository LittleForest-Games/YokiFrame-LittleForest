using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YokiFrame
{
    /// <summary>
    /// Canonical Windows project identity and Local IPC endpoint builder.
    /// </summary>
    public static class LocalIpcEndpoint
    {
        public const string PipeNamePrefix =
            "YokiFrame.ControlPlane.v1.";

        public static string BuildPipeName(
            string projectRoot,
            string engineId)
        {
            if (!CommandBridgeProtocol.IsSafeIdentifier(engineId))
            {
                throw new ArgumentException(
                    "engineId must be a safe command bridge identifier.",
                    nameof(engineId));
            }

            return PipeNamePrefix +
                   ComputeProjectRootHash(projectRoot) +
                   "." +
                   engineId;
        }

        public static string ComputeProjectRootHash(string projectRoot)
        {
            var normalized = NormalizeProjectRoot(projectRoot);
            using (var sha256 = SHA256.Create())
            {
                var digest =
                    sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                var builder = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++)
                {
                    builder.Append(digest[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public static string NormalizeProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException(
                    "Project root is required.",
                    nameof(projectRoot));

            var fullPath = Path.GetFullPath(projectRoot);
            var root = Path.GetPathRoot(fullPath) ?? string.Empty;
            if (!string.Equals(
                    fullPath,
                    root,
                    StringComparison.OrdinalIgnoreCase))
            {
                fullPath = fullPath.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            }

            return fullPath.Replace('\\', '/').ToUpperInvariant();
        }
    }
}
