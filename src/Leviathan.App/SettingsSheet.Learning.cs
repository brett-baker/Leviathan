using Leviathan.ViewModels.Triage.Learning;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>
/// The Learning part of the Triage page: one switch for the whole feature and the list of servers that can help it learn. Servers are
/// added from presets (Stash today); an address is saved in the settings file and the API key in Windows Credential Manager, never on screen again.
/// </summary>
internal sealed partial class SettingsSheet
{
    private string _learningShown = string.Empty;

    private void FillLearning(LearningSettings learning)
    {
        LearningSwitch.IsOn = learning.Enabled;
        LearningServersPanel.Visibility = learning.Enabled ? Visibility.Visible : Visibility.Collapsed;

        if (learning.Enabled)
        {
            _ = ShowLearningStatusAsync();
        }

        var signature = string.Join('|', learning.Sources.Select(s => $"{s.Id}:{s.Kind}:{s.Name}:{s.Url}:{s.Enabled}"));
        if (signature == _learningShown)
        {
            return;
        }

        _learningShown = signature;
        LearningSourceList.Children.Clear();
        foreach (var source in learning.Sources)
        {
            LearningSourceList.Children.Add(SourceRow(source));
        }
    }

    // How many decided files the learning has facts for, so the user can see it working (read off the UI thread).
    private async Task ShowLearningStatusAsync()
    {
        try
        {
            var (complete, incomplete) = await App.Host.FileFacts.CountAsync(CancellationToken.None);
            var waiting = App.Host.FeatureCollector.Pending;
            LearningStatusText.Text = complete == 0 && incomplete == 0 && waiting == 0
                ? "Nothing learned from yet. Facts are gathered in the background as you keep and discard."
                : $"Facts gathered for {complete} decided file{(complete == 1 ? string.Empty : "s")}"
                    + (incomplete > 0 ? $", {incomplete} waiting for a server to answer" : string.Empty)
                    + (waiting > 0 ? $", {waiting} being gathered now" : string.Empty) + ".";
        }
        catch (IOException)
        {
            LearningStatusText.Text = "The learning facts could not be read.";
        }
    }

    private async void OnLearningToggled(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
        {
            return;
        }

        var on = LearningSwitch.IsOn;
        await SaveAsync(s => s with { Learning = s.Learning with { Enabled = on } });
        if (on)
        {
            _ = App.Host.BackfillLearningAsync();
        }
    }

    private Grid SourceRow(LearningSource source)
    {
        var row = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 6, 12, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = $"{source.Name} ({LearningSource.InfoOf(source.Kind).Title})", TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = source.Url.Length == 0 ? "No address" : source.Url, Opacity = 0.65, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);

        var enabled = new ToggleSwitch { IsOn = source.Enabled, OnContent = string.Empty, OffContent = string.Empty, MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(enabled, $"Use {source.Name}");
        enabled.Toggled += async (_, _) =>
        {
            if (_ready && !_loading)
            {
                var on = enabled.IsOn;
                await SaveAsync(s => s with { Learning = s.Learning with { Sources = [.. s.Learning.Sources.Select(x => x.Id == source.Id ? x with { Enabled = on } : x)] } });
            }
        };
        Grid.SetColumn(enabled, 1);
        row.Children.Add(enabled);

        var edit = new Button { Content = "Edit…" };
        edit.Click += async (_, _) => await EditLearningSourceAsync(source);
        Grid.SetColumn(edit, 2);
        row.Children.Add(edit);

        var remove = new Button { Content = "Remove" };
        remove.Click += async (_, _) =>
        {
            await Task.Run(() => App.Host.LearningSecrets.Set(source.Id, null));
            await SaveAsync(s => s with { Learning = s.Learning with { Sources = [.. s.Learning.Sources.Where(x => x.Id != source.Id)] } });
        };
        Grid.SetColumn(remove, 3);
        row.Children.Add(remove);
        return row;
    }

    private async void OnAddLearningSourceClick(object sender, RoutedEventArgs e) => await EditLearningSourceAsync(null);

    // Add or change one server. A blank key box keeps the key already saved; the Test button tries what is typed, so a mistake shows now.
    private async Task EditLearningSourceAsync(LearningSource? existing)
    {
        var kind = existing?.Kind ?? LearningSourceKind.Stash;
        var info = LearningSource.InfoOf(kind);

        var kinds = new ComboBox { Header = "Kind of server", HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = existing is null };
        foreach (var k in LearningSource.Kinds)
        {
            kinds.Items.Add(k.Title);
        }

        kinds.SelectedIndex = Math.Max(0, LearningSource.Kinds.ToList().FindIndex(k => k.Kind == kind));
        var name = new TextBox { Header = "Name", Text = existing?.Name ?? info.Title, MaxLength = 40 };
        var url = new TextBox { Header = "Address", Text = existing?.Url ?? string.Empty, PlaceholderText = info.UrlExample };
        var hasKey = existing is not null && await Task.Run(() => !string.IsNullOrEmpty(App.Host.LearningSecrets.Get(existing.Id)));
        var secret = new PasswordBox
        {
            Header = info.SecretLabel,
            PlaceholderText = hasKey ? "Saved. Leave empty to keep it." : "Paste it here",
        };
        var help = new TextBlock { Text = info.Help, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var test = new Button { Content = "Test connection" };
        test.Click += async (_, _) =>
        {
            test.IsEnabled = false;
            result.Text = "Testing…";
            try
            {
                var candidate = existing is null ? LearningSource.Create(kind, name.Text, url.Text) : existing with { Url = LearningSource.NormalizeUrl(url.Text) };
                var key = secret.Password.Length > 0 ? secret.Password : existing is null ? null : await Task.Run(() => App.Host.LearningSecrets.Get(existing.Id));
                var outcome = await App.Host.LearningTester.TestAsync(candidate, key, CancellationToken.None);
                result.Text = (outcome.Ok ? "✓ " : "✗ ") + outcome.Message;
            }
            finally
            {
                test.IsEnabled = true;
            }
        };

        var content = new StackPanel { Spacing = 10, MinWidth = 400 };
        content.Children.Add(kinds);
        content.Children.Add(name);
        content.Children.Add(url);
        content.Children.Add(secret);
        content.Children.Add(help);
        content.Children.Add(test);
        content.Children.Add(result);

        var dialog = new ContentDialog
        {
            Title = existing is null ? "Add a server" : "Edit server",
            Content = content,
            PrimaryButtonText = existing is null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (LearningSource.NormalizeUrl(url.Text).Length == 0)
            {
                result.Text = "✗ That is not a web address. Try " + info.UrlExample;
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var address = LearningSource.NormalizeUrl(url.Text);
        var title = name.Text.Trim().Length == 0 ? info.Title : name.Text.Trim();
        var saved = existing is null ? LearningSource.Create(kind, title, address) : existing with { Name = title, Url = address };
        var newKey = secret.Password;
        try
        {
            if (newKey.Length > 0)
            {
                await Task.Run(() => App.Host.LearningSecrets.Set(saved.Id, newKey));
            }
        }
        catch (IOException ex)
        {
            result.Text = ex.Message;
            return;
        }

        await SaveAsync(s => s with
        {
            Learning = s.Learning with
            {
                Sources = existing is null
                    ? [.. s.Learning.Sources, saved]
                    : [.. s.Learning.Sources.Select(x => x.Id == saved.Id ? saved : x)],
            },
        });
        _ = App.Host.BackfillLearningAsync(); // files collected before this server existed are asked about it
    }
}
