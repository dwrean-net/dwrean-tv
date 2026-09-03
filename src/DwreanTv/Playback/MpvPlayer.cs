using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace DwreanTv.Playback;

internal sealed class MpvPlayer : IDisposable
{
    private readonly Control _videoHost;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    private Process? _process;
    private string? _pipeName;
    private bool _muted;
    private bool _paused;
    private int _volume;
    private long _nextRequestId;
    private bool _disposed;

    public MpvPlayer(Control videoHost, int initialVolume)
    {
        _videoHost = videoHost;
        _volume = Math.Clamp(initialVolume, 0, 100);
    }

    public bool IsPaused => _paused;
    public bool IsMuted => _muted;

    public async Task<bool> PlayAsync(string url, CancellationToken cancellationToken = default)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (!EnsureStarted())
            {
                return false;
            }

            if (!await PrepareForNextStreamAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestartProcess();
                if (!EnsureStarted())
                {
                    return false;
                }
            }

            if (!await LoadAndConfirmAsync(url, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestartProcess();
                if (!EnsureStarted() || !await LoadAndConfirmAsync(url, cancellationToken))
                {
                    return false;
                }
            }

            _paused = false;
            await SendCommandAsync(new object[] { "set_property", "pause", false }, 500, cancellationToken, allowFailure: true);
            await SendCommandAsync(new object[] { "set_property", "volume", _volume }, 500, cancellationToken, allowFailure: true);
            await SendCommandAsync(new object[] { "set_property", "mute", _muted }, 500, cancellationToken, allowFailure: true);
            return true;
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public Task TogglePauseAsync() => RunSimpleCommandAsync(async () =>
    {
        _paused = !_paused;
        await SendCommandAsync(new object[] { "set_property", "pause", _paused }, 500, CancellationToken.None, allowFailure: true);
    });

    public Task ToggleMuteAsync() => RunSimpleCommandAsync(async () =>
    {
        _muted = !_muted;
        await SendCommandAsync(new object[] { "set_property", "mute", _muted }, 500, CancellationToken.None, allowFailure: true);
    });

    public Task SetVolumeAsync(int volume) => RunSimpleCommandAsync(async () =>
    {
        _volume = Math.Clamp(volume, 0, 100);
        await SendCommandAsync(new object[] { "set_property", "volume", _volume }, 500, CancellationToken.None, allowFailure: true);
    });

    private async Task RunSimpleCommandAsync(Func<Task> command)
    {
        await _commandGate.WaitAsync();
        try
        {
            if (_disposed || !EnsureStarted())
            {
                return;
            }

            await command();
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async Task<bool> PrepareForNextStreamAsync(CancellationToken cancellationToken)
    {
        var idle = await GetBooleanPropertyAsync("idle-active", 400, cancellationToken);
        if (idle is true)
        {
            return true;
        }

        await SendCommandAsync(new object[] { "stop" }, 700, cancellationToken, allowFailure: true);

        var deadline = Environment.TickCount64 + 1200;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            idle = await GetBooleanPropertyAsync("idle-active", 300, cancellationToken);
            if (idle is true)
            {
                return true;
            }

            await Task.Delay(60, cancellationToken);
        }

        return false;
    }

    private async Task<bool> LoadAndConfirmAsync(string url, CancellationToken cancellationToken)
    {
        if (!await SendCommandAsync(new object[] { "loadfile", url, "replace" }, 1200, cancellationToken))
        {
            return false;
        }

        // mpv returns from loadfile before the old file is fully stopped and before
        // the replacement actually starts loading. Wait until the player leaves idle.
        var deadline = Environment.TickCount64 + 1800;
        while (Environment.TickCount64 < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var idle = await GetBooleanPropertyAsync("idle-active", 300, cancellationToken);
            if (idle is false)
            {
                return true;
            }

            await Task.Delay(60, cancellationToken);
        }

        return false;
    }

    private bool EnsureStarted()
    {
        if (_disposed)
        {
            return false;
        }

        if (_process is not null && !_process.HasExited)
        {
            return true;
        }

        StopProcess();

        var mpvPath = Path.Combine(AppContext.BaseDirectory, "player", "mpv.exe");
        if (!File.Exists(mpvPath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "data"));
        var logPath = Path.Combine(AppContext.BaseDirectory, "data", "mpv.log");
        _pipeName = $"dwrean-tv-mpv-{Guid.NewGuid():N}";
        var pipePath = $@"\\.\pipe\{_pipeName}";

        var psi = new ProcessStartInfo
        {
            FileName = mpvPath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add($"--wid={_videoHost.Handle.ToInt64()}");
        psi.ArgumentList.Add($"--input-ipc-server={pipePath}");
        psi.ArgumentList.Add("--no-config");
        psi.ArgumentList.Add("--no-terminal");
        psi.ArgumentList.Add("--osc=no");
        psi.ArgumentList.Add("--input-default-bindings=no");
        psi.ArgumentList.Add("--idle=yes");
        psi.ArgumentList.Add("--force-window=no");
        psi.ArgumentList.Add("--keep-open=no");
        psi.ArgumentList.Add("--hwdec=auto-safe");
        psi.ArgumentList.Add("--cache=yes");
        psi.ArgumentList.Add("--demuxer-max-bytes=16MiB");
        psi.ArgumentList.Add("--demuxer-readahead-secs=3");
        psi.ArgumentList.Add($"--volume={_volume}");
        psi.ArgumentList.Add($"--log-file={logPath}");

        try
        {
            _process = Process.Start(psi);
            return _process is not null;
        }
        catch
        {
            _process = null;
            _pipeName = null;
            return false;
        }
    }

    private async Task<bool?> GetBooleanPropertyAsync(
        string propertyName,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(
            new object[] { "get_property", propertyName },
            timeoutMs,
            cancellationToken);

        if (!response.Success || response.Data is not JsonElement data)
        {
            return null;
        }

        return data.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private async Task<bool> SendCommandAsync(
        object[] command,
        int timeoutMs,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        var response = await SendRequestAsync(command, timeoutMs, cancellationToken);
        return response.Success || allowFailure;
    }

    private async Task<IpcResponse> SendRequestAsync(
        object[] command,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        var pipeName = _pipeName;
        var process = _process;
        if (string.IsNullOrWhiteSpace(pipeName) || process is null || process.HasExited)
        {
            return IpcResponse.Failed;
        }

        var requestId = Interlocked.Increment(ref _nextRequestId);

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);
            await pipe.ConnectAsync(timeout.Token);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), leaveOpen: true);

            var payload = JsonSerializer.Serialize(new { command, request_id = requestId });
            await writer.WriteLineAsync(payload.AsMemory(), timeout.Token);

            while (true)
            {
                var responseLine = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrWhiteSpace(responseLine))
                {
                    return IpcResponse.Failed;
                }

                using var response = JsonDocument.Parse(responseLine);
                var root = response.RootElement;

                // IPC connections can also receive event messages. Ignore them until
                // the response carrying our request_id arrives.
                if (!root.TryGetProperty("request_id", out var responseId) ||
                    responseId.ValueKind != JsonValueKind.Number ||
                    responseId.GetInt64() != requestId)
                {
                    continue;
                }

                if (!root.TryGetProperty("error", out var error) ||
                    !string.Equals(error.GetString(), "success", StringComparison.OrdinalIgnoreCase))
                {
                    return IpcResponse.Failed;
                }

                JsonElement? data = root.TryGetProperty("data", out var dataElement)
                    ? dataElement.Clone()
                    : null;

                return new IpcResponse(true, data);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer channel selection cancelled this request. Let the cancellation
            // propagate so the obsolete operation cannot restart or modify the player.
            throw;
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Failed;
        }
        catch
        {
            return IpcResponse.Failed;
        }
    }

    private void RestartProcess() => StopProcess();

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        _pipeName = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(700);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopProcess();
        _commandGate.Dispose();
    }

    private readonly record struct IpcResponse(bool Success, JsonElement? Data)
    {
        public static IpcResponse Failed => new(false, null);
    }
}
