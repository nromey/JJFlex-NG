#nullable enable
using System;
using System.Runtime.InteropServices;

namespace Radios;

/// <summary>
/// The one native deletion door. The progress sink rejects any operation that
/// Windows does not identify as a recycle, and success requires the Shell's
/// returned item in the Recycle Bin. No permanent-delete prompt is requested.
/// </summary>
public static class RecycleBinFileOperation
{
    // FOF_NO_UI: there is already an application confirmation. The sink below
    // rejects nonrecycle intent before this can accept a shell confirmation.
    private const uint FofNoUi = 0x0004 | 0x0010 | 0x0200 | 0x0400;
    private const uint FofxRecycleOnDelete = 0x00080000;
    private const uint FofxEarlyFailure = 0x00100000;
    internal const uint TsfDeleteRecycleIfPossible = 0x80;
    private static readonly Guid ShellItemId = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid FileOperationId = new("3AD05575-8857-4850-9277-11B85BDB8E09");

    internal static RecycleResult Move(string path, IntPtr owner)
    {
        if (System.Threading.Thread.CurrentThread.GetApartmentState() != System.Threading.ApartmentState.STA)
            return new(false, RecycleRefusal.Failed, "The Recycle Bin requires an STA thread.");
        IFileOperation? operation = null;
        IntPtr item = IntPtr.Zero;
        try
        {
            Guid shellItemId = ShellItemId;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref shellItemId, out item);
            if (hr < 0 || item == IntPtr.Zero) return Failure(hr);
            operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(FileOperationId)!)!;
            hr = operation.SetOperationFlags(FofxRecycleOnDelete | FofxEarlyFailure | FofNoUi);
            if (hr < 0) return Failure(hr);
            if (owner != IntPtr.Zero && (hr = operation.SetOwnerWindow(owner)) < 0) return Failure(hr);
            var sink = new RecycleOnlySink();
            hr = operation.DeleteItem(item, sink);
            if (hr < 0) return Failure(hr);
            hr = operation.PerformOperations();
            if (hr < 0) return Failure(hr);
            hr = operation.GetAnyOperationsAborted(out bool aborted);
            if (hr < 0 || aborted || !sink.Recycled)
                return new(false, RecycleRefusal.Failed, RecycleBin.NotConfirmedRecycled);
            return new(true, RecycleRefusal.None, ConfirmedRecycled: true);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (operation != null) Marshal.ReleaseComObject(operation);
        }
    }

    internal static bool IsRecycleDelete(uint flags) => (flags & TsfDeleteRecycleIfPossible) != 0;

    private static RecycleResult Failure(int hr) =>
        new(false, RecycleRefusal.Failed, "Windows error 0x" + hr.ToString("X8") + ".");

    [DllImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", ExactSpelling = true,
        CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext,
        ref Guid interfaceId, out IntPtr item);

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4EB2C7B65CAA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IntPtr sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage(IntPtr message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IntPtr item, IntPtr name, IntPtr sink);
        [PreserveSig] int RenameItems(IntPtr items, IntPtr name);
        [PreserveSig] int MoveItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        [PreserveSig] int MoveItems(IntPtr items, IntPtr destination);
        [PreserveSig] int CopyItem(IntPtr item, IntPtr destination, IntPtr name, IntPtr sink);
        [PreserveSig] int CopyItems(IntPtr items, IntPtr destination);
        [PreserveSig] int DeleteItem(IntPtr item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IntPtr destination, uint attributes, IntPtr name, IntPtr template, IntPtr sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, IntPtr newName);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr newlyCreated);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newlyCreated);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newlyCreated);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newlyCreated);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, IntPtr newName);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr newItem);
        [PreserveSig] int UpdateProgress(uint total, uint soFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class RecycleOnlySink : IFileOperationProgressSink
    {
        internal bool Recycled { get; private set; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, IntPtr newName) => 0;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr newName, int result, IntPtr newlyCreated) => 0;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newlyCreated) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr newName, int result, IntPtr newlyCreated) => 0;
        public int PreDeleteItem(uint flags, IntPtr item) => IsRecycleDelete(flags) ? 0 : unchecked((int)0x80004004);
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr newlyCreated)
        {
            Recycled = IsRecycleDelete(flags) && result >= 0 && newlyCreated != IntPtr.Zero;
            return Recycled ? 0 : unchecked((int)0x80004004);
        }
        public int PreNewItem(uint flags, IntPtr destination, IntPtr newName) => 0;
        public int PostNewItem(uint flags, IntPtr destination, IntPtr newName, IntPtr template, uint attributes, int result, IntPtr newItem) => 0;
        public int UpdateProgress(uint total, uint soFar) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
