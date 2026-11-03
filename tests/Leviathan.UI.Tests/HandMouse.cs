using System.Drawing;
using System.Runtime.InteropServices;

namespace Leviathan.UI.Tests;

/// <summary>
/// A mouse that drags the way the system's own pointer does: every move is an input event (absolute, over the whole virtual desktop),
/// not a jump of the cursor, so a control that has captured the pointer sees the button still held while it moves. FlaUI's own
/// <c>Mouse.MoveTo</c> places the cursor directly, which XAML does not report as a drag in progress.
/// </summary>
internal static partial class HandMouse
{
    private const uint Move = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint Absolute = 0x8000;
    private const uint VirtualDesk = 0x4000;
    private const int VirtualLeft = 76;
    private const int VirtualTop = 77;
    private const int VirtualWidth = 78;
    private const int VirtualHeight = 79;

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    /// <summary>Moves the pointer to <paramref name="point"/> (screen pixels).</summary>
    public static void MoveTo(Point point)
    {
        var left = GetSystemMetrics(VirtualLeft);
        var top = GetSystemMetrics(VirtualTop);
        var width = Math.Max(1, GetSystemMetrics(VirtualWidth));
        var height = Math.Max(1, GetSystemMetrics(VirtualHeight));
        var x = (int)(((point.X - left) * 65535L / (width - 1)) + 0);
        var y = (int)(((point.Y - top) * 65535L / (height - 1)) + 0);
        mouse_event(Move | Absolute | VirtualDesk, x, y, 0, 0);
        Thread.Sleep(15);
    }

    /// <summary>Presses the left button.</summary>
    public static void Down() => mouse_event(LeftDown, 0, 0, 0, 0);

    /// <summary>Releases the left button.</summary>
    public static void Up() => mouse_event(LeftUp, 0, 0, 0, 0);

    /// <summary>Drags from one point to another in steps and lets go.</summary>
    public static void Drag(Point from, Point to, int steps = 20, int pauseMs = 25)
    {
        MoveTo(from);
        Thread.Sleep(150);
        Down();
        Thread.Sleep(150);
        for (var step = 1; step <= steps; step++)
        {
            MoveTo(new Point(from.X + ((to.X - from.X) * step / steps), from.Y + ((to.Y - from.Y) * step / steps)));
            Thread.Sleep(pauseMs);
        }

        Up();
        Thread.Sleep(300);
    }
}
