using System.IO;

namespace R8er.Api.Tests.Infrastructure;

/// Walk up from the test assembly to the repo root (the dir containing .git),
/// so container resource mappings (dev/firebase/firebase.json) resolve
/// regardless of the working directory the runner uses. `.git` is a directory
/// in a normal clone but a file in a git worktree — Path.Exists matches both.
public static class RepoRoot
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Path.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (.git) not found");
    }
}
