using System.IO;

namespace ChatGPTCleanupTool.Services;

internal sealed record DevelopmentGitState(string Root, string Head, string CommonDirectory, bool Clean, string Branch,
    string Target, string TargetHead, bool Merged, bool Main, bool Locked, bool Managed, string Remote)
{
    public string Summary => $"{Branch} · {(Clean ? "干净" : "未提交 / 未跟踪内容")} · {(Merged ? "已合并到 " + Target : "未确认合并")}";
}

internal static class DevelopmentGit
{
    internal static DevelopmentGitState? Inspect(string path, string mergeTarget, CancellationToken token)
    {
        var top = DevelopmentProcess.Git(path, token, "rev-parse", "--show-toplevel");
        if (top.ExitCode != 0) return null;
        var root = DiskAnalysisService.NormalizePath(top.Output.Trim());
        string Read(params string[] args)
        { var result = DevelopmentProcess.Git(root, token, args); if (result.ExitCode != 0) throw new IOException("Git 信息不可读取。"); return result.Output.Trim(); }
        var head = Read("rev-parse", "HEAD");
        var common = DiskAnalysisService.NormalizePath(Read("rev-parse", "--path-format=absolute", "--git-common-dir"));
        var status = DevelopmentProcess.Git(root, token, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (status.ExitCode != 0) throw new IOException("Git 状态不可读取。");
        var branchResult = DevelopmentProcess.Git(root, token, "symbolic-ref", "--short", "HEAD");
        var branch = branchResult.ExitCode == 0 ? branchResult.Output.Trim() : "detached HEAD";
        var target = mergeTarget.Trim();
        if (target.Length == 0)
        {
            var remoteHead = DevelopmentProcess.Git(root, token, "symbolic-ref", "--short", "refs/remotes/origin/HEAD");
            if (remoteHead.ExitCode == 0) target = remoteHead.Output.Trim();
            else foreach (var choice in new[] { "main", "master" })
            { if (DevelopmentProcess.Git(root, token, "rev-parse", "--verify", "refs/heads/" + choice).ExitCode == 0) { target = choice; break; } }
        }
        var targetHead = ""; bool merged = false;
        if (target.Length > 0 && !target.StartsWith('-'))
        {
            var resolved = DevelopmentProcess.Git(root, token, "rev-parse", "--verify", target + "^{commit}");
            if (resolved.ExitCode == 0) { targetHead = resolved.Output.Trim(); merged = DevelopmentProcess.Git(root, token, "merge-base", "--is-ancestor", head, targetHead).ExitCode == 0; }
        }
        var trees = List(root, token);
        var thisTree = trees.FirstOrDefault(t => string.Equals(t.Path, root, StringComparison.OrdinalIgnoreCase));
        var remote = DevelopmentProcess.Git(root, token, "config", "--get", "remote.origin.url");
        // Managed Codex trees must stay under the app's archive/snapshot lifecycle.
        var managed = root.Split(Path.DirectorySeparatorChar).Any(s => s is ".codex" or ".chatgpt-projects") || RecoveryFileProtection.IsManagedPath(root);
        return new(root, head, common, status.Output.Length == 0, branch, target, targetHead, merged,
            thisTree?.Main != false, thisTree?.Locked != false, managed, remote.ExitCode == 0 ? remote.Output.Trim() : "");
    }

    internal sealed record Tree(string Path, bool Main, bool Locked, bool Prunable);
    internal static IReadOnlyList<Tree> List(string root, CancellationToken token)
    {
        var result = DevelopmentProcess.Git(root, token, "worktree", "list", "--porcelain", "-z");
        if (result.ExitCode != 0) throw new IOException("无法读取工作树管理关系。");
        var trees = new List<Tree>(); string? path = null; bool locked = false, prunable = false;
        void Flush() { if (path is not null) trees.Add(new(DiskAnalysisService.NormalizePath(path), trees.Count == 0, locked, prunable)); path = null; locked = false; prunable = false; }
        foreach (var field in result.Output.Split('\0'))
        {
            if (field.Length == 0) { Flush(); continue; }
            if (field.StartsWith("worktree ")) { Flush(); path = field[9..]; }
            if (field.StartsWith("locked")) locked = true;
            if (field.StartsWith("prunable")) prunable = true;
        }
        Flush(); return trees;
    }

    internal static bool IsIgnoredUntracked(DevelopmentGitState state, string path, CancellationToken token)
    {
        var relative = Path.GetRelativePath(state.Root, path).Replace('\\', '/');
        var tracked = DevelopmentProcess.Git(state.Root, token, "ls-files", "--cached", "-z", "--", relative);
        return tracked.ExitCode == 0 && tracked.Output.Length == 0 &&
            DevelopmentProcess.Git(state.Root, token, "check-ignore", "-q", "--", relative + "/").ExitCode == 0;
    }

    internal static bool HasIgnoredOrSubmodules(DevelopmentGitState state, CancellationToken token)
    {
        var status = DevelopmentProcess.Git(state.Root, token, "status", "--porcelain=v1", "-z", "--ignored", "--untracked-files=all");
        var modules = DevelopmentProcess.Git(state.Root, token, "ls-files", "--stage", "--", ".gitmodules");
        return status.ExitCode != 0 || status.Output.Length > 0 || modules.ExitCode != 0 || modules.Output.Length > 0;
    }
}
