using System.Net;
using System.Text;
using System.Text.RegularExpressions;

public static class MarkdownParser
{
    public static string BuildHtml(string markdown, string title)
    {
        var body = ConvertToHtml(markdown);
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

    public static string ConvertToHtml(string markdown)
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

    private static bool IsTableLine(string line)
    {
        return line.StartsWith("|") && line.EndsWith("|");
    }

    private static bool IsTableSeparator(string line)
    {
        var stripped = line.Replace("|", "").Replace("-", "").Replace(":", "").Trim();
        return stripped.Length == 0;
    }

    private static List<string> ParseTableCells(string line)
    {
        return line.Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static string ProcessInlineMarkdown(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        encoded = Regex.Replace(encoded, @"`([^`]+)`", "<code>$1</code>");
        encoded = Regex.Replace(encoded, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        encoded = Regex.Replace(encoded, @"\*([^*]+)\*", "<em>$1</em>");
        return encoded;
    }

    private static string GetLanguageCssClass(string markdownLanguage)
    {
        if (string.IsNullOrWhiteSpace(markdownLanguage))
        {
            return "";
        }

        var normalized = markdownLanguage.Trim().ToLowerInvariant();
        normalized = Regex.Replace(normalized, @"[^a-z0-9#+-]", "");
        return normalized.Length == 0 ? "" : $"language-{normalized}";
    }
}
