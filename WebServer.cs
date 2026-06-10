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
        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new InvalidOperationException(BuildLowPortPermissionMessage("HTTP", _port), ex);
        }

        Console.WriteLine($"C# webserver listening on http://localhost:{_port}");
        Console.WriteLine($"Serving content from: {_contentPath}");

        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private static string BuildLowPortPermissionMessage(string protocol, int port)
    {
        return
            $"{protocol} bind failed on port {port}. Linux blocks ports below 1024 for non-root users.\n" +
            "Options:\n" +
            "1) Use non-privileged ports (for example 8080/8443), or\n" +
            "2) Grant bind capability to the app binary:\n" +
            "   sudo setcap 'cap_net_bind_service=+ep' /path/to/your/published/binary\n" +
            "3) Or run behind a reverse proxy (nginx/caddy) on 80/443.";
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            if (!IsValidHttpRequestLine(requestLine))
            {
                return;
            }

            var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = requestParts.ElementAtOrDefault(0) ?? "GET";
            var path = requestParts.ElementAtOrDefault(1) ?? "/";

            var headerLines = new List<string>();
            string? headerLine;
            do
            {
                headerLine = await reader.ReadLineAsync();
                if (!string.IsNullOrEmpty(headerLine))
                {
                    headerLines.Add(headerLine);
                }
            } while (!string.IsNullOrEmpty(headerLine));

            var headers = HttpParsing.ParseHeaders(headerLines);
            headers.TryGetValue("Host", out var hostHeader);

            var body = string.Empty;
            if (headers.TryGetValue("Content-Length", out var contentLengthHeader)
                && int.TryParse(contentLengthHeader, out var contentLength)
                && contentLength > 0)
            {
                var buffer = new char[contentLength];
                var totalRead = 0;
                while (totalRead < contentLength)
                {
                    var read = await reader.ReadAsync(buffer, totalRead, contentLength - totalRead);
                    if (read <= 0)
                    {
                        break;
                    }

                    totalRead += read;
                }

                body = new string(buffer, 0, totalRead);
            }

            if (_redirectToHttps)
            {
                await WriteRedirectResponseAsync(stream, hostHeader, path);
                return;
            }

            var request = HttpParsing.BuildRequest(method, path, body, headers);

            var (statusLine, bodyBytes, contentType, setCookieHeaders) = request.Path switch
            {
                "/health" => (
                    "HTTP/1.1 200 OK",
                    Encoding.UTF8.GetBytes("healthy"),
                    "text/plain",
                    Array.Empty<string>()
                ),
                _ => await ContentService.ServePathAsync(request, _contentPath)
            };

            var cookiesHeaderText = string.Join("", setCookieHeaders.Select(x => $"Set-Cookie: {x}\r\n"));

            var responseHeaders =
                $"{statusLine}\r\n" +
                $"Date: {DateTime.UtcNow:R}\r\n" +
                "Server: CSharpTcpServer\r\n" +
                $"Content-Type: {contentType}; charset=utf-8\r\n" +
                cookiesHeaderText +
                $"Content-Length: {bodyBytes.Length}\r\n" +
                "Connection: close\r\n\r\n";

            var headerBytes = Encoding.ASCII.GetBytes(responseHeaders);
            await stream.WriteAsync(headerBytes);
            await stream.WriteAsync(bodyBytes);
            await stream.FlushAsync();
        }
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            // Browser closed the connection before the response finished writing.
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

    private static bool IsClientDisconnect(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            var message = ioEx.Message;
            if (message.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
                || message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("transport connection", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (ex is SocketException socketEx
            && (socketEx.SocketErrorCode == SocketError.ConnectionReset
                || socketEx.SocketErrorCode == SocketError.ConnectionAborted
                || socketEx.SocketErrorCode == SocketError.Shutdown))
        {
            return true;
        }

        return ex.InnerException is not null && IsClientDisconnect(ex.InnerException);
    }
}
