public sealed record ServerConfig(int Port, string ContentPath, HttpsConfig Https);
public sealed record HttpsConfig(bool Enabled, int Port, CertificateConfig Certificate);
public sealed record CertificateConfig(string Path, string Password, PemCertificateConfig Pem);
public sealed record PemCertificateConfig(bool UsePem, string CertPath, string KeyPath, string KeyPassword);

public static class ConfigLoader
{
    public static ServerConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
        {
            CreateDefaultConfig(configPath);
        }

        var values = ParseYamlLikeValues(configPath);

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
        var enabled = ReadBool(values, "https.enabled")
            ?? ReadBool(values, "httpsEnabled")
            ?? false;
        var port = TryReadInt(values, "https.port")
            ?? TryReadInt(values, "httpsPort")
            ?? 8443;

        var certPath = ReadString(values, "https.certificate.path")
            ?? ReadString(values, "httpsCertificatePath")
            ?? string.Empty;
        var certPassword = ReadString(values, "https.certificate.pass")
            ?? ReadString(values, "httpsCertificatePassword")
            ?? string.Empty;

        var usePem = ReadBool(values, "https.certificate.pem.usePem") ?? false;
        var pemCertPath = ReadString(values, "https.certificate.pem.certPath") ?? string.Empty;
        var pemKeyPath = ReadString(values, "https.certificate.pem.keyPath") ?? string.Empty;
        var pemKeyPassword = ReadString(values, "https.certificate.pem.keyPass") ?? string.Empty;

        var certificate = new CertificateConfig(
            Path.GetFullPath(certPath),
            certPassword,
            new PemCertificateConfig(
                usePem,
                Path.GetFullPath(pemCertPath),
                Path.GetFullPath(pemKeyPath),
                pemKeyPassword
            )
        );

        if (!enabled)
        {
            return new HttpsConfig(false, port, certificate);
        }

        if (usePem)
        {
            if (string.IsNullOrWhiteSpace(pemCertPath) || string.IsNullOrWhiteSpace(pemKeyPath))
            {
                Console.WriteLine("Warning: HTTPS PEM mode is enabled but `https.certificate.pem.certPath` or `https.certificate.pem.keyPath` is empty. HTTPS will be disabled.");
                return new HttpsConfig(false, port, certificate);
            }

            if (!File.Exists(certificate.Pem.CertPath))
            {
                Console.WriteLine($"Warning: HTTPS PEM certificate file not found at '{certificate.Pem.CertPath}'. HTTPS will be disabled.");
                return new HttpsConfig(false, port, certificate);
            }

            if (!File.Exists(certificate.Pem.KeyPath))
            {
                Console.WriteLine($"Warning: HTTPS PEM key file not found at '{certificate.Pem.KeyPath}'. HTTPS will be disabled.");
                return new HttpsConfig(false, port, certificate);
            }

            return new HttpsConfig(true, port, certificate);
        }

        if (string.IsNullOrWhiteSpace(certPath))
        {
            Console.WriteLine("Warning: HTTPS is enabled but `https.certificate.path` is empty. HTTPS will be disabled.");
            return new HttpsConfig(false, port, certificate);
        }

        if (!File.Exists(certificate.Path))
        {
            Console.WriteLine($"Warning: HTTPS certificate file not found at '{certificate.Path}'. HTTPS will be disabled.");
            return new HttpsConfig(false, port, certificate);
        }

        return new HttpsConfig(true, port, certificate);
    }

    private static Dictionary<string, string> ParseYamlLikeValues(string configPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keyStack = new List<(int indent, string key)>();

        foreach (var rawLine in File.ReadAllLines(configPath))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var trimmed = rawLine.Trim();
            if (trimmed.StartsWith('#'))
            {
                continue;
            }

            var indent = CountLeadingSpaces(rawLine);
            var separatorIndex = trimmed.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();

            while (keyStack.Count > 0 && indent <= keyStack[^1].indent)
            {
                keyStack.RemoveAt(keyStack.Count - 1);
            }

            var fullKey = keyStack.Count == 0
                ? key
                : $"{string.Join('.', keyStack.Select(k => k.key))}.{key}";

            if (string.IsNullOrWhiteSpace(value))
            {
                keyStack.Add((indent, key));
                continue;
            }

            values[fullKey] = TrimQuotes(value);
        }

        return values;
    }

    private static int CountLeadingSpaces(string value)
    {
        var count = 0;
        while (count < value.Length && value[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static string TrimQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? ReadString(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var raw) ? raw : null;
    }

    private static bool? ReadBool(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return null;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static int? TryReadInt(Dictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return null;
        }

        return int.TryParse(raw, out var parsed) ? parsed : null;
    }

    private static void CreateDefaultConfig(string configPath)
    {
        var defaultContentPath = Path.GetFullPath("content");
        var newline = Environment.NewLine;
        var yaml =
            $"port: 8080{newline}" +
            $"contentPath: {defaultContentPath}{newline}" +
            $"https:{newline}" +
            $"  enabled: false{newline}" +
            $"  port: 8443{newline}" +
            $"  certificate:{newline}" +
            $"    path: ./certs/server.pfx{newline}" +
            $"    pass: change-me{newline}" +
            $"    pem:{newline}" +
            $"      usePem: false{newline}" +
            $"      certPath: ./certs/server.crt{newline}" +
            $"      keyPath: ./certs/server.key{newline}" +
            $"      keyPass: \"\"{newline}";

        File.WriteAllText(configPath, yaml);
        Console.WriteLine($"Created missing config file at: {Path.GetFullPath(configPath)}");
    }
}
