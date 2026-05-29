using System.Net;
using System.Net.Sockets;
using System.Text;

var port = 8080;
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();

Console.WriteLine($"C# webserver listening on http://localhost:{port}");

while (true)
{
    using var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClientAsync(client));
}

static async Task HandleClientAsync(TcpClient client)
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

        string? headerLine;
        do
        {
            headerLine = await reader.ReadLineAsync();
        } while (!string.IsNullOrEmpty(headerLine));

        var path = requestLine.Split(' ').ElementAtOrDefault(1) ?? "/";

        var (statusLine, body, contentType) = path switch
        {
            "/" => (
                "HTTP/1.1 200 OK",
                "{\"name\":\"C-Webserver\",\"status\":\"running\"}",
                "application/json"
            ),
            "/health" => (
                "HTTP/1.1 200 OK",
                "healthy",
                "text/plain"
            ),
            _ => (
                "HTTP/1.1 404 Not Found",
                "Not Found",
                "text/plain"
            )
        };

        var bodyBytes = Encoding.UTF8.GetBytes(body);
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
    catch
    {
    }
    finally
    {
        client.Close();
    }
}
