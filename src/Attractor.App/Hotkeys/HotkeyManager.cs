using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Attractor.App.Hotkeys;

/// <summary>
/// System-wide hotkeys via RegisterHotKey — they fire while ANY app has focus,
/// which is the point: skip an annoying game without leaving your editor.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private readonly List<string> _failures = [];
    private int _nextId = 0x4A00;

    public HotkeyManager(Window window)
    {
        _source = (HwndSource?)PresentationSource.FromVisual(window)
                  ?? throw new InvalidOperationException("window has no HWND yet (call after SourceInitialized)");
        _source.AddHook(WndProc);
    }

    /// <summary>Gestures that could not be registered (typically in use by another app).</summary>
    public IReadOnlyList<string> Failures => _failures;

    public void Register(string gesture, Action action)
    {
        if (string.IsNullOrWhiteSpace(gesture))
            return;
        if (!TryParseGesture(gesture, out uint modifiers, out uint vk))
        {
            _failures.Add($"{gesture} (unrecognized)");
            return;
        }
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, vk))
        {
            _failures.Add($"{gesture} (already in use)");
            return;
        }
        _actions[id] = action;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>"Ctrl+Alt+Right" → modifier flags + virtual key.</summary>
    public static bool TryParseGesture(string gesture, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        Key key = Key.None;
        foreach (var raw in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default:
                    if (key != Key.None || !Enum.TryParse(raw, ignoreCase: true, out key) || key == Key.None)
                        return false;
                    break;
            }
        }
        if (key == Key.None)
            return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return vk != 0;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys)
            UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        _source.RemoveHook(WndProc);
    }
}
