using System.Net;
using System.Text;
using System.Text.Json;

namespace WiFiAutoStreamSync;

public sealed class HttpManifestServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly Func<List<FolderWirePayload>> _configProvider;
    private readonly Func<object> _manifestProvider;
    private readonly Action<string?> _onTriggerSync;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public HttpManifestServer(
        Func<List<FolderWirePayload>> configProvider,
        Func<object> manifestProvider,
        Action<string?> onTriggerSync)
    {
        _configProvider = configProvider;
        _manifestProvider = manifestProvider;
        _onTriggerSync = onTriggerSync;

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://*:{Protocol.HttpManifestPort}/");
    }

    public void Start()
    {
        try
        {
            _listener.Start();
            _runTask = RunAsync(_cts.Token);
        }
        catch (HttpListenerException)
        {
            // Requires admin or netsh reservation; fallback to localhost if restricted
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://localhost:{Protocol.HttpManifestPort}/");
            _listener.Start();
            _runTask = RunAsync(_cts.Token);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = ProcessRequestAsync(context);
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch { }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            string path = ctx.Request.Url?.AbsolutePath ?? "/";

            var jsonOptions = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            if (path == "/config")
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(_configProvider(), jsonOptions);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else if (path == "/manifests")
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(_manifestProvider(), jsonOptions);
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else if (path.StartsWith("/trigger_sync"))
            {
                string? queryIp = ctx.Request.QueryString["ip"];
                _onTriggerSync(queryIp);

                byte[] data = Encoding.UTF8.GetBytes("{\"status\": \"sync_triggered\"}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
        }
        catch { }
        finally
        {
            ctx.Response.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_listener.IsListening) _listener.Stop();
        _listener.Close();
        if (_runTask != null) await _runTask;
        _cts.Dispose();
    }
}