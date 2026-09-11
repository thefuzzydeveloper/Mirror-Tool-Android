using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WiFiAutoStreamSync;

public sealed class FileTransferProgress
{
    public long BytesTransferred { get; init; }
    public long TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    public double Percentage => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes * 100.0, 0.0, 100.0) : 0.0;
}

public sealed class DeviceClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _networkLock = new(1, 1);
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;

    public string RemoteIp { get; }
    public bool IsConnected => _tcpClient?.Connected ?? false;
    public AndroidDeviceInfo? DeviceInfo { get; set; }
    public string DeletionAuthToken { get; set; } = string.Empty;

    public DeviceClient(string remoteIp)
    {
        RemoteIp = remoteIp;
    }

    public async Task<bool> ConnectAsync(int timeoutMs = 4000, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
            DisconnectInternal();
            _tcpClient = new TcpClient
            {
                NoDelay = true,
                SendTimeout = 15000,
                ReceiveTimeout = 15000
            };

            _tcpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            await _tcpClient.ConnectAsync(RemoteIp, Protocol.TcpDataPort, linkedCts.Token);
            _stream = _tcpClient.GetStream();
            return true;
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.ConnectAsync to {RemoteIp} failed: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected && _stream != null) return true;
        return await ConnectAsync(3000, ct);
    }

    public async Task<AndroidDeviceInfo?> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
            try
            {
                var slice = payload.AsMemory(0, (int)len);
                await Protocol.ReadExactAsync(_stream, slice, ct);
                var info = JsonSerializer.Deserialize<AndroidDeviceInfo>(slice.Span);
                DeviceInfo = info;
                return info;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.GetDeviceInfoAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return null;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<AndroidDirListing?> ListDirectoryAsync(string androidPath, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
            try
            {
                var slice = payload.AsMemory(0, (int)len);
                await Protocol.ReadExactAsync(_stream, slice, ct);
                return JsonSerializer.Deserialize<AndroidDirListing>(slice.Span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.ListDirectoryAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return null;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> PullFileAsync(
        string androidFilePath,
        string localDestinationPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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

            // DIRECT DISK WRITE: Ensure destination directory exists on target disk
            string? dir = Path.GetDirectoryName(localDestinationPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] chunkBuffer = ArrayPool<byte>.Shared.Rent(Protocol.ChunkStreamSize);
            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;
            long lastReportBytes = 0;
            double currentSpeed = 0;

            try
            {
                // Write directly to destination path without intermediate files on C:
                await using (var fs = new FileStream(
                    localDestinationPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 128 * 1024,
                    useAsync: true))
                {
                    long bytesRemaining = fileSize;
                    long totalDownloaded = 0;

                    progress?.Report(new FileTransferProgress
                    {
                        BytesTransferred = 0,
                        TotalBytes = fileSize,
                        BytesPerSecond = 0,
                        EstimatedTimeRemaining = null
                    });

                    while (bytesRemaining > 0)
                    {
                        int toRead = (int)Math.Min(bytesRemaining, Protocol.ChunkStreamSize);
                        int read = await _stream.ReadAsync(chunkBuffer.AsMemory(0, toRead), ct);
                        if (read == 0) throw new EndOfStreamException("Connection disconnected while streaming file.");

                        await fs.WriteAsync(chunkBuffer.AsMemory(0, read), ct);
                        bytesRemaining -= read;
                        totalDownloaded += read;

                        long elapsedMs = sw.ElapsedMilliseconds;
                        bool isFinished = bytesRemaining == 0;

                        if (progress != null && (isFinished || elapsedMs - lastReportMs >= 80))
                        {
                            long deltaMs = elapsedMs - lastReportMs;
                            long deltaBytes = totalDownloaded - lastReportBytes;
                            if (deltaMs > 0)
                            {
                                double instantSpeed = (deltaBytes * 1000.0) / deltaMs;
                                currentSpeed = currentSpeed <= 0 ? instantSpeed : (currentSpeed * 0.7) + (instantSpeed * 0.3);
                            }
                            else if (elapsedMs > 0)
                            {
                                currentSpeed = (totalDownloaded * 1000.0) / elapsedMs;
                            }

                            TimeSpan? eta = null;
                            if (currentSpeed > 0 && fileSize > totalDownloaded)
                            {
                                double remainingSec = (fileSize - totalDownloaded) / currentSpeed;
                                if (remainingSec >= 0 && remainingSec < 86400)
                                    eta = TimeSpan.FromSeconds(remainingSec);
                            }

                            progress.Report(new FileTransferProgress
                            {
                                BytesTransferred = totalDownloaded,
                                TotalBytes = fileSize,
                                BytesPerSecond = currentSpeed,
                                EstimatedTimeRemaining = eta
                            });

                            lastReportMs = elapsedMs;
                            lastReportBytes = totalDownloaded;
                        }
                    }

                    await fs.FlushAsync(ct);
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuffer);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.PullFileAsync EXCEPTION: {ex.Message}");
            try
            {
                if (File.Exists(localDestinationPath))
                    File.Delete(localDestinationPath);
            }
            catch { }
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<int> PullFolderRecursiveAsync(
        string androidFolderPath,
        string localDestinationDir,
        Action<string>? statusCallback = null,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        int filesDownloaded = 0;
        if (!Directory.Exists(localDestinationDir))
            Directory.CreateDirectory(localDestinationDir);

        var listing = await ListDirectoryAsync(androidFolderPath, ct);
        if (listing == null || !listing.Exists) return 0;

        foreach (var item in listing.Items)
        {
            if (ct.IsCancellationRequested) break;

            if (item.IsDir)
            {
                string nextLocal = Path.Combine(localDestinationDir, item.Name);
                filesDownloaded += await PullFolderRecursiveAsync(item.Path, nextLocal, statusCallback, progress, ct);
            }
            else
            {
                string targetLocalFile = Path.Combine(localDestinationDir, item.Name);
                statusCallback?.Invoke($"Downloading {item.Name} ({FormatBytes(item.Size)})...");
                bool ok = await PullFileAsync(item.Path, targetLocalFile, progress, ct);
                if (ok) filesDownloaded++;
            }
        }

        return filesDownloaded;
    }

    public async Task<bool> PushFileDirectAsync(
        string localFilePath,
        string androidDestinationPath,
        IProgress<FileTransferProgress>? progress = null,
        CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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

            try
            {
                long remaining = fileSize;
                long totalUploaded = 0;

                progress?.Report(new FileTransferProgress
                {
                    BytesTransferred = 0,
                    TotalBytes = fileSize,
                    BytesPerSecond = 0,
                    EstimatedTimeRemaining = null
                });

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, Protocol.ChunkStreamSize);
                    int read = await fs.ReadAsync(chunk.AsMemory(0, toRead), ct);
                    if (read == 0) break;

                    await Protocol.SendExactAsync(_stream, chunk.AsMemory(0, read), ct);
                    remaining -= read;
                    totalUploaded += read;

                    long elapsedMs = sw.ElapsedMilliseconds;
                    bool isFinished = remaining == 0;

                    if (progress != null && (isFinished || elapsedMs - lastReportMs >= 80))
                    {
                        long deltaMs = elapsedMs - lastReportMs;
                        long deltaBytes = totalUploaded - lastReportBytes;
                        if (deltaMs > 0)
                        {
                            double instantSpeed = (deltaBytes * 1000.0) / deltaMs;
                            currentSpeed = currentSpeed <= 0 ? instantSpeed : (currentSpeed * 0.7) + (instantSpeed * 0.3);
                        }
                        else if (elapsedMs > 0)
                        {
                            currentSpeed = (totalUploaded * 1000.0) / elapsedMs;
                        }

                        TimeSpan? eta = null;
                        if (currentSpeed > 0 && fileSize > totalUploaded)
                        {
                            double remainingSec = (fileSize - totalUploaded) / currentSpeed;
                            if (remainingSec >= 0 && remainingSec < 86400)
                                eta = TimeSpan.FromSeconds(remainingSec);
                        }

                        progress.Report(new FileTransferProgress
                        {
                            BytesTransferred = totalUploaded,
                            TotalBytes = fileSize,
                            BytesPerSecond = currentSpeed,
                            EstimatedTimeRemaining = eta
                        });

                        lastReportMs = elapsedMs;
                        lastReportBytes = totalUploaded;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.PushFileDirectAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<int> PushFolderRecursiveAsync(string localFolder, string androidDestinationFolder, Action<string>? statusCallback = null, CancellationToken ct = default)
    {
        int filesUploaded = 0;
        await CreateDirectoryDirectAsync(androidDestinationFolder, ct);

        foreach (var file in Directory.EnumerateFiles(localFolder))
        {
            if (ct.IsCancellationRequested) break;
            string fileName = Path.GetFileName(file);
            string destPath = $"{androidDestinationFolder.TrimEnd('/')}/{fileName}";
            statusCallback?.Invoke($"Uploading: {fileName}...");
            if (await PushFileDirectAsync(file, destPath, null, ct))
                filesUploaded++;
        }

        foreach (var dir in Directory.EnumerateDirectories(localFolder))
        {
            if (ct.IsCancellationRequested) break;
            string dirName = Path.GetFileName(dir);
            string nextAndroidDir = $"{androidDestinationFolder.TrimEnd('/')}/{dirName}";
            filesUploaded += await PushFolderRecursiveAsync(dir, nextAndroidDir, statusCallback, ct);
        }

        return filesUploaded;
    }

    public async Task<bool> DeletePathDirectAsync(string androidPath, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.DeletePathDirectAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> CreateDirectoryDirectAsync(string androidPath, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.CreateDirectoryDirectAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> SendConfigAsync(List<FolderConfig> folders, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
            if (_stream == null) return false;

            var payload = folders.Select(f =>
            {
                var fullPath = Path.GetFullPath(f.Path);
                return new FolderWirePayload
                {
                    Id = ConfigManager.ComputeFolderId(fullPath),
                    Name = new DirectoryInfo(fullPath).Name,
                    LocalPath = fullPath,
                    Extensions = f.Extensions,
                    IgnoredExtensions = f.IgnoredExtensions,
                    ScrubLevel = f.ScrubLevel
                };
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
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.SendConfigAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<ManifestExchangeResponse?> ExchangeManifestAsync(string folderId, Dictionary<string, long> manifest, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
            try
            {
                var slice = respPayload.AsMemory(0, (int)respLen);
                await Protocol.ReadExactAsync(_stream, slice, ct);
                return JsonSerializer.Deserialize<ManifestExchangeResponse>(slice.Span);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(respPayload);
            }
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.ExchangeManifestAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return null;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> StreamFileAsync(string folderId, string fullPath, string relTarget, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
            try
            {
                long bytesRemaining = fileSize;
                while (bytesRemaining > 0)
                {
                    int toRead = (int)Math.Min(bytesRemaining, Protocol.ChunkStreamSize);
                    int read = await fs.ReadAsync(chunkBuffer.AsMemory(0, toRead), ct);
                    if (read == 0) break;

                    await Protocol.SendExactAsync(_stream, chunkBuffer.AsMemory(0, read), ct);
                    bytesRemaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkBuffer);
            }

            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.StreamFileAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> SendDeleteAsync(string folderId, string relTarget, CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
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
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.SendDeleteAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> NotifySyncCompleteAsync(CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
            if (_stream == null) return false;
            byte[] packet = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdSyncEnd];
            await Protocol.SendExactAsync(_stream, packet, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.NotifySyncCompleteAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    public async Task<bool> SendWakeSignalAsync(CancellationToken ct = default)
    {
        await _networkLock.WaitAsync(ct);
        try
        {
            if (_stream == null) return false;
            byte[] packet = [Protocol.MagicHeader[0], Protocol.MagicHeader[1], Protocol.CmdWakeSync];
            await Protocol.SendExactAsync(_stream, packet, ct);
            return await Protocol.ReadAckAsync(_stream, ct);
        }
        catch (Exception ex)
        {
            Program.Log($"DeviceClient.SendWakeSignalAsync EXCEPTION: {ex.Message}");
            DisconnectInternal();
            return false;
        }
        finally
        {
            _networkLock.Release();
        }
    }

    private static async Task<FileStream?> OpenReadWithRetryAsync(string path, int maxRetries = 6, int delayMs = 150, CancellationToken ct = default)
    {
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
            }
            catch (IOException) when (i < maxRetries - 1)
            {
                await Task.Delay(delayMs, ct);
            }
        }
        return null;
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1 && counter < suffixes.Length - 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }

    private void DisconnectInternal()
    {
        try
        {
            _stream?.Dispose();
        }
        catch { }
        _stream = null;
        try
        {
            _tcpClient?.Dispose();
        }
        catch { }
        _tcpClient = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _networkLock.WaitAsync();
        try
        {
            DisconnectInternal();
        }
        finally
        {
            _networkLock.Release();
            _networkLock.Dispose();
        }
    }
}