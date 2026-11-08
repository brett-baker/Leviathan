using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Leviathan.Contracts;
using Microsoft.Win32;

namespace Leviathan.ShellHost;

/// <summary><see cref="IClassesRegistry"/> over the real registry.</summary>
internal sealed class WindowsClassesRegistry : IClassesRegistry
{
    public string? GetDefault(string subKey)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(subKey);
            return key?.GetValue(null) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return null;
        }
    }
}

[ComImport, Guid("8895b1c6-b41f-4c1c-a562-0d564250836f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPreviewHandler
{
    void SetWindow(nint hwnd, ref NativeRect rect);

    void SetRect(ref NativeRect rect);

    void DoPreview();

    void Unload();

    void SetFocus();

    void QueryFocus(out nint hwnd);

    [PreserveSig]
    int TranslateAccelerator(nint message);
}

[ComImport, Guid("b7d14566-0509-4cce-a71f-0a554233bd9b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithFile
{
    void Initialize([MarshalAs(UnmanagedType.LPWStr)] string path, uint mode);
}

[ComImport, Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithStream
{
    void Initialize(IStream stream, uint mode);
}

[ComImport, Guid("7f73be3f-fb79-493c-a6c7-7ee14e245841"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IInitializeWithItem
{
    void Initialize([MarshalAs(UnmanagedType.IUnknown)] object item, uint mode);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary>
/// <see cref="IShellPreviewSource"/> over the real shell (spec §9.1). It makes a small top-level window owned by the UI's window (no
/// frame, never activated, kept out of the task bar), asks the file type's registered preview handler to draw into it, and moves the
/// window when the UI says the pane moved. A top-level window, not a child of the UI's window, so the two processes never share an
/// input queue and a handler that stops answering cannot stop the UI answering. The handler is initialised the way the shell does:
/// with a stream if it takes one, else an item, else the file path.
/// </summary>
internal sealed partial class WindowsShellPreviewSource(IClassesRegistry registry) : IShellPreviewSource
{
    private const uint WsPopup = 0x80000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint StgmRead = 0;
    private const uint StgmShareDenyNone = 0x40;

    public WindowsShellPreviewSource()
        : this(new WindowsClassesRegistry())
    {
    }

    public IShellPreviewInstance? Open(ShellPreviewRequest request, out string? error)
    {
        var extension = Path.GetExtension(request.Path);
        if (PreviewHandlerLocator.Find(extension, registry) is not { } clsid)
        {
            error = $"No preview handler is registered for {(extension.Length == 0 ? "this kind of" : extension)} files. Installing the program that opens them usually adds one.";
            return null;
        }

        var bounds = request.Bounds;
        var window = CreateWindowEx(WsExNoActivate | WsExToolWindow, "STATIC", null, WsPopup | WsClipChildren, bounds.X, bounds.Y, bounds.Width, bounds.Height, (nint)request.OwnerWindow, 0, 0, 0);
        if (window == 0)
        {
            error = $"The preview window could not be created (error {Marshal.GetLastPInvokeError()}).";
            return null;
        }

        object? handler = null;
        try
        {
            handler = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(clsid), throwOnError: true)!);
            Initialize(handler!, request.Path);
            var preview = (IPreviewHandler)handler!;
            var rect = new NativeRect { Left = 0, Top = 0, Right = bounds.Width, Bottom = bounds.Height };
            preview.SetWindow(window, ref rect);
            preview.DoPreview();
            ShowWindow(window, 4); // SW_SHOWNOACTIVATE
            error = null;
            return new Instance(clsid, window, preview, handler!);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or FileNotFoundException or UnauthorizedAccessException or IOException or NotSupportedException)
        {
            error = ex is COMException { HResult: unchecked((int)0x80004002) } or InvalidCastException
                ? "The registered preview handler does not support this file."
                : $"The preview handler could not show this file: {ex.Message}";
            Release(window, handler as IPreviewHandler, handler);
            return null;
        }
    }

    private static void Initialize(object handler, string path)
    {
        if (handler is IInitializeWithStream withStream)
        {
            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(path, StgmRead | StgmShareDenyNone, 0x80, false, 0, out var stream));
            withStream.Initialize(stream, StgmRead);
        }
        else if (handler is IInitializeWithItem withItem)
        {
            var iid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref iid, out var item));
            withItem.Initialize(item, StgmRead);
        }
        else if (handler is IInitializeWithFile withFile)
        {
            withFile.Initialize(path, StgmRead);
        }
        else
        {
            throw new NotSupportedException("The handler cannot be given a file.");
        }
    }

    private static void Release(nint window, IPreviewHandler? preview, object? handler)
    {
        try
        {
            preview?.Unload();
        }
        catch (COMException)
        {
            // A handler that fails to unload is released anyway.
        }

        if (handler is not null && Marshal.IsComObject(handler))
        {
            Marshal.FinalReleaseComObject(handler);
        }

        if (window != 0)
        {
            DestroyWindow(window);
        }
    }

    private sealed class Instance(string clsid, nint window, IPreviewHandler preview, object handler) : IShellPreviewInstance
    {
        private bool _disposed;

        public string Handler => clsid;

        public void Move(ShellRect bounds)
        {
            if (_disposed)
            {
                return;
            }

            MoveWindow(window, bounds.X, bounds.Y, bounds.Width, bounds.Height, true);
            var rect = new NativeRect { Left = 0, Top = 0, Right = bounds.Width, Bottom = bounds.Height };
            preview.SetRect(ref rect);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Release(window, preview, handler);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowEx(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    // These two hand back COM objects, which the source-generated marshaller does not take; the classic form is used on purpose.
#pragma warning disable SYSLIB1054
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes, [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out IStream stream);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, nint bindContext, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
#pragma warning restore SYSLIB1054
}
