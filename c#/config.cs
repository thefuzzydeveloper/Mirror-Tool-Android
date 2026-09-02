using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WiFiAutoStreamSync;

public sealed class FolderConfig
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("extensions")]
    public List<string> Extensions { get; set; } = ["*"];

    [JsonPropertyName("ignored_extensions")]
    public List<string> IgnoredExtensions { get; set; } = [];

    [JsonPropertyName("scrub_level")]
    public int ScrubLevel { get; set; } = 0;
}

public sealed class FolderWirePayload
{
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
public sealed class AppConfig
{
    [JsonPropertyName("manual_ip")]
    public string ManualIp { get; set; } = string.Empty;

    [JsonPropertyName("network_discovery_enabled")]
    public bool NetworkDiscoveryEnabled { get; set; } = false;

    [JsonPropertyName("known_device_ips")]
    public List<string> KnownDeviceIps { get; set; } = [];

    [JsonPropertyName("windows_folders")]
    public List<FolderConfig> WindowsFolders { get; set; } = [];
}

public sealed class ManifestExchangeResponse
{
    [JsonPropertyName("local_count")]
    public int LocalCount { get; set; }

    [JsonPropertyName("remote_count")]
    public int RemoteCount { get; set; }

    [JsonPropertyName("deleted_count")]
    public int DeletedCount { get; set; }

    [JsonPropertyName("needed")]
    public List<string> Needed { get; set; } = [];
}

// Remote Android Browser Data Contracts
public sealed class AndroidRootDir
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
}

public sealed class AndroidDeviceInfo
{
    [JsonPropertyName("model")] public string Model { get; set; } = "Android Device";
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("sdk")] public int Sdk { get; set; }
    [JsonPropertyName("root_dirs")] public List<AndroidRootDir> RootDirs { get; set; } = [];
}

public sealed class AndroidFileItem
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("is_dir")] public bool IsDir { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("last_modified")] public long LastModified { get; set; }
}

public sealed class AndroidDirListing
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("items")] public List<AndroidFileItem> Items { get; set; } = [];
}

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".wifiautostreamsync_config.json");

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            var fallback = new AppConfig
            {
                WindowsFolders = [
                    new FolderConfig {
                        Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SyncWorkspace"),
                        Extensions = ["*"],
                        ScrubLevel = 0
                    }
                ]
            };
            Save(fallback);
            return fallback;
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public static void Save(AppConfig config)
    {
        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }

    public static string ComputeFolderId(string folderPath)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    public static string ComputeTargetRelPath(string relativePath, int scrubLevel)
    {
        string normalized = relativePath.Replace('\\', '/').Trim('/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (scrubLevel <= 0 || parts.Length <= scrubLevel + 1)
            return string.Join('/', parts);

        var topDirs = parts.Take(scrubLevel);
        string flattened = string.Join('_', parts.Skip(scrubLevel));
        return string.Join('/', topDirs.Concat([flattened]));
    }

    public static bool IsExtensionAllowed(string filePath, List<string> allowedExtensions, List<string>? ignoredExtensions = null)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ignoredExtensions != null && ignoredExtensions.Count > 0)
        {
            bool isIgnored = ignoredExtensions.Any(e => 
            {
                string clean = e.Trim().ToLowerInvariant();
                return clean == ext || (clean.Length > 0 && $".{clean}" == ext) || clean == "*";
            });
            if (isIgnored) return false;
        }

        if (allowedExtensions.Count == 0 || allowedExtensions.Contains("*") || allowedExtensions.Contains(".*"))
            return true;

        return allowedExtensions.Any(e => 
        {
            string clean = e.Trim().ToLowerInvariant();
            return clean == ext || (clean.Length > 0 && $".{clean}" == ext);
        });
    }
}