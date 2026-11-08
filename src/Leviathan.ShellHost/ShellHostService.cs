using System.Reflection;
using Leviathan.Contracts;

namespace Leviathan.ShellHost;

/// <summary>The shell host's IPC endpoint; a thin translation layer over <see cref="StaPool"/>.</summary>
internal sealed class ShellHostService : IShellHostService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
    private static readonly string AppVersion =
        typeof(ShellHostService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private readonly StaPool _pool;
    private readonly ShellMenuManager _menus;
    private readonly ShellPreviewManager _previews;

    public ShellHostService(StaPool pool, ShellMenuManager menus, ShellPreviewManager previews)
    {
        _previews = previews ?? throw new ArgumentNullException(nameof(previews));
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _menus = menus ?? throw new ArgumentNullException(nameof(menus));
    }

    /// <inheritdoc />
    public Task<ShellHostInfo> PingAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ShellHostInfo(ContractVersion.Major, ContractVersion.Minor, AppVersion, Environment.ProcessId, StartedAt));

    /// <inheritdoc />
    public async Task<ShellHostStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var apartment = await _pool.RunAsync(() => Thread.CurrentThread.GetApartmentState().ToString(), ProbeTimeout, cancellationToken).ConfigureAwait(false);
        var status = _pool.Status;
        return new ShellHostStatus(status.Size, status.Busy, status.Queued, status.Completed, status.Failed, status.TimedOut, status.Abandoned, apartment);
    }

    /// <inheritdoc />
    public Task<ShellMenu> OpenContextMenuAsync(ShellMenuRequest request, CancellationToken cancellationToken) => _menus.OpenAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ShellMenuItem>> LoadSubMenuAsync(Guid menuId, IReadOnlyList<int> path, CancellationToken cancellationToken) =>
        _menus.LoadSubMenuAsync(menuId, path, cancellationToken);

    /// <inheritdoc />
    public Task<ShellInvokeResult> InvokeAsync(Guid menuId, ShellInvokeRequest request, CancellationToken cancellationToken) =>
        _menus.InvokeAsync(menuId, request, cancellationToken);

    /// <inheritdoc />
    public Task CloseContextMenuAsync(Guid menuId, CancellationToken cancellationToken) => _menus.CloseAsync(menuId);

    /// <inheritdoc />
    public Task<ShellPreviewResult> OpenPreviewAsync(ShellPreviewRequest request, CancellationToken cancellationToken) => _previews.OpenAsync(request, cancellationToken);

    /// <inheritdoc />
    public Task MovePreviewAsync(Guid previewId, ShellRect bounds, CancellationToken cancellationToken) => _previews.MoveAsync(previewId, bounds, cancellationToken);

    /// <inheritdoc />
    public Task ClosePreviewAsync(Guid previewId, CancellationToken cancellationToken) => _previews.CloseAsync(previewId);
}
