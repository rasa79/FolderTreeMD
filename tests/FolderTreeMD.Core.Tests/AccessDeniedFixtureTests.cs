using System.Security.AccessControl;
using System.Security.Principal;

namespace FolderTreeMD.Core.Tests;

/// <summary>
/// The access-denied contract of <c>UI_SPEC.md</c> §5/§6 against the **real filesystem**: a deny-read ACE
/// is applied to a fixture directory and the engine runs with its production enumeration calls.
/// </summary>
/// <remarks>
/// These are the regression tests for limitation **L10**. The seam-based tests introduced with LEARN[5]
/// replace the enumeration delegates with ones that throw, which is exactly why they could not see that
/// <see cref="EnumerationOptions.IgnoreInaccessible"/> — whose framework default is <c>true</c> — stopped
/// the real enumeration from ever throwing: the engine's recovery ran only against the fakes. M6's fix
/// turns that option off, and these tests exercise the contract through the framework call.
///
/// The fixture applies the ACE to the *current user*, then verifies that the deny actually blocks this
/// process. A token that can read the directory anyway (an elevated or backup-privileged runner) cannot
/// exercise the contract, and the test says so instead of failing with a misleading assertion message.
/// </remarks>
public sealed class AccessDeniedFixtureTests
{
    /// <summary>
    /// A denied child folder is announced with the §5 <c>(access denied)</c> line, and its siblings are
    /// still listed — the per-directory recovery the seam tests cover, now through the real OS call.
    /// </summary>
    [Fact]
    public void DeniedChildFolder_IsMarkedAndSiblingsContinue()
    {
        using var tree = new TestTreeBuilder();
        tree.AddDirectory("denied");
        tree.AddFile("denied/secret.txt");
        tree.AddDirectory("allowed");
        tree.AddFile("allowed/visible.txt");

        using (new DeniedDirectory(tree.FullPath("denied")))
        {
            string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

            Assert.Equal(
                $"- [{tree.RootName}]\n" +
                "    - [allowed]\n" +
                "        - visible.txt\n" +
                "    - [denied] (access denied)\n",
                markdown);
        }
    }

    /// <summary>
    /// A denied subtree makes the parent's folder sum partial, so rule 7's tilde appears — the half of
    /// L10 that silently under-reported a whole subtree before the fix.
    /// </summary>
    [Fact]
    public void DeniedChildFolder_MakesTheParentSumPartial()
    {
        using var tree = new TestTreeBuilder();
        tree.AddDirectory("denied");
        tree.AddFileOfSize("denied/secret.bin", 4_096);
        tree.AddDirectory("allowed");
        tree.AddFileOfSize("allowed/visible.bin", 10);

        using (new DeniedDirectory(tree.FullPath("denied")))
        {
            var options = new ListingOptions { ShowFileSizes = true, ShowFolderSizes = true, ShowAttributes = false };
            string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

            Assert.Equal(
                $"- [{tree.RootName}] (~10 B)\n" +
                "    - [allowed] (10 B)\n" +
                "        - visible.bin (10 B)\n" +
                "    - [denied] (access denied)\n",
                markdown);
        }
    }

    /// <summary>
    /// An unreadable **root** propagates instead of being rendered (rule 10, D5) — the case that makes
    /// §7's exit code 2 reachable for a folder that exists but cannot be listed.
    /// </summary>
    [Fact]
    public void DeniedRoot_Throws()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("inside.txt");

        using (new DeniedDirectory(tree.Root))
        {
            Assert.Throws<UnauthorizedAccessException>(() =>
                MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None));
        }
    }

    /// <summary>Options with both annotation settings off, so a failure points at the contract, not a suffix.</summary>
    /// <returns>Listing options for the access-denied tests.</returns>
    private static ListingOptions BaseOptions() => new() { ShowFileSizes = false, ShowAttributes = false };

    /// <summary>
    /// Applies a deny-read ACE for the current user to one directory — inherited by its children — and
    /// removes that exact rule again on <see cref="Dispose"/>, so the fixture tree can be deleted.
    /// </summary>
    private sealed class DeniedDirectory : IDisposable
    {
        private readonly string _path;
        private readonly FileSystemAccessRule _rule;

        /// <summary>Denies this process read/execute access to <paramref name="path"/>.</summary>
        /// <param name="path">Directory to deny.</param>
        /// <exception cref="PlatformNotSupportedException">The platform has no Windows ACLs.</exception>
        /// <exception cref="InvalidOperationException">The deny was applied but does not block this process.</exception>
        public DeniedDirectory(string path)
        {
            _path = path;

            // Declared rather than assumed: file-system ACLs are a Windows API, and the suite is
            // otherwise platform-neutral Core code. Failing here with a reason is honest — skipping
            // silently is not (xUnit 2.x has no dynamic skip).
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "The access-denied contract is Windows ACL behaviour; this fixture needs Windows.");
            }

            SecurityIdentifier identity = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("The current Windows identity has no user SID, so no deny rule can be applied.");

            _rule = new FileSystemAccessRule(
                identity,
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Deny);

            var directory = new DirectoryInfo(path);
            DirectorySecurity security = directory.GetAccessControl();
            security.AddAccessRule(_rule);
            directory.SetAccessControl(security);

            // The rule is committed at this point, so a refusal below has to take it off again: this
            // object is never assigned by the caller's `using`, so Dispose would not run and the deny
            // (with its inherited copies) would stay on the fixture tree.
            try
            {
                if (CanRead(path))
                {
                    throw new InvalidOperationException(
                        $"The deny ACL on '{path}' does not block this process (privileged token), so the access-denied contract cannot be exercised on this machine.");
                }
            }
            catch
            {
                RemoveDenyRule();
                throw;
            }
        }

        /// <summary>Removes the deny rule again. Best effort: a teardown must not fail a passing test.</summary>
        public void Dispose() => RemoveDenyRule();

        /// <summary>Removes exactly the rule this fixture added, leaving the rest of the ACL alone.</summary>
        private void RemoveDenyRule()
        {
            if (!OperatingSystem.IsWindows())
            {
                return; // unreachable: the constructor refuses a platform without ACLs
            }

            try
            {
                var directory = new DirectoryInfo(_path);
                DirectorySecurity security = directory.GetAccessControl();
                security.RemoveAccessRuleSpecific(_rule);
                directory.SetAccessControl(security);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>
        /// Whether this process can still enumerate the directory with the framework's strict option —
        /// the same call shape the engine makes since the L10 fix.
        /// </summary>
        /// <param name="path">Directory to probe.</param>
        /// <returns>Whether the directory is still readable.</returns>
        private static bool CanRead(string path)
        {
            try
            {
                _ = Directory.GetDirectories(path, "*", new EnumerationOptions { IgnoreInaccessible = false });
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
