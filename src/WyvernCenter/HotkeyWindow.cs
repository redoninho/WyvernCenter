using System.Runtime.InteropServices;

namespace WyvernCenter;

/// <summary>One global hotkey on a hidden message window (RegisterHotKey).</summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312, Id = 0x4D48;
    private bool registered;

    public event Action? Pressed;

    public HotkeyWindow() => CreateHandle(new CreateParams());

    /// <returns>False if another app already owns the combination.</returns>
    public bool Register(uint modifiers, int key)
    {
        Unregister();
        registered = RegisterHotKey(Handle, Id, modifiers | ModNoRepeat, (uint)key);
        return registered;
    }

    public void Unregister()
    {
        if (registered) UnregisterHotKey(Handle, Id);
        registered = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && (int)m.WParam == Id) Pressed?.Invoke();
        base.WndProc(ref m);
    }

    /// <summary>Shortcuts nearly every app (or Windows) relies on: never taken globally.</summary>
    public static bool IsReserved(uint mods, int key)
    {
        var k = (Keys)key;
        var only = mods & ~ModNoRepeat;
        if (only == ModControl && k is Keys.A or Keys.C or Keys.V or Keys.X or Keys.Y or Keys.Z or Keys.S or Keys.F or Keys.Escape) return true;
        if (only == ModAlt && k is Keys.F4 or Keys.Tab or Keys.Space or Keys.Escape) return true;
        return only == (ModControl | ModAlt) && k == Keys.Delete;
    }

    public static string Describe(uint mods, int key)
    {
        var parts = new List<string>();
        if ((mods & ModControl) != 0) parts.Add("Ctrl");
        if ((mods & ModAlt) != 0) parts.Add("Alt");
        if ((mods & ModShift) != 0) parts.Add("Shift");
        if ((mods & ModWin) != 0) parts.Add("Win");
        var name = ((Keys)key).ToString();
        parts.Add(name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]) ? name[1..] : name); // D1 → 1
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        Unregister();
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
