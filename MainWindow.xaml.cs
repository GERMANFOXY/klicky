using System;
using System.Collections.Generic;
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
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Controls;
using IOPath = System.IO.Path;
using Timer = System.Timers.Timer;

namespace Klicky;

public partial class MainWindow : Window
{
    private const string AppVersion = "1.2.0";
    private const string ManifestUrl = "https://raw.githubusercontent.com/GERMANFOXY/klicky/master/manifest.json";

    private const int HotkeyId = 9000;
    private const int WmHotkey = 0x0312;
    private uint _currentVirtualKey = 0x75; // Default F6

    private static readonly HttpClient Http = new();

    // ...existing code...

    private void ShowChangelogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var changelogPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "changelog.json");
            if (!System.IO.File.Exists(changelogPath))
            {
                MessageBox.Show("Changelog-Datei nicht gefunden!", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var json = System.IO.File.ReadAllText(changelogPath);
            var changelog = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
            if (changelog == null)
            {
                MessageBox.Show("Changelog konnte nicht geladen werden!", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var version = AppVersion;
            if (!changelog.ContainsKey(version))
            {
                var keys = string.Join(", ", changelog.Keys.Select(k => $"\"{k}\""));
                MessageBox.Show($"Keine Änderungen für Version {version} gefunden! Verfügbare Versionen: {keys}", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var changes = changelog[version];
            var text = $"Version {version}:\n\n" + string.Join("\n• ", changes);
            var window = new ChangelogWindow();
            window.ChangelogText.Text = text;
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler beim Laden des Changelogs: {ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ...existing code...
    private readonly Timer _clickTimer = new();
    private readonly object _clickLock = new();
    private readonly MediaPlayer _notificationPlayer = new();
    private readonly DispatcherTimer _fireworksTimer = new();
    private readonly Random _rand = new();
    private bool _isRunning;
    private PointStruct _fixedPoint;
    private bool _hasFixedPoint;
    private bool _isHoldMode;
    private bool _isCurrentlyHolding;
    private DateTime _holdStartTime;
    private bool _fireworksRunning;
    private DateTime _fireworksEnd;

    public MainWindow()
    {
        InitializeComponent();
        this.MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
        _clickTimer.Elapsed += OnClickTimerElapsed;
        _clickTimer.AutoReset = true;
        _fireworksTimer.Interval = TimeSpan.FromMilliseconds(200);
        _fireworksTimer.Tick += FireworksTimer_Tick;
        UpdateStatus("Bereit", running: false);
        UpdateHotkeyDisplay();

        SetupFireworksVideo();
        
        // Load profiles and settings
        LoadProfiles();
        LoadSettings();
        
        // Load notification sound
        try
        {
            var soundPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "notification.mp3");
            if (File.Exists(soundPath))
            {
                _notificationPlayer.Open(new Uri(soundPath));
            }
        }
        catch { /* Ignore if sound file is missing */ }
    }

    private string ProfilesPath => IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles.json");
    private string SettingsPath => IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(ProfilesPath))
            {
                ProfileCombo.ItemsSource = null;
                return;
            }
            var json = File.ReadAllText(ProfilesPath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Profile>>(json);
            if (dict == null) return;
            ProfileCombo.ItemsSource = dict.Keys.ToList();
        }
        catch { /* ignore */ }
    }

    private void SaveProfilesDictionary(Dictionary<string, Profile> dict)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(dict, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ProfilesPath, json);
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(json);
            if (settings == null) return;
            // apply settings: hotkey index
            if (settings.HotkeyIndex >= 0 && settings.HotkeyIndex <= 11)
            {
                HotkeyComboBox.SelectedIndex = settings.HotkeyIndex;
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new Settings { HotkeyIndex = HotkeyComboBox.SelectedIndex };
            var json = System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private class Profile
    {
        public int Cps { get; set; }
        public int Interval { get; set; }
        public int ButtonIndex { get; set; }
        public bool FollowCursor { get; set; }
        public bool HoldMode { get; set; }
        public int HoldDuration { get; set; }
        public int HotkeyIndex { get; set; }
    }

    private class Settings
    {
        public int HotkeyIndex { get; set; }
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
        SaveSettings();
    }

    private void ProfileCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileCombo.SelectedItem == null) return;
        var name = ProfileCombo.SelectedItem.ToString();
        LoadProfile(name);
    }

    private void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Profilname eingeben:", "Profil speichern", "NewProfile");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Dictionary<string, Profile> dict = new();
            if (File.Exists(ProfilesPath))
            {
                var json = File.ReadAllText(ProfilesPath);
                var tmp = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Profile>>(json);
                if (tmp != null) dict = tmp;
            }
            var profile = new Profile
            {
                Cps = int.TryParse(CpsInput.Text, out var c) ? c : 10,
                Interval = int.TryParse(IntervalInput.Text, out var i) ? i : 100,
                ButtonIndex = ButtonSelect.SelectedIndex,
                FollowCursor = FollowCursorCheck.IsChecked ?? true,
                HoldMode = HoldModeCheck.IsChecked ?? false,
                HoldDuration = int.TryParse(HoldDurationInput.Text, out var h) ? h : 3,
                HotkeyIndex = HotkeyComboBox.SelectedIndex
            };
            dict[name] = profile;
            SaveProfilesDictionary(dict);
            LoadProfiles();
            ProfileCombo.SelectedItem = name;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Fehler beim Speichern des Profils: {ex.Message}", "Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileCombo.SelectedItem == null) return;
        var name = ProfileCombo.SelectedItem.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            if (!File.Exists(ProfilesPath)) return;
            var json = File.ReadAllText(ProfilesPath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Profile>>(json);
            if (dict == null) return;
            if (dict.Remove(name))
            {
                SaveProfilesDictionary(dict);
                LoadProfiles();
            }
        }
        catch { }
    }

    private void LoadProfile(string name)
    {
        try
        {
            if (!File.Exists(ProfilesPath)) return;
            var json = File.ReadAllText(ProfilesPath);
            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Profile>>(json);
            if (dict == null) return;
            if (!dict.ContainsKey(name)) return;
            var p = dict[name];
            CpsInput.Text = p.Cps.ToString();
            IntervalInput.Text = p.Interval.ToString();
            ButtonSelect.SelectedIndex = p.ButtonIndex;
            FollowCursorCheck.IsChecked = p.FollowCursor;
            HoldModeCheck.IsChecked = p.HoldMode;
            HoldDurationInput.Text = p.HoldDuration.ToString();
            HotkeyComboBox.SelectedIndex = Math.Clamp(p.HotkeyIndex, 0, 11);
            // Apply hotkey change
            UpdateHotkeyDisplay();
            RegisterHotKey();
            SaveSettings();
        }
        catch { }
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

    private void CpsInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
    }

    private void StartFireworks(TimeSpan duration)
    {
        _fireworksRunning = true;
        _fireworksEnd = DateTime.Now + duration;
        FireworksCanvas.Visibility = Visibility.Collapsed;
        FireworksCanvas.Opacity = 0.0;
        if (FireworksVideo.Source != null)
        {
            try
            {
                FireworksVideo.Position = TimeSpan.Zero;
                FireworksVideo.Opacity = 0.45;
                FireworksVideo.Play();
            }
            catch { /* ignore playback errors */ }
        }
        _fireworksTimer.Start();
    }

    private void StopFireworks()
    {
        _fireworksTimer.Stop();
        FireworksCanvas.Children.Clear();
        FireworksCanvas.Opacity = 0.0;
        FireworksCanvas.Visibility = Visibility.Collapsed;
        if (FireworksVideo.Source != null)
        {
            try
            {
                FireworksVideo.Stop();
                FireworksVideo.Opacity = 0.0;
            }
            catch { }
        }
        _fireworksRunning = false;
    }

    private void FireworksTimer_Tick(object? sender, EventArgs e)
    {
        if (!_fireworksRunning)
        {
            return;
        }

        if (DateTime.Now >= _fireworksEnd)
        {
            StopFireworks();
            return;
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Konnte Link nicht öffnen: {ex.Message}", "Link", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void SetupFireworksVideo()
    {
        try
        {
            var exeDir = IOPath.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory;
            var videoPath = IOPath.Combine(exeDir, "fireworks.mp4");
            if (File.Exists(videoPath))
            {
                FireworksVideo.Source = new Uri(videoPath);
                FireworksVideo.MediaEnded += (_, _) =>
                {
                    try
                    {
                        FireworksVideo.Position = TimeSpan.Zero;
                        FireworksVideo.Play();
                    }
                    catch { }
                };
            }
        }
        catch { /* ignore */ }
    }

    private void HoldModeCheck_Changed(object sender, RoutedEventArgs e)
    {
        var isHoldMode = HoldModeCheck.IsChecked ?? false;
        HoldDurationLabel.Visibility = isHoldMode ? Visibility.Visible : Visibility.Collapsed;
        HoldDurationInput.Visibility = isHoldMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private double ParseHoldDuration()
    {
        var text = HoldDurationInput.Dispatcher.Invoke(() => HoldDurationInput.Text);
        if (double.TryParse(text, out var val) && val > 0)
            return val;
        return 3.0; // Default 3 seconds
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
        _isHoldMode = HoldModeCheck.Dispatcher.Invoke(() => HoldModeCheck.IsChecked ?? false);
        _isCurrentlyHolding = false;
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
        
        // Release mouse if currently holding
        if (_isCurrentlyHolding)
        {
            var leftClick = ButtonSelect.Dispatcher.Invoke(() => ButtonSelect.SelectedIndex == 0);
            uint up = leftClick ? MouseEventLeftUp : MouseEventRightUp;
            mouse_event(up, 0, 0, 0, 0);
            _isCurrentlyHolding = false;
        }
        
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
        if (_isHoldMode)
        {
            PerformHoldMode();
        }
        else
        {
            PerformNormalClick();
        }
    }

    private void PerformNormalClick()
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

    private void PerformHoldMode()
    {
        var leftClick = ButtonSelect.Dispatcher.Invoke(() => ButtonSelect.SelectedIndex == 0);
        var followCursor = FollowCursorCheck.Dispatcher.Invoke(() => FollowCursorCheck.IsChecked ?? true);
        var holdDurationSeconds = ParseHoldDuration();

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

        if (!followCursor)
        {
            SetCursorPos(target.X, target.Y);
        }

        uint down = leftClick ? MouseEventLeftDown : MouseEventRightDown;
        uint up = leftClick ? MouseEventLeftUp : MouseEventRightUp;

        if (!_isCurrentlyHolding)
        {
            // Start holding
            mouse_event(down, 0, 0, 0, 0);
            _isCurrentlyHolding = true;
            _holdStartTime = DateTime.Now;
        }
        else
        {
            // Check if hold duration expired
            var elapsed = (DateTime.Now - _holdStartTime).TotalSeconds;
            if (elapsed >= holdDurationSeconds)
            {
                // Release and start new hold
                mouse_event(up, 0, 0, 0, 0);
                _isCurrentlyHolding = false;
            }
        }
    }

    private async Task CheckForUpdatesAsync(bool silent = false)
    {
        try
        {
            // Add cache-busting parameter to ensure fresh manifest
            var urlWithCache = ManifestUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var json = await Http.GetStringAsync(urlWithCache);
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

            // Always show update message when new version is available
            var result = MessageBox.Show($"Neue Version {manifest.Version} verfügbar. Jetzt herunterladen und installieren?", "Update", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
            {
                await DownloadAndInstallUpdateAsync(manifest);
            }
        }
        catch (Exception ex)
        {
            // Even in silent mode, log the error for debugging
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex.Message}");
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
            
            var tempPath = IOPath.Combine(IOPath.GetTempPath(), $"Klicky-Setup-{manifest.Version}.exe");

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
                : new SolidColorBrush(Color.FromRgb(247, 243, 255));
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
        else
        {
            UpdateStatus("Bereit", running: false);
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

    private void MainWindow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void CheckForChangelog()
    {
        try
        {
            var lastVersionPath = IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_version.txt");
            var currentVersion = AppVersion;

            string? lastVersion = null;
            if (File.Exists(lastVersionPath))
            {
                lastVersion = File.ReadAllText(lastVersionPath).Trim();
            }

            if (lastVersion != currentVersion)
            {
                // Save current version
                File.WriteAllText(lastVersionPath, currentVersion);
            }
        }
        catch { /* Ignore errors */ }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class UpdateManifest
    {
        public string? Version { get; set; }
        public string? Url { get; set; }
        public string? Sha256 { get; set; }
    }
}