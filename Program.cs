using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Text;
using HtmlAgilityPack;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

var config = LoadConfig("config.yaml");
var listener = new TcpListener(IPAddress.Any, config.Port);
listener.Start();

Console.WriteLine($"C# webserver listening on http://localhost:{config.Port}");
Console.WriteLine($"Serving content from: {config.ContentPath}");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    _ = Task.Run(() => HandleClientAsync(client, config.ContentPath));
}

static async Task HandleClientAsync(TcpClient client, string contentPath)
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
        var pathWithoutQuery = path.Split('?', 2)[0];

        var (statusLine, bodyBytes, contentType) = pathWithoutQuery switch
        {
            "/health" => (
                "HTTP/1.1 200 OK",
                Encoding.UTF8.GetBytes("healthy"),
                "text/plain"
            ),
            _ => await ServeFileAsync(pathWithoutQuery, contentPath)
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

static async Task<(string StatusLine, byte[] BodyBytes, string ContentType)> ServeFileAsync(string requestPath, string contentPath)
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
        markdown = await RenderServerSideCsInTextAsync(markdown, fullPath, requestPath);
        var html = BuildMarkdownHtml(markdown, Path.GetFileName(fullPath));
        return ("HTTP/1.1 200 OK", Encoding.UTF8.GetBytes(html), "text/html");
    }

    if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
    {
        var html = File.ReadAllText(fullPath);
        html = await RenderServerSideCsInHtmlAsync(html, fullPath, requestPath);
        return ("HTTP/1.1 200 OK", Encoding.UTF8.GetBytes(html), GetContentType(fullPath));
    }

    return ("HTTP/1.1 200 OK", File.ReadAllBytes(fullPath), GetContentType(fullPath));
}

static ScriptOptions BuildScriptOptions()
{
    return ScriptOptions.Default
        .AddReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(HtmlDocument).Assembly
        )
        .AddImports("System", "System.Linq", "System.Collections.Generic", "HtmlAgilityPack");
}

static async Task<string> RenderServerSideCsInTextAsync(string content, string filePath, string requestPath)
{
    var matches = Regex.Matches(content, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
    if (matches.Count == 0)
    {
        return content;
    }

    var rendered = new StringBuilder(content.Length);
    var cursor = 0;
    var scriptOptions = BuildScriptOptions();
    var globals = new CsScriptGlobals(filePath, requestPath);

    foreach (Match match in matches)
    {
        rendered.Append(content, cursor, match.Index - cursor);
        try
        {
            var scriptBody = match.Groups[1].Value;
            var result = await CSharpScript.EvaluateAsync<object?>(scriptBody, scriptOptions, globals);
            rendered.Append(result?.ToString() ?? string.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"<cs> parser error in '{filePath}': {ex.Message}");
            rendered.Append(string.Empty);
        }

        cursor = match.Index + match.Length;
    }

    rendered.Append(content, cursor, content.Length - cursor);
    return rendered.ToString();
}

static async Task<string> RenderServerSideCsInHtmlAsync(string html, string filePath, string requestPath)
{
    var document = new HtmlDocument();
    document.LoadHtml(html);
    var csNodes = document.DocumentNode.Descendants("cs").ToList();
    if (csNodes.Count == 0)
    {
        return html;
    }

    var scriptOptions = BuildScriptOptions();

    foreach (var node in csNodes)
    {
        var globals = new CsScriptGlobals(filePath, requestPath, document);

        try
        {
            var result = await CSharpScript.EvaluateAsync<object?>(node.InnerHtml, scriptOptions, globals);
            var replacement = result?.ToString() ?? string.Empty;
            if (replacement.Length == 0)
            {
                node.Remove();
                continue;
            }

            var fragment = HtmlNode.CreateNode($"<span>{replacement}</span>");
            var inserted = fragment.ChildNodes.ToList();
            foreach (var child in inserted)
            {
                node.ParentNode.InsertBefore(child, node);
            }
            node.Remove();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"<cs> parser error in '{filePath}': {ex.Message}");
            node.Remove();
        }
    }

    return document.DocumentNode.OuterHtml;
}

static string GetContentType(string filePath)
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

static string BuildMarkdownHtml(string markdown, string title)
{
    var body = ConvertMarkdownToHtml(markdown);
    var escapedTitle = WebUtility.HtmlEncode(title);
    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>{{escapedTitle}}</title>
  <style>
    :root { color-scheme: light; }
    body { font-family: Georgia, "Times New Roman", serif; margin: 2rem auto; max-width: 900px; padding: 0 1rem; line-height: 1.6; color: #1f2937; }
    h1, h2, h3, h4, h5, h6 { line-height: 1.2; color: #111827; }
    code { background: #f3f4f6; padding: 0.1rem 0.3rem; border-radius: 4px; }
    pre { background: #111827; color: #f9fafb; padding: 1rem; border-radius: 8px; overflow-x: auto; }
    pre code { background: transparent; padding: 0; }
    table { border-collapse: collapse; width: 100%; margin: 1rem 0; }
    th, td { border: 1px solid #d1d5db; padding: 0.5rem 0.75rem; text-align: left; }
    th { background: #f9fafb; }
    blockquote { border-left: 4px solid #9ca3af; margin: 1rem 0; padding: 0.25rem 1rem; color: #374151; background: #f9fafb; }
    hr { border: 0; border-top: 1px solid #d1d5db; margin: 2rem 0; }
  </style>
  <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.10.0/styles/github-dark.min.css" />
</head>
<body>
{{body}}
<script src="https://cdnjs.cloudflare.com/ajax/libs/highlight.js/11.10.0/highlight.min.js"></script>
<script>hljs.highlightAll();</script>
</body>
</html>
""";
}

static string ConvertMarkdownToHtml(string markdown)
{
    var lines = markdown.Replace("\r\n", "\n").Split('\n');
    var html = new StringBuilder();
    var paragraph = new List<string>();
    var listType = "";
    var inCodeFence = false;
    var codeFenceLanguage = "";
    var inTable = false;

    void FlushParagraph()
    {
        if (paragraph.Count == 0)
        {
            return;
        }

        var merged = string.Join(' ', paragraph).Trim();
        html.Append("<p>").Append(ProcessInlineMarkdown(merged)).AppendLine("</p>");
        paragraph.Clear();
    }

    void CloseList()
    {
        if (listType.Length == 0)
        {
            return;
        }

        html.AppendLine(listType == "ol" ? "</ol>" : "</ul>");
        listType = "";
    }

    void CloseTable()
    {
        if (!inTable)
        {
            return;
        }

        html.AppendLine("</tbody></table>");
        inTable = false;
    }

    foreach (var raw in lines)
    {
        var line = raw.TrimEnd();
        var trimmed = line.Trim();

        if (trimmed.StartsWith("```"))
        {
            FlushParagraph();
            CloseList();
            CloseTable();
            if (!inCodeFence)
            {
                codeFenceLanguage = trimmed[3..].Trim();
                var languageClass = GetLanguageCssClass(codeFenceLanguage);
                var classAttr = string.IsNullOrEmpty(languageClass) ? "" : $" class=\"{languageClass}\"";
                html.Append("<pre><code").Append(classAttr).AppendLine(">");
                inCodeFence = true;
            }
            else
            {
                html.AppendLine("</code></pre>");
                inCodeFence = false;
                codeFenceLanguage = "";
            }
            continue;
        }

        if (inCodeFence)
        {
            html.AppendLine(WebUtility.HtmlEncode(line));
            continue;
        }

        if (trimmed.Length == 0)
        {
            FlushParagraph();
            CloseList();
            CloseTable();
            continue;
        }

        if (trimmed == "---")
        {
            FlushParagraph();
            CloseList();
            CloseTable();
            html.AppendLine("<hr />");
            continue;
        }

        var headingLevel = 0;
        while (headingLevel < trimmed.Length && trimmed[headingLevel] == '#')
        {
            headingLevel++;
        }

        if (headingLevel is >= 1 and <= 6 && trimmed.Length > headingLevel && trimmed[headingLevel] == ' ')
        {
            FlushParagraph();
            CloseList();
            CloseTable();
            var headingText = trimmed[(headingLevel + 1)..].Trim();
            html.Append('<').Append('h').Append(headingLevel).Append('>')
                .Append(ProcessInlineMarkdown(headingText))
                .Append("</h").Append(headingLevel).AppendLine(">");
            continue;
        }

        if (trimmed.StartsWith("> "))
        {
            FlushParagraph();
            CloseList();
            CloseTable();
            html.Append("<blockquote><p>")
                .Append(ProcessInlineMarkdown(trimmed[2..].Trim()))
                .AppendLine("</p></blockquote>");
            continue;
        }

        if (IsTableLine(trimmed))
        {
            FlushParagraph();
            CloseList();

            var cells = ParseTableCells(trimmed);
            if (!inTable)
            {
                html.AppendLine("<table><thead><tr>");
                foreach (var cell in cells)
                {
                    html.Append("<th>").Append(ProcessInlineMarkdown(cell)).AppendLine("</th>");
                }
                html.AppendLine("</tr></thead><tbody>");
                inTable = true;
            }
            else if (IsTableSeparator(trimmed))
            {
                continue;
            }
            else
            {
                html.AppendLine("<tr>");
                foreach (var cell in cells)
                {
                    html.Append("<td>").Append(ProcessInlineMarkdown(cell)).AppendLine("</td>");
                }
                html.AppendLine("</tr>");
            }

            continue;
        }

        if (trimmed.StartsWith("- "))
        {
            FlushParagraph();
            CloseTable();
            if (listType != "ul")
            {
                CloseList();
                html.AppendLine("<ul>");
                listType = "ul";
            }
            html.Append("<li>").Append(ProcessInlineMarkdown(trimmed[2..].Trim())).AppendLine("</li>");
            continue;
        }

        if (Regex.IsMatch(trimmed, @"^\d+\.\s+"))
        {
            FlushParagraph();
            CloseTable();
            if (listType != "ol")
            {
                CloseList();
                html.AppendLine("<ol>");
                listType = "ol";
            }
            var itemText = Regex.Replace(trimmed, @"^\d+\.\s+", "");
            html.Append("<li>").Append(ProcessInlineMarkdown(itemText)).AppendLine("</li>");
            continue;
        }

        CloseList();
        CloseTable();
        paragraph.Add(trimmed);
    }

    FlushParagraph();
    CloseList();
    CloseTable();
    if (inCodeFence)
    {
        html.AppendLine("</code></pre>");
    }

    return html.ToString();
}

static bool IsTableLine(string line)
{
    return line.StartsWith("|") && line.EndsWith("|");
}

static bool IsTableSeparator(string line)
{
    var stripped = line.Replace("|", "").Replace("-", "").Replace(":", "").Trim();
    return stripped.Length == 0;
}

static List<string> ParseTableCells(string line)
{
    return line.Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
}

static string ProcessInlineMarkdown(string text)
{
    var encoded = WebUtility.HtmlEncode(text);
    encoded = Regex.Replace(encoded, @"`([^`]+)`", "<code>$1</code>");
    encoded = Regex.Replace(encoded, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
    encoded = Regex.Replace(encoded, @"\*([^*]+)\*", "<em>$1</em>");
    return encoded;
}

static string GetLanguageCssClass(string markdownLanguage)
{
    if (string.IsNullOrWhiteSpace(markdownLanguage))
    {
        return "";
    }

    var normalized = markdownLanguage.Trim().ToLowerInvariant();
    normalized = Regex.Replace(normalized, @"[^a-z0-9#+-]", "");
    return normalized.Length == 0 ? "" : $"language-{normalized}";
}

static ServerConfig LoadConfig(string configPath)
{
    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException($"Missing config file: {configPath}");
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

    return new ServerConfig(port, contentPath);
}

record ServerConfig(int Port, string ContentPath);

public sealed class CsScriptGlobals(string filePath, string requestPath, HtmlDocument? document = null)
{
    public string FilePath { get; } = filePath;
    public string RequestPath { get; } = requestPath;
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
    public HtmlDocument? Document { get; } = document;
    public HtmlNode? GetById(string id) => Document?.GetElementbyId(id);
}
