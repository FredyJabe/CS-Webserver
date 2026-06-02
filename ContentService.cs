using System.Text;

public static class ContentService
{
    public static async Task<(string StatusLine, byte[] BodyBytes, string ContentType, IReadOnlyList<string> SetCookieHeaders)> ServePathAsync(
        HttpRequestContext request,
        string contentPath)
    {
        var response = new HttpResponseContext();
        var requestPath = request.Path;
        var relativePath = requestPath == "/" ? "index.html" : requestPath.TrimStart('/');
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var pathSegments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Any(segment => segment.Equals("hidden", StringComparison.OrdinalIgnoreCase)))
        {
            return ("HTTP/1.1 404 Not Found", Encoding.UTF8.GetBytes("Not Found"), "text/plain", response.SetCookieHeaders);
        }

        var fullPath = Path.GetFullPath(Path.Combine(contentPath, relativePath));
        var contentRoot = Path.GetFullPath(contentPath);

        if (!fullPath.StartsWith(contentRoot, StringComparison.Ordinal))
        {
            return ("HTTP/1.1 403 Forbidden", Encoding.UTF8.GetBytes("Forbidden"), "text/plain", response.SetCookieHeaders);
        }

        if (!File.Exists(fullPath))
        {
            return ("HTTP/1.1 404 Not Found", Encoding.UTF8.GetBytes("Not Found"), "text/plain", response.SetCookieHeaders);
        }

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var markdown = File.ReadAllText(fullPath);
            markdown = await CsTagsParser.RenderInTextAsync(markdown, fullPath, contentPath, request, response);
            var html = MarkdownParser.BuildHtml(markdown, Path.GetFileName(fullPath));
            return ("HTTP/1.1 200 OK", Encoding.UTF8.GetBytes(html), "text/html", response.SetCookieHeaders);
        }

        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(fullPath);
            html = await CsTagsParser.RenderInHtmlAsync(html, fullPath, contentPath, request, response);
            return ("HTTP/1.1 200 OK", Encoding.UTF8.GetBytes(html), GetContentType(fullPath), response.SetCookieHeaders);
        }

        return ("HTTP/1.1 200 OK", File.ReadAllBytes(fullPath), GetContentType(fullPath), response.SetCookieHeaders);
    }

    public static string GetContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" => "text/html",
            ".md" => "text/markdown",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".txt" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }
}
