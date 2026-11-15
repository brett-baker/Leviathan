using System.Xml.Linq;

namespace Leviathan.ViewModels.Tests;

/// <summary>
/// Every interactive control declared in the App's XAML has a name a screen reader can announce (accessibility pass, build plan G4).
/// A button that holds only an icon, a slider, a progress bar, an image or a list has no name of its own, so it must carry
/// <c>AutomationProperties.Name</c> (or a label, header, placeholder or text). This reads the XAML files themselves, so it needs no UI.
/// </summary>
public sealed class XamlAccessibilityTests
{
    private static readonly HashSet<string> Interactive =
    [
        "Button", "ToggleButton", "DropDownButton", "SplitButton", "ToggleSplitButton", "AppBarButton", "AppBarToggleButton",
        "ComboBox", "Slider", "ProgressBar", "ListView", "GridView", "TextBox", "AutoSuggestBox", "CheckBox", "ToggleSwitch",
        "NumberBox", "Image", "TabView", "HyperlinkButton", "RadioButton", "TreeView", "PasswordBox",
        "MenuFlyoutItem", "ToggleMenuFlyoutItem",
    ];

    private static readonly string[] NamingAttributes = ["Content", "Header", "Label", "PlaceholderText", "Text"];

    private static string AppFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Leviathan.App");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("src/Leviathan.App was not found above the test folder.");
    }

    private static bool HasName(XElement element)
    {
        bool Named(XElement e) => e.Attributes().Any(a =>
            a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy"
            || (Array.IndexOf(NamingAttributes, a.Name.LocalName) >= 0 && a.Value.Length > 0));

        // Text inside the control names it: a TextBlock child (its text may be set in code, as in the status bar) or a named child.
        return Named(element) || element.Descendants().Any(d => Named(d) || d.Name.LocalName == "TextBlock") || element.Value.Trim().Length > 0;
    }

    [Fact]
    public void EveryInteractiveControlInTheXaml_HasANameForAScreenReader()
    {
        var unnamed = new List<string>();
        foreach (var file in Directory.EnumerateFiles(AppFolder(), "*.xaml", SearchOption.TopDirectoryOnly))
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants().Where(e => Interactive.Contains(e.Name.LocalName)))
            {
                if (!HasName(element))
                {
                    var line = ((System.Xml.IXmlLineInfo)element).LineNumber;
                    var id = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value;
                    unnamed.Add($"{Path.GetFileName(file)}:{line} <{element.Name.LocalName}{(id is null ? string.Empty : " " + id)}>");
                }
            }
        }

        Assert.True(unnamed.Count == 0, "Controls with no name for a screen reader (add AutomationProperties.Name):\n" + string.Join("\n", unnamed));
    }
}
