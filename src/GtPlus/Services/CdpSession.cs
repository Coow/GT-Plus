using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GtPlus.Services;

/// <summary>one headless browser, kept alive and driven over the Chrome DevTools Protocol. The page streams itself as a series of PNG frames (<c>Page.startScreencast</c>) so what the canvas draws is the page as it is right now rather than a snapshot, and pointer and key events go the other way down the same socket (<c>Input.dispatch*</c>) so the page can be used, not just looked at. Deliberately free of Avalonia types: this layer talks bytes and numbers, the canvas turns them into bitmaps and input</summary>
public sealed class CdpSession : IAsyncDisposable
{
    /// <summary>raw PNG bytes of one screencast frame; raised off the UI thread</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>the session has died and will produce no more frames</summary>
    public event Action<string>? Failed;

    private readonly Process        _process;
    private readonly ClientWebSocket _socket;
    private readonly string         _profileDir;
    private readonly SemaphoreSlim  _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _life = new();

    private int  _nextId;
    private bool _disposed;

    private CdpSession(Process process, ClientWebSocket socket, string profileDir)
    {
        _process    = process;
        _socket     = socket;
        _profileDir = profileDir;
    }

    /// <summary>how long the browser is given to come up and publish its debugging port</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>launches a headless browser, attaches to its first page and starts the screencast; returns null if it never came up, with the reason logged</summary>
    public static async Task<CdpSession?> StartAsync(string exe, int width, int height, bool transparent,
                                                     CancellationToken ct)
    {
        width  = Math.Clamp(width,  16, 4096);
        height = Math.Clamp(height, 16, 4096);

        var profileDir = Path.Combine(Path.GetTempPath(), "GtPlus-web", Guid.NewGuid().ToString("N"));
        Process? process = null;

        try
        {
            Directory.CreateDirectory(profileDir);

            var psi = new ProcessStartInfo
            {
                FileName               = exe,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardError  = true,
                RedirectStandardOutput = true,
            };

            psi.ArgumentList.Add("--headless=new");
            psi.ArgumentList.Add("--disable-gpu");
            psi.ArgumentList.Add("--hide-scrollbars");
            psi.ArgumentList.Add("--no-first-run");
            psi.ArgumentList.Add("--no-default-browser-check");
            psi.ArgumentList.Add("--disable-extensions");
            psi.ArgumentList.Add("--disable-sync");
            psi.ArgumentList.Add("--force-device-scale-factor=1");
            // without these the browser decides an offscreen window is not worth drawing and the stream drops to a few frames a second
            psi.ArgumentList.Add("--disable-background-timer-throttling");
            psi.ArgumentList.Add("--disable-backgrounding-occluded-windows");
            psi.ArgumentList.Add("--disable-renderer-backgrounding");
            psi.ArgumentList.Add("--disable-ipc-flooding-protection");
            psi.ArgumentList.Add("--disable-features=CalculateNativeWinOcclusion");
            // port 0 lets the browser pick a free one and write it to DevToolsActivePort, which avoids racing another instance for a fixed port
            psi.ArgumentList.Add("--remote-debugging-port=0");
            psi.ArgumentList.Add("--remote-allow-origins=*");
            psi.ArgumentList.Add($"--user-data-dir={profileDir}");
            psi.ArgumentList.Add($"--window-size={width},{height}");
            psi.ArgumentList.Add("about:blank");

            process = new Process { StartInfo = psi };
            process.Start();

            // stderr and stdout have to be drained or a chatty browser fills its pipe and stops
            _ = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEndAsync();

            var port = await ReadDebuggingPortAsync(profileDir, process, ct).ConfigureAwait(false);
            if (port is null) throw new InvalidOperationException("browser never published a debugging port");

            var wsUrl = await FindPageSocketAsync(port.Value, ct).ConfigureAwait(false);
            if (wsUrl is null) throw new InvalidOperationException("browser exposed no page to attach to");

            var socket = new ClientWebSocket();
            // screencast frames are whole PNGs and arrive back to back
            socket.Options.SetBuffer(64 * 1024, 16 * 1024);
            await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);

            var session = new CdpSession(process, socket, profileDir);
            _ = Task.Run(session.ReadLoopAsync);

            await session.CommandAsync("Page.enable").ConfigureAwait(false);
            await session.ResizeAsync(width, height).ConfigureAwait(false);
            await session.SetTransparentAsync(transparent).ConfigureAwait(false);
            await session.StartScreencastAsync(width, height).ConfigureAwait(false);

            return session;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not start a live web session", ex);
            try { process?.Kill(entireProcessTree: true); } catch { }
            try { if (Directory.Exists(profileDir)) Directory.Delete(profileDir, recursive: true); } catch { }
            return null;
        }
    }

    /// <summary>the browser writes its chosen port into DevToolsActivePort once it is listening</summary>
    private static async Task<int?> ReadDebuggingPortAsync(string profileDir, Process process, CancellationToken ct)
    {
        var file    = Path.Combine(profileDir, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + StartTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited) return null;

            try
            {
                if (File.Exists(file))
                {
                    var lines = await File.ReadAllLinesAsync(file, ct).ConfigureAwait(false);
                    if (lines.Length > 0 && int.TryParse(lines[0].Trim(), out var port) && port > 0)
                        return port;
                }
            }
            catch (IOException) { /* still being written */ }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>websocket url of the browser's first page target</summary>
    private static async Task<string?> FindPageSocketAsync(int port, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + StartTimeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var type) && type.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                        return ws.GetString();
                }
            }
            catch (Exception) { /* endpoint not up yet */ }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        return null;
    }

    // ---- protocol plumbing -------------------------------------------------

    private async Task ReadLoopAsync()
    {
        var buffer  = new byte[64 * 1024];
        var message = new MemoryStream();

        try
        {
            while (_socket.State == WebSocketState.Open && !_life.IsCancellationRequested)
            {
                message.SetLength(0);

                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _life.Token)
                                          .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                Dispatch(message.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) Failed?.Invoke(ex.Message);
        }
    }

    private void Dispatch(byte[] payload)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch { return; }

        using (doc)
        {
            var root = doc.RootElement;

            // a command that failed answers with an error rather than a result, and silently dropping those hides protocol mistakes
            if (root.TryGetProperty("error", out var error))
                Logger.Debug($"CDP error: {error}");

            if (root.TryGetProperty("id", out var idValue) && idValue.TryGetInt32(out var id))
            {
                if (_pending.TryRemove(id, out var waiter))
                {
                    // the result is cloned out because the document dies with this scope
                    var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
                    waiter.TrySetResult(result);
                }
                return;
            }

            if (!root.TryGetProperty("method", out var method)) return;
            if (method.GetString() != "Page.screencastFrame") return;
            if (!root.TryGetProperty("params", out var p)) return;

            // every frame must be acknowledged or the browser stops sending them
            if (p.TryGetProperty("sessionId", out var sessionId) && sessionId.TryGetInt32(out var sid))
                _ = SendAsync("Page.screencastFrameAck", new { sessionId = sid });

            if (!p.TryGetProperty("data", out var data)) return;
            var base64 = data.GetString();
            if (string.IsNullOrEmpty(base64)) return;

            try { FrameReceived?.Invoke(Convert.FromBase64String(base64!)); }
            catch (FormatException) { }
        }
    }

    /// <summary>sends a command and forgets it; the protocol replies to every command and the reply is simply dropped</summary>
    private async Task SendAsync(string method, object? parameters = null)
    {
        if (_disposed || _socket.State != WebSocketState.Open) return;

        var id      = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters ?? new { },
        });

        await _sendLock.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _life.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* a dying socket is handled by the read loop */ }
        finally { _sendLock.Release(); }
    }

    /// <summary>sends a command and waits for its reply, so start-up steps happen in order</summary>
    private async Task<JsonElement> CommandAsync(string method, object? parameters = null)
    {
        if (_disposed || _socket.State != WebSocketState.Open) return default;

        var id     = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters ?? new { },
        });

        await _sendLock.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, _life.Token).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await using var registration = timeout.Token.Register(() => waiter.TrySetCanceled()).ConfigureAwait(false);

        try { return await waiter.Task.ConfigureAwait(false); }
        catch (OperationCanceledException) { _pending.TryRemove(id, out _); return default; }
    }

    // ---- page control ------------------------------------------------------

    public Task NavigateAsync(string url) => SendAsync("Page.navigate", new { url });

    public Task ReloadAsync() => SendAsync("Page.reload", new { ignoreCache = true });

    /// <summary>resizes the page and restarts the stream so frames arrive at the new size</summary>
    public async Task ResizeAsync(int width, int height)
    {
        width  = Math.Clamp(width,  16, 4096);
        height = Math.Clamp(height, 16, 4096);

        await CommandAsync("Emulation.setDeviceMetricsOverride", new
        {
            width,
            height,
            deviceScaleFactor = 1,
            mobile            = false,
        }).ConfigureAwait(false);

        await SendAsync("Page.stopScreencast").ConfigureAwait(false);
        await StartScreencastAsync(width, height).ConfigureAwait(false);
    }

    /// <summary>PNG rather than the protocol's default JPEG: a design overlay needs the page's own alpha, and JPEG has none</summary>
    private Task StartScreencastAsync(int width, int height) =>
        CommandAsync("Page.startScreencast", new
        {
            format        = "png",
            maxWidth      = width,
            maxHeight     = height,
            everyNthFrame = 1,
        });

    public Task SetTransparentAsync(bool transparent) => transparent
        ? SendAsync("Emulation.setDefaultBackgroundColorOverride",
                    new { color = new { r = 0, g = 0, b = 0, a = 0 } })
        // omitting the colour restores whatever the page itself asks for
        : SendAsync("Emulation.setDefaultBackgroundColorOverride");

    // ---- input -------------------------------------------------------------

    /// <summary>CDP modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8</summary>
    public Task MouseAsync(string type, double x, double y, string button, int clickCount, int modifiers) =>
        SendAsync("Input.dispatchMouseEvent", new
        {
            type,
            x,
            y,
            button,
            clickCount,
            modifiers,
            buttons = button switch { "left" => 1, "right" => 2, "middle" => 4, _ => 0 },
        });

    public Task WheelAsync(double x, double y, double deltaX, double deltaY, int modifiers) =>
        SendAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseWheel",
            x,
            y,
            deltaX,
            deltaY,
            modifiers,
            button = "none",
        });

    public Task KeyAsync(string type, string? text, string key, string code, int windowsVirtualKeyCode,
                         int modifiers) =>
        SendAsync("Input.dispatchKeyEvent", new
        {
            type,
            text                  = text ?? "",
            unmodifiedText        = text ?? "",
            key,
            code,
            windowsVirtualKeyCode,
            nativeVirtualKeyCode  = windowsVirtualKeyCode,
            modifiers,
        });

    /// <summary>tears the browser down without awaiting anything; used when the editor is closing, where a fire-and-forget disposal would race the process exit and leave an orphaned headless browser behind</summary>
    public void Kill()
    {
        if (_disposed) return;
        _disposed = true;

        _life.Cancel();
        try { _socket.Abort(); }  catch { }
        try { _socket.Dispose(); } catch { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true); } catch { }

        _life.Dispose();
        _sendLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await SendAsync("Page.stopScreencast").ConfigureAwait(false); } catch { }

        _life.Cancel();

        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                             .ConfigureAwait(false);
        }
        catch { }

        try { _socket.Dispose(); } catch { }
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { _process.Dispose(); } catch { }
        try { if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true); } catch { }

        _life.Dispose();
        _sendLock.Dispose();
    }
}
