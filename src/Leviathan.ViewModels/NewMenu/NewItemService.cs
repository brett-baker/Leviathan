using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.ViewModels.NewMenu;

/// <summary>
/// What the New menu and the New commands use (spec §6.1). Reading the registry for <c>ShellNew</c> entries and creating files
/// both touch the disk, so both run on the thread pool; the template list is read once and kept, because scanning the registry
/// is the slow part and the menu must open at once.
/// </summary>
public sealed class NewItemService
{
    private readonly INewItemCatalog _catalog;
    private readonly NewItemCreator _creator;
    private Task<IReadOnlyList<NewItemTemplate>>? _templates;

    /// <summary>Creates the service.</summary>
    public NewItemService(INewItemCatalog catalog, NewItemCreator creator)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(creator);
        _catalog = catalog;
        _creator = creator;
    }

    /// <summary>The Folder template, which Ctrl+Shift+N uses.</summary>
    public static NewItemTemplate FolderTemplate => ShellNewCatalog.BuiltIn[0];

    /// <summary>Starts reading the templates in the background if that has not happened yet, e.g. at startup.</summary>
    public void Prime() => _ = TemplatesAsync();

    /// <summary>The menu entries. The first call reads them; later calls return the same list. Forget it with <see cref="Refresh"/>.</summary>
    public Task<IReadOnlyList<NewItemTemplate>> TemplatesAsync() =>
        // The read is shared and not cancellable by one caller: a menu closed early must not leave the next one with a cancelled list.
        _templates ??= Task.Run(() => _catalog.GetTemplatesAsync(CancellationToken.None));

    /// <summary>Forgets the list, so the next menu reads the registry and the templates folder again.</summary>
    public void Refresh() => _templates = null;

    /// <summary>Creates the item for <paramref name="templateId"/> in <paramref name="folder"/> and returns its location.</summary>
    /// <exception cref="KeyNotFoundException">No such template.</exception>
    /// <exception cref="IOException">The item could not be created.</exception>
    public async Task<LocationPath> CreateAsync(string templateId, LocationPath folder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(templateId);
        ArgumentNullException.ThrowIfNull(folder);
        var template = templateId == FolderTemplate.Id
            ? FolderTemplate
            : (await TemplatesAsync().ConfigureAwait(false)).FirstOrDefault(t => t.Id == templateId)
                ?? throw new KeyNotFoundException($"There is no New menu item '{templateId}'.");
        return await Task.Run(() => _creator.Create(template, folder), cancellationToken).ConfigureAwait(false);
    }
}
