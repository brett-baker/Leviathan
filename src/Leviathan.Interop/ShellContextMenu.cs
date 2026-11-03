// The way the menu is built and read (shell item array, IContextMenu, QueryContextMenu with ids from 1, GetMenuItemInfo walk,
// WM_INITMENUPOPUP through IContextMenu2 for submenus, GetCommandString with the 5000 offset guard, InvokeCommand with a
// unicode verb) is adapted from Files (brett-baker/Files) src/Files.App/Utils/Shell/ContextMenu.cs, Copyright (c) Files Community,
// MIT. Rewritten against Leviathan's pointer-style bindings and contract types; see THIRD-PARTY-NOTICES.md.

using System.Runtime.InteropServices;
using System.Text;
using Leviathan.Contracts;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Leviathan.Interop;

/// <summary>
/// The shell's context menu for some files, with the handlers of every installed shell extension (spec §8.5). The COM objects
/// belong to the apartment that made them, so create, read, invoke and dispose all run on the same <see cref="StaMessageThread"/>.
/// It is meant to live in <c>Leviathan.ShellHost</c>: handlers are third-party code and some of them crash.
/// </summary>
public sealed unsafe class ShellContextMenu : IDisposable
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;

    // A handler that returns an enormous command offset has made GetCommandString raise an access violation, which .NET cannot
    // catch (Files found it with the NVIDIA "Run with graphics processor" item). Offsets that large are never real commands.
    private const uint MaxCommandOffset = 5000;

    // Menu bitmaps are 16 to 32 pixels; anything much bigger is not an icon and would only bloat the reply.
    private const int MaxIconSide = 64;

    private IContextMenu* _menu;
    private IContextMenu2* _menu2;
    private HMENU _handle;

    private ShellContextMenu(IContextMenu* menu, HMENU handle)
    {
        _menu = menu;
        _handle = handle;
        IContextMenu2* menu2;
        var iid = IContextMenu2.IID_Guid;
        _menu2 = menu->QueryInterface(&iid, (void**)&menu2).Succeeded ? menu2 : null;
        Items = [];
    }

    /// <summary>The top-level items. Submenus are not loaded; see <see cref="LoadSubMenu"/>.</summary>
    public IReadOnlyList<ShellMenuItem> Items { get; private set; }

    /// <summary>
    /// Builds the menu. Must run on the thread that will later read and invoke it.
    /// </summary>
    /// <param name="paths">The items; all must exist.</param>
    /// <param name="background">The empty-space menu of the single folder in <paramref name="paths"/>.</param>
    /// <param name="extended">Add the shell's extended verbs (Shift held).</param>
    /// <exception cref="ArgumentException">There are no paths, or a background menu was asked for several.</exception>
    /// <exception cref="COMException">The shell could not build the menu (an item vanished, a handler failed).</exception>
    public static ShellContextMenu Create(IReadOnlyList<string> paths, bool background, bool extended)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            throw new ArgumentException("There is nothing to build a menu for.", nameof(paths));
        }

        if (background && paths.Count != 1)
        {
            throw new ArgumentException("A background menu belongs to one folder.", nameof(paths));
        }

        IContextMenu* menu = background ? BindBackground(paths[0]) : BindItems(paths);
        HMENU handle = default;
        try
        {
            handle = PInvoke.CreatePopupMenu();
            if (handle.IsNull)
            {
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            }

            var flags = (uint)(extended ? PInvoke.CMF_EXTENDEDVERBS : PInvoke.CMF_NORMAL);
            menu->QueryContextMenu(handle, 0, FirstCommand, LastCommand, flags).ThrowOnFailure();
            var result = new ShellContextMenu(menu, handle);
            menu = null;
            handle = default;
            result.Items = result.Read(result._handle);
            return result;
        }
        finally
        {
            if (!handle.IsNull)
            {
                PInvoke.DestroyMenu(handle);
            }

            if (menu is not null)
            {
                menu->Release();
            }
        }
    }

    /// <summary>
    /// Reads the submenu reached by following <paramref name="path"/> (positions from the top level). The shell fills some
    /// submenus ("Send to", "Open with") only when told the menu is about to open, so this sends that message first.
    /// </summary>
    public IReadOnlyList<ShellMenuItem> LoadSubMenu(IReadOnlyList<int> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ObjectDisposedException.ThrowIf(_menu is null, this);
        var current = _handle;
        var position = 0;
        foreach (var step in path)
        {
            MENUITEMINFOW info = default;
            info.cbSize = (uint)sizeof(MENUITEMINFOW);
            info.fMask = MENU_ITEM_MASK.MIIM_SUBMENU;
            if (!PInvoke.GetMenuItemInfo(current, (uint)step, true, &info) || info.hSubMenu.IsNull)
            {
                throw new ArgumentException("There is no submenu at that position.", nameof(path));
            }

            current = info.hSubMenu;
            position = step;
        }

        if (_menu2 is not null)
        {
            try
            {
                _menu2->HandleMenuMsg(PInvoke.WM_INITMENUPOPUP, (WPARAM)(nuint)current.Value, (LPARAM)position).ThrowOnFailure();
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException or InvalidCastException or ArgumentException)
            {
                // The handler declined to prepare the menu (E_NOTIMPL is common); read what is there.
            }
        }

        return Read(current);
    }

    /// <summary>Runs the item with command id <paramref name="itemId"/>, or the canonical <paramref name="verb"/> when the id is -1.</summary>
    /// <param name="itemId">The <see cref="ShellMenuItem.Id"/>, or -1.</param>
    /// <param name="verb">A verb such as "properties", used when <paramref name="itemId"/> is -1.</param>
    /// <param name="workingDirectory">Folder the command runs in, or null.</param>
    /// <param name="ownerWindow">Window that dialogs belong to, or 0.</param>
    /// <returns>The HRESULT of the call: 0 or positive for success.</returns>
    public int Invoke(int itemId, string? verb, string? workingDirectory, nint ownerWindow)
    {
        ObjectDisposedException.ThrowIf(_menu is null, this);
        if (itemId < 0 && string.IsNullOrEmpty(verb))
        {
            return unchecked((int)0x80070057);
        }

        var verbBytes = itemId < 0 ? Encoding.ASCII.GetBytes(verb + '\0') : null;
        fixed (byte* verbPointer = verbBytes)
        fixed (char* directory = workingDirectory)
        {
            CMINVOKECOMMANDINFOEX command = default;
            command.cbSize = (uint)sizeof(CMINVOKECOMMANDINFOEX);
            command.hwnd = (HWND)ownerWindow;
            command.nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL;
            command.lpDirectoryW = directory;
            if (itemId >= 0)
            {
                // Commands are named by offset from the first id; the unicode flag says the verb field holds a number, not text.
                command.fMask = 0x00004000;
                command.lpVerb = (PCSTR)(byte*)(nuint)(uint)itemId;
                command.lpVerbW = (PCWSTR)(char*)(nuint)(uint)itemId;
            }
            else
            {
                command.lpVerb = (PCSTR)verbPointer;
            }

            return _menu->InvokeCommand((CMINVOKECOMMANDINFO*)&command).Value;
        }
    }

    /// <summary>Destroys the native menu and releases the handlers. Runs on the owning thread.</summary>
    public void Dispose()
    {
        if (_menu is null)
        {
            return;
        }

        if (!_handle.IsNull)
        {
            PInvoke.DestroyMenu(_handle);
            _handle = default;
        }

        if (_menu2 is not null)
        {
            _menu2->Release();
            _menu2 = null;
        }

        _menu->Release();
        _menu = null;
    }

    private static IContextMenu* BindItems(IReadOnlyList<string> paths)
    {
        var pidls = new ITEMIDLIST*[paths.Count];
        IShellItemArray* array = null;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                PInvoke.SHParseDisplayName(paths[i], null, out var pidl, 0, out _).ThrowOnFailure();
                pidls[i] = pidl;
            }

            fixed (ITEMIDLIST** list = pidls)
            {
                IShellItemArray* created;
                PInvoke.SHCreateShellItemArrayFromIDLists((uint)paths.Count, list, &created).ThrowOnFailure();
                array = created;
            }

            var handler = PInvoke.BHID_SFUIObject;
            var iid = IContextMenu.IID_Guid;
            IContextMenu* menu;
            array->BindToHandler(null, &handler, &iid, (void**)&menu).ThrowOnFailure();
            return menu;
        }
        finally
        {
            if (array is not null)
            {
                array->Release();
            }

            foreach (var pidl in pidls)
            {
                if (pidl is not null)
                {
                    PInvoke.CoTaskMemFree(pidl);
                }
            }
        }
    }

    private static IContextMenu* BindBackground(string folder)
    {
        PInvoke.SHCreateItemFromParsingName(folder, null, out IShellItem* item).ThrowOnFailure();
        try
        {
            var handler = PInvoke.BHID_SFViewObject;
            var iid = IContextMenu.IID_Guid;
            IContextMenu* menu;
            item->BindToHandler(null, &handler, &iid, (void**)&menu).ThrowOnFailure();
            return menu;
        }
        finally
        {
            item->Release();
        }
    }

    private List<ShellMenuItem> Read(HMENU source)
    {
        var result = new List<ShellMenuItem>();
        var count = PInvoke.GetMenuItemCount(source);
        const int bufferLength = 512;
        var text = stackalloc char[bufferLength];
        for (var position = 0; position < count; position++)
        {
            text[0] = ' ';
            MENUITEMINFOW info = default;
            info.cbSize = (uint)sizeof(MENUITEMINFOW);
            info.fMask = MENU_ITEM_MASK.MIIM_BITMAP | MENU_ITEM_MASK.MIIM_FTYPE | MENU_ITEM_MASK.MIIM_STRING | MENU_ITEM_MASK.MIIM_ID
                | MENU_ITEM_MASK.MIIM_SUBMENU | MENU_ITEM_MASK.MIIM_STATE;
            info.dwTypeData = text;
            info.cch = bufferLength - 1;
            if (!PInvoke.GetMenuItemInfo(source, (uint)position, true, &info))
            {
                continue;
            }

            var type = (MENU_ITEM_TYPE)info.fType;
            if (type.HasFlag(MENU_ITEM_TYPE.MFT_SEPARATOR))
            {
                result.Add(new ShellMenuItem(position, -1, ShellMenuItemKind.Separator, string.Empty, null, true, false, false, null, null));
                continue;
            }

            var id = (int)(info.wID - FirstCommand);
            var state = (MENU_ITEM_STATE)info.fState;
            var hasSubMenu = !info.hSubMenu.IsNull;
            result.Add(new ShellMenuItem(
                position,
                id,
                hasSubMenu ? ShellMenuItemKind.SubMenu : ShellMenuItemKind.Item,
                new string(info.dwTypeData),
                hasSubMenu ? null : VerbOf((uint)id),
                !state.HasFlag(MENU_ITEM_STATE.MFS_DISABLED) && !state.HasFlag(MENU_ITEM_STATE.MFS_GRAYED),
                state.HasFlag(MENU_ITEM_STATE.MFS_CHECKED),
                state.HasFlag(MENU_ITEM_STATE.MFS_DEFAULT),
                IconOf(info.hbmpItem),
                null));
        }

        return result;
    }

    private string? VerbOf(uint offset)
    {
        if (offset > MaxCommandOffset)
        {
            return null;
        }

        const int capacity = 512;
        var buffer = stackalloc char[capacity];
        try
        {
            return _menu->GetCommandString(offset, PInvoke.GCS_VERBW, null, (PSTR)(byte*)buffer, capacity).Succeeded ? new string(buffer) : null;
        }
        catch (Exception ex) when (ex is InvalidCastException or ArgumentException or COMException or NotImplementedException)
        {
            return null;
        }
    }

    private static ShellMenuIcon? IconOf(HBITMAP bitmap)
    {
        // Small values are the system's own placeholders (HBMMENU_SYSTEM and friends), not bitmaps.
        var value = (nint)bitmap.Value;
        if (value is >= -1 and <= 11)
        {
            return null;
        }

        var image = ShellThumbnailSource.CopyPixels(bitmap);
        return image is { Width: <= MaxIconSide, Height: <= MaxIconSide } ? new ShellMenuIcon(image.Width, image.Height, image.Bgra.ToArray()) : null;
    }
}
