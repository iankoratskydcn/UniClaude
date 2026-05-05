using System;
using System.IO;
using NUnit.Framework;
using UniClaude.Editor.MCP;

namespace UniClaude.Editor.Tests.MCP
{
    /// <summary>
    /// Tests for <see cref="PathSandbox"/>, verifying that path resolution enforces
    /// the project root sandbox boundary and blocks writes to protected directories.
    /// </summary>
    public class PathSandboxTests
    {
        // -------------------------------------------------------------------------
        // Resolve
        // -------------------------------------------------------------------------

        [Test]
        public void Resolve_ValidRelativePath_ReturnsAbsolutePath()
        {
            var result = PathSandbox.Resolve("Assets/Scripts/Test.cs");

            Assert.That(result, Does.EndWith("Assets" + Path.DirectorySeparatorChar + "Scripts" + Path.DirectorySeparatorChar + "Test.cs"));
        }

        [Test]
        public void Resolve_PathWithTraversal_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("../../../etc/passwd"));
        }

        [Test]
        public void Resolve_NestedTraversal_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("Assets/../../secret.txt"));
        }

        [Test]
        public void Resolve_AbsoluteUnixPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve("/etc/passwd"));
        }

        [Test]
        public void Resolve_AbsoluteWindowsPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve("C:\\Windows\\System32"));
        }

        [Test]
        public void Resolve_NullPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve(null));
        }

        [Test]
        public void Resolve_EmptyPath_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve(""));
        }

        [Test]
        public void Resolve_InternalTraversalStaysInProject_Succeeds()
        {
            var expected = Path.GetFullPath(Path.Combine(PathSandbox.ProjectRoot, "Assets", "Scripts", "Test.cs"));

            var result = PathSandbox.Resolve("Assets/../Assets/Scripts/Test.cs");

            Assert.AreEqual(expected, result);
        }

        // -------------------------------------------------------------------------
        // ResolveWritable
        // -------------------------------------------------------------------------

        [Test]
        public void ResolveWritable_ValidPath_ReturnsAbsolutePath()
        {
            var expected = Path.GetFullPath(Path.Combine(PathSandbox.ProjectRoot, "Assets", "Scripts", "Test.cs"));

            var result = PathSandbox.ResolveWritable("Assets/Scripts/Test.cs");

            Assert.AreEqual(expected, result);
        }

        [Test]
        public void ResolveWritable_GitDirectory_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git/config"));

            Assert.That(ex.Message, Does.Contain(".git"));
        }

        [Test]
        public void ResolveWritable_GitRoot_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git"));
        }

        [Test]
        public void ResolveWritable_GitNestedPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git/hooks/pre-commit"));
        }

        [Test]
        public void ResolveWritable_TraversalPath_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable("../outside.txt"));
        }

        // -------------------------------------------------------------------------
        // MakeRelative
        // -------------------------------------------------------------------------

        [Test]
        public void MakeRelative_ProjectPath_StripsRoot()
        {
            var absolute = Path.Combine(PathSandbox.ProjectRoot, "Assets", "Scripts", "Test.cs");

            var result = PathSandbox.MakeRelative(absolute);

            Assert.AreEqual(Path.Combine("Assets", "Scripts", "Test.cs"), result);
        }

        [Test]
        public void MakeRelative_NonProjectPath_ReturnsAsIs()
        {
            var outsidePath = "/some/other/path";

            var result = PathSandbox.MakeRelative(outsidePath);

            Assert.AreEqual(outsidePath, result);
        }

        // -------------------------------------------------------------------------
        // Backslash handling
        // -------------------------------------------------------------------------

        [Test]
        public void Resolve_BackslashRelativePath_Succeeds()
        {
            var result = PathSandbox.Resolve("Assets\\Scripts\\Test.cs");

            Assert.That(result, Does.Contain("Assets"));
            Assert.That(result, Does.Contain("Test.cs"));
        }

        [Test]
        public void Resolve_BackslashTraversal_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("Assets\\..\\..\\secret.txt"));
        }

        [Test]
        public void ResolveWritable_GitWithBackslash_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git\\config"));
        }

        [Test]
        public void ResolveWritable_GitWithMixedSlashes_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => PathSandbox.ResolveWritable(".git\\hooks/pre-commit"));
        }

        // -------------------------------------------------------------------------
        // Newly blocked write locations
        // -------------------------------------------------------------------------

        [Test]
        public void ResolveWritable_LibraryDirectory_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                PathSandbox.ResolveWritable("Library/UniClaude/settings.json"));
            Assert.That(ex.Message, Does.Contain("Library"));
        }

        // -------------------------------------------------------------------------
        // Newly blocked read locations
        // -------------------------------------------------------------------------

        [Test]
        public void Resolve_GitDirectory_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve(".git/config"));
            Assert.That(ex.Message, Does.Contain(".git"));
        }

        [Test]
        public void Resolve_LibraryDirectory_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("Library/somefile.txt"));
            Assert.That(ex.Message, Does.Contain("Library"));
        }

        [Test]
        public void Resolve_LogsDirectory_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => PathSandbox.Resolve("Logs/some.log"));
            Assert.That(ex.Message, Does.Contain("Logs"));
        }

        [Test]
        public void ResolveWritable_PackagesManifest_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                PathSandbox.ResolveWritable("Packages/manifest.json"));
        }

        [Test]
        public void ResolveWritable_PackagesLockFile_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                PathSandbox.ResolveWritable("Packages/packages-lock.json"));
        }

        [Test]
        public void ResolveWritable_UniClaudePackageDirectory_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                PathSandbox.ResolveWritable("Packages/com.arcforge.uniclaude/Editor/foo.cs"));
        }

        [Test]
        public void ResolveWritable_BlockedPathWithDotSlashPrefix_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                PathSandbox.ResolveWritable("./Packages/manifest.json"));
        }

        [Test]
        public void ResolveWritable_OtherPackagesAreFine()
        {
            // Packages/com.unity.something/foo.cs is not under the UniClaude package and is
            // not the manifest/lock file — write should be allowed by the sandbox layer.
            // (The OS may still block it; we only verify the sandbox check.)
            Assert.DoesNotThrow(() => PathSandbox.ResolveWritable("Packages/com.unity.test/foo.cs"));
        }

        // -------------------------------------------------------------------------
        // Control characters
        // -------------------------------------------------------------------------

        [Test]
        public void Resolve_NulByte_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve("Assets/test\0evil"));
        }

        [Test]
        public void Resolve_ControlCharacter_Throws()
        {
            Assert.Throws<ArgumentException>(() => PathSandbox.Resolve("Assets/test\x01evil"));
        }

        [Test]
        public void Resolve_TabIsAllowed()
        {
            // Tab in a filename is valid (if unusual); ensure we don't over-block.
            Assert.DoesNotThrow(() => PathSandbox.Resolve("Assets/with\tname.cs"));
        }
    }
}
