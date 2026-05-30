using System.Text;

public static class ContentService
{
    public static async Task<(string StatusLine, byte[] BodyBytes, string ContentType)> ServePathAsync(string requestPath, string contentPath)
    {
        var relativePath = requestPath == "/" ? "index.html" : requestPath.TrimStart('/');
        relativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);

        var fullPath = Path.GetFullPath(Path.Combine(contentPath, relativePath));
        var contentRoot = Path.GetFullPath(contentPath);

        if (!fullPath.StartsWith(contentRoot, StringComparison.Ordinal))
        {
            return ("HTTP/1.1 403 Forbidden", Encoding.UTF8.GetBytes("Forbidden"), "text/plain");
        }

        if (!File.Exists(fullPath))
        {
            return ("HTTP/1.1 404 Not Found", Encoding.UTF8.GetBytes("Not Found"), "text/plain");
        }

        var extension = Path.GetExtension(fullPath);
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            var markdown = File.ReadAllText(fullPath);
            markdown = await CsTagsParser.RenderInTextAsync(markdown, fullPath, requestPath);
            var html = MarkdownParser.BuildHtml(markdown, Path.GetFileName(fullPath));
            return ("HTTP/1.1 200 OK", Encoding.UTF8.GetBytes(html), "text/html");
        }

        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(fullPath);
            html = await CsTagsParser.RenderInHtmlAsync(html, fullPath, requestPath);
            return ("HTTP/1.1 200 OK", Encoding.UTF8.GetBytes(html), GetContentType(fullPath));
        }

        return ("HTTP/1.1 200 OK", File.ReadAllBytes(fullPath), GetContentType(fullPath));
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
