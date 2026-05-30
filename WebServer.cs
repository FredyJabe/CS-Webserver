using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

public sealed class WebServer(int port, string contentPath, bool redirectToHttps = false, int httpsPort = 443)
{
    private readonly int _port = port;
    private readonly string _contentPath = contentPath;
    private readonly bool _redirectToHttps = redirectToHttps;
    private readonly int _httpsPort = httpsPort;

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        listener.Start();

        Console.WriteLine($"C# webserver listening on http://localhost:{_port}");
        Console.WriteLine($"Serving content from: {_contentPath}");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            if (!IsValidHttpRequestLine(requestLine))
            {
                return;
            }

            string? headerLine;
            string? hostHeader = null;
            do
            {
                headerLine = await reader.ReadLineAsync();
                if (!string.IsNullOrWhiteSpace(headerLine) && headerLine.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    hostHeader = headerLine[5..].Trim();
                }
            } while (!string.IsNullOrEmpty(headerLine));

            var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
            if (_redirectToHttps)
            {
                await WriteRedirectResponseAsync(stream, hostHeader, path);
                return;
            }

            var pathWithoutQuery = path.Split('?', 2)[0];

            var (statusLine, bodyBytes, contentType) = pathWithoutQuery switch
            {
                "/health" => (
                    "HTTP/1.1 200 OK",
                    Encoding.UTF8.GetBytes("healthy"),
                    "text/plain"
                ),
                _ => await ContentService.ServePathAsync(pathWithoutQuery, _contentPath)
            };

            var responseHeaders =
                $"{statusLine}\r\n" +
                $"Date: {DateTime.UtcNow:R}\r\n" +
                "Server: CSharpTcpServer\r\n" +
                $"Content-Type: {contentType}; charset=utf-8\r\n" +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(responseHeaders);
            await stream.WriteAsync(headerBytes);
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Request handling error: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
    }

    private async Task WriteRedirectResponseAsync(Stream stream, string? hostHeader, string pathAndQuery)
    {
        var location = BuildHttpsLocation(hostHeader, pathAndQuery);
        var responseHeaders =
            "HTTP/1.1 308 Permanent Redirect\r\n" +
            $"Date: {DateTime.UtcNow:R}\r\n" +
            "Server: CSharpTcpServer\r\n" +
            $"Location: {location}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n";

        var headerBytes = Encoding.ASCII.GetBytes(responseHeaders);
        await stream.WriteAsync(headerBytes);
        await stream.FlushAsync();
    }

    private string BuildHttpsLocation(string? hostHeader, string pathAndQuery)
    {
        var hostOnly = (hostHeader ?? "localhost").Trim();
        var colonIndex = hostOnly.LastIndexOf(':');
        if (colonIndex > -1)
        {
            hostOnly = hostOnly[..colonIndex];
        }

        var portSegment = _httpsPort == 443 ? "" : $":{_httpsPort}";
        var normalizedPath = string.IsNullOrWhiteSpace(pathAndQuery) ? "/" : pathAndQuery;
        return $"https://{hostOnly}{portSegment}{normalizedPath}";
    }

    private static bool IsValidHttpRequestLine(string requestLine)
    {
        return Regex.IsMatch(requestLine, @"^[A-Z]+\s+\S+\s+HTTP/\d\.\d$");
    }
}
