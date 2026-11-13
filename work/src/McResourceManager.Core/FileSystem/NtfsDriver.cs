using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using McResourceManager.Core.Domain;
using Microsoft.Win32.SafeHandles;

namespace McResourceManager.Core.FileSystem;

/// <summary>
/// Windows NTFS 底层文件操作示范实现（硬链接、FileID 提取、快速指纹比对、流式 SHA256、带自描述备份回滚的部署）。
/// 【硬约束】所有磁盘硬链接与 FileID 读取必须统一通过本类执行，禁止在其他类中散落复制 P/Invoke。
/// </summary>
public static partial class NtfsDriver
{
    public const string StagePrefix = ".mcrm-stage.";
    public const string BackupPrefix = ".mcrm-bak.";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    public readonly record struct FastFileStat(
        string FileId,
        uint VolumeSerialNumber,
        long Size,
        long LastWriteTimeUtcTicks,
        uint NumberOfLinks
    );

    public static FastFileStat GetFastStat(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"拒绝操作符号链接或目录联接：{fullPath}");

        using var handle = File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法读取 NTFS 文件信息：{fullPath}");

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var writeTicks = ((long)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
        var fileId = $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}";
        return new FastFileStat(fileId, info.VolumeSerialNumber, size, DateTime.FromFileTimeUtc(writeTicks).Ticks, info.NumberOfLinks);
    }

    public static string GetVolumeRoot(string path) =>
        (Path.GetPathRoot(Path.GetFullPath(path)) ?? throw new ArgumentException($"无法解析磁盘卷根路径：{path}", nameof(path))).ToUpperInvariant();

    public static bool IsSameVolume(string pathA, string pathB) =>
        string.Equals(GetVolumeRoot(pathA), GetVolumeRoot(pathB), StringComparison.OrdinalIgnoreCase);

    public static bool IsInternalTempOrBackupName(string entryName) =>
        entryName.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase) ||
        entryName.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 扫描并恢复目录中因进程异常中断遗留的暂存项 (.mcrm-stage.&lt;TargetName&gt;) 与备份项 (.mcrm-bak.&lt;TargetName&gt;)：
    /// 1. 任何 .mcrm-stage.* 均为未提交半成品，直接清理；
    /// 2. 若存在 .mcrm-bak.&lt;TargetName&gt; 且原目标 &lt;TargetName&gt; 缺失，说明在替换中途中断，自动将备份改名还原回 &lt;TargetName&gt;；
    /// 3. 若原目标 &lt;TargetName&gt; 已存在，为防止误删尚未核实的恢复材料，默认保留 .mcrm-bak.&lt;TargetName&gt; 并返回告警路径供上层核验。
    /// </summary>
    public static IReadOnlyList<string> RecoverInterruptedSwapsInDirectory(string directoryPath)
    {
        ResourceFiles.AssertNoLinks(directoryPath);
        if (!Directory.Exists(directoryPath))
            return [];

        var warnings = new List<string>();
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            ResourceFiles.AssertNoLinks(entryPath);
            var name = Path.GetFileName(entryPath);
            if (name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                CleanupPathIfExists(entryPath);
                continue;
            }

            if (name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var originalName = name[BackupPrefix.Length..];
                if (string.IsNullOrWhiteSpace(originalName))
                    continue;

                var originalFullPath = Path.Combine(directoryPath, originalName);
                if (!File.Exists(originalFullPath) && !Directory.Exists(originalFullPath))
                {
                    if (Directory.Exists(entryPath)) Directory.Move(entryPath, originalFullPath);
                    else File.Move(entryPath, originalFullPath);
                }
                else
                {
                    warnings.Add($"发现中断残留备份 {entryPath} 且目标 {originalFullPath} 已存在，已保留备份未自动删除。");
                }
            }
        }
        return warnings;
    }

    /// <summary>
    /// 安全部署或替换单文件/文件夹硬链接（文件名直接编码原目标名：.mcrm-stage.&lt;TargetName&gt; 与 .mcrm-bak.&lt;TargetName&gt;）。
    /// </summary>
    public static void DeployHardLinkEntry(string sourceEntryPath, string targetEntryPath)
    {
        var srcFull = Path.GetFullPath(sourceEntryPath);
        var dstFull = Path.GetFullPath(targetEntryPath);
        _ = ResourceFiles.Files(srcFull);
        ResourceFiles.AssertNoLinks(dstFull);
        if (ResourceFiles.Same(srcFull, dstFull) || ResourceFiles.Inside(srcFull, dstFull))
            throw new IOException("不能将资源部署到自身内部。");

        if (!IsSameVolume(srcFull, dstFull))
            throw new InvalidOperationException($"跨盘符无法直接建立硬链接（源：{srcFull}，目标：{dstFull}），必须先同步至跨盘共享缓存区。");

        var parentDir = Path.GetDirectoryName(dstFull)
            ?? throw new InvalidOperationException($"目标路径缺少父目录：{dstFull}");
        var targetName = Path.GetFileName(dstFull);
        Directory.CreateDirectory(parentDir);

        var stagePath = Path.Combine(parentDir, $"{StagePrefix}{targetName}");
        var backupPath = Path.Combine(parentDir, $"{BackupPrefix}{targetName}");
        if (ResourceFiles.Exists(stagePath) || ResourceFiles.Exists(backupPath))
            throw new IOException("目标存在待核验的暂存或备份，请先执行恢复检查。");

        try
        {
            if (File.Exists(srcFull))
            {
                CreateSingleHardLink(srcFull, stagePath);
                SwapStagedIntoTarget(stagePath, dstFull, backupPath, isDirectory: false);
                return;
            }

            if (Directory.Exists(srcFull))
            {
                Directory.CreateDirectory(stagePath);
                foreach (var dir in Directory.EnumerateDirectories(srcFull, "*", SearchOption.AllDirectories))
                {
                    Directory.CreateDirectory(Path.Combine(stagePath, Path.GetRelativePath(srcFull, dir)));
                }
                foreach (var file in Directory.EnumerateFiles(srcFull, "*", SearchOption.AllDirectories))
                {
                    CreateSingleHardLink(file, Path.Combine(stagePath, Path.GetRelativePath(srcFull, file)));
                }
                SwapStagedIntoTarget(stagePath, dstFull, backupPath, isDirectory: true);
                return;
            }

            throw new FileNotFoundException($"硬链接源路径不存在：{srcFull}");
        }
        finally
        {
            CleanupPathIfExists(stagePath);
        }
    }

    private static void SwapStagedIntoTarget(string stagePath, string dstFull, string backupPath, bool isDirectory)
    {
        var hadExisting = File.Exists(dstFull) || Directory.Exists(dstFull);
        var existingWasDir = Directory.Exists(dstFull);

        if (hadExisting)
        {
            if (existingWasDir) Directory.Move(dstFull, backupPath);
            else File.Move(dstFull, backupPath);
        }

        try
        {
            if (isDirectory) Directory.Move(stagePath, dstFull);
            else File.Move(stagePath, dstFull);
        }
        catch
        {
            if (hadExisting && (File.Exists(backupPath) || Directory.Exists(backupPath)))
            {
                CleanupPathIfExists(dstFull);
                if (existingWasDir) Directory.Move(backupPath, dstFull);
                else File.Move(backupPath, dstFull);
            }
            throw;
        }

        CleanupPathIfExists(backupPath);
    }

    private static void CleanupPathIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void CreateSingleHardLink(string existingFile, string newLinkFile)
    {
        if (File.Exists(newLinkFile))
            File.Delete(newLinkFile);

        if (!CreateHardLinkW(newLinkFile, existingFile, IntPtr.Zero))
        {
            var err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"创建硬链接失败（错误码 {err}）：{newLinkFile} -> {existingFile}");
        }
    }

}
