using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Microsoft.Data.Sqlite;
using SQLitePCL;

public static class CsTagsParser
{
    public static async Task<string> RenderInTextAsync(
        string content,
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response)
    {
        var matches = Regex.Matches(content, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        if (matches.Count == 0)
        {
            return content;
        }

        var rendered = new StringBuilder(content.Length);
        var cursor = 0;
        var scriptOptions = BuildScriptOptions();
        var context = new CsScriptExecutionContext(scriptOptions);
        var globals = new CsScriptGlobals(filePath, contentRoot, request, response, context);

        foreach (Match match in matches)
        {
            rendered.Append(content, cursor, match.Index - cursor);
            try
            {
                var scriptBody = match.Groups[1].Value;
                if (TryGetHiddenIncludePath(scriptBody, out var includePath))
                {
                    await ExecuteHiddenIncludeAsync(includePath, filePath, contentRoot, request, response, context, globals);
                    rendered.Append(string.Empty);
                    cursor = match.Index + match.Length;
                    continue;
                }

                context.State = context.State is null
                    ? await CSharpScript.RunAsync<object?>(scriptBody, scriptOptions, globals: globals)
                    : await context.State.ContinueWithAsync<object?>(scriptBody, scriptOptions);
                if (globals.ExecutionContext.State is not null)
                {
                    context.State = globals.ExecutionContext.State;
                }
                rendered.Append(context.State.ReturnValue?.ToString() ?? string.Empty);
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

    public static async Task<string> RenderInHtmlAsync(
        string html,
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response)
    {
        var csMatches = Regex.Matches(html, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        if (csMatches.Count == 0)
        {
            return html;
        }

        var scripts = new List<string>(csMatches.Count);
        var htmlWithPlaceholders = Regex.Replace(
            html,
            "<cs>([\\s\\S]*?)</cs>",
            match =>
            {
                var scriptBody = match.Groups[1].Value;
                scripts.Add(scriptBody);
                var marker = $"CSBLOCK_{scripts.Count - 1}";
                return $"<!--{marker}-->";
            },
            RegexOptions.IgnoreCase);

        var document = new HtmlDocument();
        document.LoadHtml(htmlWithPlaceholders);

        var scriptOptions = BuildScriptOptions();
        var context = new CsScriptExecutionContext(scriptOptions);
        var globals = new CsScriptGlobals(filePath, contentRoot, request, response, context, document);

        for (var i = 0; i < scripts.Count; i++)
        {
            var marker = document.DocumentNode
                .DescendantsAndSelf()
                .FirstOrDefault(n =>
                    n.NodeType == HtmlNodeType.Comment &&
                    n.InnerHtml.StartsWith($"CSBLOCK_{i}", StringComparison.Ordinal));

            try
            {
                if (TryGetHiddenIncludePath(scripts[i], out var includePath))
                {
                    await ExecuteHiddenIncludeAsync(includePath, filePath, contentRoot, request, response, context, globals);
                    marker?.Remove();
                    continue;
                }

                context.State = context.State is null
                    ? await CSharpScript.RunAsync<object?>(scripts[i], scriptOptions, globals: globals)
                    : await context.State.ContinueWithAsync<object?>(scripts[i], scriptOptions);
                if (globals.ExecutionContext.State is not null)
                {
                    context.State = globals.ExecutionContext.State;
                }
                if (marker is null)
                {
                    continue;
                }

                var replacement = context.State.ReturnValue?.ToString() ?? string.Empty;
                if (replacement.Length == 0)
                {
                    marker.Remove();
                    continue;
                }

                var fragment = HtmlNode.CreateNode($"<span>{replacement}</span>");
                var inserted = fragment.ChildNodes.ToList();
                foreach (var child in inserted)
                {
                    marker.ParentNode?.InsertBefore(child, marker);
                }
                marker.Remove();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"<cs> parser error in '{filePath}': {ex.Message}");
                marker?.Remove();
            }
        }

        var renderedHtml = document.DocumentNode.OuterHtml;
        renderedHtml = Regex.Replace(
            renderedHtml,
            "<!--\\s*CSBLOCK_\\d+\\s*-->",
            string.Empty,
            RegexOptions.IgnoreCase);
        return renderedHtml;
    }

    private static ScriptOptions BuildScriptOptions()
    {
        return ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(HtmlDocument).Assembly,
                typeof(SqliteConnection).Assembly,
                typeof(Batteries).Assembly,
                typeof(SqliteException).Assembly
            )
            .AddImports(
                "System",
                "System.IO",
                "System.Linq",
                "System.Collections.Generic",
                "HtmlAgilityPack",
                "Microsoft.Data.Sqlite",
                "SQLitePCL"
            );
    }

    private static bool TryGetHiddenIncludePath(string scriptBody, out string relativePath)
    {
        var match = Regex.Match(
            scriptBody,
            @"^\s*await\s+IncludeHiddenAsync\(\s*""([^""]+)""\s*\)\s*;\s*$",
            RegexOptions.IgnoreCase);

        if (!match.Success)
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = match.Groups[1].Value;
        return true;
    }

    private static async Task ExecuteHiddenIncludeAsync(
        string relativePath,
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response,
        CsScriptExecutionContext context,
        CsScriptGlobals globals)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var includePath = Path.GetFullPath(Path.Combine(contentRoot, "hidden", normalized));
        var hiddenRoot = Path.GetFullPath(Path.Combine(contentRoot, "hidden"));

        if (!includePath.StartsWith(hiddenRoot, StringComparison.Ordinal))
        {
            return;
        }

        if (!File.Exists(includePath))
        {
            return;
        }

        var html = await File.ReadAllTextAsync(includePath);
        var matches = Regex.Matches(html, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            var scriptBody = match.Groups[1].Value;
            if (TryGetHiddenIncludePath(scriptBody, out var nestedIncludePath))
            {
                await ExecuteHiddenIncludeAsync(nestedIncludePath, filePath, contentRoot, request, response, context, globals);
                continue;
            }

            context.State = context.State is null
                ? await CSharpScript.RunAsync<object?>(scriptBody, context.ScriptOptions, globals: globals)
                : await context.State.ContinueWithAsync<object?>(scriptBody, context.ScriptOptions);
            if (globals.ExecutionContext.State is not null)
            {
                context.State = globals.ExecutionContext.State;
            }
        }
    }
}

public sealed class CsScriptExecutionContext(ScriptOptions scriptOptions)
{
    public ScriptOptions ScriptOptions { get; } = scriptOptions;
    public ScriptState<object?>? State { get; set; }
}

public sealed class CsScriptGlobals
{
    private readonly HttpRequestContext request;
    private readonly HttpResponseContext response;
    private readonly CsScriptExecutionContext executionContext;

    public CsScriptGlobals(
        string filePath,
        string contentRoot,
        HttpRequestContext request,
        HttpResponseContext response,
        CsScriptExecutionContext executionContext,
        HtmlDocument? document = null)
    {
        FilePath = filePath;
        ContentRoot = contentRoot;
        this.request = request;
        this.response = response;
        this.executionContext = executionContext;
        Document = document;
    }

    public string FilePath { get; }
    public string ContentRoot { get; }
    public string RequestPath => request.RawPath;
    public string RequestMethod => request.Method;
    public string RequestBody => request.Body;
    public IReadOnlyDictionary<string, string> RequestHeaders => request.Headers;
    public IReadOnlyDictionary<string, string> Query => request.Query;
    public IReadOnlyDictionary<string, string> Form => request.Form;
    public IReadOnlyDictionary<string, string> Cookies => request.Cookies;
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
    public HtmlDocument? Document { get; }
    public CsScriptExecutionContext ExecutionContext => executionContext;
    public HtmlNode? GetById(string id) => Document?.GetElementbyId(id);
    public async Task IncludeHiddenAsync(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var includePath = Path.GetFullPath(Path.Combine(ContentRoot, "hidden", normalized));
        var hiddenRoot = Path.GetFullPath(Path.Combine(ContentRoot, "hidden"));

        if (!includePath.StartsWith(hiddenRoot, StringComparison.Ordinal))
        {
            return;
        }

        if (!File.Exists(includePath))
        {
            return;
        }

        var html = await File.ReadAllTextAsync(includePath);
        var matches = Regex.Matches(html, "<cs>([\\s\\S]*?)</cs>", RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            executionContext.State = executionContext.State is null
                ? await CSharpScript.RunAsync<object?>(match.Groups[1].Value, executionContext.ScriptOptions, globals: this)
                : await executionContext.State.ContinueWithAsync<object?>(match.Groups[1].Value, executionContext.ScriptOptions);
        }
    }
    public string? Header(string name) => request.Headers.TryGetValue(name, out var value) ? value : null;
    public string? Cookie(string name) => request.Cookies.TryGetValue(name, out var value) ? value : null;
    public void SetCookie(
        string name,
        string value,
        string path = "/",
        bool httpOnly = true,
        int? maxAgeSeconds = null,
        bool secure = false,
        string sameSite = "Lax")
    {
        response.SetCookie(name, value, path, httpOnly, maxAgeSeconds, secure, sameSite);
    }
}
