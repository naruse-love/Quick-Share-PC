using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace QuickShare.PC.Models
{
    public class QuickShareDirectory
    {
        public const int FILE_SYSTEM_UNIX = 0;
        public const int FILE_SYSTEM_WINDOWS = 1;

        public string Path { get; }
        public int FileSystem { get; }

        public QuickShareDirectory(string path, int fileSystem)
        {
            FileSystem = fileSystem;
            Path = NormalizePath(path);
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            if (path == "/") return "/";

            string separator = (FileSystem == FILE_SYSTEM_UNIX) ? "/" : "\\";

            if (FileSystem == FILE_SYSTEM_WINDOWS)
            {
                path = path.Replace("/", separator);
            }

            if (FileSystem == FILE_SYSTEM_WINDOWS && Regex.IsMatch(path, "^[A-Za-z]:$"))
            {
                path += separator;
            }

            if (path != separator && !path.EndsWith(separator))
            {
                path += separator;
            }

            return path;
        }

        public QuickShareDirectory? Parent()
        {
            if (FileSystem == FILE_SYSTEM_UNIX)
            {
                if (Path == "/") return null;
                string trimmed = Path.Substring(0, Path.Length - 1);
                int idx = trimmed.LastIndexOf('/');
                string parentPath = (idx <= 0) ? "/" : trimmed.Substring(0, idx + 1);
                return new QuickShareDirectory(parentPath, FileSystem);
            }
            else
            {
                if (Path == "/") return null;
                string norm = Path.Substring(0, Path.Length - 1);
                int idx = norm.LastIndexOf('\\');
                if (idx <= 2) return new QuickShareDirectory("/", FileSystem);
                return new QuickShareDirectory(norm.Substring(0, idx + 1), FileSystem);
            }
        }

        public QuickShareDirectory Append(string child)
        {
            if (string.IsNullOrEmpty(child)) return this;
            while (child.StartsWith("/") || child.StartsWith("\\"))
            {
                child = child.Substring(1);
            }
            return new QuickShareDirectory(Path + child, FileSystem);
        }

        public string GenerateTransferPath(string file, QuickShareDirectory remote)
        {
            string localSep = (this.FileSystem == FILE_SYSTEM_UNIX) ? "/" : "\\";
            string remoteSep = (remote.FileSystem == FILE_SYSTEM_UNIX) ? "/" : "\\";

            string normalizedFile = this.FileSystem == FILE_SYSTEM_UNIX ? file : file.Replace("/", localSep);

            string localFolder = this.Path;
            string relativePath;

            if (normalizedFile.StartsWith(localFolder, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = normalizedFile.Substring(localFolder.Length);
            }
            else
            {
                if (normalizedFile.StartsWith(localSep))
                {
                    relativePath = normalizedFile.Substring(1);
                }
                else
                {
                    relativePath = normalizedFile;
                }
            }

            string[] segments = relativePath.Split(new[] { localSep }, StringSplitOptions.None);
            List<string> sanitizedSegments = new List<string>();

            foreach (var seg in segments)
            {
                if (string.IsNullOrEmpty(seg)) continue;
                // Replace invalid characters: \ : * ? " < > | with _
                string sanitized = Regex.Replace(seg, @"[\\:*?""<>|]", "_");
                sanitizedSegments.Add(sanitized);
            }

            string sanitizedRelative = string.Join(remoteSep, sanitizedSegments);

            if (string.IsNullOrEmpty(sanitizedRelative))
            {
                return remote.Path;
            }
            else
            {
                return remote.Path + sanitizedRelative;
            }
        }

        public static int GetCurrentFileSystem()
        {
            return System.IO.Path.DirectorySeparatorChar == '\\' ? FILE_SYSTEM_WINDOWS : FILE_SYSTEM_UNIX;
        }

        public override string ToString()
        {
            return $"Directory{{path='{Path}', fileSystem={(FileSystem == FILE_SYSTEM_UNIX ? "UNIX" : "WINDOWS")}}}";
        }
    }
}
