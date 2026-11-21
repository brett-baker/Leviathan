using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace Leviathan.App;

/// <summary>
/// One setting as a Windows-settings style card (spec §17.2): an icon, the name with an (i) that explains it, a line saying what it
/// does, an optional suggestion, and the control on the right. The label sits beside the control instead of above it, so a page uses its
/// width. <see cref="Stacked"/> puts the control under the text for wide ones (a list, a long address).
/// </summary>
internal sealed partial class SettingCard : ContentControl
{
    public static readonly DependencyProperty TitleProperty = Property(nameof(Title));
    public static readonly DependencyProperty DescriptionProperty = Property(nameof(Description));
    public static readonly DependencyProperty InfoProperty = Property(nameof(Info));
    public static readonly DependencyProperty AdviceProperty = Property(nameof(Advice));
    public static readonly DependencyProperty GlyphProperty = Property(nameof(Glyph));
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(nameof(Stacked), typeof(bool), typeof(SettingCard), new PropertyMetadata(false, OnChanged));

    private const string TemplateXaml = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="using:Leviathan.App" TargetType="local:SettingCard">
          <Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}" BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
                  BorderThickness="1" CornerRadius="4" MinHeight="60" Padding="16,10,16,10">
            <Grid ColumnSpacing="16" RowSpacing="8">
              <Grid.ColumnDefinitions>
                <ColumnDefinition x:Name="IconColumn" Width="Auto" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
              </Grid.ColumnDefinitions>
              <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
              </Grid.RowDefinitions>
              <FontIcon x:Name="IconPart" FontSize="18" Width="24" VerticalAlignment="Center" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
              <StackPanel Grid.Column="1" VerticalAlignment="Center" Spacing="2">
                <StackPanel Orientation="Horizontal" Spacing="6">
                  <TextBlock x:Name="TitlePart" TextWrapping="Wrap" VerticalAlignment="Center" />
                  <FontIcon x:Name="InfoPart" Glyph="&#xE946;" FontSize="13" VerticalAlignment="Center" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                </StackPanel>
                <TextBlock x:Name="DescriptionPart" Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                <TextBlock x:Name="AdvicePart" Style="{StaticResource CaptionTextBlockStyle}" TextWrapping="Wrap" Foreground="{ThemeResource AccentTextFillColorPrimaryBrush}" />
              </StackPanel>
              <ContentPresenter x:Name="ControlPart" Grid.Column="2" VerticalAlignment="Center" HorizontalAlignment="Right" Content="{TemplateBinding Content}" />
            </Grid>
          </Border>
        </ControlTemplate>
        """;

    private FontIcon? _icon;
    private TextBlock? _title;
    private TextBlock? _description;
    private TextBlock? _advice;
    private FontIcon? _info;
    private ContentPresenter? _control;
    private ColumnDefinition? _iconColumn;

    public SettingCard()
    {
        Template = (ControlTemplate)XamlReader.Load(TemplateXaml);
        HorizontalContentAlignment = HorizontalAlignment.Right;
        IsTabStop = false;
    }

    /// <summary>The setting's name.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>One line saying what choosing each value does.</summary>
    public string Description { get => (string)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }

    /// <summary>The text of the (i): what the setting is and when it matters. No (i) when empty.</summary>
    public string Info { get => (string)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }

    /// <summary>A suggestion, drawn in the accent colour under the description.</summary>
    public string Advice { get => (string)GetValue(AdviceProperty); set => SetValue(AdviceProperty, value); }

    /// <summary>A Segoe Fluent glyph drawn at the left; the column is left out when empty.</summary>
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>True to put the control under the text, across the card, instead of beside it.</summary>
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _icon = GetTemplateChild("IconPart") as FontIcon;
        _title = GetTemplateChild("TitlePart") as TextBlock;
        _description = GetTemplateChild("DescriptionPart") as TextBlock;
        _advice = GetTemplateChild("AdvicePart") as TextBlock;
        _info = GetTemplateChild("InfoPart") as FontIcon;
        _control = GetTemplateChild("ControlPart") as ContentPresenter;
        _iconColumn = GetTemplateChild("IconColumn") as ColumnDefinition;
        Refresh();
    }

    private static DependencyProperty Property(string name) =>
        DependencyProperty.Register(name, typeof(string), typeof(SettingCard), new PropertyMetadata(string.Empty, OnChanged));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingCard)d).Refresh();

    private void Refresh()
    {
        if (_title is null)
        {
            return;
        }

        _title.Text = Title;
        Show(_description!, Description);
        Show(_advice!, Advice);
        if (_icon is not null && _iconColumn is not null)
        {
            _icon.Glyph = Glyph;
            _icon.Visibility = Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _iconColumn.Width = Glyph.Length > 0 ? GridLength.Auto : new GridLength(0);
        }

        if (_info is not null)
        {
            _info.Visibility = Info.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(_info, Info.Length > 0 ? Info : null);
        }

        if (_control is not null)
        {
            Grid.SetRow(_control, Stacked ? 1 : 0);
            Grid.SetColumn(_control, Stacked ? 1 : 2);
            Grid.SetColumnSpan(_control, Stacked ? 2 : 1);
            _control.HorizontalAlignment = Stacked ? HorizontalAlignment.Stretch : HorizontalAlignment.Right;
        }

        static void Show(TextBlock block, string text)
        {
            block.Text = text;
            block.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
