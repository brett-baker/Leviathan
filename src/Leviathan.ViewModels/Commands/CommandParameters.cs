using Leviathan.Core.Paths;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Commands;

/// <summary>Parameter of <see cref="CommandIds.TabMove"/>.</summary>
/// <param name="Tab">The tab dragged.</param>
/// <param name="Index">Where it landed.</param>
public sealed record TabMove(TabViewModel Tab, int Index);

/// <summary>Parameter of <see cref="CommandIds.TabTearOff"/>.</summary>
/// <param name="Tab">The tab dragged out.</param>
/// <param name="ScreenX">Drop point in screen pixels.</param>
/// <param name="ScreenY">Drop point in screen pixels.</param>
public sealed record TabTearOff(TabViewModel Tab, int ScreenX, int ScreenY);

/// <summary>Parameter of <see cref="CommandIds.TabGroupAdd"/>.</summary>
/// <param name="Tab">The tab to add.</param>
/// <param name="Group">The group to add it to.</param>
/// <param name="Position">Where among the group's tabs, or null for the end.</param>
public sealed record TabGroupAdd(TabViewModel Tab, TabGroup Group, int? Position = null);

/// <summary>Parameter of <see cref="CommandIds.TabGroupMove"/>.</summary>
/// <param name="Group">The group dragged.</param>
/// <param name="Index">Where its first tab should land among the other tabs.</param>
public sealed record TabGroupMove(TabGroup Group, int Index);

/// <summary>Parameter of <see cref="CommandIds.TabGroupTearOff"/>.</summary>
/// <param name="Group">The group dragged out.</param>
/// <param name="ScreenX">Drop point in screen pixels.</param>
/// <param name="ScreenY">Drop point in screen pixels.</param>
public sealed record TabGroupTearOff(TabGroup Group, int ScreenX, int ScreenY);

/// <summary>Parameter of <see cref="CommandIds.TabGroupDrop"/>.</summary>
/// <param name="Group">The group dropped, from this or another window.</param>
/// <param name="Index">Where in the strip, or null for the end.</param>
public sealed record TabGroupDrop(TabGroup Group, int? Index);

/// <summary>Parameter of <see cref="CommandIds.DropItems"/>.</summary>
/// <param name="Sources">The items dropped.</param>
/// <param name="Modifiers">The modifier keys held at the drop.</param>
public sealed record ItemDrop(IReadOnlyList<LocationPath> Sources, Transfers.DropModifiers Modifiers);

/// <summary>Parameter of <see cref="CommandIds.TabDrop"/>.</summary>
/// <param name="Tab">The tab dropped.</param>
/// <param name="Index">Where in the strip, or null for the end.</param>
public sealed record TabDrop(TabViewModel Tab, int? Index);
