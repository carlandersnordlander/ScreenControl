using System.Runtime.InteropServices;

namespace SkarmStyrning.Net;

/// <summary>Läser vilka skärmar den här datorn har på, med EDID-nyckel och plats på skrivbordet.</summary>
public static class LocalMonitors
{
    [StructLayout(LayoutKind.Sequential)] struct Luid { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    struct PathInfo
    {
        public Luid srcAdapter; public uint srcId; public uint srcModeIdx; public uint srcStatus;
        public Luid tgtAdapter; public uint tgtId; public uint tgtModeIdx; public uint outputTech, rotation, scaling;
        public uint refreshNum, refreshDen; public uint scanLine; public int targetAvailable; public uint tgtStatus;
        public uint flags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct ModeInfo
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public Luid adapter;
        [FieldOffset(16)] public uint width;
        [FieldOffset(20)] public uint height;
        [FieldOffset(24)] public uint pixelFormat;
        [FieldOffset(28)] public int x;
        [FieldOffset(32)] public int y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TargetName
    {
        public uint type, size; public Luid adapter; public uint id;
        public uint flags, outputTech; public ushort mfg, product; public uint connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string path;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint np, out uint nm);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint np, [Out] PathInfo[] p, ref uint nm, [Out] ModeInfo[] m, IntPtr topo);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName req);

    /// <summary>"MMMM-PPPP" ur EDID: tillverkare och produktkod.</summary>
    public static string EdidKey(ushort manufacturer, ushort product) => $"{manufacturer:X4}-{product:X4}";

    public static List<ClientMonitor> Read()
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        PathInfo[] paths; ModeInfo[] modes;
        while (true)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return new();
            paths = new PathInfo[np];
            modes = new ModeInfo[nm];
            int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r == 122) continue;
            if (r != 0) return new();
            Array.Resize(ref paths, (int)np);
            Array.Resize(ref modes, (int)nm);
            break;
        }

        var list = new List<ClientMonitor>();
        foreach (var p in paths)
        {
            if (p.srcModeIdx >= modes.Length || modes[p.srcModeIdx].infoType != 1) continue;
            var m = modes[p.srcModeIdx];
            var tn = new TargetName { type = 2, size = (uint)Marshal.SizeOf<TargetName>(), adapter = p.tgtAdapter, id = p.tgtId };
            if (DisplayConfigGetDeviceInfo(ref tn) != 0) continue;
            list.Add(new ClientMonitor
            {
                Key = EdidKey(tn.mfg, tn.product),
                Name = string.IsNullOrWhiteSpace(tn.friendly) ? "Skärm" : tn.friendly,
                X = m.x, Y = m.y, W = (int)m.width, H = (int)m.height,
                Primary = m.x == 0 && m.y == 0,
            });
        }
        return list;
    }
}
