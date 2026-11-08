using Leviathan.Core.Platform;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Leviathan.App;

/// <summary>
/// Markdown and HTML in the preview pane (spec §9.1), drawn by WebView2 in a sandbox with three layers: the page carries a
/// Content-Security-Policy that allows nothing but inline style and embedded pictures (<see cref="WebPreviewHtml"/>), the engine's scripting
/// is switched off, and every request that is not the page itself is answered 403 and every navigation after the first is cancelled, so a
/// link, a meta refresh or a form goes nowhere. The web view is made the first time it is needed and kept, because starting its browser
/// process is the slow part; its profile lives in the data folder so a test run never touches the real one.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The web view is released with the window; a control cannot express that as IDisposable.")]
internal sealed partial class WebPreview : Grid
{
    private readonly WebView2 _view = new();
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Visibility = Visibility.Collapsed,
    };

    private CoreWebView2Environment? _environment;
    private TaskCompletionSource? _navigated;
    private bool _allowNavigation;
    private ulong _pageId;
    private int _generation;

    public WebPreview()
    {
        Children.Add(_view);
        Children.Add(_message);
    }

    /// <summary>Shows <paramref name="html"/>, a page already made safe by <see cref="WebPreviewHtml"/>; throws <see cref="OperationCanceledException"/> when the selection moves on first.</summary>
    public async Task ShowAsync(string html, CancellationToken cancellationToken)
    {
        Clear();
        var generation = _generation;
        try
        {
            await EnsureReadyAsync(cancellationToken);
            if (generation != _generation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _navigated = done;
            _allowNavigation = true;
            _view.Visibility = Visibility.Visible;
            _view.NavigateToString(html);
            await done.Task.WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Fail("This page took too long to draw.");
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or FileNotFoundException)
        {
            Fail($"The web view could not start: {ex.Message}");
        }
        finally
        {
            _allowNavigation = false;
        }
    }

    /// <summary>Blanks the page.</summary>
    public void Clear()
    {
        _generation++;
        _message.Visibility = Visibility.Collapsed;
        _navigated?.TrySetCanceled();
        _navigated = null;
        _allowNavigation = false;
        if (_view.CoreWebView2 is not null)
        {
            _view.CoreWebView2.Navigate("about:blank");
        }
    }

    /// <summary>Shuts the web view down when the window closes.</summary>
    public void Release() => _view.Close();

    private async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_view.CoreWebView2 is not null)
        {
            return;
        }

        _environment ??= await CoreWebView2Environment.CreateWithOptionsAsync(null, DataLocation.Combine("webview2"), new CoreWebView2EnvironmentOptions());
        await _view.EnsureCoreWebView2Async(_environment).AsTask(cancellationToken);
        var core = _view.CoreWebView2 ?? throw new InvalidOperationException("The web view did not start.");
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            // Blanking the page is always fine; the page itself is allowed once; anything else (a link, a meta refresh) goes nowhere.
            if (e.Uri.StartsWith("about:blank", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!_allowNavigation)
            {
                e.Cancel = true;
                return;
            }

            _allowNavigation = false;
            _pageId = e.NavigationId;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var uri = e.Request.Uri;
            if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && !uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                && e.ResourceContext != CoreWebView2WebResourceContext.Document)
            {
                e.Response = _environment!.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            // Only the page's own navigation ends the wait, not the blanking before it.
            if (e.NavigationId == _pageId)
            {
                _navigated?.TrySetResult();
            }
        };
    }

    private void Fail(string text)
    {
        _view.Visibility = Visibility.Collapsed;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }
}
