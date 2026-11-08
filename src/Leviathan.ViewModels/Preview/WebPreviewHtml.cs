using System.Text.RegularExpressions;
using Markdig;

namespace Leviathan.ViewModels.Preview;

/// <summary>
/// The HTML the preview pane hands to its sandboxed web view (spec §9.1: "Markdown, HTML rendered in a sandboxed WebView2 with scripts
/// and network disabled"). The sandbox has three layers and this is the first: every page starts with a Content-Security-Policy that
/// allows no script, no network and no frames, only inline style and pictures embedded in the page. The others are in the control: the
/// engine's scripting is switched off and every navigation but the first is cancelled.
/// </summary>
public static partial class WebPreviewHtml
{
    /// <summary>The policy: nothing loads from anywhere except inline style and <c>data:</c> pictures.</summary>
    public const string Policy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:";

    private const string Meta = "<meta http-equiv=\"Content-Security-Policy\" content=\"" + Policy + "\">";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml() // raw HTML inside Markdown is shown as text, not run
        .Build();

    private const string Style = """
        <style>
        :root { color-scheme: light dark; }
        body { font-family: "Segoe UI", sans-serif; font-size: 13px; line-height: 1.5; margin: 10px; word-wrap: break-word; }
        pre, code { font-family: "Cascadia Mono", Consolas, monospace; font-size: 12px; }
        pre { padding: 8px; overflow-x: auto; background: rgba(127,127,127,.15); border-radius: 4px; }
        code { background: rgba(127,127,127,.15); padding: 0 3px; border-radius: 3px; } pre code { background: none; padding: 0; }
        table { border-collapse: collapse; } td, th { border: 1px solid rgba(127,127,127,.5); padding: 3px 8px; }
        blockquote { margin-left: 0; padding-left: 10px; border-left: 3px solid rgba(127,127,127,.5); }
        img { max-width: 100%; } h1, h2 { border-bottom: 1px solid rgba(127,127,127,.3); padding-bottom: 3px; }
        </style>
        """;

    /// <summary>A whole page for Markdown text.</summary>
    public static string FromMarkdown(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        return $"<!doctype html><html><head><meta charset=\"utf-8\">{Meta}{Style}</head><body>{Markdown.ToHtml(markdown, Pipeline)}</body></html>";
    }

    /// <summary>The HTML file's own text with the policy put first in its head, so nothing in the file can load or run anything.</summary>
    public static string FromHtml(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        // Any policy the file brings itself is kept, but ours comes first and a policy can only add restrictions.
        if (HeadOpen().Match(html) is { Success: true } head)
        {
            return html.Insert(head.Index + head.Length, Meta);
        }

        if (HtmlOpen().Match(html) is { Success: true } root)
        {
            return html.Insert(root.Index + root.Length, "<head>" + Meta + "</head>");
        }

        return "<!doctype html><html><head><meta charset=\"utf-8\">" + Meta + "</head><body>" + html + "</body></html>";
    }

    [GeneratedRegex("<head(\\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeadOpen();

    [GeneratedRegex("<html(\\s[^>]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlOpen();
}
