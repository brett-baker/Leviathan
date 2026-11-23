using Leviathan.Contracts;
using Leviathan.Core.Images;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Commands;
using Leviathan.ViewModels.ContextMenu;

namespace Leviathan.ViewModels.Tests;

public sealed class ImageCommandTests
{
    private sealed class FakeEditor : IImageEditor
    {
        public List<(LocationPath Source, ImageEdit Edit)> Edits { get; } = [];

        /// <summary>Source names that fail.</summary>
        public HashSet<string> Failing { get; } = [];

        public Task<LocationPath> EditAsync(LocationPath source, ImageEdit edit, CancellationToken cancellationToken)
        {
            if (Failing.Contains(source.Name))
            {
                throw new ImageEditException("broken picture");
            }

            Edits.Add((source, edit));
            var name = ImageEditRules.OutputName(source.Name, edit, _ => false);
            return Task.FromResult(LocationPath.Parse(@"C:\a\" + name));
        }
    }

    private static async Task<(WindowCommandsTests.Harness Harness, FakeEditor Editor)> MakeAsync(bool withEditor = true)
    {
        var editor = new FakeEditor();
        var h = new WindowCommandsTests.Harness(imageEditor: withEditor ? editor : null, extraFiles: [@"C:\a\one.jpg", @"C:\a\two.png"]);
        await h.Ready;
        return (h, editor);
    }

    private static void Select(WindowCommandsTests.Harness h, params string[] names)
    {
        h.Tab.Selection.Click(h.Tab.Items.Rows.First(r => r.Name == names[0]));
        foreach (var name in names.Skip(1))
        {
            h.Tab.Selection.Toggle(h.Tab.Items.Rows.First(r => r.Name == name));
        }
    }

    private static MenuEntry ImageMenu(WindowCommandsTests.Harness h) =>
        RightClickMenu.Build(h.Tab.Selection.SelectedEntries, h.Tab.History.Current, h.Commands).Single(e => e.Title == "Image");

    [Fact]
    public async Task RotateLeft_SavesACopyOfThePicture_AndSaysSo()
    {
        var (h, editor) = await MakeAsync();
        Select(h, "one.jpg");

        var result = await h.Run(CommandIds.RotateLeft);

        Assert.Equal(CommandResult.Executed, result);
        var (source, edit) = Assert.Single(editor.Edits);
        Assert.Equal(@"C:\a\one.jpg", source.Value);
        Assert.Equal(ImageEditKind.RotateLeft, edit.Kind);
        Assert.Equal("Rotated left: saved one (rotated left).jpg", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task ASeveralPictureSelection_EditsEachOne_AndSelectsTheCopies()
    {
        var (h, editor) = await MakeAsync();
        Select(h, "one.jpg", "two.png");

        await h.Run(CommandIds.ImageFlipHorizontal);

        Assert.Equal(2, editor.Edits.Count);
        Assert.Equal("Flipped horizontally: saved 2 new pictures", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task Resize_UsesThePercentageParameter()
    {
        var (h, editor) = await MakeAsync();
        Select(h, "one.jpg");

        await h.Run(CommandIds.ImageResize, parameter: 50);

        Assert.Equal(new ImageEdit(ImageEditKind.Resize, 50), editor.Edits.Single().Edit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    [InlineData("half")]
    public async Task Resize_RefusesAPercentageOutOfRange(object parameter)
    {
        var (h, editor) = await MakeAsync();
        Select(h, "one.jpg");

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.ImageResize, parameter: parameter));
        Assert.Empty(editor.Edits);
    }

    [Fact]
    public async Task Convert_UsesTheFormatParameter()
    {
        var (h, editor) = await MakeAsync();
        Select(h, "one.jpg");

        await h.Run(CommandIds.ImageConvertTo, parameter: ImageFormat.Png);

        Assert.Equal(new ImageEdit(ImageEditKind.Convert, Format: ImageFormat.Png), editor.Edits.Single().Edit);
        Assert.Equal("Converted to PNG: saved one.png", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task APictureThatFails_IsReported_AndTheOthersStillRun()
    {
        var (h, editor) = await MakeAsync();
        editor.Failing.Add("one.jpg");
        Select(h, "one.jpg", "two.png");

        await h.Run(CommandIds.RotateRight);

        Assert.Single(editor.Edits);
        Assert.Equal("Rotated right 1 of 2 pictures. one.jpg: broken picture", h.Shell.Messages[^1]);
    }

    [Fact]
    public async Task APictureEdit_IsDisabled_WithNoSelection_ForANonPicture_AndWithoutAnEditor()
    {
        var (h, _) = await MakeAsync();
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.RotateLeft));

        Select(h, "1.txt");
        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.RotateLeft));

        var (bare, _) = await MakeAsync(withEditor: false);
        Select(bare, "one.jpg");
        Assert.Equal(CommandResult.Disabled, await bare.Run(CommandIds.RotateLeft));
    }

    [Fact]
    public async Task APictureMixedWithAnotherFile_IsDisabled()
    {
        var (h, editor) = await MakeAsync();
        Select(h, "one.jpg", "1.txt");

        Assert.Equal(CommandResult.Disabled, await h.Run(CommandIds.RotateLeft));
        Assert.Empty(editor.Edits);
    }

    [Fact]
    public async Task OnAPicture_TheMenuHasAnImageSubmenu_WithRotateFlipResizeAndConvert()
    {
        var (h, _) = await MakeAsync();
        Select(h, "one.jpg");

        var menu = ImageMenu(h);
        var titles = menu.Children!.Select(c => c.Title).ToArray();

        Assert.Equal(["Rotate left", "Rotate right", "Flip horizontally", "Flip vertically", "Resize", "Convert to"], titles);
        Assert.Equal(["75%", "50%", "25%", "200%"], menu.Children!.Single(c => c.Title == "Resize").Children!.Select(c => c.Title));
        Assert.Equal(["PNG", "JPEG", "BMP", "GIF", "TIFF"], menu.Children!.Single(c => c.Title == "Convert to").Children!.Select(c => c.Title));
        Assert.All(menu.Children!.SelectMany(c => c.Children ?? [c]), c => Assert.True(c.Enabled));
    }

    [Fact]
    public async Task TheRotateLinesAreNotRepeated_OutsideTheImageSubmenu()
    {
        var (h, _) = await MakeAsync();
        Select(h, "one.jpg");

        var top = RightClickMenu.Build(h.Tab.Selection.SelectedEntries, h.Tab.History.Current, h.Commands).Select(e => e.Title).ToArray();

        Assert.DoesNotContain("Rotate left", top);
        Assert.DoesNotContain("Rotate right", top);
    }

    [Fact]
    public async Task SeveralPictures_GetTheImageSubmenuToo()
    {
        var (h, _) = await MakeAsync();
        Select(h, "one.jpg", "two.png");

        Assert.NotNull(ImageMenu(h));
    }

    [Fact]
    public async Task ANonPicture_HasNoImageSubmenu()
    {
        var (h, _) = await MakeAsync();
        Select(h, "1.txt");

        Assert.DoesNotContain(RightClickMenu.Build(h.Tab.Selection.SelectedEntries, h.Tab.History.Current, h.Commands), e => e.Title == "Image");
    }

    [Fact]
    public async Task WithoutAnEditor_TheImageSubmenuIsGreyed()
    {
        var (h, _) = await MakeAsync(withEditor: false);
        Select(h, "one.jpg");

        Assert.False(ImageMenu(h).Enabled);
    }
}
