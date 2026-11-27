namespace Leviathan.ViewModels.Triage.Learning;

/// <summary>
/// One of the user's own reasons for discarding a video ("Too short", "Bad quality", "Seen it"). The title is all the window shows, so a
/// reason is one click; the description is not for display, it tells the learning what the title means when it weighs the reason.
/// </summary>
/// <param name="Id">A stable id, so renaming a reason does not detach it from the discards that used it.</param>
/// <param name="Title">What the chip says.</param>
/// <param name="Description">What the title means, in the user's words; empty when the title says enough.</param>
public sealed record RejectReason(string Id, string Title, string Description);
