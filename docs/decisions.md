# Decisions made while building

One line each: the decision, then why. Newest last.

- **D6: mapped network drives are listed under Network only, not also under Local.** The spec (§7.1) says both; one entry per location keeps "follow the current folder" unambiguous and the tree shorter. Revisit when saved servers arrive.
- **D6: the tree is rebuilt (collapsed) when Favorites change.** Pinning is rare and the tree re-expands to the current folder at once; keeping expanded state through a rebuild is not worth the bookkeeping yet.
- **D6: tree rows are inserted 40 at a time with a yield between.** One big range insert took 350 to 420 ms in the list control, single inserts 70 ms for 280 rows; chunking keeps every step under the 50 ms stall budget.
- **D6: hidden folders are not shown in the tree.** Matches Explorer's navigation pane; the file view still shows them when the user asks.
- **D7: the context zone shows only actions whose command is registered.** The §6.2 table is encoded in full (ContextActions.Wanted) but a button that does nothing is worse than a missing one; the zone fills in as features land.
- **D7: Ctrl+Shift+F focuses the filter box.** Ctrl+F is reserved for search (Phase 4); filtering is per tab and clears when the tab navigates.
- **D7: Ctrl+A selects rows unless a text box has focus.** The window-level key map would otherwise swallow Ctrl+A in the address and filter boxes.
- **D8: Ctrl+click on an item toggles its selection; it does not open a folder in a new tab.** The spec (§5.1) gives Ctrl+click both jobs; multi-select is used far more. Middle-click, the command bar and the sidebar menu open folders in new tabs.
- **D8: rename runs straight on the thread pool for now.** `RenameService` is the seam: slice E4 routes it through the journaled worker so it can be undone.
- **D8: a rename box that is refused (bad name) stays open; one that fails on disk (name taken, access denied) closes with the reason in the status bar.**
- **D8: the rubber band starts on empty space, and in Details also on the blank part of a row.** Rows span the full width, so only the name and icon are reserved for drag and drop (D9).
