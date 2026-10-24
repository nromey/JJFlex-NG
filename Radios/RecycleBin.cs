#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
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
    /// <summary>Windows refused or cancelled the move.</summary>
    Failed,
}

/// <summary>The answer to one move.</summary>
public sealed record RecycleResult(bool Moved, RecycleRefusal Refusal, string Detail = "");

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
/// <para>
/// <b>And a backstop, in case the checks miss one.</b> The move itself passes
/// <c>FOF_WANTNUKEWARNING</c>, which makes Windows ask before any permanent
/// delete even when every other confirmation is switched off. A case the
/// checks did not foresee therefore reaches the operator as a Windows question
/// they can answer No to, never as a file quietly gone. Answering No leaves
/// the file and reports a refusal.
/// </para>
/// <para>
/// <b>The checks read Windows' own per-drive settings</b> under
/// <c>HKCU\...\Explorer\BitBucket\Volume\{volume}</c> (<c>NukeOnDelete</c> and
/// <c>MaxCapacity</c> in megabytes) and the <c>NoRecycleFiles</c> policy. A
/// value that cannot be read is treated as Windows' default, which recycles;
/// the backstop above is what covers being wrong about that.
/// </para>
/// </remarks>
public static class RecycleBin
{
    /// <summary>The move itself. Replaced by tests, which must never put a window on the desktop.</summary>
    internal static Func<string, IntPtr, RecycleResult> Implementation { get; set; } = MoveWithShell;

    /// <summary>Move one file, or refuse with the reason. Never deletes permanently.</summary>
    /// <param name="path">The file.</param>
    /// <param name="owner">The window Windows should parent any question to, or zero.</param>
    public static RecycleResult Move(string path, IntPtr owner = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return new(false, RecycleRefusal.Missing);
        try { return Implementation(path, owner); }
        catch (Exception ex)
        {
            JJTrace.Tracing.TraceLine("RecycleBin: " + path + ": " + ex, System.Diagnostics.TraceLevel.Error);
            return new(false, RecycleRefusal.Failed, ex.Message);
        }
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

        const ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_WANTNUKEWARNING;
        int code;
        bool aborted;
        if (IntPtr.Size == 8)
        {
            var op = new SHFILEOPSTRUCT64 { hwnd = owner, wFunc = FO_DELETE, pFrom = full + "\0\0", fFlags = flags };
            code = SHFileOperation64(ref op);
            aborted = op.fAnyOperationsAborted;
        }
        else
        {
            var op = new SHFILEOPSTRUCT32 { hwnd = owner, wFunc = FO_DELETE, pFrom = full + "\0\0", fFlags = flags };
            code = SHFileOperation32(ref op);
            aborted = op.fAnyOperationsAborted;
        }
        if (code != 0) return new(false, RecycleRefusal.Failed, "Windows error " + code + ".");
        if (aborted) return new(false, RecycleRefusal.Failed, "The move was cancelled.");
        return File.Exists(full) ? new(false, RecycleRefusal.Failed, "The file is still there.") : new(true, RecycleRefusal.None);
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

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    // shellapi.h packs this structure to one byte on 32-bit Windows only, so
    // the two layouts differ from fFlags on; each architecture gets its own.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT64
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct SHFILEOPSTRUCT32
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, System.Text.StringBuilder name, int length);
}
