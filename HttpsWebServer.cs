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
        listener.Start();

        Console.WriteLine($"C# webserver listening on https://localhost:{_port}");
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
            using var networkStream = client.GetStream();
            using var sslStream = new SslStream(networkStream, false);

            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false
            });

            using var reader = new StreamReader(sslStream, Encoding.ASCII, leaveOpen: true);

            var requestLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                return;
            }

            string? headerLine;
            do
            {
                headerLine = await reader.ReadLineAsync();
            } while (!string.IsNullOrEmpty(headerLine));

            var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";
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
            await sslStream.WriteAsync(headerBytes);
            await sslStream.WriteAsync(bodyBytes);
            await sslStream.FlushAsync();
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
}
