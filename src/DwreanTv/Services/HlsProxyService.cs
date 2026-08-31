using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace DwreanTv.Services;

public sealed class HlsProxyService : IDisposable
{
    private static readonly Regex UriAttributeRegex = new(
        "URI=\\\"(?<uri>[^\\\"]+)\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _httpClient;
    private readonly Task _acceptLoop;

    public HlsProxyService()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public string BuildProxyUrl(string remoteUrl, string referrer = "")
    {
        return $"http://127.0.0.1:{Port}/hls?u={Encode(remoteUrl)}&r={Encode(referrer)}";
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleClientAsync(client), _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 4096,
                    leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return;
                }

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !parts[0].Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(stream, 405, "Method Not Allowed");
                    return;
                }

                while (true)
                {
                    var header = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(header))
                    {
                        break;
                    }
                }

                var localUri = new Uri($"http://127.0.0.1:{Port}{parts[1]}");
                var query = ParseQuery(localUri.Query);
                if (!query.TryGetValue("u", out var encodedUrl))
                {
                    await WriteErrorAsync(stream, 400, "Missing URL");
                    return;
                }

                var remoteUrl = Decode(encodedUrl);
                var referrer = query.TryGetValue("r", out var encodedReferrer)
                    ? Decode(encodedReferrer)
                    : string.Empty;

                if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var remoteUri) ||
                    remoteUri.Scheme is not ("http" or "https"))
                {
                    await WriteErrorAsync(stream, 400, "Invalid URL");
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, remoteUri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/152 Safari/537.36");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                if (Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri))
                {
                    request.Headers.Referrer = referrerUri;
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    _cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    await WriteErrorAsync(stream, 502, $"Upstream {(int)response.StatusCode}");
                    return;
                }

                var data = await response.Content.ReadAsByteArrayAsync(_cts.Token);
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

                if (LooksLikePlaylist(remoteUri, contentType, data))
                {
                    var text = Encoding.UTF8.GetString(data);
                    text = RewritePlaylist(text, remoteUri, referrer);
                    data = Encoding.UTF8.GetBytes(text);
                    contentType = "application/vnd.apple.mpegurl";
                }

                await WriteResponseAsync(stream, data, contentType);
            }
            catch (OperationCanceledException)
            {
                // Application is closing or the upstream request timed out.
            }
            catch
            {
                try
                {
                    if (client.Connected)
                    {
                        await WriteErrorAsync(client.GetStream(), 502, "Proxy Error");
                    }
                }
                catch
                {
                    // Nothing else to do for a closed client connection.
                }
            }
        }
    }

    private string RewritePlaylist(string playlist, Uri baseUri, string referrer)
    {
        var lines = playlist.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                lines[i] = UriAttributeRegex.Replace(lines[i], match =>
                {
                    var child = match.Groups["uri"].Value;
                    var absolute = Resolve(baseUri, child);
                    return $"URI=\"{BuildProxyUrl(absolute, referrer)}\"";
                });
                continue;
            }

            lines[i] = BuildProxyUrl(Resolve(baseUri, line), referrer);
        }

        return string.Join("\n", lines);
    }

    private static string Resolve(Uri baseUri, string child)
    {
        return Uri.TryCreate(child, UriKind.Absolute, out var absolute)
            ? absolute.AbsoluteUri
            : new Uri(baseUri, child).AbsoluteUri;
    }

    private static bool LooksLikePlaylist(Uri uri, string contentType, byte[] data)
    {
        if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return data.Length >= 7 && Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 16))
            .StartsWith("#EXTM3U", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var trimmed = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return result;
        }

        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            result[pair[..separator]] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return result;
    }

    private static string Encode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
    }

    private static async Task WriteResponseAsync(NetworkStream stream, byte[] body, string contentType)
    {
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    private static async Task WriteErrorAsync(NetworkStream stream, int statusCode, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {statusCode} {message}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");

        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _httpClient.Dispose();
        _cts.Dispose();
    }
}
