# UAT playbooks

Walk-throughs that show whether a feature is **fit for purpose**, which passing tests cannot. Each one has a goal, setup, numbered steps with the
expected result, what to look at by eye, known edge cases, and a place for the result. Start from `TEMPLATE.md` for a new one.

## Rules (from CLAUDE.md)

- **Never real data.** Every step uses the folders built by `tools/New-UatFixtures.ps1` under `D:\Playbooks\uat`. Some playbooks make a small
  extra scratch folder inside it (the steps say so).
- **Network steps** use the local share `\\localhost\LeviathanUat` (`.\tools\New-UatFixtures.ps1 -Share`, elevated; `-RemoveShare` takes it away) or are
  marked **[lab]**. Never Y:, Z: or `\\192.168.1.10`.
- **Scratch settings.** Start the app with `LEVIATHAN_DATA_DIR = D:\Playbooks\data`, so your real settings, favourites and saved servers are untouched.
- Running the fixture script again **deletes and rebuilds** `D:\Playbooks\uat`. Do not run it with `-Share` in the middle of a share test; use
  `New-SmbShare` as the steps say.
- UI automation (FlaUI) tests are not part of this: they use the real mouse and keyboard and only run on your say-so.

## Fixtures

| Folder | For |
| --- | --- |
| `01-basic`, `02-odd-names`, `03-long-paths`, `04-attributes`, `05-conflicts`, `06-ten-thousand`, `07-bulk-copy`, `08-targets` | Phases 1 and 2: browsing, copying, conflicts, awkward names, big sparse file |
| `09-media`, `10-triage`, `14-corrupt` | Phase 3: media, triage, damaged files (need ffmpeg) |
| `11-preview`, `12-duplicates`, `13-tags` | Previews, name-based duplicates, colour tags |
| `15-search` | Search and the space map |
| `16-rename` | Bulk rename |
| `17-archives` | Archives and checksums (zip, tar, tar.gz, 7z if installed, good and bad checksum files) |
| `18-sync` | Compare and sync |
| `19-git` | A Git repository with one of each state |

## Suggested order for a full pass

Roughly four to five hours in total. Do them in this order: each builds on what the earlier ones showed.

### Phase 1: the core browser

| Playbook | What it covers | Time |
| --- | --- | --- |
| [P1-A](P1-A-tabs-windows-groups.md) | Tabs, tab groups, windows, tear-off, sessions and profiles | 30 min |
| [P1-B](P1-B-address-bar-sidebar.md) | Address bar, history, favourites, sidebar, drive card | 25 min |
| [P1-C](P1-C-file-views-selection.md) | Details, List, Thumbnails, sort, group, filter, selection, rename, columns | 35 min |
| [P1-D](P1-D-copy-move-delete-undo-drag.md) | Clipboard, copy, move, delete, undo, drag and drop | 40 min |
| [P1-E](P1-E-menus-command-bar-hub.md) | Right-click menu, command bar, status bar, status hub, settings sheet | 30 min |
| [P1-F](P1-F-appearance-icons-startup.md) | Theme, density, icons, listing cache, startup choice | 25 min |
| [F1-split-pane](F1-split-pane.md) | Split panes (Phase 2 slice F1) | 15 min |

### Phase 2: NAS resiliency and operations

| Playbook | What it covers | Time |
| --- | --- | --- |
| [A6-network-section](A6-network-section.md), [A7-link-speed](A7-link-speed.md) | Network tree, saved servers, link speed | 20 min each |
| [P2-A](P2-A-share-health-offline-reconnect.md) | Dead shares, offline listing, change detection, sign in | 30 min |
| [P2-B](P2-B-copy-engine-resume-verify.md) | Part files, pause, kill, resume, verify, failures, odd names | 45 min |
| [P2-C](P2-C-operations-queue-conflicts.md) | Activity strip, Operations panel, bandwidth, conflict dialog | 40 min |
| [P2-D](P2-D-soft-delete-bin.md) | Soft delete on shares, the bin, restore, purge | 30 min |
| [P2-E](P2-E-pane-operations-folder-size-columns.md) | F5/F6/swap, folder-size columns and deltas | 30 min |
| [E1-folder-size-detail](E1-folder-size-detail.md), [C5b-hub-additions](C5b-hub-additions.md) | Size age, activity reports | 15 min each |

### Phase 3: media and triage

| Playbook | What it covers | Time |
| --- | --- | --- |
| [P3-A](P3-A-media-probe-cache-quarantine.md) | Reading media details, cache, quarantine, media process | 30 min |
| [A5a-media-columns](A5a-media-columns.md), [A5b-tiles-gallery](A5b-tiles-gallery.md), [A6-media-background](A6-media-background.md) | Media columns, Tiles and Gallery, the Background tab | 15 to 20 min each |
| [B1-video-thumbnails](B1-video-thumbnails.md), [B2-contact-sheets](B2-contact-sheets.md), [B3-hover-scrub](B3-hover-scrub.md) | Thumbnails, contact sheets, hover scrub | 15 to 20 min each |
| [C3-player-window](C3-player-window.md) | Player and read-ahead | 20 min |
| [D1](D1-preview-pane-shell.md), [D2](D2-preview-renderers.md), [D3](D3-details-pane.md) | Preview and details panes | 20 to 30 min each |
| [E1-E4-triage](E1-E4-triage.md), [E5-triage-inboxes](E5-triage-inboxes.md), [E6-skim-playback](E6-skim-playback.md) | Triage | 30 min each |
| [F-colour-tags](F-colour-tags.md), [G1-corrupt-media](G1-corrupt-media.md), [H4-find-duplicates](H4-find-duplicates.md) | Tags, damaged files, name-based duplicates | 20 to 30 min each |
| [P3-H](P3-H-eager-updates-properties-tweaks.md) | Eager list updates, Properties, slow-click rename | 20 min |

### Phase 4: search, space map, more locations

| Playbook | What it covers |
| --- | --- |
| [A4-A5-search](A4-A5-search.md), [B-C-search-index](B-C-search-index.md) | Search box and results, the index |
| [D-space-map](D-space-map.md) | Space map and the Space tab |
| [E-remote-locations](E-remote-locations.md) | SFTP, FTP, FTPS, NFS, cloud sync status (needs Docker for two servers) |

### Phase 5: power features and polish

| Playbook | What it covers | Time |
| --- | --- | --- |
| [P5-A](P5-A-duplicate-files-review.md) | Identical and similar files, keep rules, apply and undo | 35 min |
| [P5-B](P5-B-bulk-rename.md) | The rename steps, tokens, conflicts, presets, batch undo | 35 min |
| [P5-C](P5-C-archives-and-checksums.md) | Browse, extract, compress; checksums and verify | 40 min |
| [P5-D](P5-D-compare-and-sync.md) | Compare, five sync modes, dry run, saved pairs | 40 min |
| [P5-E](P5-E-palette-terminal-targets-git-notes.md) | Palette, terminal, quick targets, elevation, Git, folder notes | 40 min |
| [P5-F](P5-F-settings-keyboard-diagnostics.md) | Every settings page, key editor, crash reports | 45 min |
| [P5-G](P5-G-hardening-stress-accessibility.md) | Budgets, stress, odd names in the view, keyboard-only walk, Narrator, contrast | 60 min |

## What the playbooks were written from

The older playbooks (no `P` prefix) were written as each slice landed. The `P1` to `P5` ones were written afterwards from the phase plans, the
phase documents and the code's own wording (window titles, button labels, command names and keys). Where a step says "note what you see" or a
**Known gap**, the behaviour was not checked by hand when the playbook was written: it is the first real check, so write down what happens.

## Recording results

Put what you find in the **Result** section of each playbook, and anything that is broken in `docs/phase-5.md` under "Issues from the month of use"
(each gets a commit and a regression test).
