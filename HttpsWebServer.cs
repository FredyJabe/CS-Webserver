using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public sealed class HttpsWebServer(int port, string contentPath, X509Certificate2 certificate)
{
    private readonly int _port = port;
    private readonly string _contentPath = contentPath;
    private readonly X509Certificate2 _certificate = certificate;

    public async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Any, _port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new InvalidOperationException(BuildLowPortPermissionMessage("HTTPS", _port), ex);
        }

        Console.WriteLine($"C# webserver listening on https://localhost:{_port}");
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
            using var networkStream = client.GetStream();
            using var sslStream = new SslStream(networkStream, false);

            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false
            });

            using var reader = new StreamReader(sslStream, Encoding.UTF8, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
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
            await sslStream.WriteAsync(headerBytes);
            await sslStream.WriteAsync(bodyBytes);
            await sslStream.FlushAsync();
        }
        catch (Exception ex) when (IsClientDisconnect(ex))
        {
            // Browser closed the connection before the response finished writing.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTPS request handling error: {ex.Message}");
        }
        finally
        {
            client.Close();
        }
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
