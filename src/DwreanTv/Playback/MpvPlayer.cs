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

            await SendCommandAsync(new object[] { "stop" }, 700, cancellationToken, allowFailure: true);
            await Task.Delay(60, cancellationToken);

            if (!await SendCommandAsync(new object[] { "loadfile", url, "replace" }, 1200, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                RestartProcess();
                if (!EnsureStarted())
                {
                    return false;
                }

                if (!await SendCommandAsync(new object[] { "loadfile", url, "replace" }, 1600, cancellationToken))
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

    public async Task TogglePauseAsync()
    {
        _paused = !_paused;
        await SendSimpleCommandAsync(new object[] { "set_property", "pause", _paused });
    }

    public async Task ToggleMuteAsync()
    {
        _muted = !_muted;
        await SendSimpleCommandAsync(new object[] { "set_property", "mute", _muted });
    }

    public async Task SetVolumeAsync(int volume)
    {
        _volume = Math.Clamp(volume, 0, 100);
        await SendSimpleCommandAsync(new object[] { "set_property", "volume", _volume });
    }

    private async Task SendSimpleCommandAsync(object[] command)
    {
        if (_disposed || !EnsureStarted())
        {
            return;
        }

        await SendCommandAsync(command, 500, CancellationToken.None, allowFailure: true);
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

    private async Task<bool> SendCommandAsync(
        object[] command,
        int timeoutMs,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        var pipeName = _pipeName;
        var process = _process;
        if (string.IsNullOrWhiteSpace(pipeName) || process is null || process.HasExited)
        {
            return allowFailure;
        }

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

            var payload = JsonSerializer.Serialize(new { command });
            await writer.WriteLineAsync(payload.AsMemory(), timeout.Token);

            var responseLine = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(responseLine))
            {
                return allowFailure;
            }

            using var response = JsonDocument.Parse(responseLine);
            if (response.RootElement.TryGetProperty("error", out var error))
            {
                return string.Equals(error.GetString(), "success", StringComparison.OrdinalIgnoreCase) || allowFailure;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return allowFailure;
        }
        catch
        {
            return allowFailure;
        }
    }

    private void RestartProcess()
    {
        StopProcess();
    }

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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

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
}
