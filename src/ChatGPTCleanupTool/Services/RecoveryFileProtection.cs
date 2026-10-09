using System.IO;

namespace ChatGPTCleanupTool.Services;

/// <summary>Interrupted hard-link recovery names must not be offered to another cleaner.</summary>
internal static class RecoveryFileProtection
{
    public static bool IsManagedPath(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(part => part.StartsWith(".diskcleanup-", StringComparison.OrdinalIgnoreCase));
    public static void EnsureNotManaged(string path)
    { if (IsManagedPath(path)) throw new InvalidOperationException("此路径是本工具的异常恢复项，请先查看重复文件操作记录；禁止自动清理。"); }
}
