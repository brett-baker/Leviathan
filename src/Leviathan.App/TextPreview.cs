using System.Globalization;
using System.Text;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>
/// Text and source code in the preview pane (spec §9.1): the first 2 MB of the file, its encoding detected, coloured by
/// <see cref="SyntaxHighlighter"/>. The file is read, decoded, split and coloured off the UI thread; the lines then go into a virtualized
/// list, so only the lines in view are ever laid out and even a 2 MB file with 60,000 lines costs the UI nothing to show. A line is cut
/// at <see cref="MaxLineLength"/> characters so one enormous line (minified JavaScript) cannot make a single row huge. Read-only; the
/// price of virtualizing is that a selection cannot span lines, so a line is the unit of copying.
/// </summary>
internal sealed partial class TextPreview : Grid
{
    private const int MaxLineLength = 2000;
    private const int MaxLines = 100_000;

    private sealed record Line(string Text, SyntaxToken[] Tokens);

    private readonly ListView _list = new()
    {
        SelectionMode = ListViewSelectionMode.None,
        Padding = new Thickness(0),
        IsItemClickEnabled = false,
    };

    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };

    private readonly Border _badge = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(6),
        Padding = new Thickness(6, 2, 6, 2),
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(190, 0, 0, 0)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _badgeText = new() { FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    private int _generation;

    public TextPreview()
    {
        _list.ItemTemplate = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
            "<TextBlock FontFamily='Cascadia Mono, Consolas' FontSize='12' TextWrapping='NoWrap' />" +
            "</DataTemplate>");
        _list.ItemContainerStyle = (Style)XamlReader.Load(
            "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'>" +
            "<Setter Property='MinHeight' Value='0' /><Setter Property='Padding' Value='6,0,6,0' /><Setter Property='Margin' Value='0' />" +
            "<Setter Property='HorizontalContentAlignment' Value='Left' /></Style>");
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollMode(_list, ScrollMode.Enabled);
        _list.ContainerContentChanging += OnContainerContentChanging;
        _badge.Child = _badgeText;
        Children.Add(_list);
        Children.Add(_message);
        Children.Add(_badge);
    }

    /// <summary>Loads the start of <paramref name="path"/>; throws <see cref="OperationCanceledException"/> when the selection moves on first. A file that cannot be shown leaves a message in the pane.</summary>
    public async Task OpenAsync(string path, string extension, CancellationToken cancellationToken)
    {
        Clear();
        var generation = _generation;
        try
        {
            var language = SyntaxHighlighter.LanguageFor(extension);
            var loaded = await Task.Run(() => Load(path, language), CancellationToken.None).WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
            if (generation != _generation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (loaded.Binary)
            {
                Fail($"This looks like a binary file ({loaded.Size:N0} bytes), so there is no text to show.");
                return;
            }

            _list.ItemsSource = loaded.Lines;
            var note = loaded.Truncated ? $" · first {loaded.Read >> 20} MB of {FormatSize(loaded.Size)}" : string.Empty;
            _badgeText.Text = $"{loaded.EncodingName} · {loaded.Lines.Count:N0} lines{note}";
            _badge.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Fail("This file took too long to read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"This file could not be read: {ex.Message}");
        }
    }

    /// <summary>Empties the pane.</summary>
    public void Clear()
    {
        _generation++;
        _list.ItemsSource = null;
        _message.Visibility = Visibility.Collapsed;
        _badge.Visibility = Visibility.Collapsed;
    }

    private sealed record LoadedText(List<Line> Lines, string EncodingName, bool Binary, bool Truncated, long Size, long Read);

    // Runs off the UI thread: one read of at most 2 MB, then decoding, splitting and colouring.
    private static LoadedText Load(string path, SyntaxLanguage language)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        var size = file.Length;
        var buffer = new byte[(int)Math.Min(size, PreviewLimits.MaxTextBytes)];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = file.Read(buffer, read, buffer.Length - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var truncated = size > read;
        var decoded = TextDecoder.Decode(buffer.AsSpan(0, read), truncated);
        if (decoded.IsBinary)
        {
            return new LoadedText([], decoded.EncodingName, true, truncated, size, read);
        }

        var texts = Split(decoded.Text);
        var tokens = SyntaxHighlighter.Highlight(texts, language);
        var lines = new List<Line>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            lines.Add(new Line(texts[i], tokens[i]));
        }

        return new LoadedText(lines, decoded.EncodingName, false, truncated, size, read);
    }

    // Lines end at \r\n, \n or \r; tabs become four spaces; a very long line is cut with an ellipsis so no row is huge.
    private static List<string> Split(string text)
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length && lines.Count < MaxLines; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                lines.Add(sb.ToString());
                sb.Clear();
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (sb.Length < MaxLineLength)
            {
                sb.Append(c == '\t' ? "    " : c.ToString());
            }
            else if (sb.Length == MaxLineLength)
            {
                sb.Append('…');
            }
        }

        if (sb.Length > 0 || lines.Count == 0)
        {
            lines.Add(sb.ToString());
        }

        return lines;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0.#} MB" : $"{bytes >> 10} KB";

    private void Fail(string text)
    {
        _list.ItemsSource = null;
        _badge.Visibility = Visibility.Collapsed;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer.ContentTemplateRoot is not TextBlock block || args.Item is not Line line)
        {
            return;
        }

        var dark = ActualTheme == ElementTheme.Dark;
        block.Inlines.Clear();
        var at = 0;
        foreach (var token in line.Tokens)
        {
            if (token.Start >= line.Text.Length)
            {
                break;
            }

            var length = Math.Min(token.Length, line.Text.Length - token.Start);
            if (token.Start > at)
            {
                block.Inlines.Add(new Run { Text = line.Text[at..token.Start] });
            }

            block.Inlines.Add(new Run { Text = line.Text.Substring(token.Start, length), Foreground = Brush(token.Kind, dark) });
            at = token.Start + length;
        }

        if (at < line.Text.Length)
        {
            block.Inlines.Add(new Run { Text = line.Text[at..] });
        }

        if (line.Text.Length == 0)
        {
            block.Inlines.Add(new Run { Text = " " }); // keeps an empty line its height
        }
    }

    private static readonly Dictionary<(TokenKind, bool), SolidColorBrush> Brushes = [];

    private static SolidColorBrush Brush(TokenKind kind, bool dark)
    {
        if (Brushes.TryGetValue((kind, dark), out var cached))
        {
            return cached;
        }

        var color = (kind, dark) switch
        {
            (TokenKind.Keyword, false) => Windows.UI.Color.FromArgb(255, 0, 0, 200),
            (TokenKind.Keyword, true) => Windows.UI.Color.FromArgb(255, 86, 156, 214),
            (TokenKind.String, false) => Windows.UI.Color.FromArgb(255, 163, 21, 21),
            (TokenKind.String, true) => Windows.UI.Color.FromArgb(255, 206, 145, 120),
            (TokenKind.Comment, false) => Windows.UI.Color.FromArgb(255, 0, 128, 0),
            (TokenKind.Comment, true) => Windows.UI.Color.FromArgb(255, 106, 153, 85),
            (TokenKind.Number, false) => Windows.UI.Color.FromArgb(255, 9, 134, 88),
            (TokenKind.Number, true) => Windows.UI.Color.FromArgb(255, 181, 206, 168),
            _ => Windows.UI.Color.FromArgb(255, 128, 128, 128),
        };
        return Brushes[(kind, dark)] = new SolidColorBrush(color);
    }
}
