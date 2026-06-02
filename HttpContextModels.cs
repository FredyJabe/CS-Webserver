using System.Text;

public sealed class HttpRequestContext
{
    public required string Method { get; init; }
    public required string RawPath { get; init; }
    public required string Path { get; init; }
    public required string QueryString { get; init; }
    public required string Body { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required IReadOnlyDictionary<string, string> Cookies { get; init; }
    public required IReadOnlyDictionary<string, string> Query { get; init; }
    public required IReadOnlyDictionary<string, string> Form { get; init; }
}

public sealed class HttpResponseContext
{
    private readonly List<string> _setCookieHeaders = [];

    public IReadOnlyList<string> SetCookieHeaders => _setCookieHeaders;

    public void SetCookie(
        string name,
        string value,
        string path = "/",
        bool httpOnly = true,
        int? maxAgeSeconds = null,
        bool secure = false,
        string sameSite = "Lax")
    {
        var encodedName = Uri.EscapeDataString(name);
        var encodedValue = Uri.EscapeDataString(value);
        var cookie = new StringBuilder($"{encodedName}={encodedValue}; Path={path}; SameSite={sameSite}");
        if (httpOnly)
        {
            cookie.Append("; HttpOnly");
        }

        if (secure)
        {
            cookie.Append("; Secure");
        }

        if (maxAgeSeconds.HasValue)
        {
            cookie.Append($"; Max-Age={maxAgeSeconds.Value}");
        }

        _setCookieHeaders.Add(cookie.ToString());
    }
}

public static class HttpParsing
{
    public static HttpRequestContext BuildRequest(
        string method,
        string rawPath,
        string body,
        IReadOnlyDictionary<string, string> headers)
    {
        var pathWithoutQuery = rawPath.Split('?', 2)[0];
        var queryString = rawPath.Contains('?') ? rawPath.Split('?', 2)[1] : string.Empty;
        var query = ParseFormLike(queryString);
        var form = ParseFormLike(body);
        var cookies = ParseCookies(headers.TryGetValue("Cookie", out var cookieHeader) ? cookieHeader : string.Empty);

        return new HttpRequestContext
        {
            Method = method,
            RawPath = rawPath,
            Path = pathWithoutQuery,
            QueryString = queryString,
            Body = body,
            Headers = headers,
            Cookies = cookies,
            Query = query,
            Form = form
        };
    }

    public static Dictionary<string, string> ParseHeaders(IEnumerable<string> headerLines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in headerLines)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            headers[key] = value;
        }

        return headers;
    }

    private static Dictionary<string, string> ParseCookies(string cookieHeader)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return cookies;
        }

        var parts = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            var key = SafeDecode(kv[0].Trim().Replace('+', ' '));
            var value = kv.Length > 1 ? SafeDecode(kv[1].Trim().Replace('+', ' ')) : string.Empty;
            cookies[key] = value;
        }

        return cookies;
    }

    private static Dictionary<string, string> ParseFormLike(string input)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(input))
        {
            return values;
        }

        var parts = input.Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            var key = SafeDecode(kv[0].Replace('+', ' '));
            var value = kv.Length > 1 ? SafeDecode(kv[1].Replace('+', ' ')) : string.Empty;
            values[key] = value;
        }

        return values;
    }

    private static string SafeDecode(string input)
    {
        try
        {
            return Uri.UnescapeDataString(input);
        }
        catch
        {
            return input;
        }
    }
}
