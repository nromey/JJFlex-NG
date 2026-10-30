using System.Runtime.InteropServices;

namespace JJFlex.RecordIndex.Crypto;

/// <summary>
/// The clipboard, handled the way password managers handle it. A recovery key
/// placed here is marked so that Windows keeps it OUT of clipboard history
/// (Windows+V) and OUT of cloud clipboard sync, and is cleared again as soon as
/// the operator says they have pasted it.
///
/// The three registered formats are the documented Windows conventions:
/// ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory = 0,
/// CanUploadToCloudClipboard = 0.
/// </summary>
public static class SecureClipboard
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static void SetSecret(string text)
    {
        Open();
        try
        {
            if (!EmptyClipboard()) throw Fail("empty the clipboard");
            Put(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes(text + "\0"));
            Put(RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), [0, 0, 0, 0]);
            Put(RegisterClipboardFormat("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0));
            Put(RegisterClipboardFormat("CanUploadToCloudClipboard"), BitConverter.GetBytes(0));
        }
        finally { CloseClipboard(); }
    }

    public static string? ReadText()
    {
        Open();
        try
        {
            var h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return null;
            var p = GlobalLock(h);
            try { return Marshal.PtrToStringUni(p); }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Clears the clipboard only if it still holds exactly <paramref name="text"/>.</summary>
    public static bool ClearIfEquals(string text)
    {
        var now = ReadText();
        if (now is null || !string.Equals(now.Trim(), text.Trim(), StringComparison.Ordinal)) return false;
        Open();
        try { return EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    private static void Open()
    {
        for (int i = 0; i < 20; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return;
            Thread.Sleep(50);
        }
        throw Fail("open the clipboard (another program is holding it)");
    }

    private static void Put(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) throw Fail("allocate clipboard memory");
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero) { GlobalFree(h); throw Fail("place data on the clipboard"); }
    }

    private static UnlockException Fail(string what) => new($"Could not {what}. Windows error {Marshal.GetLastWin32Error()}.");

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string lpszFormat);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
}
