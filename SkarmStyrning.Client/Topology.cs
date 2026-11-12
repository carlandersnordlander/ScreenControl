using System.Runtime.InteropServices;

namespace SkarmStyrning.Client;

/// <summary>Utöka eller duplicera klientens skärmar (som Win+P), på begäran från servern.</summary>
static class Topology
{
    const uint SDC_TOPOLOGY_CLONE = 0x2, SDC_TOPOLOGY_EXTEND = 0x4, SDC_APPLY = 0x80;

    [DllImport("user32.dll")]
    static extern int SetDisplayConfig(uint numPaths, IntPtr paths, uint numModes, IntPtr modes, uint flags);

    /// <summary>1 = utöka, 2 = duplicera. Returnerar Windows felkod (0 = ok).</summary>
    public static int Apply(byte mode) => mode switch
    {
        1 => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND),
        2 => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_CLONE),
        _ => 87,
    };
}
