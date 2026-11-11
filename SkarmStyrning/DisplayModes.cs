using System.Runtime.InteropServices;

namespace SkarmStyrning;

public readonly record struct VideoMode(int Width, int Height, int Hz);

/// <summary>Vilka upplösningar och uppdateringsfrekvenser en skärm klarar, och att byta till en av dem.</summary>
public static class DisplayModes
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsEx(string device, int mode, ref DEVMODE dm, int flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);

    const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const int CDS_UPDATEREGISTRY = 0x1;

    static readonly Dictionary<string, (DateTime at, List<VideoMode> modes)> cache = new();

    /// <summary>Alla lägen skärmen (\\.\DISPLAYn) klarar i 32-bitars färg, bäst först.</summary>
    public static List<VideoMode> List(string gdiName)
    {
        lock (cache)
            if (cache.TryGetValue(gdiName, out var c) && DateTime.Now - c.at < TimeSpan.FromSeconds(30)) return c.modes;
        var set = new HashSet<VideoMode>();
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettingsEx(gdiName, i, ref dm, 0); i++)
        {
            if (dm.dmBitsPerPel != 32 || dm.dmDisplayFrequency <= 1) continue;
            set.Add(new VideoMode(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency));
        }
        var list = set.OrderByDescending(m => (long)m.Width * m.Height).ThenByDescending(m => m.Hz).ToList();
        lock (cache) cache[gdiName] = (DateTime.Now, list);
        return list;
    }

    /// <summary>Byter läge på skärmen och sparar det i Windows.</summary>
    public static void Set(string gdiName, VideoMode mode)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsEx(gdiName, -1 /*nuvarande*/, ref dm, 0))
            throw new InvalidOperationException("Kunde inte läsa skärmens nuvarande läge.");
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmBitsPerPel = 32;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        int r = ChangeDisplaySettingsEx(gdiName, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (r != 0)
            throw new InvalidOperationException(r switch
            {
                -2 => $"Skärmen klarar inte {mode.Width}×{mode.Height} @ {mode.Hz} Hz.",
                1 => "Windows behöver startas om för att byta läge.",
                _ => $"Windows kunde inte byta läge (kod {r}).",
            });
        lock (cache) cache.Remove(gdiName);
    }
}
