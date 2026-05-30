public sealed record ServerConfig(int Port, string ContentPath, HttpsConfig Https);

public sealed record HttpsConfig(bool Enabled, int Port, string CertificatePath, string CertificatePassword);

public static class ConfigLoader
{
    public static ServerConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            CreateDefaultConfig(configPath);
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadAllLines(configPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        if (!values.TryGetValue("port", out var portRaw) || !int.TryParse(portRaw, out var port))
        {
            throw new InvalidOperationException("config.yaml must contain a valid integer `port`.");
        }

        if (!values.TryGetValue("contentPath", out var contentPathRaw) || string.IsNullOrWhiteSpace(contentPathRaw))
        {
            throw new InvalidOperationException("config.yaml must contain `contentPath`.");
        }

        var contentPath = Path.GetFullPath(contentPathRaw);
        if (!Directory.Exists(contentPath))
        {
            throw new DirectoryNotFoundException($"Configured contentPath does not exist: {contentPath}");
        }

        var https = LoadHttpsConfig(values);
        return new ServerConfig(port, contentPath, https);
    }

    private static HttpsConfig LoadHttpsConfig(Dictionary<string, string> values)
    {
        var enabled = values.TryGetValue("httpsEnabled", out var enabledRaw) && bool.TryParse(enabledRaw, out var parsedEnabled) && parsedEnabled;
        var port = values.TryGetValue("httpsPort", out var httpsPortRaw) && int.TryParse(httpsPortRaw, out var parsedPort)
            ? parsedPort
            : 8443;
        var certPath = values.TryGetValue("httpsCertificatePath", out var certPathRaw)
            ? certPathRaw
            : string.Empty;
        var certPassword = values.TryGetValue("httpsCertificatePassword", out var certPasswordRaw)
            ? certPasswordRaw
            : string.Empty;

        if (!enabled)
        {
            return new HttpsConfig(false, port, certPath, certPassword);
        }

        if (string.IsNullOrWhiteSpace(certPath))
        {
            Console.WriteLine("Warning: HTTPS is enabled but `httpsCertificatePath` is empty. HTTPS will be disabled.");
            return new HttpsConfig(false, port, certPath, certPassword);
        }

        var fullCertPath = Path.GetFullPath(certPath);
        if (!File.Exists(fullCertPath))
        {
            Console.WriteLine($"Warning: HTTPS certificate file not found at '{fullCertPath}'. HTTPS will be disabled.");
            return new HttpsConfig(false, port, fullCertPath, certPassword);
        }

        return new HttpsConfig(true, port, fullCertPath, certPassword);
    }

    private static void CreateDefaultConfig(string configPath)
    {
        var defaultContentPath = Path.GetFullPath("content");
        var newline = Environment.NewLine;
        var yaml =
            $"port: 8080{newline}" +
            $"contentPath: {defaultContentPath}{newline}" +
            $"httpsEnabled: false{newline}" +
            $"httpsPort: 8443{newline}" +
            $"httpsCertificatePath: ./certs/server.pfx{newline}" +
            $"httpsCertificatePassword: change-me{newline}";

        File.WriteAllText(configPath, yaml);
        Console.WriteLine($"Created missing config file at: {Path.GetFullPath(configPath)}");
    }
}
