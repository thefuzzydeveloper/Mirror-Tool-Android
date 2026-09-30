// dotnet/Program.cs
namespace WiFiAutoStreamSync;

// ============================================================================
// 1. APPLICATION ENTRY POINT & TRAY HOST
// ============================================================================
internal static class Program{
    private const string AppName = "WiFiAutoStreamSync";
    private const string MutexName = $@"Local\{AppName}_SingleInstance_Mutex";
    private const string WindowMessageName = "WIFI_AUTO_STREAM_SYNC_ACTIVATE";
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static readonly uint WmActivateApp = RegisterWindowMessage(WindowMessageName);
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private static readonly string LogFilePath = Path.Combine(AppContext.BaseDirectory, "startup_debug.log");

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
    private static SingleInstanceReceiver? _instanceReceiver;

    private static Icon? _idleIcon;
    private static Icon? _syncingIcon;

    public static void Log(string message){
        try{
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            Debug.WriteLine(logLine);
            File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
        }
        catch { }
    }

    [STAThread]
    private static void Main(){
        try{
            Log("=== Application Main Started ===");
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
            Log($"Checking single instance mutex: {MutexName}");
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew){
                Log("Another instance is already running. Broadcasting activation message and exiting.");
                PostMessage(HwndBroadcast, WmActivateApp, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>{
                Log($"UNHANDLED EXCEPTION (non-fatal): {e.ExceptionObject}");
            };
            Application.ThreadException += (s, e) =>{
                Log($"THREAD EXCEPTION (non-fatal): {e.Exception.Message}");
            };
            Log("Ensuring firewall rule...");
            EnsureFirewallRule();
            Log("Creating dynamic tray icons...");
            _idleIcon = TrayIconHelper.CreateDynamicIcon(syncing: false);
            _syncingIcon = TrayIconHelper.CreateDynamicIcon(syncing: true);
            Log("Loading configuration...");
            _config = ConfigManager.Load();
            Log("Initializing Tray icon & context menu...");
            InitializeTray();
            _instanceReceiver = new SingleInstanceReceiver(WmActivateApp, ShowOrFocusBrowserWindow);
            _ = _instanceReceiver.Handle;
            Log("Starting SyncEngine...");
            StartEngine();
            Log("Entering Application.Run() (minimized in tray)...");
            Application.Run();
            Log("Application.Run() exited. Releasing resources...");
            _instanceReceiver.Dispose();
            _mutex.ReleaseMutex();
            _idleIcon?.Dispose();
            _syncingIcon?.Dispose();
            Log("Application shutdown complete.");
        }
        catch (Exception ex){
            Log($"FATAL EXCEPTION IN MAIN: {ex}");
            MessageBox.Show($"Fatal startup error:\n{ex.Message}\n\nExhaustive debug log written to:\n{LogFilePath}", "WiFiAutoStreamSync Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void InitializeTray(){
        var menu = new ContextMenuStrip();
        var headerItem = new ToolStripMenuItem("Mirror Sync Engine") { Enabled = false };
        headerItem.Font = new Font("Segoe UI", 9f, FontStyle.Italic);
        menu.Items.Add(headerItem);
        menu.Items.Add(new ToolStripSeparator());
        var browseItem = new ToolStripMenuItem("📂 Open Wireless Device Explorer...") { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
        browseItem.Click += (s, e) => ShowOrFocusBrowserWindow();
        menu.Items.Add(browseItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("⚡ Sync All Folders Now", null, (s, e) => _engine?.TriggerSync(null));
        menu.Items.Add("📋 View Live Manifest Dump...", null, (s, e) => ShowManifestInspector());
        menu.Items.Add(new ToolStripSeparator());
        var discoveryItem = new ToolStripMenuItem("📡 Pairing Discovery (Auto-off in 5 min)") { Checked = _config.NetworkDiscoveryEnabled, CheckOnClick = true };
        discoveryItem.Click += (s, e) =>{
            if (_engine != null) { _engine.SetDiscoveryMode(discoveryItem.Checked, autoDisableMinutes: discoveryItem.Checked ? 5 : 0); }
        };
        menu.Items.Add(discoveryItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("⚙️ Manage Synced Folders...", null, (s, e) => ShowOrFocusConfigWindow());
        var startupItem = new ToolStripMenuItem("Run on Windows Startup") { Checked = IsStartupEnabled() };
        startupItem.Click += (s, e) =>{
            ToggleStartup(!startupItem.Checked);
            startupItem.Checked = IsStartupEnabled();
        };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Exit Mirror Sync", null, async (s, e) =>{
            if (_trayIcon != null) _trayIcon.Visible = false;
            if (_engine != null) await _engine.DisposeAsync();
            Application.Exit();
        });
        _trayIcon = new NotifyIcon { Icon = _idleIcon ?? SystemIcons.Application, ContextMenuStrip = menu, Text = "Wi-Fi Sync | Initializing...", Visible = true };
        _trayIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) { ToggleBrowserWindow(); }
        };
        _trayIcon.ShowBalloonTip(2000, "WiFiAutoStreamSync", "App is running minimized in system tray.", ToolTipIcon.Info);
    }

    private static void StartEngine(){
        try{
            Log("StartEngine: Instantiating SyncEngine...");
            _engine = new SyncEngine(_config, (status, syncing) =>{
                if (_trayIcon != null){
                    string basePrefix = "Sync | ";
                    int maxStatusLen = Math.Max(0, 63 - basePrefix.Length);
                    string truncatedStatus = status.Length > maxStatusLen ? status[..(maxStatusLen - 3)] + "..." : status;
                    _trayIcon.Text = $"{basePrefix}{truncatedStatus}";
                    _trayIcon.Icon = syncing ? _syncingIcon : _idleIcon;
                }
            });
            _engine.OnDiscoveryStateChanged += enabled =>{
                if (_trayIcon?.ContextMenuStrip?.InvokeRequired == true){
                    _trayIcon.ContextMenuStrip.Invoke((Action)(() =>{
                        foreach (ToolStripItem item in _trayIcon.ContextMenuStrip.Items){
                            if (item is ToolStripMenuItem mi && mi.Text.StartsWith("📡"))
                                mi.Checked = enabled;
                        }
                    }));
                }
            };
            _engine.Start();
            Log("StartEngine: SyncEngine started successfully.");
        }
        catch (Exception ex){
            Log($"StartEngine EXCEPTION: {ex}");
            if (_trayIcon != null) { _trayIcon.Text = "Wi-Fi Sync | Engine Warning"; }
        }
    }

    public static void ToggleBrowserWindow()
    {
        if (_engine == null) return;
        if (_activeBrowserWindow != null && !_activeBrowserWindow.IsDisposed)
        {
            _activeBrowserWindow.Close();
            _activeBrowserWindow = null;
            return;
        }
        ShowOrFocusBrowserWindow();
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

    public static void ShowManifestInspector(){
        if (_engine == null) return;
        var payload = _engine.GetManifestsPayload();
        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions{
            WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        using var viewer = new Form { Text = "Local Sync Manifest Inspector", Size = new Size(720, 520), MinimumSize = new Size(480, 320), StartPosition = FormStartPosition.CenterScreen, Font = new Font("Segoe UI", 9.5f) };
        var textBox = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Text = json, Font = new Font("Consolas", 10f), WordWrap = false };
        viewer.Controls.Add(textBox);
        viewer.ShowDialog();
    }

    public static void ShowOrFocusConfigWindow(){
        if (_activeConfigWindow != null && !_activeConfigWindow.IsDisposed){
            if (_activeConfigWindow.WindowState == FormWindowState.Minimized)
                _activeConfigWindow.WindowState = FormWindowState.Normal;
            _activeConfigWindow.Activate();
            _activeConfigWindow.BringToFront();
            return;
        }
        _activeConfigWindow = new ConfigWindow(_config, async newConfig =>{
            _config = newConfig;
            ConfigManager.Save(_config);
            if (_engine != null) { await _engine.DisposeAsync(); }
            StartEngine();
        });
        _activeConfigWindow.Show();
        _activeConfigWindow.BringToFront();
    }

    private static bool IsStartupEnabled(){
        try{
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, false);
            return key?.GetValue(AppName) != null;
        }
        catch { return false; }
    }

    private static void ToggleStartup(bool enable){
        try{
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, true);
            if (key == null) return;
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;
            if (enable)
                key.SetValue(AppName, $"\"{exePath}\"");
            else
                key.DeleteValue(AppName, false);
        }
        catch (Exception ex){
            Log($"ToggleStartup EXCEPTION: {ex.Message}");
        }
    }

    private static void EnsureFirewallRule(){
        try{
            string cmd = $"advfirewall firewall add rule name=\"{AppName}\" dir=in action=allow protocol=TCP localport={Protocol.TcpDataPort},{Protocol.HttpManifestPort}";
            using var p = Process.Start(new ProcessStartInfo("netsh", cmd){
                CreateNoWindow = true, UseShellExecute = false
            });
            Log("EnsureFirewallRule executed.");
        }
        catch (Exception ex){
            Log($"EnsureFirewallRule EXCEPTION (non-fatal): {ex.Message}");
        }
    }

    private sealed class SingleInstanceReceiver : Form{
        private readonly uint _activateMsg;
        private readonly Action _onActivate;

        public SingleInstanceReceiver(uint activateMsg, Action onActivate){
            _activateMsg = activateMsg;
            _onActivate = onActivate;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            Size = new Size(0, 0);
            Opacity = 0;
        }

        protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

        protected override void WndProc(ref Message m){
            if (m.Msg == _activateMsg){
                Log("SingleInstanceReceiver: Received activation signal. Activating window.");
                if (InvokeRequired) { Invoke(_onActivate); }
                else { _onActivate(); }
            }
            base.WndProc(ref m);
        }
    }
}

// ============================================================================
// 2. PROTOCOL SPECIFICATION & PACKET CONSTANTS
// ============================================================================
public static class Protocol{
    public static readonly byte[] MagicHeader = [0xAA, 0x55];
    public const int TcpDataPort = 58421;
    public const int HttpManifestPort = 58422;
    public const int UdpBeaconPort = 58423;
    public const int ChunkStreamSize = 4 * 1024 * 1024; // 4MB chunks

    public const byte CmdPing = 0x00;
    public const byte CmdConfig = 0x01;
    public const byte CmdManifestExchange = 0x02;
    public const byte CmdFileStream = 0x03;
    public const byte CmdDelete = 0x04;
    public const byte CmdSyncEnd = 0x05;
    public const byte CmdWakeSync = 0x0C;

    public const byte CmdGetDeviceInfo = 0x06;
    public const byte CmdListDir = 0x07;
    public const byte CmdPullFile = 0x08;
    public const byte CmdPushFileDirect = 0x09;
    public const byte CmdDeletePathDirect = 0x0A;
    public const byte CmdMkdirDirect = 0x0B;
    public const byte CmdSetDeletionToken = 0x0D;

    public static async ValueTask SendExactAsync(Stream stream, ReadOnlyMemory<byte> buffer, CancellationToken ct = default){
        await stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async ValueTask ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct = default){
        int totalRead = 0;
        while (totalRead < buffer.Length){
            int read = await stream.ReadAsync(buffer[totalRead..], ct).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("Socket disconnected unexpectedly during stream read.");
            totalRead += read;
        }
    }

    public static async Task<bool> ReadAckAsync(Stream stream, CancellationToken ct = default){
        byte[] ack = new byte[1];
        await ReadExactAsync(stream, ack, ct).ConfigureAwait(false);
        return ack[0] == 0x00;
    }
}

// ============================================================================
// 3. CONFIGURATION & WIRE DATA MODELS
// ============================================================================
public sealed class FolderConfig{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = ["*"];

    [JsonPropertyName("ignored_extensions")]
    public List<string> IgnoredExtensions { get; set; } = [];

    [JsonPropertyName("scrub_level")]
    public int ScrubLevel { get; set; } = 0;
}

public sealed class FolderWirePayload{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("local_path")]
    public string LocalPath { get; set; } = string.Empty;

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = [];

    [JsonPropertyName("ignored_extensions")]
    public List<string> IgnoredExtensions { get; set; } = [];

    [JsonPropertyName("scrub_level")]
    public int ScrubLevel { get; set; }
}

public sealed class AppConfig{
    [JsonPropertyName("manual_ip")]
    public string ManualIp { get; set; } = string.Empty;

    [JsonPropertyName("network_discovery_enabled")]
    public bool NetworkDiscoveryEnabled { get; set; } = false;

    [JsonPropertyName("known_device_ips")]
    public List<string> KnownDeviceIps { get; set; } = [];

    [JsonPropertyName("windows_folders")]
    public List<FolderConfig> WindowsFolders { get; set; } = [];
}

public sealed class ManifestExchangeResponse{
    [JsonPropertyName("local_count")]
    public int LocalCount { get; set; }

    [JsonPropertyName("remote_count")]
    public int RemoteCount { get; set; }

    [JsonPropertyName("deleted_count")]
    public int DeletedCount { get; set; }

    [JsonPropertyName("needed")]
    public List<string> Needed { get; set; } = [];
}

public sealed class AndroidRootDir{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
}

public sealed class AndroidDeviceInfo{
    [JsonPropertyName("model")] public string Model { get; set; } = "Android Device";
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("sdk")] public int Sdk { get; set; }
    [JsonPropertyName("root_dirs")] public List<AndroidRootDir> RootDirs { get; set; } = [];
}

public sealed class AndroidFileItem{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("last_modified")] public long LastModified { get; set; }
}

public sealed class AndroidDirListing{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("items")] public List<AndroidFileItem> Items { get; set; } = [];
}

public static class ConfigManager{
    private static readonly string ConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wifiautostreamsync_config.json");

    public static AppConfig Load(){
        try{
            Program.Log($"ConfigManager.Load: Checking path {ConfigPath}");
            if (!File.Exists(ConfigPath)){
                Program.Log("ConfigManager.Load: Config file not found. Creating fallback default config.");
                var fallback = new AppConfig{
                    WindowsFolders = [new FolderConfig { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncWorkspace"), Extensions = ["*"], ScrubLevel = 0 }]
                };
                Save(fallback);
                return fallback;
            }
            string json = File.ReadAllText(ConfigPath);
            Program.Log("ConfigManager.Load: Successfully read config file. Deserializing...");
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch (Exception ex){
            Program.Log($"ConfigManager.Load EXCEPTION: {ex.Message}. Returning default AppConfig.");
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config){
        try{
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
            Program.Log("ConfigManager.Save: Successfully saved config.");
        }
        catch (Exception ex){
            Program.Log($"ConfigManager.Save EXCEPTION: {ex.Message}");
        }
    }

    public static string ComputeFolderId(string folderPath){
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    public static string ComputeTargetRelPath(string relativePath, int scrubLevel){
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (scrubLevel <= 0 || parts.Length <= scrubLevel + 1)
            return string.Join('/', parts);
        var topDirs = parts.Take(scrubLevel);
        string flattened = string.Join('_', parts.Skip(scrubLevel));
        return string.Join('/', topDirs.Concat([flattened]));
    }

    public static bool IsIntermediateOrLockFile(string filePath){
        try{
            string fileName = Path.GetFileName(filePath);
            if (string.IsNullOrEmpty(fileName)) return true;

            // Microsoft Office owner lock files (e.g. ~$document.docx, ~$newdocument.docx)
            if (fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase))
                return true;

            // LibreOffice / OpenOffice lock files (e.g. .~lock.document.docx#)
            if (fileName.StartsWith(".~lock.", StringComparison.OrdinalIgnoreCase))
                return true;

            // Text editor and auto-save lock files (.#document.docx, document.docx~)
            if (fileName.StartsWith(".#") || fileName.EndsWith("~"))
                return true;

            // Office temporary and scratch working files (~WRL*.tmp, ~WRD*.tmp, ~*.tmp)
            if (fileName.StartsWith("~") && (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".temp", StringComparison.OrdinalIgnoreCase)))
                return true;

            // Common intermediate, swap, and incomplete transfer files
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is ".tmp" or ".temp" or ".swp" or ".swo" or ".crdownload" or ".part" or ".partial" or ".upload_tmp")
                return true;

            // OS-generated metadata files
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase))
                return true;

            if (File.Exists(filePath)){
                var attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.Temporary) != 0)
                    return true;
                if (fileName.StartsWith("~") && (attrs & FileAttributes.Hidden) != 0)
                    return true;
            }
        }
        catch { }
        return false;
    }

    public static bool IsFileLocked(string filePath){
        try{
            if (!File.Exists(filePath)) return true;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException){
            return true;
        }
        catch (UnauthorizedAccessException){
            return true;
        }
        catch{
            return false;
        }
    }

    public static bool IsSyncableFile(string filePath, List<string> allowedExtensions, List<string>? ignoredExtensions = null, bool checkLock = true){
        if (IsIntermediateOrLockFile(filePath)) return false;
        if (!IsExtensionAllowed(filePath, allowedExtensions, ignoredExtensions)) return false;
        if (checkLock && IsFileLocked(filePath)) return false;
        return true;
    }

    public static bool IsExtensionAllowed(string filePath, List<string> allowedExtensions, List<string>? ignoredExtensions = null){
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ignoredExtensions != null && ignoredExtensions.Count > 0){
            bool isIgnored = ignoredExtensions.Any(e =>{
                string clean = e.Trim().ToLowerInvariant();
                return clean == ext || (clean.Length > 0 && $".{clean}" == ext) || clean == "*";
            });
            if (isIgnored) return false;
        }
        if (allowedExtensions.Count == 0 || allowedExtensions.Contains("*") || allowedExtensions.Contains(".*"))
            return true;
        return allowedExtensions.Any(e =>{
            string clean = e.Trim().ToLowerInvariant();
            return clean == ext || (clean.Length > 0 && $".{clean}" == ext);
        });
    }
}

// ============================================================================
// 4. DEVICE CLIENT & NETWORK TRANSFERS
// ============================================================================
public sealed class FileTransferProgress{
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public double Percentage => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes * 100.0, 0.0, 100.0) : 0.0;
}

public sealed class DeviceClient : IAsyncDisposable{
    private readonly SemaphoreSlim _networkLock = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public string RemoteIp { get; }
    public bool IsConnected => _tcpClient?.Connected ?? false;
    public AndroidDeviceInfo? DeviceInfo { get; set; }
    public string DeletionAuthToken { get; set; } = string.Empty;

    public DeviceClient(string remoteIp) { RemoteIp = remoteIp; }

    public async Task<bool> ConnectAsync(int timeoutMs = 4000, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            DisconnectInternal();
            _tcpClient = new TcpClient { NoDelay = true, SendTimeout = 15000, ReceiveTimeout = 15000 };
            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await _tcpClient.ConnectAsync(RemoteIp, Protocol.TcpDataPort, linkedCts.Token);
            _stream = _tcpClient.GetStream();
            return true;
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.ConnectAsync to {RemoteIp} failed: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default){
        if (IsConnected && _stream != null) return true;
        return await ConnectAsync(3000, ct);
    }

    public async Task<AndroidDeviceInfo?> GetDeviceInfoAsync(CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return null;
            byte[] header = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdGetDeviceInfo];
            await Protocol.SendExactAsync(_stream, header, ct);
            byte[] ack = new byte[1];
            await Protocol.ReadExactAsync(_stream, ack, ct);
            if (ack[0] != 0x00) return null;
            byte[] lenBytes = new byte[4];
            await Protocol.ReadExactAsync(_stream, lenBytes, ct);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
            byte[] payload = ArrayPool<byte>.Shared.Rent((int)len);
            try{
                var slice = payload.AsMemory(0, (int)len);
                await Protocol.ReadExactAsync(_stream, slice, ct);
                var info = JsonSerializer.Deserialize<AndroidDeviceInfo>(slice.Span);
                DeviceInfo = info;
                return info;
            }
            finally { ArrayPool<byte>.Shared.Return(payload); }
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.GetDeviceInfoAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return null;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<AndroidDirListing?> ListDirectoryAsync(string androidPath, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return null;
            byte[] pathBytes = Encoding.UTF8.GetBytes(androidPath);
            byte[] packet = new byte[3 + 2 + pathBytes.Length];
            Protocol.MagicHeader.CopyTo(packet, 0);
            packet[2] = Protocol.CmdListDir;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3, 2), (ushort)pathBytes.Length);
            pathBytes.CopyTo(packet, 5);
            await Protocol.SendExactAsync(_stream, packet, ct);
            byte[] ack = new byte[1];
            await Protocol.ReadExactAsync(_stream, ack, ct);
            if (ack[0] != 0x00) return null;
            byte[] lenBytes = new byte[4];
            await Protocol.ReadExactAsync(_stream, lenBytes, ct);
            uint len = BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
            byte[] payload = ArrayPool<byte>.Shared.Rent((int)len);
            try{
                var slice = payload.AsMemory(0, (int)len);
                await Protocol.ReadExactAsync(_stream, slice, ct);
                return JsonSerializer.Deserialize<AndroidDirListing>(slice.Span);
            }
            finally { ArrayPool<byte>.Shared.Return(payload); }
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.ListDirectoryAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return null;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> PullFileAsync(string androidFilePath, string localDestinationPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            byte[] pathBytes = Encoding.UTF8.GetBytes(androidFilePath);
            byte[] packet = new byte[3 + 2 + pathBytes.Length];
            Protocol.MagicHeader.CopyTo(packet, 0);
            packet[2] = Protocol.CmdPullFile;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3, 2), (ushort)pathBytes.Length);
            pathBytes.CopyTo(packet, 5);
            await Protocol.SendExactAsync(_stream, packet, ct);
            byte[] ack = new byte[1];
            await Protocol.ReadExactAsync(_stream, ack, ct);
            if (ack[0] != 0x00) return false;
            byte[] sizeBytes = new byte[8];
            await Protocol.ReadExactAsync(_stream, sizeBytes, ct);
            long fileSize = BinaryPrimitives.ReadInt64BigEndian(sizeBytes);
            string? dir = Path.GetDirectoryName(localDestinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(Protocol.ChunkStreamSize);
            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;
            long lastReportBytes = 0;
            double currentSpeed = 0;
            try{
                await using (var fs = new FileStream(localDestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 128 * 1024, useAsync: true)){
                    long bytesRemaining = fileSize;
                    long totalDownloaded = 0;

                    progress?.Report(new FileTransferProgress{
                        BytesTransferred = 0, TotalBytes = fileSize, BytesPerSecond = 0, EstimatedTimeRemaining = null
                    });
                    while (bytesRemaining > 0){
                        int toRead = (int)Math.Min(bytesRemaining, Protocol.ChunkStreamSize);
                        int read = await _stream.ReadAsync(chunkBuffer.AsMemory(0, toRead), ct);
                        if (read == 0) throw new EndOfStreamException("Connection disconnected while streaming file.");
                        await fs.WriteAsync(chunkBuffer.AsMemory(0, read), ct);
                        bytesRemaining -= read;
                        totalDownloaded += read;
                        long elapsedMs = sw.ElapsedMilliseconds;
                        bool isFinished = bytesRemaining == 0;
                        if (progress != null && (isFinished || elapsedMs - lastReportMs >= 80)){
                            long deltaMs = elapsedMs - lastReportMs;
                            long deltaBytes = totalDownloaded - lastReportBytes;
                            if (deltaMs > 0){
                                double instantSpeed = (deltaBytes * 1000.0) / deltaMs;
                                currentSpeed = currentSpeed <= 0 ? instantSpeed : (currentSpeed * 0.7) + (instantSpeed * 0.3);
                            }
                            else if (elapsedMs > 0){currentSpeed = (totalDownloaded * 1000.0) / elapsedMs;}
                            TimeSpan? eta = null;
                            if (currentSpeed > 0 && fileSize > totalDownloaded){
                                double remainingSec = (fileSize - totalDownloaded) / currentSpeed;
                                if (remainingSec >= 0 && remainingSec < 86400)
                                    eta = TimeSpan.FromSeconds(remainingSec);
                            }

                            progress.Report(new FileTransferProgress{
                                BytesTransferred = totalDownloaded, TotalBytes = fileSize, BytesPerSecond = currentSpeed, EstimatedTimeRemaining = eta
                            });
                            lastReportMs = elapsedMs;
                            lastReportBytes = totalDownloaded;
                        }
                    }
                    await fs.FlushAsync(ct);
                }
                return true;
            }
            finally { ArrayPool<byte>.Shared.Return(chunkBuffer); }
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.PullFileAsync EXCEPTION: {ex.Message}");
            try{
                if (File.Exists(localDestinationPath))
                    File.Delete(localDestinationPath);
            }
            catch { }
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<int> PullFolderRecursiveAsync(string androidFolderPath, string localDestinationDir, Action<string>? statusCallback = null, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default){
        int filesDownloaded = 0;
        if (!Directory.Exists(localDestinationDir))
            Directory.CreateDirectory(localDestinationDir);
        var listing = await ListDirectoryAsync(androidFolderPath, ct);
        if (listing == null || !listing.Exists) return 0;
        foreach (var item in listing.Items){
            if (ct.IsCancellationRequested) break;
            if (item.IsDir){
                string nextLocal = Path.Combine(localDestinationDir, item.Name);
                filesDownloaded += await PullFolderRecursiveAsync(item.Path, nextLocal, statusCallback, progress, ct);
            }
            else{
                string targetLocalFile = Path.Combine(localDestinationDir, item.Name);
                statusCallback?.Invoke($"Downloading {item.Name} ({FormatBytes(item.Size)})...");
                bool ok = await PullFileAsync(item.Path, targetLocalFile, progress, ct);
                if (ok) filesDownloaded++;
            }
        }
        return filesDownloaded;
    }

    public async Task<bool> PushFileDirectAsync(string localFilePath, string androidDestinationPath, IProgress<FileTransferProgress>? progress = null, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            using var fs = await OpenReadWithRetryAsync(localFilePath, ct: ct);
            if (fs == null) return false;
            byte[] pathBytes = Encoding.UTF8.GetBytes(androidDestinationPath);
            long fileSize = fs.Length;
            byte[] header = new byte[3 + 2 + pathBytes.Length + 8];
            Protocol.MagicHeader.CopyTo(header, 0);
            header[2] = Protocol.CmdPushFileDirect;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3, 2), (ushort)pathBytes.Length);
            pathBytes.CopyTo(header, 5);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(5 + pathBytes.Length, 8), fileSize);
            await Protocol.SendExactAsync(_stream, header, ct);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(Protocol.ChunkStreamSize);
            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;
            long lastReportBytes = 0;
            double currentSpeed = 0;
            try{
                long remaining = fileSize;
                long totalUploaded = 0;

                progress?.Report(new FileTransferProgress{
                    BytesTransferred = 0, TotalBytes = fileSize, BytesPerSecond = 0, EstimatedTimeRemaining = null
                });
                while (remaining > 0){
                    int toRead = (int)Math.Min(remaining, Protocol.ChunkStreamSize);
                    int read = await fs.ReadAsync(chunk.AsMemory(0, toRead), ct);
                    if (read == 0) break;
                    await Protocol.SendExactAsync(_stream, chunk.AsMemory(0, read), ct);
                    remaining -= read;
                    totalUploaded += read;
                    long elapsedMs = sw.ElapsedMilliseconds;
                    bool isFinished = remaining == 0;
                    if (progress != null && (isFinished || elapsedMs - lastReportMs >= 80)){
                        long deltaMs = elapsedMs - lastReportMs;
                        long deltaBytes = totalUploaded - lastReportBytes;
                        if (deltaMs > 0){
                            double instantSpeed = (deltaBytes * 1000.0) / deltaMs;
                            currentSpeed = currentSpeed <= 0 ? instantSpeed : (currentSpeed * 0.7) + (instantSpeed * 0.3);
                        }
                        else if (elapsedMs > 0){currentSpeed = (totalUploaded * 1000.0) / elapsedMs;}
                        TimeSpan? eta = null;
                        if (currentSpeed > 0 && fileSize > totalUploaded){
                            double remainingSec = (fileSize - totalUploaded) / currentSpeed;
                            if (remainingSec >= 0 && remainingSec < 86400)
                                eta = TimeSpan.FromSeconds(remainingSec);
                        }

                        progress.Report(new FileTransferProgress{
                            BytesTransferred = totalUploaded, TotalBytes = fileSize, BytesPerSecond = currentSpeed, EstimatedTimeRemaining = eta
                        });
                        lastReportMs = elapsedMs;
                        lastReportBytes = totalUploaded;
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.PushFileDirectAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<int> PushFolderRecursiveAsync(string localFolder, string androidDestinationFolder, Action<string>? statusCallback = null, CancellationToken ct = default){
        int filesUploaded = 0;
        await CreateDirectoryDirectAsync(androidDestinationFolder, ct);
        foreach (var file in Directory.EnumerateFiles(localFolder)){
            if (ct.IsCancellationRequested) break;
            if (ConfigManager.IsIntermediateOrLockFile(file)) continue;
            string fileName = Path.GetFileName(file);
            string destPath = $"{androidDestinationFolder.TrimEnd('/')}/{fileName}";
            statusCallback?.Invoke($"Uploading: {fileName}...");
            if (await PushFileDirectAsync(file, destPath, null, ct))
                filesUploaded++;
        }
        foreach (var dir in Directory.EnumerateDirectories(localFolder)){
            if (ct.IsCancellationRequested) break;
            string dirName = Path.GetFileName(dir);
            string nextAndroidDir = $"{androidDestinationFolder.TrimEnd('/')}/{dirName}";
            filesUploaded += await PushFolderRecursiveAsync(dir, nextAndroidDir, statusCallback, ct);
        }
        return filesUploaded;
    }

    public async Task<bool> DeletePathDirectAsync(string androidPath, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            byte[] pathBytes = Encoding.UTF8.GetBytes(androidPath);
            byte[] tokenBytes = Encoding.UTF8.GetBytes(DeletionAuthToken ?? string.Empty);
            byte[] packet = new byte[3 + 2 + tokenBytes.Length + 2 + pathBytes.Length];
            Protocol.MagicHeader.CopyTo(packet, 0);
            packet[2] = Protocol.CmdDeletePathDirect;
            int offset = 3;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)tokenBytes.Length);
            offset += 2;
            tokenBytes.CopyTo(packet, offset);
            offset += tokenBytes.Length;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(offset, 2), (ushort)pathBytes.Length);
            offset += 2;
            pathBytes.CopyTo(packet, offset);
            await Protocol.SendExactAsync(_stream, packet, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.DeletePathDirectAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> CreateDirectoryDirectAsync(string androidPath, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            byte[] pathBytes = Encoding.UTF8.GetBytes(androidPath);
            byte[] packet = new byte[3 + 2 + pathBytes.Length];
            Protocol.MagicHeader.CopyTo(packet, 0);
            packet[2] = Protocol.CmdMkdirDirect;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3, 2), (ushort)pathBytes.Length);
            pathBytes.CopyTo(packet, 5);
            await Protocol.SendExactAsync(_stream, packet, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.CreateDirectoryDirectAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> SendConfigAsync(List<FolderConfig> folders, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            var payload = folders.Select(f =>{
                var fullPath = Path.GetFullPath(f.Path);
                return new FolderWirePayload{Id = ConfigManager.ComputeFolderId(fullPath), Name = new DirectoryInfo(fullPath).Name, LocalPath = fullPath, Extensions = f.Extensions, IgnoredExtensions = f.IgnoredExtensions, ScrubLevel = f.ScrubLevel};
            }).ToList();
            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, options);
            byte[] header = new byte[7];
            Protocol.MagicHeader.CopyTo(header, 0);
            header[2] = Protocol.CmdConfig;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(3, 4), (uint)jsonBytes.Length);
            await Protocol.SendExactAsync(_stream, header, ct);
            await Protocol.SendExactAsync(_stream, jsonBytes, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.SendConfigAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<ManifestExchangeResponse?> ExchangeManifestAsync(string folderId, Dictionary<string, long> manifest, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return null;
            byte[] fIdBytes = Encoding.UTF8.GetBytes(folderId);
            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { files = manifest }, options);
            byte[] header = new byte[3 + 2 + fIdBytes.Length + 4];
            Protocol.MagicHeader.CopyTo(header, 0);
            header[2] = Protocol.CmdManifestExchange;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(3, 2), (ushort)fIdBytes.Length);
            fIdBytes.CopyTo(header, 5);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5 + fIdBytes.Length, 4), (uint)payload.Length);
            await Protocol.SendExactAsync(_stream, header, ct);
            await Protocol.SendExactAsync(_stream, payload, ct);
            byte[] respLenBytes = new byte[4];
            await Protocol.ReadExactAsync(_stream, respLenBytes, ct);
            uint respLen = BinaryPrimitives.ReadUInt32BigEndian(respLenBytes);
            byte[] respPayload = ArrayPool<byte>.Shared.Rent((int)respLen);
            try{
                var slice = respPayload.AsMemory(0, (int)respLen);
                await Protocol.ReadExactAsync(_stream, slice, ct);
                return JsonSerializer.Deserialize<ManifestExchangeResponse>(slice.Span);
            }
            finally { ArrayPool<byte>.Shared.Return(respPayload); }
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.ExchangeManifestAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return null;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> StreamFileAsync(string folderId, string fullPath, string relTarget, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            using var fs = await OpenReadWithRetryAsync(fullPath, ct: ct);
            if (fs == null) return false;
            byte[] fIdBytes = Encoding.UTF8.GetBytes(folderId);
            byte[] relBytes = Encoding.UTF8.GetBytes(relTarget);
            long fileSize = fs.Length;
            byte[] header = new byte[3 + 2 + fIdBytes.Length + 2 + relBytes.Length + 8];
            Protocol.MagicHeader.CopyTo(header, 0);
            header[2] = Protocol.CmdFileStream;
            int offset = 3;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), (ushort)fIdBytes.Length);
            offset += 2;
            fIdBytes.CopyTo(header, offset);
            offset += fIdBytes.Length;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), (ushort)relBytes.Length);
            offset += 2;
            relBytes.CopyTo(header, offset);
            offset += relBytes.Length;
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(offset, 8), fileSize);
            await Protocol.SendExactAsync(_stream, header, ct);
            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(Protocol.ChunkStreamSize);
            try{
                long bytesRemaining = fileSize;
                while (bytesRemaining > 0){
                    int toRead = (int)Math.Min(bytesRemaining, Protocol.ChunkStreamSize);
                    int read = await fs.ReadAsync(chunkBuffer.AsMemory(0, toRead), ct);
                    if (read == 0) break;
                    await Protocol.SendExactAsync(_stream, chunkBuffer.AsMemory(0, read), ct);
                    bytesRemaining -= read;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(chunkBuffer); }
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.StreamFileAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> SendDeleteAsync(string folderId, string relTarget, CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            byte[] tokenBytes = Encoding.UTF8.GetBytes(DeletionAuthToken ?? string.Empty);
            byte[] fIdBytes = Encoding.UTF8.GetBytes(folderId);
            byte[] relBytes = Encoding.UTF8.GetBytes(relTarget);
            byte[] header = new byte[3 + 2 + tokenBytes.Length + 2 + fIdBytes.Length + 2 + relBytes.Length];
            Protocol.MagicHeader.CopyTo(header, 0);
            header[2] = Protocol.CmdDelete;
            int offset = 3;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), (ushort)tokenBytes.Length);
            offset += 2;
            tokenBytes.CopyTo(header, offset);
            offset += tokenBytes.Length;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), (ushort)fIdBytes.Length);
            offset += 2;
            fIdBytes.CopyTo(header, offset);
            offset += fIdBytes.Length;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(offset, 2), (ushort)relBytes.Length);
            offset += 2;
            relBytes.CopyTo(header, offset);
            await Protocol.SendExactAsync(_stream, header, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.SendDeleteAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> NotifySyncCompleteAsync(CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            byte[] packet = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdSyncEnd];
            await Protocol.SendExactAsync(_stream, packet, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.NotifySyncCompleteAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    public async Task<bool> SendWakeSignalAsync(CancellationToken ct = default){
        await _networkLock.WaitAsync(ct);
        try{
            if (_stream == null) return false;
            byte[] packet = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdWakeSync];
            await Protocol.SendExactAsync(_stream, packet, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex){
            Program.Log($"DeviceClient.SendWakeSignalAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally { _networkLock.Release(); }
    }

    private static async Task<FileStream?> OpenReadWithRetryAsync(string path, int maxRetries = 8, int delayMs = 250, CancellationToken ct = default){
        for (int i = 0; i < maxRetries; i++){
            try{
                if (!File.Exists(path) || ConfigManager.IsIntermediateOrLockFile(path)) return null;
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
                long initialLen = fs.Length;
                await Task.Delay(40, ct);
                long currentLen = new FileInfo(path).Length;
                if (initialLen != currentLen){
                    fs.Dispose();
                    await Task.Delay(delayMs, ct);
                    continue;
                }
                return fs;
            }
            catch (IOException) when (i < maxRetries - 1){await Task.Delay(delayMs, ct);}
            catch (UnauthorizedAccessException) when (i < maxRetries - 1){await Task.Delay(delayMs, ct);}
        }
        return null;
    }

    private static string FormatBytes(long bytes){
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1){
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    private void DisconnectInternal(){
        try { _stream?.Dispose(); }
        catch { }
        _stream = null;
        try { _tcpClient?.Dispose(); }
        catch { }
        _tcpClient = null;
    }

    public async ValueTask DisposeAsync(){
        await _networkLock.WaitAsync();
        try { DisconnectInternal(); }
        finally{
            _networkLock.Release();
            _networkLock.Dispose();
        }
    }
}

// ============================================================================
// 5. LOCAL NETWORK PROBING & DISCOVERY
// ============================================================================
public static class NetworkDiscovery{
    public static List<string> GetActiveIPv4Subnets(){
        var ips = new HashSet<string>();
        try{
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces()){
                if (iface.OperationalStatus != OperationalStatus.Up || iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses){
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork){
                        string ip = addr.Address.ToString();
                        if (!ip.StartsWith("127.") && !ip.StartsWith("169.254"))
                            ips.Add(ip);
                    }
                }
            }
        }
        catch (Exception ex){
            Program.Log($"GetActiveIPv4Subnets EXCEPTION: {ex.Message}");
        }
        return [.. ips];
    }

    public static async Task<List<string>> ScanSubnetDevicesAsync(string manualIpCsv, HashSet<string> excludeIps, CancellationToken ct){
        var candidates = new HashSet<string>();
        if (!string.IsNullOrWhiteSpace(manualIpCsv)){
            foreach (var ip in manualIpCsv.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)){
                if (!excludeIps.Contains(ip))
                    candidates.Add(ip);
            }
        }
        foreach (var localIp in GetActiveIPv4Subnets()){
            var parts = localIp.Split('.');
            if (parts.Length == 4){
                string prefix = $"{parts[0]}.{parts[1]}.{parts[2]}";
                for (int i = 1; i < 255; i++){
                    string ip = $"{prefix}.{i}";
                    if (!excludeIps.Contains(ip))
                        candidates.Add(ip);
                }
            }
        }
        var discovered = new List<string>();
        using var throttle = new SemaphoreSlim(40);
        var tasks = candidates.Select(async ip =>{
            await throttle.WaitAsync(ct);
            try{
                if (await ProbePortAsync(ip, ct)){
                    lock (discovered)
                        discovered.Add(ip);
                }
            }
            finally { throttle.Release(); }
        });
        await Task.WhenAll(tasks);
        return discovered;
    }

    private static async Task<bool> ProbePortAsync(string ip, CancellationToken ct){
        try{
            using var client = new TcpClient();
            using var timeoutCts = new CancellationTokenSource(700);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await client.ConnectAsync(ip, Protocol.TcpDataPort, linked.Token);
            var stream = client.GetStream();
            byte[] ping = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdPing];
            await stream.WriteAsync(ping, linked.Token);
            byte[] resp = new byte[1];
            int read = await stream.ReadAsync(resp, linked.Token);
            return read == 1 && resp[0] == 0x00;
        }
        catch { return false; }
    }
}

// ============================================================================
// 6. HTTP REST MANIFEST SERVER
// ============================================================================
public sealed class HttpManifestServer : IAsyncDisposable{
    private readonly HttpListener _listener;
    private readonly Func<List<FolderWirePayload>> _configProvider;
    private readonly Func<object> _manifestProvider;
    private readonly Action<string?> _onTriggerSync;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public HttpManifestServer(Func<List<FolderWirePayload>> configProvider, Func<object> manifestProvider, Action<string?> onTriggerSync){
        _configProvider = configProvider;
        _manifestProvider = manifestProvider;
        _onTriggerSync = onTriggerSync;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{Protocol.HttpManifestPort}/");
    }

    public void Start(){
        try{
            Program.Log($"HttpManifestServer: Attempting to start HttpListener on http://*:{Protocol.HttpManifestPort}/");
            _listener.Start();
            _runTask = RunAsync(_cts.Token);
            Program.Log("HttpManifestServer: Successfully started on wildcard prefix.");
        }
        catch (HttpListenerException ex){
            Program.Log(string.Format("HttpManifestServer: Wildcard prefix failed ({0}). Falling back to http://localhost:{1}/", ex.Message, Protocol.HttpManifestPort));
            try{
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://localhost:{Protocol.HttpManifestPort}/");
                _listener.Start();
                _runTask = RunAsync(_cts.Token);
                Program.Log("HttpManifestServer: Successfully started on localhost fallback.");
            }
            catch (Exception ex2){
                Program.Log($"HttpManifestServer: Localhost fallback also failed: {ex2.Message}");
                throw;
            }
        }
    }

    private async Task RunAsync(CancellationToken ct){
        while (!ct.IsCancellationRequested && _listener.IsListening){
            try{
                var context = await _listener.GetContextAsync();
                _ = ProcessRequestAsync(context);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex){
                Program.Log(string.Format("HttpManifestServer RunAsync EXCEPTION: {0}", ex.Message));
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext ctx){
        try{
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            if (path == "/config"){
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(_configProvider(), jsonOptions);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else if (path == "/manifests"){
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(_manifestProvider(), jsonOptions);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else if (path.StartsWith("/trigger_sync")){
                string? queryIp = ctx.Request.QueryString["ip"];
                if (string.IsNullOrWhiteSpace(queryIp) && ctx.Request.RemoteEndPoint != null){
                    var addr = ctx.Request.RemoteEndPoint.Address;
                    queryIp = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4().ToString() : addr.ToString();
                }
                _onTriggerSync(queryIp);
                byte[] data = Encoding.UTF8.GetBytes("{\"status\": \"sync_triggered\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else{ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;}
        }
        catch (Exception ex){
            Program.Log($"HttpManifestServer ProcessRequestAsync EXCEPTION: {ex.Message}");
        }
        finally{
            try { ctx.Response.Close(); }
            catch { }
        }
    }

    public async ValueTask DisposeAsync(){
        try{
            _cts.Cancel();
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
            if (_runTask != null) await _runTask;
            _cts.Dispose();
        }
        catch (Exception ex){
            Program.Log($"HttpManifestServer Dispose EXCEPTION: {ex.Message}");
        }
    }
}

// ============================================================================
// 7. SYNC ENGINE
// ============================================================================
public sealed class SyncEngine : IAsyncDisposable{
    private readonly AppConfig _config;
    private readonly Action<string, bool> _statusCallback;
    private readonly ConcurrentDictionary<string, DeviceClient> _clients = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceMap = new();
    private readonly ConcurrentDictionary<string, byte> _activeAudits = new();
    private readonly SemaphoreSlim _syncThrottleLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private readonly List<FileSystemWatcher> _watchers = [];
    private HttpManifestServer? _httpServer;
    private Task? _supervisorLoop;
    private Task? _udpLoop;
    private CancellationTokenSource? _discoveryTimerCts;

    public IReadOnlyDictionary<string, DeviceClient> ConnectedClients => _clients;
    public event Action? OnDevicesChanged;
    public event Action<bool>? OnDiscoveryStateChanged;

    public SyncEngine(AppConfig config, Action<string, bool> statusCallback){
        _config = config;
        _statusCallback = statusCallback;
    }

    public void Start(){
        try{
            Program.Log("SyncEngine.Start: Initializing HttpManifestServer...");
            _httpServer = new HttpManifestServer(GetFoldersConfig, GetManifestsPayload, TriggerSync);
            _httpServer.Start();
            Program.Log("SyncEngine.Start: Starting supervisor and UDP beacon loops...");
            _supervisorLoop = Task.Run(() => MaintainConnectionsAsync(_cts.Token));
            _udpLoop = Task.Run(() => RunUdpBeaconAsync(_cts.Token));
            Program.Log("SyncEngine.Start: Setting up FileSystemWatchers...");
            SetupFileSystemWatchers();
            SetDiscoveryMode(true, autoDisableMinutes: 5);
            Program.Log("SyncEngine.Start completed successfully.");
        }
        catch (Exception ex){
            Program.Log(string.Format("SyncEngine.Start EXCEPTION: {0}", ex.Message));
            throw;
        }
    }

    public bool IsDiscoveryEnabled => _config.NetworkDiscoveryEnabled;

    public void SetDiscoveryMode(bool enabled, int autoDisableMinutes = 0){
        _discoveryTimerCts?.Cancel();
        _discoveryTimerCts?.Dispose();
        _discoveryTimerCts = null;
        _config.NetworkDiscoveryEnabled = enabled;
        ConfigManager.Save(_config);
        UpdateTrayState();
        OnDiscoveryStateChanged?.Invoke(enabled);
        if (enabled && autoDisableMinutes > 0){
            _discoveryTimerCts = new CancellationTokenSource();
            var token = _discoveryTimerCts.Token;
            _ = Task.Run(async () =>{
                try{
                    await Task.Delay(TimeSpan.FromMinutes(autoDisableMinutes), token);
                    if (!token.IsCancellationRequested){
                        _config.NetworkDiscoveryEnabled = false;
                        ConfigManager.Save(_config);
                        UpdateTrayState();
                        OnDiscoveryStateChanged?.Invoke(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    private void SetupFileSystemWatchers(){
        foreach (var folder in _config.WindowsFolders){
            try{
                if (!Directory.Exists(folder.Path)){
                    Program.Log($"SetupFileSystemWatchers [Warning]: Folder '{folder.Path}' does not exist or drive is disconnected. Skipping watcher.");
                    continue;
                }
                var fsw = new FileSystemWatcher(folder.Path){IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size};
                fsw.Created += (s, e) => ScheduleFolderManifestSync(folder);
                fsw.Changed += (s, e) => ScheduleFolderManifestSync(folder);
                fsw.Deleted += (s, e) => ScheduleFolderManifestSync(folder);
                fsw.Renamed += (s, e) => ScheduleFolderManifestSync(folder);
                fsw.EnableRaisingEvents = true;
                _watchers.Add(fsw);
            }
            catch (Exception ex){
                Program.Log($"SetupFileSystemWatchers [Warning] for folder '{folder.Path}': {ex.Message}");
            }
        }
    }

    private void ScheduleFolderManifestSync(FolderConfig folder){
        string key = folder.Path.ToLowerInvariant();
        if (_debounceMap.TryGetValue(key, out var existingCts)){
            existingCts.Cancel();
            existingCts.Dispose();
        }
        var newCts = new CancellationTokenSource();
        _debounceMap[key] = newCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1200, newCts.Token);
                await ExecuteFolderSyncAcrossAllDevicesAsync(folder, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex){
                Program.Log($"[Watcher Sync Error] {ex.Message}");
            }
            finally{
                if (_debounceMap.TryGetValue(key, out var cur) && cur == newCts){_debounceMap.TryRemove(key, out _);}
            }
        });
    }

    public async Task ExecuteFolderSyncAcrossAllDevicesAsync(FolderConfig folder, CancellationToken ct){
        await _syncThrottleLock.WaitAsync(ct);
        try{
            var clients = _clients.Values.Where(c => c.IsConnected).ToList();
            if (clients.Count == 0) return;
            string folderPath = Path.GetFullPath(folder.Path);
            if (!Directory.Exists(folderPath)){
                Program.Log($"ExecuteFolderSyncAcrossAllDevicesAsync [Warning]: Folder '{folderPath}' not found or unreachable. Skipping.");
                return;
            }
            string folderId = ConfigManager.ComputeFolderId(folderPath);
            var winManifest = new Dictionary<string, long>();
            var targetToLocal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                if (!ConfigManager.IsSyncableFile(file, folder.Extensions, folder.IgnoredExtensions)) continue;
                string rel = Path.GetRelativePath(folderPath, file).Replace('\\', '/').TrimStart('/');
                string targetRel = ConfigManager.ComputeTargetRelPath(rel, folder.ScrubLevel).Replace('\\', '/').TrimStart('/');
                try
                {
                    winManifest[targetRel] = new FileInfo(file).Length;
                    targetToLocal[targetRel] = file;
                }
                catch { }
            }

            foreach (var client in clients)
            {
                if (ct.IsCancellationRequested) break;
                if (!await client.EnsureConnectedAsync(ct)) continue;

                _statusCallback($"Auditing manifest ({client.RemoteIp})...", true);
                var report = await client.ExchangeManifestAsync(folderId, winManifest, ct);
                if (report == null) continue;

                if (report.Needed.Count > 0)
                {
                    foreach (var neededRel in report.Needed)
                    {
                        if (ct.IsCancellationRequested) break;
                        string cleanKey = neededRel.Replace('\\', '/').TrimStart('/');
                        if (targetToLocal.TryGetValue(cleanKey, out var localPath) && File.Exists(localPath))
                        {
                            if (ConfigManager.IsIntermediateOrLockFile(localPath)) continue;
                            _statusCallback($"Syncing: {Path.GetFileName(localPath)}", true);
                            await client.StreamFileAsync(folderId, localPath, cleanKey, ct);
                        }
                    }
                }
            }
            _statusCallback("Active", false);
            UpdateTrayState();
        }
        finally { _syncThrottleLock.Release(); }
    }

    private async Task MaintainConnectionsAsync(CancellationToken ct){
        while (!ct.IsCancellationRequested){
            try{
                bool changed = false;
                foreach (var (ip, client) in _clients){
                    if (!client.IsConnected){
                        await client.DisposeAsync();
                        if (_clients.TryRemove(ip, out _))
                            changed = true;
                    }
                }
                var connectedIps = _clients.Keys.ToHashSet();
                var targetsToVerify = new HashSet<string>(_config.KnownDeviceIps, StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_config.ManualIp)){
                    foreach (var ip in _config.ManualIp.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        targetsToVerify.Add(ip);
                }
                foreach (var ip in targetsToVerify){
                    if (ct.IsCancellationRequested) break;
                    if (!connectedIps.Contains(ip)){
                        if (await ConnectSingleDeviceAsync(ip, ct))
                            changed = true;
                    }
                }
                if (_config.NetworkDiscoveryEnabled){
                    var discovered = await NetworkDiscovery.ScanSubnetDevicesAsync(_config.ManualIp, connectedIps, ct);
                    foreach (var ip in discovered){
                        if (ct.IsCancellationRequested) break;
                        if (await ConnectSingleDeviceAsync(ip, ct))
                            changed = true;
                    }
                }
                if (changed) { OnDevicesChanged?.Invoke(); }
                UpdateTrayState();
            }
            catch (Exception ex){
                Program.Log($"MaintainConnectionsAsync loop EXCEPTION: {ex.Message}");
            }
            await Task.Delay(_config.NetworkDiscoveryEnabled ? 4000 : 15000, ct);
        }
    }

    public async Task<bool> ConnectSingleDeviceAsync(string ip, CancellationToken ct){
        if (_clients.ContainsKey(ip)) return false;
        var client = new DeviceClient(ip);
        if (await client.ConnectAsync(3000, ct)){
            if (await client.SendConfigAsync(_config.WindowsFolders, ct)){
                await client.GetDeviceInfoAsync(ct);
                if (_clients.TryAdd(ip, client)){
                    if (!_config.KnownDeviceIps.Contains(ip, StringComparer.OrdinalIgnoreCase)){
                        _config.KnownDeviceIps.Add(ip);
                        ConfigManager.Save(_config);
                    }
                    UpdateTrayState();
                    OnDevicesChanged?.Invoke();
                    _ = SyncFullDeviceAuditAsync(client, ct);
                    return true;
                }
                else { await client.DisposeAsync(); }
            }
            else { await client.DisposeAsync(); }
        }
        return false;
    }

    public async Task SyncFullDeviceAuditAsync(DeviceClient client, CancellationToken ct){
        if (!_activeAudits.TryAdd(client.RemoteIp, 0)) return;
        try{
            await _syncThrottleLock.WaitAsync(ct);
            try{
                foreach (var folder in _config.WindowsFolders){
                    try{
                        string folderPath = Path.GetFullPath(folder.Path);
                        if (!Directory.Exists(folderPath)){
                            Program.Log($"SyncFullDeviceAuditAsync [Warning]: Directory '{folderPath}' not found or drive disconnected. Ignoring folder.");
                            continue;
                        }
                        string folderId = ConfigManager.ComputeFolderId(folderPath);
                        var winManifest = new Dictionary<string, long>();
                        var targetToLocal = new Dictionary<string, string>();
                        foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)){
                            if (!ConfigManager.IsSyncableFile(file, folder.Extensions, folder.IgnoredExtensions)) continue;
                            string rel = Path.GetRelativePath(folderPath, file).Replace('\\', '/').TrimStart('/');
                            string targetRel = ConfigManager.ComputeTargetRelPath(rel, folder.ScrubLevel).Replace('\\', '/').TrimStart('/');
                            try{
                                winManifest[targetRel] = new FileInfo(file).Length;
                                targetToLocal[targetRel] = file;
                            }
                            catch { }
                        }
                        var report = await client.ExchangeManifestAsync(folderId, winManifest, ct);
                        if (report == null) continue;
                        foreach (var targetPosix in report.Needed){
                            if (ct.IsCancellationRequested || !client.IsConnected) break;
                            string cleanKey = targetPosix.TrimStart('/');
                            string? localFile = null;
                            if (!targetToLocal.TryGetValue(cleanKey, out localFile)){
                                var match = targetToLocal.FirstOrDefault(kvp => kvp.Key.Equals(cleanKey, StringComparison.OrdinalIgnoreCase));
                                localFile = match.Value;
                            }
                            if (localFile != null && File.Exists(localFile)){
                                if (ConfigManager.IsIntermediateOrLockFile(localFile)) continue;
                                _statusCallback($"Syncing: {Path.GetFileName(localFile)}", true);
                                await client.StreamFileAsync(folderId, localFile, cleanKey, ct);
                            }
                        }
                    }
                    catch (Exception folderEx){
                        Program.Log($"SyncFullDeviceAuditAsync [Warning] for folder '{folder.Path}': {folderEx.Message}. Skipping.");
                    }
                }
                await client.NotifySyncCompleteAsync(ct);
                _statusCallback("Active", false);
                UpdateTrayState();
            }
            finally{_syncThrottleLock.Release();}
        }
        catch (Exception ex){
            Program.Log($"SyncFullDeviceAuditAsync EXCEPTION: {ex.Message}");
        }
        finally{
            _activeAudits.TryRemove(client.RemoteIp, out _);
        }
    }

    private async Task RunUdpBeaconAsync(CancellationToken ct){
        try{
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.UdpBeaconPort));
            var lastAnnounce = DateTime.MinValue;
            while (!ct.IsCancellationRequested){
                if (_config.NetworkDiscoveryEnabled && (DateTime.UtcNow - lastAnnounce).TotalSeconds > 4){
                    lastAnnounce = DateTime.UtcNow;
                    foreach (var ip in NetworkDiscovery.GetActiveIPv4Subnets()){
                        var parts = ip.Split('.');
                        if (parts.Length == 4){
                            var bcast = IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.255");
                            byte[] msg = Encoding.UTF8.GetBytes($"MIRROR_PC_ANNOUNCE:{ip}");
                            await udp.SendAsync(msg, msg.Length, new IPEndPoint(bcast, Protocol.UdpBeaconPort));
                        }
                    }
                }
                try{
                    var receiveTask = udp.ReceiveAsync(ct).AsTask();
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(1000, ct));
                    if (completedTask == receiveTask){
                        var res = await receiveTask;
                        string text = Encoding.UTF8.GetString(res.Buffer).Trim();
                        if (text.StartsWith("MIRROR_PHONE_ANNOUNCE:")){
                            string phoneIp = text.Split(':', 2)[1].Trim();
                            if (string.IsNullOrEmpty(phoneIp)) phoneIp = res.RemoteEndPoint.Address.ToString();
                            _ = ConnectSingleDeviceAsync(phoneIp, ct);
                        }
                        else if (text == "MIRROR_QUERY_PC"){
                            foreach (var ip in NetworkDiscovery.GetActiveIPv4Subnets()){
                                byte[] reply = Encoding.UTF8.GetBytes($"MIRROR_PC_ANNOUNCE:{ip}");
                                await udp.SendAsync(reply, reply.Length, res.RemoteEndPoint);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex){
                    Program.Log(string.Format("RunUdpBeaconAsync inner EXCEPTION: {0}", ex.Message));
                }
            }
        }
        catch (Exception ex){
            Program.Log(string.Format("RunUdpBeaconAsync FATAL EXCEPTION: {0}", ex.Message));
        }
    }

    public void TriggerSync(string? ip){
        if (!string.IsNullOrWhiteSpace(ip)){
            _ = Task.Run(async () =>{
                try{
                    if (_clients.TryGetValue(ip, out var client) && client.IsConnected){
                        await SyncFullDeviceAuditAsync(client, _cts.Token);
                    }
                    else{
                        Program.Log($"TriggerSync: Device {ip} requested sync, connecting...");
                        await ConnectSingleDeviceAsync(ip, _cts.Token);
                    }
                }
                catch (Exception ex){
                    Program.Log($"TriggerSync for {ip} EXCEPTION: {ex.Message}");
                }
            });
        }
        else{
            foreach (var c in _clients.Values.Where(c => c.IsConnected)){
                _ = SyncFullDeviceAuditAsync(c, _cts.Token);
            }
        }
    }

    private void UpdateTrayState(){
        int count = _clients.Values.Count(c => c.IsConnected);
        if (count == 0) _statusCallback("Scanning Wi-Fi for devices...", false);
        else if (count == 1){
            var first = _clients.Values.First();
            string name = first.DeviceInfo?.Model ?? first.RemoteIp;
            _statusCallback($"Connected ({name})", false);
        }
        else _statusCallback($"Connected to {count} devices", false);
    }

    public List<FolderWirePayload> GetFoldersConfig() => _config.WindowsFolders.Select(f => new FolderWirePayload{
        Id = ConfigManager.ComputeFolderId(Path.GetFullPath(f.Path)), Name = new DirectoryInfo(f.Path).Name, LocalPath = Path.GetFullPath(f.Path), Extensions = f.Extensions, IgnoredExtensions = f.IgnoredExtensions, ScrubLevel = f.ScrubLevel
    }).ToList();

    public object GetManifestsPayload(){
        var foldersData = new List<object>();
        foreach (var folder in _config.WindowsFolders){
            string full = Path.GetFullPath(folder.Path);
            var manifest = new Dictionary<string, long>();
            if (Directory.Exists(full)){
                try{
                    foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    {
                        if (!ConfigManager.IsSyncableFile(file, folder.Extensions, folder.IgnoredExtensions, checkLock: false)) continue;
                        string rel = Path.GetRelativePath(full, file).Replace('\\', '/').TrimStart('/');
                        string targetRel = ConfigManager.ComputeTargetRelPath(rel, folder.ScrubLevel).Replace('\\', '/').TrimStart('/');
                        manifest[targetRel] = new FileInfo(file).Length;
                    }
                }
                catch (Exception ex){
                    Program.Log($"GetManifestsPayload [Warning] for folder '{full}': {ex.Message}");
                }
            }
            else{
                Program.Log($"GetManifestsPayload: Folder '{full}' does not exist. Omitting files.");
            }

            foldersData.Add(new{
                id = ConfigManager.ComputeFolderId(full), name = new DirectoryInfo(full).Name, local_path = full, scrub_level = folder.ScrubLevel, extensions = folder.Extensions, manifest
            });
        }
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        string rawJson = JsonSerializer.Serialize(new { folders = foldersData }, options);
        return JsonSerializer.Deserialize<JsonElement>(rawJson);
    }

    public async ValueTask DisposeAsync(){
        try{
            _cts.Cancel();
            _discoveryTimerCts?.Cancel();
            _discoveryTimerCts?.Dispose();
            foreach (var cts in _debounceMap.Values){
                cts.Cancel();
                cts.Dispose();
            }
            _debounceMap.Clear();
            foreach (var w in _watchers){
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            if (_httpServer != null) await _httpServer.DisposeAsync();
            foreach (var client in _clients.Values)
                await client.DisposeAsync();
            _clients.Clear();
            _cts.Dispose();
            _syncThrottleLock.Dispose();
        }
        catch (Exception ex){
            Program.Log($"SyncEngine Dispose EXCEPTION: {ex.Message}");
        }
    }
}

// ============================================================================
// 8. TRAY ICON GENERATOR
// ============================================================================
public static class TrayIconHelper{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateDynamicIcon(bool syncing){
        try{
            using var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp)){
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using var bgBrush = new SolidBrush(Color.FromArgb(255, 30, 41, 59));
                g.FillEllipse(bgBrush, 1, 1, 30, 30);
                Color arcColor = syncing ? Color.FromArgb(74, 222, 128) : Color.FromArgb(56, 189, 248);
                using var arcPen = new Pen(arcColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(arcPen, 6, 6, 20, 20, 30, 120);
                g.DrawArc(arcPen, 6, 6, 20, 20, 210, 120);
                Color centerColor = syncing ? Color.FromArgb(250, 204, 21) : Color.FromArgb(148, 163, 184);
                using var centerBrush = new SolidBrush(centerColor);
                g.FillEllipse(centerBrush, 13, 13, 6, 6);
            }
            IntPtr hIcon = bmp.GetHicon();
            var icon = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return icon;
        }
        catch (Exception ex){
            Program.Log($"TrayIconHelper.CreateDynamicIcon EXCEPTION: {ex.Message}");
            return SystemIcons.Application;
        }
    }
}

// ============================================================================
// 9. CONFIGURATION FORM
// ============================================================================
public sealed class ConfigWindow : Form{
    private readonly AppConfig _currentConfig;
    private readonly Func<AppConfig, Task> _onSaveCallback;
    private readonly TextBox _ipBox;
    private readonly ListView _listView;
    private readonly List<FolderConfig> _foldersList;
    private readonly Panel _warningPanel;
    private readonly Label _warningLabel;

    public ConfigWindow(AppConfig config, Func<AppConfig, Task> onSaveCallback){
        _currentConfig = config;
        _onSaveCallback = onSaveCallback;
        _foldersList = config.WindowsFolders.Select(f => new FolderConfig{
            Path = f.Path, Extensions = [.. f.Extensions], IgnoredExtensions = [.. f.IgnoredExtensions], ScrubLevel = f.ScrubLevel
        }).ToList();
        Text = "Auto Wi-Fi Mirror Folders Configuration";
        Size = new Size(840, 560);
        MinimumSize = new Size(740, 480);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = false;
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);
        _warningPanel = new Panel { Dock = DockStyle.Top, Height = 36, BackColor = Color.FromArgb(254, 243, 199), Padding = new Padding(12, 8, 12, 8), Visible = false };
        _warningLabel = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(146, 64, 14), Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
        _warningPanel.Controls.Add(_warningLabel);
        var ipPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 10, 12, 6) };
        var ipLabel = new Label { Text = "Target IP(s) (comma-separated, or blank for auto-discovery):", AutoSize = true, Dock = DockStyle.Left };
        _ipBox = new TextBox { Text = _currentConfig.ManualIp, Width = 280, Dock = DockStyle.Right };
        ipPanel.Controls.Add(ipLabel);
        ipPanel.Controls.Add(_ipBox);
        _listView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, GridLines = true };
        _listView.Columns.Add("Source Folder Path", 320);
        _listView.Columns.Add("Matching Extensions", 130);
        _listView.Columns.Add("Ignored Extensions", 130);
        _listView.Columns.Add("Scrub Level", 160);
        var actionPanel = new Panel { Dock = DockStyle.Bottom, Height = 45, Padding = new Padding(12, 6, 12, 6) };
        var addBtn = new Button { Text = "+ Add Folder...", Width = 130, Dock = DockStyle.Left };
        var editBtn = new Button { Text = "Edit Selected", Width = 110, Dock = DockStyle.Left };
        var removeBtn = new Button { Text = "Remove", Width = 90, Dock = DockStyle.Left };
        addBtn.Click += (s, e) => OpenFolderEditor(null);
        editBtn.Click += (s, e) =>{
            if (_listView.SelectedIndices.Count > 0)
                OpenFolderEditor(_listView.SelectedIndices[0]);
        };
        removeBtn.Click += (s, e) =>{
            if (_listView.SelectedIndices.Count > 0){
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
        saveBtn.Click += async (s, e) =>{
            if (_foldersList.Count == 0){
                MessageBox.Show(this, "Please configure at least one folder.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            saveBtn.Enabled = false;
            var updatedConfig = new AppConfig { ManualIp = _ipBox.Text.Trim(), WindowsFolders = _foldersList };
            await _onSaveCallback(updatedConfig);
            Close();
        };
        cancelBtn.Click += (s, e) => Close();
        footerPanel.Controls.Add(cancelBtn);
        footerPanel.Controls.Add(saveBtn);
        Controls.Add(_listView);
        Controls.Add(actionPanel);
        Controls.Add(ipPanel);
        Controls.Add(_warningPanel);
        Controls.Add(footerPanel);
        RefreshList();
    }

    private void RefreshList(){
        _listView.Items.Clear();
        var missingPaths = new List<string>();
        foreach (var item in _foldersList){
            bool exists = Directory.Exists(item.Path);
            string displayPath = exists ? item.Path : $"⚠️ {item.Path} (Unreachable/Missing)";
            if (!exists) missingPaths.Add(item.Path);
            var lvi = new ListViewItem(displayPath);
            if (!exists) { lvi.ForeColor = Color.FromArgb(180, 83, 9); }
            lvi.SubItems.Add(string.Join(", ", item.Extensions));
            lvi.SubItems.Add(string.Join(", ", item.IgnoredExtensions));
            lvi.SubItems.Add(FormatScrubLabel(item.ScrubLevel));
            _listView.Items.Add(lvi);
        }
        if (missingPaths.Count > 0){
            _warningLabel.Text = $"⚠️ Notice: {missingPaths.Count} folder(s) not found on disk (drive unmounted or missing). They are safely ignored.";
            _warningPanel.Visible = true;
        }
        else{_warningPanel.Visible = false;}
    }

    private static string FormatScrubLabel(int lvl) => lvl switch{
        0 => "0 - Disabled (Full Tree)", 1 => "1 - Max 1 Level Deep", _ => $"{lvl} - Max {lvl} Levels Deep"
    };

    private void OpenFolderEditor(int? editIndex){
        var target = editIndex.HasValue ? _foldersList[editIndex.Value] : new FolderConfig { Path = string.Empty, Extensions = ["*"], IgnoredExtensions = [], ScrubLevel = 0 };
        using var dlg = new Form{Text = editIndex.HasValue ? "Edit Broadcast Folder" : "Add Broadcast Folder", Size = new Size(580, 360), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, TopMost = false, Font = new Font("Segoe UI", 9.5f)};
        var pathLbl = new Label { Text = "Windows Source Directory:", Top = 14, Left = 16, AutoSize = true };
        var pathBox = new TextBox { Text = target.Path, Top = 36, Left = 16, Width = 430 };
        var browseBtn = new Button { Text = "Browse...", Top = 35, Left = 454, Width = 90 };
        browseBtn.Click += (s, e) =>{
            using var fbd = new FolderBrowserDialog{Description = "Select a Windows folder to sync", UseDescriptionForTitle = true, InitialDirectory = Directory.Exists(pathBox.Text) ? pathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)};
            if (fbd.ShowDialog(dlg) == DialogResult.OK) { pathBox.Text = fbd.SelectedPath; }
        };
        var extLbl = new Label { Text = "Allowed Extensions (comma-separated, e.g. .md, .png or *):", Top = 76, Left = 16, AutoSize = true };
        var extBox = new TextBox { Text = string.Join(", ", target.Extensions), Top = 98, Left = 16, Width = 528 };
        var ignoreLbl = new Label { Text = "Ignored/Blacklisted Extensions (comma-separated, e.g. .tmp, .log):", Top = 138, Left = 16, AutoSize = true };
        var ignoreBox = new TextBox { Text = string.Join(", ", target.IgnoredExtensions), Top = 160, Left = 16, Width = 528 };
        var scrubLbl = new Label { Text = "Folder Scrubbing Level (flatten directory tree):", Top = 200, Left = 16, AutoSize = true };
        var scrubCb = new ComboBox { Top = 222, Left = 16, Width = 528, DropDownStyle = ComboBoxStyle.DropDownList };
        for (int i = 0; i <= 5; i++) scrubCb.Items.Add(FormatScrubLabel(i));
        scrubCb.SelectedIndex = Math.Clamp(target.ScrubLevel, 0, 5);
        var okBtn = new Button { Text = "Apply", DialogResult = DialogResult.OK, Top = 270, Left = 444, Width = 100, BackColor = Color.FromArgb(2, 132, 199), ForeColor = Color.White };
        var cancelModalBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Top = 270, Left = 334, Width = 100 };
        dlg.Controls.AddRange([pathLbl, pathBox, browseBtn, extLbl, extBox, ignoreLbl, ignoreBox, scrubLbl, scrubCb, okBtn, cancelModalBtn]);
        dlg.AcceptButton = okBtn;
        dlg.CancelButton = cancelModalBtn;
        if (dlg.ShowDialog(this) == DialogResult.OK){
            string chosenPath = pathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(chosenPath)){
                MessageBox.Show(this, "The source directory path cannot be empty.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!Directory.Exists(chosenPath)){
                var warnRes = MessageBox.Show(dlg, $"The folder '{chosenPath}' does not currently exist or its drive is disconnected.\n\nDo you want to save it anyway? The app will safely ignore it until it becomes available.", "Unreachable Path Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (warnRes != DialogResult.Yes)
                    return;
            }
            var parts = extBox.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var normalizedExts = parts.Length == 0 ? new List<string> { "*" } : parts.Select(p => p.StartsWith('.') || p == "*" ? p : $".{p}").ToList();
            var ignoreParts = ignoreBox.Text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var normalizedIgnores = ignoreParts.Select(p => p.StartsWith('.') ? p : $".{p}").ToList();
            var resultConfig = new FolderConfig{Path = chosenPath, Extensions = normalizedExts, IgnoredExtensions = normalizedIgnores, ScrubLevel = scrubCb.SelectedIndex};
            if (editIndex.HasValue)
                _foldersList[editIndex.Value] = resultConfig;
            else
                _foldersList.Add(resultConfig);
            RefreshList();
        }
    }
}
