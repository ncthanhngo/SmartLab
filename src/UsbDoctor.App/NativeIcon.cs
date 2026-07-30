using System.Runtime.InteropServices;

namespace UsbDoctor.App;

/// <summary>
/// Releases icon handles produced by <c>Bitmap.GetHicon</c>.
/// </summary>
/// <remarks>
/// <c>GetHicon</c> hands back a raw GDI handle that nothing in the framework owns:
/// <c>Icon.FromHandle</c> wraps it without taking responsibility, and disposing
/// that wrapper leaves the handle alive. An app that sits in the tray all day would
/// leak one per render, so the handle is destroyed explicitly once the icon has been
/// cloned into managed memory.
/// </remarks>
internal static class NativeIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static void Destroy(IntPtr handle)
    {
        if (handle != IntPtr.Zero) DestroyIcon(handle);
    }
}
