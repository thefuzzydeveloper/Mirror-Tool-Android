using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WiFiAutoStreamSync;

public sealed class SyncEngine : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly Action<string, bool> _statusCallback;
    private readonly ConcurrentDictionary<string, DeviceClient> _clients = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _debounceMap = new();
    private readonly SemaphoreSlim _syncThrottleLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private readonly List<FileSystemWatcher> _watchers = [];
    private HttpManifestServer? _httpServer;
    private Task? _supervisorLoop;
    private Task? _udpLoop;

    public IReadOnlyDictionary<string, DeviceClient> ConnectedClients => _clients;
    public event Action? OnDevicesChanged;

    public SyncEngine(AppConfig config, Action<string, bool> statusCallback)
    {
        _config = config;
        _statusCallback = statusCallback;
    }

    public void Start()
    {
        _httpServer = new HttpManifestServer(GetFoldersConfig, GetManifestsPayload, TriggerSync);
        _httpServer.Start();

        _supervisorLoop = Task.Run(() => MaintainConnectionsAsync(_cts.Token));
        _udpLoop = Task.Run(() => RunUdpBeaconAsync(_cts.Token));

        SetupFileSystemWatchers();
    }

    private void SetupFileSystemWatchers()
    {
        foreach (var folder in _config.WindowsFolders)
        {
            if (!Directory.Exists(folder.Path))
                Directory.CreateDirectory(folder.Path);

            var fsw = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName | NotifyFilters.Size
            };

            fsw.Created += (s, e) => ScheduleFolderManifestSync(folder);
            fsw.Changed += (s, e) => ScheduleFolderManifestSync(folder);
            fsw.Deleted += (s, e) => ScheduleFolderManifestSync(folder);
            fsw.Renamed += (s, e) => ScheduleFolderManifestSync(folder);

            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }
    }

    private void ScheduleFolderManifestSync(FolderConfig folder)
    {
        string key = folder.Path.ToLowerInvariant();

        if (_debounceMap.TryGetValue(key, out var existingCts))
        {
            existingCts.Cancel();
            existingCts.Dispose();
        }

        var newCts = new CancellationTokenSource();
        _debounceMap[key] = newCts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, newCts.Token);
                await ExecuteFolderSyncAcrossAllDevicesAsync(folder, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Watcher Sync Error] {ex.Message}");
            }
            finally
            {
                if (_debounceMap.TryGetValue(key, out var cur) && cur == newCts)
                {
                    _debounceMap.TryRemove(key, out _);
                }
            }
        });
    }

    public async Task ExecuteFolderSyncAcrossAllDevicesAsync(FolderConfig folder, CancellationToken ct)
    {
        await _syncThrottleLock.WaitAsync(ct);
        try
        {
            var clients = _clients.Values.Where(c => c.IsConnected).ToList();
            if (clients.Count == 0) return;

            string folderPath = Path.GetFullPath(folder.Path);
            if (!Directory.Exists(folderPath)) return;

            string folderId = ConfigManager.ComputeFolderId(folderPath);

            var winManifest = new Dictionary<string, long>();
            var targetToLocal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                if (!ConfigManager.IsExtensionAllowed(file, folder.Extensions)) continue;

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
                await client.EnsureConnectedAsync(ct);

                _statusCallback($"Auditing manifest ({client.RemoteIp})...", true);
                var report = await client.ExchangeManifestAsync(folderId, winManifest, ct);
                if (report == null) continue;

                // Transfer ONLY what Android reported is actually needed
                if (report.Needed.Count > 0)
                {
                    foreach (var neededRel in report.Needed)
                    {
                        if (ct.IsCancellationRequested) break;
                        string cleanKey = neededRel.Replace('\\', '/').TrimStart('/');

                        if (targetToLocal.TryGetValue(cleanKey, out var localPath) && File.Exists(localPath))
                        {
                            _statusCallback($"Syncing: {Path.GetFileName(localPath)}", true);
                            await client.StreamFileAsync(folderId, localPath, cleanKey, ct);
                        }
                    }
                }

                await client.NotifySyncCompleteAsync(ct);
            }

            _statusCallback("Active", false);
            UpdateTrayState();
        }
        finally
        {
            _syncThrottleLock.Release();
        }
    }

    private async Task MaintainConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool changed = false;
            foreach (var (ip, client) in _clients)
            {
                if (!client.IsConnected)
                {
                    await client.DisposeAsync();
                    if (_clients.TryRemove(ip, out _))
                        changed = true;
                }
            }

            var connectedIps = _clients.Keys.ToHashSet();
            var discovered = await NetworkDiscovery.ScanSubnetDevicesAsync(_config.ManualIp, connectedIps, ct);

            foreach (var ip in discovered)
            {
                if (ct.IsCancellationRequested) break;
                if (await ConnectSingleDeviceAsync(ip, ct))
                    changed = true;
            }

            if (changed)
            {
                OnDevicesChanged?.Invoke();
            }

            UpdateTrayState();
            await Task.Delay(3500, ct);
        }
    }

    public async Task<bool> ConnectSingleDeviceAsync(string ip, CancellationToken ct)
    {
        if (_clients.ContainsKey(ip)) return false;

        var client = new DeviceClient(ip);
        if (await client.ConnectAsync(3000, ct))
        {
            if (await client.SendConfigAsync(_config.WindowsFolders, ct))
            {
                await client.GetDeviceInfoAsync(ct);

                if (_clients.TryAdd(ip, client))
                {
                    UpdateTrayState();
                    OnDevicesChanged?.Invoke();
                    _ = SyncFullDeviceAuditAsync(client, ct);
                    return true;
                }
                else
                {
                    await client.DisposeAsync();
                }
            }
            else
            {
                await client.DisposeAsync();
            }
        }
        return false;
    }

    public async Task SyncFullDeviceAuditAsync(DeviceClient client, CancellationToken ct)
    {
        await _syncThrottleLock.WaitAsync(ct);
        try
        {
            foreach (var folder in _config.WindowsFolders)
            {
                string folderPath = Path.GetFullPath(folder.Path);
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                string folderId = ConfigManager.ComputeFolderId(folderPath);
                var winManifest = new Dictionary<string, long>();
                var targetToLocal = new Dictionary<string, string>();

                foreach (var file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
                {
                    if (!ConfigManager.IsExtensionAllowed(file, folder.Extensions)) continue;

                    string rel = Path.GetRelativePath(folderPath, file).Replace('\\', '/').TrimStart('/');
                    string targetRel = ConfigManager.ComputeTargetRelPath(rel, folder.ScrubLevel).Replace('\\', '/').TrimStart('/');

                    try
                    {
                        winManifest[targetRel] = new FileInfo(file).Length;
                        targetToLocal[targetRel] = file;
                    }
                    catch { }
                }

                var report = await client.ExchangeManifestAsync(folderId, winManifest, ct);
                if (report == null) continue;

                foreach (var targetPosix in report.Needed)
                {
                    if (ct.IsCancellationRequested || !client.IsConnected) break;
                    string cleanKey = targetPosix.TrimStart('/');

                    string? localFile = null;
                    if (!targetToLocal.TryGetValue(cleanKey, out localFile))
                    {
                        var match = targetToLocal.FirstOrDefault(kvp => kvp.Key.Equals(cleanKey, StringComparison.OrdinalIgnoreCase));
                        localFile = match.Value;
                    }

                    if (localFile != null && File.Exists(localFile))
                    {
                        _statusCallback($"Syncing: {Path.GetFileName(localFile)}", true);
                        await client.StreamFileAsync(folderId, localFile, cleanKey, ct);
                    }
                }
            }

            await client.NotifySyncCompleteAsync(ct);
            _statusCallback("Active", false);
            UpdateTrayState();
        }
        finally
        {
            _syncThrottleLock.Release();
        }
    }

    private async Task RunUdpBeaconAsync(CancellationToken ct)
    {
        using var udp = new UdpClient();
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.UdpBeaconPort));

        var lastAnnounce = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            if ((DateTime.UtcNow - lastAnnounce).TotalSeconds > 3)
            {
                lastAnnounce = DateTime.UtcNow;
                foreach (var ip in NetworkDiscovery.GetActiveIPv4Subnets())
                {
                    var parts = ip.Split('.');
                    if (parts.Length == 4)
                    {
                        var bcast = IPAddress.Parse($"{parts[0]}.{parts[1]}.{parts[2]}.255");
                        byte[] msg = Encoding.UTF8.GetBytes($"MIRROR_PC_ANNOUNCE:{ip}");
                        await udp.SendAsync(msg, msg.Length, new IPEndPoint(bcast, Protocol.UdpBeaconPort));
                    }
                }
            }

            if (udp.Available > 0)
            {
                var res = await udp.ReceiveAsync(ct);
                string text = Encoding.UTF8.GetString(res.Buffer).Trim();

                if (text.StartsWith("MIRROR_PHONE_ANNOUNCE:"))
                {
                    string phoneIp = text.Split(':', 2)[1].Trim();
                    if (string.IsNullOrEmpty(phoneIp)) phoneIp = res.RemoteEndPoint.Address.ToString();
                    _ = ConnectSingleDeviceAsync(phoneIp, ct);
                }
                else if (text == "MIRROR_QUERY_PC")
                {
                    foreach (var ip in NetworkDiscovery.GetActiveIPv4Subnets())
                    {
                        byte[] reply = Encoding.UTF8.GetBytes($"MIRROR_PC_ANNOUNCE:{ip}");
                        await udp.SendAsync(reply, reply.Length, res.RemoteEndPoint);
                    }
                }
            }

            await Task.Delay(500, ct);
        }
    }

    public void TriggerSync(string? ip)
    {
        if (ip != null && _clients.TryGetValue(ip, out var client))
        {
            _ = SyncFullDeviceAuditAsync(client, _cts.Token);
        }
        else
        {
            foreach (var c in _clients.Values.Where(c => c.IsConnected))
            {
                _ = SyncFullDeviceAuditAsync(c, _cts.Token);
            }
        }
    }

    private void UpdateTrayState()
    {
        int count = _clients.Values.Count(c => c.IsConnected);
        if (count == 0) _statusCallback("Scanning Wi-Fi for devices...", false);
        else if (count == 1)
        {
            var first = _clients.Values.First();
            string name = first.DeviceInfo?.Model ?? first.RemoteIp;
            _statusCallback($"Connected ({name})", false);
        }
        else _statusCallback($"Connected to {count} devices", false);
    }

    public List<FolderWirePayload> GetFoldersConfig() =>
        _config.WindowsFolders.Select(f => new FolderWirePayload
        {
            Id = ConfigManager.ComputeFolderId(Path.GetFullPath(f.Path)),
            Name = new DirectoryInfo(f.Path).Name,
            LocalPath = Path.GetFullPath(f.Path),
            Extensions = f.Extensions,
            ScrubLevel = f.ScrubLevel
        }).ToList();

    public object GetManifestsPayload()
    {
        var foldersData = new List<object>();
        foreach (var folder in _config.WindowsFolders)
        {
            string full = Path.GetFullPath(folder.Path);
            var manifest = new Dictionary<string, long>();
            if (Directory.Exists(full))
            {
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    if (!ConfigManager.IsExtensionAllowed(file, folder.Extensions)) continue;
                    string rel = Path.GetRelativePath(full, file).Replace('\\', '/').TrimStart('/');
                    string targetRel = ConfigManager.ComputeTargetRelPath(rel, folder.ScrubLevel).Replace('\\', '/').TrimStart('/');
                    manifest[targetRel] = new FileInfo(file).Length;
                }
            }
            foldersData.Add(new
            {
                id = ConfigManager.ComputeFolderId(full),
                name = new DirectoryInfo(full).Name,
                local_path = full,
                scrub_level = folder.ScrubLevel,
                extensions = folder.Extensions,
                manifest
            });
        }
        
        var options = new JsonSerializerOptions 
        { 
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
        };
        string rawJson = JsonSerializer.Serialize(new { folders = foldersData }, options);
        return JsonSerializer.Deserialize<JsonElement>(rawJson);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        foreach (var cts in _debounceMap.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _debounceMap.Clear();

        foreach (var w in _watchers)
        {
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
}