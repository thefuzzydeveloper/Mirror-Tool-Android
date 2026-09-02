using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace WiFiAutoStreamSync;

internal static class Program
{
    private const string AppName = "WiFiAutoStreamSync";
    private const string MutexName = $@"Global\{AppName}_SingleInstance_Mutex";
    private const string WindowMessageName = "WIFI_AUTO_STREAM_SYNC_ACTIVATE";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly uint WmActivateApp = RegisterWindowMessage(WindowMessageName);
    private static readonly IntPtr HwndBroadcast = new(0xffff);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static Mutex? _mutex;
    private static NotifyIcon? _trayIcon;
    private static SyncEngine? _engine;
    private static AppConfig _config = new();
    private static ConfigWindow? _activeConfigWindow;
    private static DeviceBrowserWindow? _activeBrowserWindow;
    private static InstanceMessageFilter? _messageFilter;

    private static Icon? _idleIcon;
    private static Icon? _syncingIcon;

    [STAThread]
    private static void Main()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            PostMessage(HwndBroadcast, WmActivateApp, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        EnsureFirewallRule();

        _idleIcon = TrayIconHelper.CreateDynamicIcon(syncing: false);
        _syncingIcon = TrayIconHelper.CreateDynamicIcon(syncing: true);

        _config = ConfigManager.Load();
        InitializeTray();
        StartEngine();

        _messageFilter = new InstanceMessageFilter(WmActivateApp, ShowOrFocusBrowserWindow);
        Application.AddMessageFilter(_messageFilter);

        Application.Run();

        _mutex.ReleaseMutex();
        _idleIcon?.Dispose();
        _syncingIcon?.Dispose();
    }

    private static void InitializeTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Wi-Fi Auto Stream Sync (Active)") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        var browseItem = new ToolStripMenuItem("📱 Browse Android Devices & Storage...")
        {
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold)
        };
        browseItem.Click += (s, e) => ShowOrFocusBrowserWindow();
        menu.Items.Add(browseItem);

        menu.Items.Add("⚡ Verify Manifests & Sync Now", null, (s, e) => _engine?.TriggerSync(null));
        menu.Items.Add("📋 Inspect Local Manifests...", null, (s, e) => ShowManifestInspector());
        menu.Items.Add("Configure Sync Folders...", null, (s, e) => ShowOrFocusConfigWindow());

        var startupItem = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
        startupItem.Click += (s, e) =>
        {
            ToggleStartup(!startupItem.Checked);
            startupItem.Checked = IsStartupEnabled();
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (s, e) =>
        {
            if (_trayIcon != null) _trayIcon.Visible = false;
            if (_engine != null) await _engine.DisposeAsync();
            Application.Exit();
        });

        _trayIcon = new NotifyIcon
        {
            Icon = _idleIcon,
            ContextMenuStrip = menu,
            Text = "Wi-Fi Sync | Scanning for peers...",
            Visible = true
        };

        // Left single-click opens the wireless browser directly
        _trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowOrFocusBrowserWindow();
            }
        };
    }

    private static void StartEngine()
    {
        _engine = new SyncEngine(_config, (status, syncing) =>
        {
            if (_trayIcon != null)
            {
                _trayIcon.Text = $"Wi-Fi Sync | {(status.Length > 50 ? status[..47] + "..." : status)}";
                _trayIcon.Icon = syncing ? _syncingIcon : _idleIcon;
            }
        });
        _engine.Start();
    }

    public static void ShowOrFocusBrowserWindow()
    {
        if (_engine == null) return;

        if (_activeBrowserWindow != null && !_activeBrowserWindow.IsDisposed)
        {
            if (_activeBrowserWindow.WindowState == FormWindowState.Minimized)
                _activeBrowserWindow.WindowState = FormWindowState.Normal;

            _activeBrowserWindow.Activate();
            _activeBrowserWindow.BringToFront();
            return;
        }

        _activeBrowserWindow = new DeviceBrowserWindow(_engine);
        _activeBrowserWindow.Show();
        _activeBrowserWindow.BringToFront();
    }

    public static void ShowManifestInspector()
    {
        if (_engine == null) return;

        var payload = _engine.GetManifestsPayload();
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions 
        { 
            WriteIndented = true, 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        });

        using var viewer = new Form
        {
            Text = "Local Sync Manifest Inspector",
            Size = new Size(720, 520),
            MinimumSize = new Size(480, 320),
            StartPosition = FormStartPosition.CenterScreen,
            Font = new Font("Segoe UI", 9.5f)
        };

        var textBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Text = json,
            Font = new Font("Consolas", 10f),
            WordWrap = false
        };

        viewer.Controls.Add(textBox);
        viewer.ShowDialog();
    }

    public static void ShowOrFocusConfigWindow()
    {
        if (_activeConfigWindow != null && !_activeConfigWindow.IsDisposed)
        {
            if (_activeConfigWindow.WindowState == FormWindowState.Minimized)
                _activeConfigWindow.WindowState = FormWindowState.Normal;

            _activeConfigWindow.Activate();
            _activeConfigWindow.BringToFront();
            return;
        }

        _activeConfigWindow = new ConfigWindow(_config, async newConfig =>
        {
            _config = newConfig;
            ConfigManager.Save(_config);

            if (_engine != null)
            {
                await _engine.DisposeAsync();
            }
            StartEngine();
        });

        _activeConfigWindow.Show();
        _activeConfigWindow.BringToFront();
    }

    private static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch
        {
            return false;
        }
    }

    private static void ToggleStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            if (enable)
                key.SetValue(AppName, $"\"{exePath}\"");
            else
                key.DeleteValue(AppName, false);
        }
        catch { }
    }

    private static void EnsureFirewallRule()
    {
        try
        {
            string cmd = $"advfirewall firewall add rule name=\"{AppName}\" dir=in action=allow protocol=TCP localport={Protocol.TcpDataPort},{Protocol.HttpManifestPort}";
            using var p = Process.Start(new ProcessStartInfo("netsh", cmd)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    private sealed class InstanceMessageFilter(uint targetMessage, Action onMessageReceived) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg == targetMessage)
            {
                onMessageReceived();
                return true;
            }
            return false;
        }
    }
}

// ---------------------------------------------------------------------------
// Standard Configuration Window (Normal Windows Behavior & Native Pickers)
// ---------------------------------------------------------------------------

public sealed class ConfigWindow : Form
{
    private readonly AppConfig _currentConfig;
    private readonly Func<AppConfig, Task> _onSaveCallback;
    private readonly TextBox _ipBox;
    private readonly ListView _listView;
    private readonly List<FolderConfig> _foldersList;

    public ConfigWindow(AppConfig config, Func<AppConfig, Task> onSaveCallback)
    {
        _currentConfig = config;
        _onSaveCallback = onSaveCallback;
        _foldersList = config.WindowsFolders.Select(f => new FolderConfig
        {
            Path = f.Path,
            Extensions = [.. f.Extensions],
            ScrubLevel = f.ScrubLevel
        }).ToList();

        Text = "Auto Wi-Fi Mirror Folders Configuration";
        Size = new Size(820, 520);
        MinimumSize = new Size(720, 460);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

        var ipPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 10, 12, 6) };
        var ipLabel = new Label { Text = "Target IP(s) (comma-separated, or blank for auto-discovery):", AutoSize = true, Dock = DockStyle.Left };
        _ipBox = new TextBox { Text = _currentConfig.ManualIp, Width = 280, Dock = DockStyle.Right };
        ipPanel.Controls.Add(ipLabel);
        ipPanel.Controls.Add(_ipBox);

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            GridLines = true
        };
        _listView.Columns.Add("Source Folder Path", 400);
        _listView.Columns.Add("Matching Extensions", 180);
        _listView.Columns.Add("Scrub Level", 160);

        var actionPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(12, 6, 12, 6) };
        var addBtn = new Button { Text = "+ Add Folder...", Width = 130, Dock = DockStyle.Left };
        var editBtn = new Button { Text = "Edit Selected", Width = 110, Dock = DockStyle.Left };
        var removeBtn = new Button { Text = "Remove", Width = 90, Dock = DockStyle.Left };

        addBtn.Click += (s, e) => OpenFolderEditor(null);
        editBtn.Click += (s, e) =>
        {
            if (_listView.SelectedIndices.Count > 0)
                OpenFolderEditor(_listView.SelectedIndices[0]);
        };
        removeBtn.Click += (s, e) =>
        {
            if (_listView.SelectedIndices.Count > 0)
            {
                _foldersList.RemoveAt(_listView.SelectedIndices[0]);
                RefreshList();
            }
        };

        actionPanel.Controls.Add(removeBtn);
        actionPanel.Controls.Add(editBtn);
        actionPanel.Controls.Add(addBtn);

        var footerPanel = new Panel { Dock = DockStyle.Bottom, Height = 55, Padding = new Padding(12, 10, 12, 10) };
        var saveBtn = new Button { Text = "Save & Broadcast", Width = 160, Dock = DockStyle.Right, BackColor = Color.FromArgb(22, 163, 74), ForeColor = Color.White };
        var cancelBtn = new Button { Text = "Cancel", Width = 90, Dock = DockStyle.Right };

        saveBtn.Click += async (s, e) =>
        {
            if (_foldersList.Count == 0)
            {
                MessageBox.Show(this, "Please configure at least one folder.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            saveBtn.Enabled = false;
            var updatedConfig = new AppConfig
            {
                ManualIp = _ipBox.Text.Trim(),
                WindowsFolders = _foldersList
            };

            await _onSaveCallback(updatedConfig);
            Close();
        };

        cancelBtn.Click += (s, e) => Close();

        footerPanel.Controls.Add(cancelBtn);
        footerPanel.Controls.Add(saveBtn);

        Controls.Add(_listView);
        Controls.Add(actionPanel);
        Controls.Add(ipPanel);
        Controls.Add(footerPanel);

        RefreshList();
    }

    private void RefreshList()
    {
        _listView.Items.Clear();
        foreach (var item in _foldersList)
        {
            var lvi = new ListViewItem(item.Path);
            lvi.SubItems.Add(string.Join(", ", item.Extensions));
            lvi.SubItems.Add(FormatScrubLabel(item.ScrubLevel));
            _listView.Items.Add(lvi);
        }
    }

    private static string FormatScrubLabel(int lvl) => lvl switch
    {
        0 => "0 - Disabled (Full Tree)",
        1 => "1 - Max 1 Level Deep",
        _ => $"{lvl} - Max {lvl} Levels Deep"
    };

    private void OpenFolderEditor(int? editIndex)
    {
        var target = editIndex.HasValue
            ? _foldersList[editIndex.Value]
            : new FolderConfig { Path = string.Empty, Extensions = ["*"], ScrubLevel = 0 };

        using var dlg = new Form
        {
            Text = editIndex.HasValue ? "Edit Broadcast Folder" : "Add Broadcast Folder",
            Size = new Size(580, 300),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = false,
            Font = new Font("Segoe UI", 9.5f)
        };

        var pathLbl = new Label { Text = "Windows Source Directory:", Top = 14, Left = 16, AutoSize = true };
        var pathBox = new TextBox { Text = target.Path, Top = 36, Left = 16, Width = 430 };
        var browseBtn = new Button { Text = "Browse...", Top = 35, Left = 454, Width = 90 };

        browseBtn.Click += (s, e) =>
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "Select a Windows folder to sync",
                UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(pathBox.Text) ? pathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };

            if (fbd.ShowDialog(dlg) == DialogResult.OK)
            {
                pathBox.Text = fbd.SelectedPath;
            }
        };

        var extLbl = new Label { Text = "File Filter Extensions (comma-separated, e.g. .md, .png or *):", Top = 76, Left = 16, AutoSize = true };
        var extBox = new TextBox { Text = string.Join(", ", target.Extensions), Top = 98, Left = 16, Width = 528 };

        var scrubLbl = new Label { Text = "Folder Scrubbing Level (flatten directory tree):", Top = 138, Left = 16, AutoSize = true };
        var scrubCb = new ComboBox { Top = 160, Left = 16, Width = 528, DropDownStyle = ComboBoxStyle.DropDownList };
        for (int i = 0; i <= 5; i++) scrubCb.Items.Add(FormatScrubLabel(i));
        scrubCb.SelectedIndex = Math.Clamp(target.ScrubLevel, 0, 5);

        var okBtn = new Button { Text = "Apply", DialogResult = DialogResult.OK, Top = 210, Left = 444, Width = 100, BackColor = Color.FromArgb(2, 132, 199), ForeColor = Color.White };
        var cancelModalBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Top = 210, Left = 334, Width = 100 };

        dlg.Controls.AddRange([pathLbl, pathBox, browseBtn, extLbl, extBox, scrubLbl, scrubCb, okBtn, cancelModalBtn]);
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelModalBtn;

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            string chosenPath = pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(chosenPath))
            {
                MessageBox.Show(this, "The source directory path cannot be empty.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var parts = extBox.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var normalizedExts = parts.Length == 0 ? new List<string> { "*" } : parts.Select(p => p.StartsWith('.') || p == "*" ? p : $".{p}").ToList();

            var resultConfig = new FolderConfig
            {
                Path = chosenPath,
                Extensions = normalizedExts,
                ScrubLevel = scrubCb.SelectedIndex
            };

            if (editIndex.HasValue)
                _foldersList[editIndex.Value] = resultConfig;
            else
                _foldersList.Add(resultConfig);

            RefreshList();
        }
    }
}