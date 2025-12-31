using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Timer = System.Timers.Timer;

namespace Klicky;

public partial class MainWindow : Window
{
    private const string AppVersion = "0.0.0";
    private const string ManifestUrl = "https://raw.githubusercontent.com/GERMANFOXY/klicky/master/manifest.json";

    private const int HotkeyId = 9000;
    private const int WmHotkey = 0x0312;
    private uint _currentVirtualKey = 0x75; // Default F6

    private static readonly HttpClient Http = new();
    private readonly Timer _clickTimer = new();
    private readonly object _clickLock = new();
    private readonly MediaPlayer _notificationPlayer = new();
    private bool _isRunning;
    private PointStruct _fixedPoint;
    private bool _hasFixedPoint;

    public MainWindow()
    {
        InitializeComponent();
        _clickTimer.Elapsed += OnClickTimerElapsed;
        _clickTimer.AutoReset = true;
        UpdateStatus("Bereit", running: false);
        UpdateHotkeyDisplay();
        
        // Load notification sound
        try
        {
            var soundPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "notification.mp3");
            if (File.Exists(soundPath))
            {
                _notificationPlayer.Open(new Uri(soundPath));
            }
        }
        catch { /* Ignore if sound file is missing */ }
    }

    private uint GetVirtualKeyCode(int fKeyIndex)
    {
        // F1-F12 have VK codes 0x70-0x7B
        return (uint)(0x70 + fKeyIndex);
    }

    private void UpdateHotkeyDisplay()
    {
        int fKeyNumber = (int)(_currentVirtualKey - 0x70) + 1;
        HotkeyText.Text = $"F{fKeyNumber} zum Start/Stop";
    }

    private void HotkeyComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (HotkeyComboBox.SelectedIndex < 0) return;
        
        uint newVirtualKey = GetVirtualKeyCode(HotkeyComboBox.SelectedIndex);
        if (newVirtualKey == _currentVirtualKey) return;

        // Unregister old hotkey
        UnregisterHotKey();
        
        // Update to new key
        _currentVirtualKey = newVirtualKey;
        
        // Register new hotkey
        RegisterHotKey();
        
        // Update display
        UpdateHotkeyDisplay();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(WndProc);
        RegisterHotKey();
        _ = CheckForUpdatesAsync(silent: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _clickTimer?.Stop();
        _clickTimer?.Dispose();
        UnregisterHotKey();
        base.OnClosing(e);
    }

    private void StartStopButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleClicking();
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        try
        {
            await CheckForUpdatesAsync(silent: false);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ToggleClicking()
    {
        if (_isRunning)
        {
            StopClicking();
        }
        else
        {
            StartClicking();
        }
    }

    private void StartClicking()
    {
        var intervalMs = ParseIntervalMs();
        var cps = ParseCps();

        double interval;
        if (intervalMs > 0)
        {
            interval = intervalMs;
        }
        else if (cps > 0)
        {
            interval = 1000.0 / cps;
        }
        else
        {
            UpdateStatus("Bitte CPS oder Intervall setzen", running: false);
            return;
        }

        _hasFixedPoint = false;
        var followCursor = FollowCursorCheck.Dispatcher.Invoke(() => FollowCursorCheck.IsChecked ?? true);
        if (!followCursor && GetCursorPos(out var point))
        {
            _fixedPoint = point;
            _hasFixedPoint = true;
        }

        _clickTimer.Interval = interval;
        _clickTimer.Start();
        _isRunning = true;
        Dispatcher.Invoke(() => StartStopButton.Content = "Stoppen");
        UpdateStatus("Läuft", running: true);
    }

    private void StopClicking()
    {
        _clickTimer.Stop();
        _isRunning = false;
        Dispatcher.Invoke(() => StartStopButton.Content = "Starten");
        UpdateStatus("Bereit", running: false);
    }

    private void OnClickTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!_isRunning)
        {
            return;
        }

        lock (_clickLock)
        {
            PerformClick();
        }
    }

    private void PerformClick()
    {
        var leftClick = ButtonSelect.Dispatcher.Invoke(() => ButtonSelect.SelectedIndex == 0);
        var followCursor = FollowCursorCheck.Dispatcher.Invoke(() => FollowCursorCheck.IsChecked ?? true);

        PointStruct target = default;
        if (followCursor)
        {
            if (!GetCursorPos(out target))
            {
                return;
            }
        }
        else
        {
            if (!_hasFixedPoint && !GetCursorPos(out target))
            {
                return;
            }

            if (_hasFixedPoint)
            {
                target = _fixedPoint;
            }
        }

        // Move if a fixed target is used so the click hits the saved coordinates.
        if (!followCursor)
        {
            SetCursorPos(target.X, target.Y);
        }

        uint down = leftClick ? MouseEventLeftDown : MouseEventRightDown;
        uint up = leftClick ? MouseEventLeftUp : MouseEventRightUp;

        mouse_event(down, 0, 0, 0, 0);
        mouse_event(up, 0, 0, 0, 0);
    }

    private async Task CheckForUpdatesAsync(bool silent = false)
    {
        try
        {
            var json = await Http.GetStringAsync(ManifestUrl);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, options);

            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                if (!silent)
                    MessageBox.Show("Manifest fehlerhaft oder leer.", "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.Equals(manifest.Version.Trim(), AppVersion, StringComparison.OrdinalIgnoreCase))
            {
                if (!silent)
                    MessageBox.Show($"Du hast bereits Version {AppVersion}.", "Update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"Neue Version {manifest.Version} verfügbar. Jetzt herunterladen und installieren?", "Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                await DownloadAndInstallUpdateAsync(manifest);
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show($"Update-Check fehlgeschlagen: {ex.Message}", "Update", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task DownloadAndInstallUpdateAsync(UpdateManifest manifest)
    {
        try
        {
            UpdateStatus("Update wird heruntergeladen...", running: false);

            // Get installer URL - construct direct download URL from GitHub release
            var installerUrl = $"https://github.com/GERMANFOXY/klicky/releases/download/v{manifest.Version}/Klicky-Setup-{manifest.Version}.exe";
            
            var tempPath = Path.Combine(Path.GetTempPath(), $"Klicky-Setup-{manifest.Version}.exe");

            // Download installer
            var response = await Http.GetAsync(installerUrl);
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show($"Download fehlgeschlagen: {response.StatusCode}", "Update Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                UpdateStatus("Bereit", running: false);
                return;
            }

            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(fs);
            }

            UpdateStatus("Update wird installiert...", running: false);

            // Start installer
            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                UseShellExecute = true,
                Verb = "runas" // Run as admin
            });

            // Close current app
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Update fehlgeschlagen: {ex.Message}", "Update Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            UpdateStatus("Bereit", running: false);
        }
    }

    private void UpdateStatus(string message, bool running)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = running
                ? new SolidColorBrush(Color.FromRgb(120, 255, 189))
                : new SolidColorBrush(Color.FromRgb(201, 182, 255));
        }, DispatcherPriority.Background);
    }

    private int ParseCps()
    {
        var text = CpsInput.Dispatcher.Invoke(() => CpsInput.Text.Trim());
        return int.TryParse(text, out var cps)
            ? Math.Clamp(cps, 1, 50)
            : 0;
    }

    private int ParseIntervalMs()
    {
        var text = IntervalInput.Dispatcher.Invoke(() => IntervalInput.Text.Trim());
        return int.TryParse(text, out var ms)
            ? Math.Clamp(ms, 1, 5000)
            : 0;
    }

    private void RegisterHotKey()
    {
        var helper = new WindowInteropHelper(this);
        UnregisterHotKey();
        if (helper.Handle == IntPtr.Zero)
        {
            UpdateStatus("Hotkey nicht verfügbar", running: false);
            return;
        }

        if (!WinRegisterHotKey(helper.Handle, HotkeyId, 0, _currentVirtualKey))
        {
            UpdateStatus("Hotkey konnte nicht registriert werden", running: false);
        }
    }

    private void UnregisterHotKey()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            WinUnregisterHotKey(helper.Handle, HotkeyId);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            PlayNotificationSound();
            ToggleClicking();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void PlayNotificationSound()
    {
        try
        {
            _notificationPlayer.Position = TimeSpan.Zero;
            _notificationPlayer.Play();
        }
        catch { /* Ignore playback errors */ }
    }

    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const uint MouseEventRightDown = 0x0008;
    private const uint MouseEventRightUp = 0x0010;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "RegisterHotKey")]
    private static extern bool WinRegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UnregisterHotKey")]
    private static extern bool WinUnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

    private sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
        public string? Sha256 { get; set; }
    }
}