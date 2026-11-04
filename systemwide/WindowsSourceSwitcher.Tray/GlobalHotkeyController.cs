using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowsSourceSwitcher.Tray;

internal enum TrayHotkeyAction
{
    OpenSources,
    OpenMixer,
    PreviousFavorite,
    NextFavorite,
    ReleaseMaster,
    ToggleFullscreenPriority
}

internal sealed record TrayHotkeyBinding(TrayHotkeyAction Action, string Name, uint VirtualKey);

internal interface IHotkeyRegistrar
{
    bool Register(IntPtr window, int id, uint modifiers, uint virtualKey, out int error);
    bool Unregister(IntPtr window, int id, out int error);
}

internal interface IHotkeyMessageWindow : IDisposable
{
    IntPtr Handle { get; }
    event Action<int, uint, uint>? HotkeyReceived;
}

/// <summary>Windows delivers opted-in shortcuts to an idle message-only window on the UI thread.</summary>
internal sealed class GlobalHotkeyController : IDisposable
{
    internal const uint Modifiers = 0x0001 | 0x0002 | 0x0004 | 0x4000; // Alt, Ctrl, Shift, no repeat.
    internal static readonly IReadOnlyList<TrayHotkeyBinding> Bindings = Array.AsReadOnly<TrayHotkeyBinding>([
        new(TrayHotkeyAction.OpenSources, "Open sources", 0x4F),
        new(TrayHotkeyAction.OpenMixer, "Open mixer", 0x4D),
        new(TrayHotkeyAction.PreviousFavorite, "Previous favorite", 0x25),
        new(TrayHotkeyAction.NextFavorite, "Next favorite", 0x27),
        new(TrayHotkeyAction.ReleaseMaster, "Release master", 0x52),
        new(TrayHotkeyAction.ToggleFullscreenPriority, "Toggle fullscreen priority", 0x46)
    ]);
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly IHotkeyRegistrar _registrar;
    private readonly Func<IHotkeyMessageWindow> _windowFactory;
    private readonly Dictionary<int, TrayHotkeyBinding> _owned = [];
    private readonly Dictionary<int, TrayHotkeyBinding> _active = [];
    private IHotkeyMessageWindow? _window;
    private bool _enabled, _disposed;
    private int _nextId = 0x1000;

    public GlobalHotkeyController() : this(new WindowsHotkeyRegistrar(), () => new NativeHotkeyWindow()) { }

    internal GlobalHotkeyController(IHotkeyRegistrar registrar, Func<IHotkeyMessageWindow> windowFactory)
    {
        _registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
    }

    public event Action<TrayHotkeyAction>? Invoked;
    public event Action? StatusChanged;
    public string Status { get; private set; } = "Global hotkeys off.";

    public void SetEnabled(bool enabled)
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled == enabled && (enabled || _owned.Count == 0)) return;
        _enabled = enabled;
        Configure();
    }

    public void Retry()
    {
        CheckOwner();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Configure();
    }

    private void Configure()
    {
        _active.Clear();
        var failures = UnregisterOwned();
        if (!_enabled)
        {
            SetStatus(failures.Count == 0 ? "Global hotkeys off."
                : "Global hotkeys off; cleanup: " + string.Join("; ", failures));
            return;
        }

        if (_nextId + Bindings.Count - 1 > 0xBFFF)
        {
            SetStatus("Global hotkey identifiers exhausted. Restart the app to register shortcuts again.");
            return;
        }
        try
        {
            if (_window is null)
            {
                _window = _windowFactory();
                _window.HotkeyReceived += OnHotkeyReceived;
            }
            foreach (var binding in Bindings)
            {
                // Never recycle identifiers within this window's lifetime. A queued old
                // WM_HOTKEY must not dispatch a newly configured action after a retry.
                var id = _nextId++;
                if (_registrar.Register(_window.Handle, id, Modifiers, binding.VirtualKey, out var error))
                {
                    _owned.Add(id, binding);
                    _active.Add(id, binding);
                }
                else
                {
                    failures.Add($"{binding.Name}: unavailable (Win32 {error})");
                }
            }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            failures.Add("Registration failed: " + error.Message);
        }

        var status = $"Global hotkeys: {_active.Count}/{Bindings.Count} registered.";
        if (failures.Count > 0) status += " " + string.Join("; ", failures) + ". Retry after freeing the shortcut.";
        SetStatus(status);
    }

    private List<string> UnregisterOwned()
    {
        var failures = new List<string>();
        if (_window is null) return failures;
        foreach (var (id, binding) in _owned.ToArray())
        {
            try
            {
                if (_registrar.Unregister(_window.Handle, id, out var error) || error == 1419 /* not registered */)
                    _owned.Remove(id);
                else
                    failures.Add($"{binding.Name}: unregister failed (Win32 {error})");
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException)
            {
                failures.Add(binding.Name + ": unregister failed (" + error.Message + ")");
            }
        }
        return failures;
    }

    private void OnHotkeyReceived(int id, uint modifiers, uint virtualKey)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId
            || _disposed || !_enabled || !_active.TryGetValue(id, out var binding)
            || modifiers != (Modifiers & 0x000F) || virtualKey != binding.VirtualKey) return;
        try { Invoked?.Invoke(binding.Action); }
        catch (Exception error) { SetStatus("Hotkey action failed: " + error.Message); }
    }

    private void SetStatus(string status)
    {
        if (string.Equals(Status, status, StringComparison.Ordinal)) return;
        Status = status;
        // A status consumer must not interrupt registration or native-window cleanup.
        try { StatusChanged?.Invoke(); }
        catch (Exception) { }
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Global hotkeys must be configured and disposed on their owning UI thread.");
    }

    public void Dispose()
    {
        CheckOwner();
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _active.Clear();
        var failures = UnregisterOwned();
        if (_window is not null)
        {
            _window.HotkeyReceived -= OnHotkeyReceived;
            _window.Dispose();
            _window = null;
        }
        _owned.Clear();
        SetStatus(failures.Count == 0 ? "Global hotkeys off."
            : "Global hotkeys stopped; cleanup: " + string.Join("; ", failures));
    }

    private sealed class NativeHotkeyWindow : NativeWindow, IHotkeyMessageWindow
    {
        public event Action<int, uint, uint>? HotkeyReceived;

        public NativeHotkeyWindow() => CreateHandle(new CreateParams
        {
            Caption = "DX5 global hotkeys",
            Parent = new IntPtr(-3) // HWND_MESSAGE: never visible or included in task switching.
        });

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0312)
            {
                var packed = unchecked((uint)message.LParam.ToInt64());
                HotkeyReceived?.Invoke(message.WParam.ToInt32(), packed & 0xFFFF, packed >> 16);
            }
            base.WndProc(ref message);
        }

        public void Dispose() => DestroyHandle();
    }

    private sealed class WindowsHotkeyRegistrar : IHotkeyRegistrar
    {
        public bool Register(IntPtr window, int id, uint modifiers, uint virtualKey, out int error)
        {
            var success = RegisterHotKey(window, id, modifiers, virtualKey);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }

        public bool Unregister(IntPtr window, int id, out int error)
        {
            var success = UnregisterHotKey(window, id);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr window, int id);
    }
}
