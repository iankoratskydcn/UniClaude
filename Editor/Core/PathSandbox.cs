using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace UniClaude.Editor.MCP
{
    /// <summary>
    /// Centralized path validation utility that prevents path traversal attacks by ensuring
    /// all resolved paths remain within the Unity project root.
    /// </summary>
    public static class PathSandbox
    {
        /// <summary>
        /// Path comparison mode: case-insensitive on Windows, case-sensitive elsewhere.
        /// </summary>
        static readonly StringComparison PathComparison =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        /// <summary>
        /// Project-relative segments that may never be written to via the file MCP tools.
        /// Each entry is matched as either an exact path or a directory prefix.
        /// </summary>
        static readonly string[] BlockedWritePrefixes =
        {
            ".git",
            "Library",
            "Logs",
            "Temp",
            "obj",
            "Build",
            "Builds",
            "Packages/manifest.json",
            "Packages/packages-lock.json",
            "Packages/com.arcforge.uniclaude",
        };

        /// <summary>
        /// Path prefixes that may never be read via the file MCP tools.
        /// </summary>
        static readonly string[] BlockedReadPrefixes =
        {
            ".git",
            "Library",
            "Logs",
            "Temp",
            "obj",
        };

        /// <summary>
        /// Gets the absolute, canonicalized path to the Unity project root directory.
        /// </summary>
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// Resolves a relative path to a canonical absolute path within the project root.
        /// Suitable for read operations.
        /// </summary>
        /// <param name="relativePath">The path relative to the project root. Must not be null, empty, or absolute.</param>
        /// <returns>The canonical absolute path within the project root.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="relativePath"/> is null, empty, or is an absolute path.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the resolved path escapes the project root (e.g. via <c>../</c> traversal,
        /// a symlink target outside the root, or NUL bytes / control characters).
        /// </exception>
        public static string Resolve(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
                throw new ArgumentException("Path must not be null or empty.", nameof(relativePath));

            var blockedRead = MatchPrefix(relativePath, BlockedReadPrefixes);
            if (blockedRead != null)
                throw new InvalidOperationException(
                    $"Reading from '{blockedRead}' is not allowed via MCP file tools.");

            // Reject NUL and other control bytes outright; they have no business in a path
            // and certain platforms truncate at NUL, which would defeat the prefix check.
            for (int i = 0; i < relativePath.Length; i++)
            {
                var c = relativePath[i];
                if (c == '\0' || (c < 0x20 && c != '\t'))
                    throw new ArgumentException(
                        "Path contains a NUL byte or other control character.",
                        nameof(relativePath));
            }

            if (relativePath.StartsWith("/") || relativePath.StartsWith("\\") ||
                (relativePath.Length >= 2 && relativePath[1] == ':'))
                throw new ArgumentException($"Path '{relativePath}' must be relative, not absolute.", nameof(relativePath));

            // Normalize backslashes so traversal via '\' is caught on all platforms.
            var normalized = relativePath.Replace('\\', '/');

            var root = ProjectRoot;
            var combined = Path.Combine(root, normalized);
            var canonical = Path.GetFullPath(combined);

            var rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!canonical.StartsWith(rootWithSeparator, PathComparison) &&
                !string.Equals(canonical, root, PathComparison))
                throw new InvalidOperationException($"Path '{relativePath}' resolves outside the project root. Access denied.");

            // Defence in depth: walk every directory component along the path and refuse if
            // any of them is a symlink (or junction) that points outside the project root.
            // Path.GetFullPath only resolves '.'/'..' segments — it does NOT follow symlinks,
            // so an in-tree symlink to /etc would otherwise pass the prefix check above.
            EnsureNoSymlinkEscape(root, canonical);

            return canonical;
        }

        /// <summary>
        /// Resolves a relative path to a canonical absolute path within the project root,
        /// additionally checking that the path is not blocked for write or delete operations.
        /// </summary>
        /// <param name="relativePath">The path relative to the project root. Must not be null, empty, or absolute.</param>
        /// <returns>The canonical absolute path within the project root.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="relativePath"/> is null, empty, or is an absolute path.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the resolved path escapes the project root, or targets a path segment
        /// that is blocked for write operations.
        /// </exception>
        public static string ResolveWritable(string relativePath)
        {
            var canonical = Resolve(relativePath);

            var blocked = MatchBlockedPrefix(relativePath);
            if (blocked != null)
                throw new InvalidOperationException(
                    $"Writing to '{blocked}' is not allowed. UniClaude refuses to modify Unity-managed " +
                    "or UniClaude-managed locations through the file MCP tools — use the appropriate " +
                    "Unity API (or the Settings UI) instead.");

            return canonical;
        }

        /// <summary>
        /// Validates an AssetDatabase destination path (e.g. "Assets/.../file.ext" or
        /// "Packages/.../file.ext") using the same sandbox rules as file writes.
        /// </summary>
        public static string ValidateAssetPath(string assetPath)
        {
            return ResolveWritable(assetPath);
        }

        /// <summary>
        /// Strips the project root prefix from an absolute path, returning a project-relative path.
        /// If the path does not start with the project root, it is returned as-is.
        /// </summary>
        /// <param name="absolutePath">The absolute file path to make relative.</param>
        /// <returns>
        /// The path relative to the project root, or <paramref name="absolutePath"/> unchanged
        /// if it does not start with the project root.
        /// </returns>
        public static string MakeRelative(string absolutePath)
        {
            var root = ProjectRoot;

            var withSep = root + Path.DirectorySeparatorChar;
            if (absolutePath.StartsWith(withSep, PathComparison))
                return absolutePath.Substring(withSep.Length);

            var withAltSep = root + Path.AltDirectorySeparatorChar;
            if (absolutePath.StartsWith(withAltSep, PathComparison))
                return absolutePath.Substring(withAltSep.Length);

            return absolutePath;
        }

        /// <summary>
        /// Returns the matching blocked-prefix string when the given path targets a location
        /// that must not be written to; null otherwise.
        /// </summary>
        /// <param name="relativePath">The path relative to the project root.</param>
        /// <returns>The blocked segment name, or <c>null</c> if the path is allowed.</returns>
        static string MatchBlockedPrefix(string relativePath)
        {
            return MatchPrefix(relativePath, BlockedWritePrefixes);
        }

        static string MatchPrefix(string relativePath, string[] prefixes)
        {
            var normalized = relativePath.Replace('\\', '/');
            // Trim leading "./" so users can't bypass checks with "./Library/foo".
            while (normalized.StartsWith("./"))
                normalized = normalized.Substring(2);

            foreach (var prefix in prefixes)
            {
                if (normalized.Equals(prefix, PathComparison))
                    return prefix;
                if (normalized.StartsWith(prefix + "/", PathComparison))
                    return prefix;
            }

            return null;
        }

        /// <summary>
        /// Walks each directory component of the resolved path and ensures none of them is a
        /// symlink whose target lies outside the project root. This complements the lexical
        /// prefix check, which on its own does not follow symlinks.
        /// </summary>
        /// <param name="root">Absolute, canonical project root.</param>
        /// <param name="resolvedPath">Absolute, canonical path inside the project root.</param>
        static void EnsureNoSymlinkEscape(string root, string resolvedPath)
        {
            // Walk from the project root down to the leaf, examining each component once.
            // Both DirectoryInfo and FileInfo expose LinkTarget on .NET 6+; if it is non-null
            // the entry is a reparse point / symlink and we resolve it to verify containment.
            try
            {
                var rootFull = Path.GetFullPath(root);
                var current = resolvedPath;

                while (!string.IsNullOrEmpty(current) &&
                       !string.Equals(current, rootFull, PathComparison))
                {
                    if (Directory.Exists(current))
                    {
                        var di = new DirectoryInfo(current);
                        if (di.LinkTarget != null)
                        {
                            var target = ResolveLinkChain(current);
                            EnsureContained(rootFull, target, current);
                        }
                    }
                    else if (File.Exists(current))
                    {
                        var fi = new FileInfo(current);
                        if (fi.LinkTarget != null)
                        {
                            var target = ResolveLinkChain(current);
                            EnsureContained(rootFull, target, current);
                        }
                    }

                    var parent = Path.GetDirectoryName(current);
                    if (string.IsNullOrEmpty(parent) ||
                        string.Equals(parent, current, PathComparison))
                        break;
                    current = parent;
                }
            }
            catch (UnauthorizedAccessException) { /* Cannot probe — fall through, the OS will reject */ }
            catch (IOException) { /* Stale handles, transient — caller will see the real error */ }
        }

        static string ResolveLinkChain(string path)
        {
            // Follow up to 40 hops (matches Linux's MAXSYMLINKS) before giving up.
            string current = path;
            for (int i = 0; i < 40; i++)
            {
                FileSystemInfo info = Directory.Exists(current)
                    ? (FileSystemInfo)new DirectoryInfo(current)
                    : new FileInfo(current);
                if (info.LinkTarget == null)
                    return Path.GetFullPath(current);

                var next = info.LinkTarget;
                // LinkTarget can be relative; resolve against the link's parent directory.
                if (!Path.IsPathRooted(next))
                    next = Path.Combine(Path.GetDirectoryName(current) ?? string.Empty, next);
                current = Path.GetFullPath(next);
            }
            throw new InvalidOperationException($"Symlink chain at '{path}' exceeds 40 hops.");
        }

        static void EnsureContained(string rootFull, string target, string offendingPath)
        {
            var withSeparator = rootFull + Path.DirectorySeparatorChar;
            if (!target.StartsWith(withSeparator, PathComparison) &&
                !string.Equals(target, rootFull, PathComparison))
                throw new InvalidOperationException(
                    $"Path '{offendingPath}' is a symlink that points outside the project root " +
                    $"('{target}'). Access denied.");
        }
    }
}
