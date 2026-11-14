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
    public const string StagePrefix = ".pcrm-stage.";
    public const string BackupPrefix = ".pcrm-bak.";
    private const string LegacyStagePrefix = ".mcrm-stage.";
    private const string LegacyBackupPrefix = ".mcrm-bak.";

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

    public static bool IsInternalTempOrBackupName(string entryName) => IsStage(entryName) || IsBackup(entryName);
    private static bool IsStage(string name) => name.StartsWith(StagePrefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(LegacyStagePrefix, StringComparison.OrdinalIgnoreCase);
    private static bool IsBackup(string name) => name.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase)
        || name.StartsWith(LegacyBackupPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 清理两代程序的暂存文件。只有目标缺失且备份唯一时才还原，歧义时保留全部备份。
    /// </summary>
    public static IReadOnlyList<string> RecoverInterruptedSwapsInDirectory(string directoryPath)
    {
        ResourceFiles.AssertNoLinks(directoryPath);
        if (!Directory.Exists(directoryPath))
            return [];

        var warnings = new List<string>();
        var backups = new List<string>();
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            ResourceFiles.AssertNoLinks(entryPath);
            var name = Path.GetFileName(entryPath);
            if (IsStage(name)) CleanupPathIfExists(entryPath);
            else if (IsBackup(name)) backups.Add(entryPath);
        }
        foreach (var group in backups.GroupBy(path => Path.GetFileName(path)[BackupPrefix.Length..], StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key) || group.Key is "." or "..") continue;
            var original = ResourceFiles.Child(directoryPath, group.Key);
            if (!ResourceFiles.Exists(original) && group.Count() == 1) ResourceFiles.Move(group.First(), original);
            else
            {
                warnings.Add($"发现恢复备份，请核对后再操作：{string.Join("、", group)}");
            }
        }
        return warnings;
    }

    /// <summary>
    /// 通过同目录暂存、备份与替换部署单文件或文件夹硬链接。
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
        if (ResourceFiles.Exists(stagePath) || ResourceFiles.Exists(backupPath)
            || ResourceFiles.Exists(Path.Combine(parentDir, LegacyStagePrefix + targetName))
            || ResourceFiles.Exists(Path.Combine(parentDir, LegacyBackupPrefix + targetName)))
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
