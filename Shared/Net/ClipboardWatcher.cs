using System.Runtime.InteropServices;
using System.Text;

namespace SkarmStyrning.Net;

/// <summary>
/// Känner av när text kopieras (WM_CLIPBOARDUPDATE) och kan sätta urklippet utan att det räknas
/// som en egen ändring (så att det inte skickas tillbaka i en loop). Måste skapas på en UI-tråd.
/// </summary>
public sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    /// <summary>Största text som delas (UTF-8-byte) – ryms med marginal i en krypterad ram.</summary>
    public const int MaxBytes = 900_000;

    const int WM_CLIPBOARDUPDATE = 0x031D;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    string? suppress;
    string? last;

    /// <summary>Ny text kopierades på den här datorn.</summary>
    public event Action<string>? TextChanged;

    public ClipboardWatcher()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        AddClipboardFormatListener(Handle);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) OnChanged();
        base.WndProc(ref m);
    }

    void OnChanged()
    {
        var text = TryGetText();
        if (text == null || text == last) return;
        last = text;
        if (text == suppress) { suppress = null; return; } // vi satte den själva (från den andra datorn)
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes) return;
        TextChanged?.Invoke(text);
    }

    /// <summary>Sätter urklippet med text från den andra datorn.</summary>
    public void SetText(string text)
    {
        if (string.IsNullOrEmpty(text) || text == last) return;
        suppress = text;
        for (int i = 0; i < 6; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch (ExternalException) { Thread.Sleep(40); } // urklippet är tillfälligt låst av ett annat program
        }
        suppress = null;
    }

    static string? TryGetText()
    {
        for (int i = 0; i < 6; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return null;
    }
}
