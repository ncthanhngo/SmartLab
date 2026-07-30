using System.Runtime.InteropServices;

namespace UsbDoctor.Win32.Devices;

public enum VolumeChangeKind { None, Arrived, Removed }

/// <summary>
/// Decodes <c>WM_DEVICECHANGE</c> into the drive letters that appeared or went away.
/// </summary>
/// <remarks>
/// <para>
/// Window messages are used rather than WMI polling. Arrival is pushed the moment
/// the volume mounts, needs no elevation, and costs nothing while idle - which
/// matters for something meant to sit running all day.
/// </para>
/// <para>
/// The bitmask decoding is a pure function so it can be tested without a device.
/// Getting it wrong would mean scanning the wrong drive, and that is not something
/// to discover by plugging things in.
/// </para>
/// </remarks>
public static class VolumeChangeMessage
{
    public const int WM_DEVICECHANGE = 0x0219;

    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public uint Size;
        public uint DeviceType;
        public uint Reserved;
        public uint UnitMask;
        public ushort Flags;
    }

    /// <summary>
    /// Turns a <c>dbcv_unitmask</c> into drive letters. Bit 0 is A:, bit 25 is Z:.
    /// </summary>
    public static IReadOnlyList<char> DecodeUnitMask(uint unitMask)
    {
        var letters = new List<char>(1);

        for (var i = 0; i < 26; i++)
        {
            if ((unitMask & (1u << i)) != 0)
                letters.Add((char)('A' + i));
        }

        return letters;
    }

    /// <summary>
    /// Interprets a window message, returning the affected drive letters.
    /// </summary>
    /// <returns><see cref="VolumeChangeKind.None"/> for anything that is not a volume event.</returns>
    public static VolumeChangeKind Interpret(int wParam, IntPtr lParam, out IReadOnlyList<char> driveLetters)
    {
        driveLetters = [];

        var kind = wParam switch
        {
            DBT_DEVICEARRIVAL => VolumeChangeKind.Arrived,
            DBT_DEVICEREMOVECOMPLETE => VolumeChangeKind.Removed,
            _ => VolumeChangeKind.None,
        };

        if (kind == VolumeChangeKind.None || lParam == IntPtr.Zero) return VolumeChangeKind.None;

        // Device arrivals also fire for things that are not volumes - interfaces,
        // ports, handles - and those carry a different payload entirely.
        var header = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
        if (header.DeviceType != DBT_DEVTYP_VOLUME) return VolumeChangeKind.None;

        driveLetters = DecodeUnitMask(header.UnitMask);
        return driveLetters.Count == 0 ? VolumeChangeKind.None : kind;
    }
}
