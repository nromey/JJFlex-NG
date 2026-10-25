#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;

namespace Radios;

/// <summary>Why a file was not moved to the Recycle Bin.</summary>
public enum RecycleRefusal
{
    /// <summary>It was moved.</summary>
    None,
    /// <summary>The file was already gone.</summary>
    Missing,
    /// <summary>The drive has no Recycle Bin (a network share, a CD, a RAM drive).</summary>
    NoRecycleBin,
    /// <summary>Windows is set to delete files on this drive immediately instead of recycling them.</summary>
    RecycleBinOff,
    /// <summary>The file is bigger than this drive's Recycle Bin will hold.</summary>
    TooLarge,
    /// <summary>The file resolves outside the recordings archive.</summary>
    OutsideArchive,
    /// <summary>Windows refused or cancelled the move.</summary>
    Failed,
}

/// <summary>The answer to one move.</summary>
public sealed record RecycleResult(bool Moved, RecycleRefusal Refusal, string Detail = "",
    bool ConfirmedRecycled = false);

/// <summary>
/// Move a file to the Recycle Bin, and refuse rather than delete it when the
/// Recycle Bin cannot take it (#787, ruled by Noel 2026-10-24 16:01: "recycle
/// bin is standard and good").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the one-line API.</b> Every ordinary "send to the Recycle Bin"
/// call, including <c>Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile</c>
/// with <c>RecycleOption.SendToRecycleBin</c>, asks Windows to recycle IF IT
/// CAN and to delete permanently when it cannot: a network share, a drive set
/// to "Don't move files to the Recycle Bin", or a file larger than the bin.
/// With confirmations off that permanent delete is silent. So this asks first
/// and refuses each of those cases in words.
/// </para>
/// <para>The native operation requests recycling and its progress sink aborts
/// a delete that is not identified as a recycle. Success also requires Windows
/// to return the newly created Recycle Bin item.</para>
/// <para>
/// <b>The checks read Windows' own per-drive settings</b> under
/// <c>HKCU\...\Explorer\BitBucket\Volume\{volume}</c> (<c>NukeOnDelete</c> and
/// <c>MaxCapacity</c> in megabytes) and the <c>NoRecycleFiles</c> policy. A
/// value that cannot be read is treated as Windows' default; the native
/// recycle-only operation is the final decision.
/// </para>
/// </remarks>
public static class RecycleBin
{
    /// <summary>The move itself. Replaced by tests, which must never put a window on the desktop.</summary>
    internal static Func<string, IntPtr, RecycleResult> Implementation { get; set; } = MoveWithShell;

    /// <summary>The one wording for a move Windows reported without a Recycle Bin item.</summary>
    internal const string NotConfirmedRecycled = "Windows did not confirm a recycled item.";

    /// <summary>Move one file, or refuse with the reason. Never deletes permanently.</summary>
    /// <param name="path">The file.</param>
    /// <param name="owner">The window Windows should parent any question to, or zero.</param>
    public static RecycleResult Move(string path, IntPtr owner = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new(false, RecycleRefusal.Missing);
        try
        {
            if (!ResolvesInsideArchive(path)) return new(false, RecycleRefusal.OutsideArchive);
            var result = Implementation(path, owner);
            return result.Moved && !result.ConfirmedRecycled
                ? new(false, RecycleRefusal.Failed, NotConfirmedRecycled)
                : result;
        }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("RecycleBin: " + path + ": " + ex, System.Diagnostics.TraceLevel.Error);
            return new(false, RecycleRefusal.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Compare the paths Windows actually opened. A lexical prefix accepts a
    /// junction inside Recorded QSOs that points elsewhere; these handles
    /// resolve directory junctions and file symbolic links before comparison.
    /// </summary>
    private static bool ResolvesInsideArchive(string path)
    {
        string archive = Path.GetFullPath(RecordedQsoCatalog.FolderPath);
        string file = Path.GetFullPath(path);
        if (!RecordedQsoCatalog.IsInFolder(file, archive)) return false;
        using var folderHandle = OpenForPath(archive);
        using var fileHandle = OpenForPath(file);
        if (folderHandle.IsInvalid || fileHandle.IsInvalid) return false;
        string? folder = FinalPath(folderHandle);
        string? actual = FinalPath(fileHandle);
        return folder != null && actual != null &&
            actual.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static SafeFileHandle OpenForPath(string path) => CreateFile(path, 0,
        FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) return null;
        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
        }
        return buffer.ToString();
    }

    /// <summary>
    /// The pre-move checks alone, as data, so a test can hold each refusal.
    /// <paramref name="driveType"/> is the drive's kind; <paramref name="binExists"/>
    /// whether Windows reports a Recycle Bin on it; <paramref name="nukeOnDelete"/>
    /// whether the drive is set to delete immediately; <paramref name="maxCapacityMegabytes"/>
    /// the bin's size limit, or null when unknown; <paramref name="policyForbids"/>
    /// the NoRecycleFiles policy.
    /// </summary>
    internal static RecycleRefusal Check(DriveType driveType, bool binExists, bool nukeOnDelete,
        long? maxCapacityMegabytes, bool policyForbids, long fileBytes)
    {
        if (driveType is not (DriveType.Fixed or DriveType.Removable)) return RecycleRefusal.NoRecycleBin;
        if (!binExists) return RecycleRefusal.NoRecycleBin;
        if (policyForbids || nukeOnDelete) return RecycleRefusal.RecycleBinOff;
        if (maxCapacityMegabytes is long max && max >= 0 && fileBytes > max * 1024L * 1024L)
            return RecycleRefusal.TooLarge;
        return RecycleRefusal.None;
    }

    private static RecycleResult MoveWithShell(string path, IntPtr owner)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        long bytes = new FileInfo(full).Length;
        var drive = new DriveInfo(root);
        bool binExists = BinExists(root);
        var (nuke, max) = VolumeSettings(root);
        var refusal = Check(drive.DriveType, binExists, nuke, max, PolicyForbids(), bytes);
        if (refusal != RecycleRefusal.None) return new(false, refusal);

        return RecycleBinFileOperation.Move(full, owner);
    }

    private static bool BinExists(string root)
    {
        // 24 bytes with natural packing on 64-bit; 20 on 32-bit, where
        // shellapi.h packs to one byte. The buffer is always 24, and only
        // the return code is read, so the 32-bit layout difference is harmless.
        var info = new SHQUERYRBINFO { cbSize = IntPtr.Size == 8 ? 24 : 20 };
        try { return SHQueryRecycleBin(root, ref info) == 0; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static (bool Nuke, long? MaxMegabytes) VolumeSettings(string root)
    {
        try
        {
            var name = new System.Text.StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root, name, name.Capacity)) return (false, null);
            // "\\?\Volume{guid}\" -> "{guid}"
            string volume = name.ToString();
            int open = volume.IndexOf('{'), close = volume.IndexOf('}');
            if (open < 0 || close < open) return (false, null);
            string guid = volume.Substring(open, close - open + 1);
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid);
            if (key == null) return (false, null);
            bool nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            long? max = key.GetValue("MaxCapacity") is int m ? m : null;
            return (nuke, max);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (false, null);
        }
    }

    private static bool PolicyForbids()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
            return key?.GetValue("NoRecycleFiles") is int n && n != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, System.Text.StringBuilder name, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
