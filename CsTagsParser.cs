using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

public static class CsTagsParser
{
    public static async Task<string> RenderInTextAsync(string content, string filePath, string requestPath)
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

    public static async Task<string> RenderInHtmlAsync(string html, string filePath, string requestPath)
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

    private static ScriptOptions BuildScriptOptions()
    {
        return ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,
                typeof(Enumerable).Assembly,
                typeof(HtmlDocument).Assembly
            )
            .AddImports("System", "System.Linq", "System.Collections.Generic", "HtmlAgilityPack");
    }
}

public sealed class CsScriptGlobals(string filePath, string requestPath, HtmlDocument? document = null)
{
    public string FilePath { get; } = filePath;
    public string RequestPath { get; } = requestPath;
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTime Now => DateTime.Now;
    public HtmlDocument? Document { get; } = document;
    public HtmlNode? GetById(string id) => Document?.GetElementbyId(id);
}
