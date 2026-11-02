# Decisions made while building

One line each: the decision, then why. Newest last.

- **D6: mapped network drives are listed under Network only, not also under Local.** The spec (§7.1) says both; one entry per location keeps "follow the current folder" unambiguous and the tree shorter. Revisit when saved servers arrive.
- **D6: the tree is rebuilt (collapsed) when Favorites change.** Pinning is rare and the tree re-expands to the current folder at once; keeping expanded state through a rebuild is not worth the bookkeeping yet.
- **D6: tree rows are inserted 40 at a time with a yield between.** One big range insert took 350 to 420 ms in the list control, single inserts 70 ms for 280 rows; chunking keeps every step under the 50 ms stall budget.
- **D6: hidden folders are not shown in the tree.** Matches Explorer's navigation pane; the file view still shows them when the user asks.
