> Exported from the **Leviathan File Manager — Software Specification** doc on 2026-11-01. The doc is the source of truth; this copy lets Claude Code and readers of the repo see the spec. The roadmap diagram is not included; see section 21.

# Leviathan File Manager — Software Specification

Nov 1, 2026 · @Brett Baker

## 1. Overview

Leviathan is a standalone Windows 11 file manager in WinUI 3 / .NET 10. It is built for a power user who moves and triages large media collections on a NAS. Performance, stability and resiliency outrank feature count: the UI must never freeze, a crash must never lose a file operation, and a dropped network share must degrade one tab, not the app.

### Goals

1. **Never block the UI thread.** Every file-system, shell, network and media call runs off the UI thread, is cancellable, and has a timeout.
2. **Survive failure.** File operations live in a separate worker process with a persistent journal. They survive UI crashes and reboots, and resume when the network comes back.
3. **NAS-first.** SMB, SFTP/FTP(S) and NFS are first-class. The app detects slow or offline shares, backs off, and tells the user plainly.
4. **Fast media triage.** Reviewing hundreds of videos for keep/discard is a core workflow with a dedicated mode, not an afterthought.
5. **Feature parity** with File Explorer and the Files app for daily use, plus power features neither has (dual pane, folder sync, duplicate finder, space map).

### Non-goals (v1)

- Becoming the default file manager (Win+E, Open/Save dialogs). Standalone only, but the architecture should not rule it out later.
- Direct cloud APIs. Cloud drives are reached through their installed sync clients.
- Public release concerns: localization, telemetry, Store listing. Personal / power-user audience.
- Windows 10 support.

### Decisions log

| # | Decision | Choice |
| --- | --- | --- |
| D1 | Relationship to Files fork | New app; port proven pieces (license permitting) |
| D2 | Target OS | Windows 11 only (22H2+) |
| D3 | Integration depth | Standalone app |
| D4 | Audience | Personal / power-user |
| D5 | Network protocols | SMB, SFTP/FTP(S), NFS |
| D6 | Cloud storage | Via installed sync clients |
| D7 | Video triage | All modes (triage mode, skim, contact sheet, hover scrub), each toggleable; jump hotkeys for 30 s / 1 min / 5 min |
| D8 | Delete on NAS | App-managed soft delete |
| D9 | Tab tear-off | New window, same process |
| D10 | Video engine | libmpv and LibVLC, switchable in Settings |
| D11 | Tag storage | Hybrid: NTFS alternate data stream + local index DB |
| D12 | Copy engine | Custom, journaled, in worker process |
| D13 | Dual pane | Optional split per tab |
| D14 | Search | Own index (NTFS change journal) + opt-in NAS index + live search |
| D15 | Extras | Duplicate finder, bulk rename, archives + hashing, folder compare/sync |
| D16 | ffprobe scope | Lazy, visible items only, cached |
| D17 | Triage keys | Keep = green tag, Discard = soft delete, 1–9 = tags |
| D18 | Visual style | Fluent / Mica with Compact and Comfortable density |
| D19 | Startup | Profiles of folder sets; one built-in profile is "Last session" (superseded by D37: profiles became workspaces) |
| D20 | App structure | Not Aspire; multi-process desktop app (see §2) |
| D21 | Operations queue | One persistent queue; global and per-device concurrency limits, editable live; Operations panel (§12.3, §12.6) |
| D22 | Activity strip | 1–2 px line per running job, color-coded by type, under the tab strip |
| D23 | Folder sizes | Lazy, bottom-up, stored in SQLite for every subfolder, updated by deltas on change (§12.7) |
| D24 | Soft-delete location | NAS recycle folder (#recycle) when present, else the app's own hidden folder |
| D25 | NFS | Windows Client for NFS |
| D26 | Triage Keep | Green tag, optionally also move to a chosen folder |
| D27 | ffmpeg build | GPL (personal project) |
| D28 | Packaging | Unpackaged, self-contained |
| D29 | Local search backend | Everything (voidtools) via its SDK when installed; Leviathan.Indexer as fallback; NAS index stays Leviathan's own (§13) |
| D30 | Job colors | Distinct color per job, from a palette of 8 (§12.6) |
| D31 | Queue defaults | 3 running jobs globally, 1 per NAS volume |
| D32 | Tab groups | Edge-style named, coloured, collapsible tab groups (§4.1) |
| D33 | Icons | Full-colour icon set in the Windows 11 Explorer style instead of monochrome glyphs (§4.3) |
| D34 | Status hub | A fly-up from the status bar with Operations, Activity, Background, Quick settings and Space tabs; it hosts the Operations panel (§16.1) |
| D35 | Session activity log | Every user action in the session is logged and browsable in the status hub (§16.1) |
| D36 | Settings surface | Settings open as a tall sheet rising from the bottom of the window, not a dialog (§17.2) |
| D37 | Workspaces | Startup profiles are folded into **workspaces**: one saved arrangement of windows, tabs, split layouts and panes, loaded at startup or at any time. "Last session" is the built-in workspace (§17.1, replaces D19) |
| D38 | Column view | A fourth view, Miller columns: one folder per column, user-resizable between a minimum and maximum width, as many columns as fit; the preview pane stays separate (§8.8) |
| D39 | Terminal drawer | A **Terminal** tab in the status hub fly-up hosts a docked Windows Terminal running cmd, opened at the current folder (§16.2) |
| D40 | Built-in tools | Copy path, Open terminal here and Open in editor are built-in commands, in the context menu, the command bar and the command palette (§15.7) |
| D41 | Registry explorer | An optional, read-only **Registry** node in the sidebar, off by default; keys behave as folders and values as items (§7.3) |

## 2. Architecture

Leviathan is a multi-process desktop app: one UI process plus small, purpose-built worker processes that talk over named pipes. Anything that can hang or crash (shell extensions, ffmpeg, network I/O on long jobs) runs outside the UI. If a worker dies, it restarts and the UI keeps going.

### 2.1 Should this be an Aspire app?

No, not as the shipping structure. Aspire is an orchestrator for distributed, cloud-hosted services: containers, service discovery, connection strings, and deploying to Azure or Kubernetes. A desktop app installs as one unit on one machine, and Aspire's AppHost is a development-time orchestrator that would not ship.

| Criterion | Aspire AppHost | Plain multi-process desktop app |
| --- | --- | --- |
| Ships to the user | No, the AppHost is dev-only | Yes, one installer |
| Process supervision at runtime | None in production | UI supervises workers (restart, back-off) |
| WinUI 3 support | Can launch it, no special value | Native |
| Diagnostics | Excellent dashboard (OpenTelemetry) | Same OpenTelemetry, exported to a local file or a dev dashboard |
| Complexity added | AppHost + ServiceDefaults projects, dev certs | None |

**Recommendation:** borrow the good parts without the AppHost. Use OpenTelemetry tracing, metrics and logs in every process (the same libraries ServiceDefaults wraps). In development, point the exporter at the standalone Aspire Dashboard container to see cross-process traces. In production, write to a rolling local log and an optional diagnostics view.

### 2.2 Process model

| Process | Responsibility | Lifetime | Crash behaviour |
| --- | --- | --- | --- |
| **Leviathan.App** (UI) | Windows, tabs, views, view models, enumeration of visible folders, preview UI | User-driven; one process for all windows | Workers keep running; on restart the UI reattaches and restores the session |
| **Leviathan.Worker** | Copy/move/delete engine, operation journal, soft-delete purge, folder sync jobs | Starts on first operation; exits when idle and queue is empty | Journal replay on restart; in-flight files resume or roll back |
| **Leviathan.Media** | ffprobe metadata, thumbnails, contact sheets, perceptual hashes | On demand; pool of 1–N; killed when idle | The bad file is quarantined in the cache (no retry loop); the process restarts |
| **Leviathan.ShellHost** | Third-party shell context-menu handlers and preview handlers (COM, STA) | On demand | Menu shows built-in items only; the failing handler is noted and can be disabled |
| **Leviathan.Indexer** (optional) | NTFS change-journal (USN) reader for instant local search; NAS folder indexer | Windows service (needs admin to read the journal) or per-user fallback | Search falls back to live search |

IPC uses named pipes with StreamJsonRpc and MessagePack. The pipe ACL is restricted to the current user. Contracts live in one shared assembly and are versioned, so a UI update cannot break a worker mid-job.

**Why named pipes, not TCP.** All of Leviathan's processes run on the same PC, and for that case named pipes are the better transport:

| Concern | Named pipes | TCP on localhost |
| --- | --- | --- |
| Who can connect | Pipe ACL limited to your Windows account; remote clients rejected by flag | Any process of any user on the PC can connect to the port; needs its own auth token |
| Verify the caller | The server can read the client's process ID and check it is a Leviathan executable | Not available |
| Ports and firewall | None | Port choice, conflicts with other apps, possible firewall prompts |
| Speed | Lowest latency locally | Slightly higher; irrelevant at these message sizes |
| Library support | StreamJsonRpc supports both equally | Same |

File data never travels over IPC (workers read and write files themselves), so throughput is not a factor. The contracts are transport-agnostic, so TCP with TLS can be added later if a component ever runs on another machine, for example the NAS agent idea in §21.

### 2.3 Solution layout

- `Leviathan.App`: WinUI 3 app (MVVM with CommunityToolkit.Mvvm)
- `Leviathan.Controls`: custom controls (breadcrumb bar, virtualized file grid, tab strip, treemap, video scrubber)
- `Leviathan.Core`: domain model, location providers, commands, settings; no UI references
- `Leviathan.Providers.Local`, `.Smb`, `.Sftp`, `.Ftp`, `.Nfs`, `.Cloud`, `.Archive`: one location provider each, behind `ILocationProvider`
- `Leviathan.Interop`: CsWin32-generated P/Invoke and COM (shell, file system, network)
- `Leviathan.Contracts`: IPC contracts and DTOs
- `Leviathan.Worker`, `Leviathan.Media`, `Leviathan.ShellHost`, `Leviathan.Indexer`: the worker processes
- `Leviathan.Data`: SQLite stores (tags, metadata cache, journal, index)
- `Leviathan.Tests.*`: unit, integration (including a fault-injecting fake SMB share) and UI smoke tests

### 2.4 Technology stack

| Area | Choice | Why |
| --- | --- | --- |
| Runtime | .NET 10, C# 14 | Current LTS, matches the Files fork |
| UI | WinUI 3 (Windows App SDK) | Native Windows 11 look; required by the brief |
| Packaging | Unpackaged, self-contained (a sparse package can be added later if a shell feature needs app identity) | Simple native DLL loading and full file access; identity still available for shell features |
| Win32 / COM | CsWin32 source-generated bindings | Typed, trim-safe, no hand-written P/Invoke |
| Enumeration | `FindFirstFileExW` with `FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`; `NtQueryDirectoryFile` for large local folders | Several times faster than `StorageFolder` APIs |
| Change detection | `ReadDirectoryChangesW` (local), polling with back-off (network) | SMB change notifications are unreliable on many NAS boxes |
| Data | SQLite (Microsoft.Data.Sqlite, WAL mode) | Crash-safe, single-file, fast |
| IPC | StreamJsonRpc over named pipes | Mature, cancellation and progress built in |
| Video playback | libmpv and LibVLCSharp behind `IVideoEngine` | Broad codec coverage; switchable (D10) |
| Metadata | ffprobe (bundled ffmpeg build) | Complete stream and container info |
| SFTP / FTP | SSH.NET, FluentFTP | Well-maintained, async |
| NFS | Windows "Client for NFS" feature (Windows Pro/Enterprise) | Native mounts behave like drives |
| Archives | 7-Zip (`7z.dll`) via SevenZipExtractor, as Files does | Zip, 7z, rar, tar and more |
| Telemetry | OpenTelemetry to local file; Aspire Dashboard in dev | See 2.1 |

## 3. Performance, stability and resiliency

These are release-blocking requirements, each with a measurable budget checked in CI or a benchmark suite.

### 3.1 Performance budgets

| Scenario | Budget (p95) | Notes |
| --- | --- | --- |
| Cold start to first folder painted | < 600 ms | Session restore loads the active tab first, others lazily |
| New tab / new window | < 150 ms | Same process (D9) |
| Local folder, 10,000 items, first rows visible | < 100 ms | Streamed enumeration; rows appear as batches arrive |
| Local folder, 100,000 items, fully enumerated | < 1.5 s | Sort and filter applied incrementally |
| NAS folder (SMB, 1 Gbit), 10,000 items, first rows | < 400 ms |  |
| UI thread stall | Never > 50 ms | Enforced by a debug-build watchdog that logs the stack |
| Thumbnail for a visible item | < 200 ms from cache, < 1 s generated | Off-screen requests are cancelled |
| Video preview start (local) | < 300 ms | Engine is pre-warmed while the preview pane is open |
| Idle memory, 10 tabs | < 250 MB | Thumbnails held in a size-capped LRU |

### 3.2 Threading rules

1. The UI thread only touches view models and controls. No file, shell, network or COM call ever runs on it.
2. Every I/O call takes a `CancellationToken` and a timeout. Navigating away cancels all work for the old location.
3. Shell COM objects that need STA run on a dedicated pool of STA threads (or in ShellHost), never on the UI thread.
4. Results are marshalled to the UI in batches (every 16–50 ms), never per item.
5. Lists use `ItemsRepeater` with virtualization; item containers are recycled and never hold file handles.

### 3.3 Network resiliency

- **Health tracking per share.** Each server and share has a state: Online, Slow, Unreachable, Auth required. State comes from real I/O latency plus a lightweight probe, not from Windows' cached view.
- **Fast failure.** Calls on an Unreachable share fail in < 2 s with a clear banner in the tab ("\\\\nas\\media is not responding — Retry / Work offline"), instead of hanging the way Explorer does.
- **Isolation.** A hung share never blocks another tab, the tree, or the status bar. The tree marks the node offline and stops expanding it.
- **Back-off.** Retries use exponential back-off with jitter (1 s, 2 s, 4 s … up to 60 s). Change detection on network folders falls back to polling with the same back-off.
- **Reconnect.** Disconnected mapped drives are reconnected with stored credentials (Windows Credential Manager) when first used. Leviathan never stores passwords itself.
- **Offline cache.** The last listing of each visited network folder is cached and shown greyed out, marked "offline — as of 14:02", when the share is unreachable.

### 3.4 Stability

- Unhandled exceptions in the UI write a crash report, save the session, and offer a restart that restores all windows.
- Workers are supervised: restart on crash with back-off; after 3 crashes in 5 minutes the feature is disabled and the user is told.
- Any file that crashes ffprobe, a thumbnailer or a preview handler is recorded and skipped next time.
- Every destructive action is journaled and undoable (Ctrl+Z) until the journal entry expires.
- Settings and databases are written atomically (write to temp, flush, rename) and backed up daily.

### 3.5 Reference NAS

Leviathan is tuned and benchmarked against your NAS. Its 1 Gb/s link, not the disks, is the bottleneck for large transfers.

| Item | Value |
| --- | --- |
| Model | Synology DS2419+ · Intel Atom C3538 (4 cores, 2.1 GHz) · 16 GB RAM |
| OS | DSM 7.2.2-72806 Update 5 |
| Storage | One 82 TB volume (46.2 TB used, 56%) on 12 WD NAS drives of 5.5–16.4 TB in one storage pool |
| SSD cache | 2 × 1 TB WD\_BLACK NVMe on an E10M20-T1 card |
| Network | LAN 1 (onboard) and LAN 5 (the E10M20-T1's 10 GbE port) connected, **both at 1 Gb/s**, MTU 1500, same subnet; LAN 2–4 unused |
| Services in use | SMB, NFS, SSH (so SFTP works), WS-Discovery (so the NAS appears under Nearby) |

**What this changes in the design:**

1. **Throughput target.** One large-file copy over SMB should hold at least 105 MB/s, about 90% of the 1 Gb/s line rate. At that speed a 100 GB file takes about 16 minutes, so pause/resume and resume-after-drop matter more than raw speed.
2. **One job per NAS by default is right.** A single stream fills the link. Extra parallel jobs only split the bandwidth and add seek load on the hard drives. The per-device limit stays at 1, and the background lane pauses during NAS copies.
3. **Adaptive buffers.** 1–4 MB at 1 Gb/s, growing to 8 MB if the link is upgraded. Leviathan measures real throughput per share instead of assuming a speed, and shows the negotiated link speed in the status bar.
4. **Verification cost.** A full checksum of a 100 GB file means reading it back over the link: another 16 minutes. Default policy: full xxHash3 verify for files up to 4 GB; above that, verify size plus a sampled hash (first and last 64 MB and 16 random 4 MB blocks). Full verification stays available per job and per destination.
5. **One server, several names.** The NAS answers on two IP addresses, its NetBIOS name and a DDNS name. Leviathan identifies a server by its SMB server identity, not by the name typed. The tree shows it once, caches and folder sizes are shared across names, and server-side copy is used whenever both ends are on this NAS, whatever path was used.
6. **Same-NAS moves never cross the network.** Moves within a share are renames; copies between shares on the same volume use server-side copy, so a 100 GB move inside the NAS finishes at disk speed with no traffic to the PC.
7. **Recycle folder.** `#recycle` is used when the DSM Recycle Bin is enabled on that shared folder; Leviathan checks per share and falls back to its own folder where it is off.

**The switch is the bottleneck.** The PC's network card is a Realtek 2.5 GbE controller, but it links at 1000 Mb/s, and the NAS's 10 GbE port also links at 1 Gb/s. Both ends can go faster, so the switch between them is what holds everything to 1 Gb/s. A multi-gig switch (2.5 GbE ports plus at least one 10 GbE port for the NAS) would let a single large copy run at about 280 MB/s, 2.5× today's speed, with no other change. The design is ready for that: buffers adapt to measured throughput, and the per-share speed shown in the status bar will confirm the upgrade. SMB3 multichannel adds little here, because the PC has only one active port.

## 4. Window shell and tabs

Each window has, top to bottom: the tab strip in the title bar, a navigation row (back, forward, up, reload, address bar, search), the activity strip of thin progress lines (§12.6), the command bar, then a three-column body (sidebar tree | file view, optionally split | preview and details pane), and the status bar. The sidebar and the preview pane can each be collapsed and resized; their widths are remembered per window.

Layouts for the main window, split view with preview, triage mode, operations panel, drive summary and space map are in [Leviathan UI mockups](https://claude.ai/artifact/TBbfv46mBRQszt7giterdm).

### 4.1 Tab behaviours

| Behaviour | Trigger | Detail |
| --- | --- | --- |
| New tab | Ctrl+T, + button, double-click empty strip | Opens the workspace's "new tab" location (default: Home) |
| Open in new tab | Middle-click or Ctrl+click a folder (file view, tree, breadcrumb) | Opens in the background; Ctrl+Shift+click opens in the foreground |
| Duplicate tab | Ctrl+Shift+K, tab context menu | Copies path, history, view mode, sort, split state and selection |
| Close tab | Ctrl+W, middle-click tab, × | Last tab closes the window (setting: or resets to Home) |
| Reopen closed tab | Ctrl+Shift+T | Stack of the last 25 closed tabs, with history |
| Reorder | Drag within the strip | Animated; keyboard Ctrl+Shift+PgUp/PgDn |
| Tear off | Drag a tab out of the strip | New window in the same process at the drop point (D9) |
| Merge | Drag a tab onto another window's strip | Moves the tab with its history |
| Switch | Ctrl+Tab / Ctrl+Shift+Tab, Ctrl+1–8, Ctrl+9 = last | Optional MRU order for Ctrl+Tab |
| Pin tab | Tab context menu | Compact, icon only, left side, can't be closed by Ctrl+W, restored by every workspace that saves it |
| Lock tab | Tab context menu | Navigating into a folder opens a new tab instead; good for a fixed "inbox" tab |
| Color / rename tab | Tab context menu | Custom label and accent, e.g. "NAS inbox" in orange |
| Close others / to the right | Tab context menu |  |
| Tab groups | Tab context menu "Add tab to new group / to group ▸", or drag a tab into a group | Named, coloured, collapsible; see below |
| Drop files on a tab | Drag files over a tab, hover 600 ms | Switches to that tab so you can drop into a subfolder; dropping on the tab itself copies/moves into its folder |
| Offline indicator | Automatic | Tab icon shows a warning badge when its share is unreachable |
| Busy indicator | Automatic | Thin progress line under the tab while enumerating or while an operation targets it |
| Tab overflow | Many tabs | Scroll arrows plus a drop-down list of all tabs with search |

#### Tab groups (D32)

Modelled on tab groups in Microsoft Edge.

- A group has a name and a colour (from the same palette as tab colours). Its tabs sit together, with a coloured bar under them and a label chip at the start of the group.
- **Collapse:** clicking the label collapses the group down to just the label chip, showing the tab count; clicking again expands it. Ctrl+Tab and Ctrl+1–9 skip collapsed tabs.
- **Create:** right-click a tab → Add tab to new group (name it inline), or select several tabs (Ctrl+click) and add them together. Dragging a tab between two tabs of a group adds it; dragging it out removes it.
- **Group menu** (right-click the label): rename, change colour, new tab in group, ungroup, close group, move group to new window.
- **Drag the label** to move the whole group within the strip, out to a new window, or onto another window's strip.
- Pinned tabs stay outside groups. Groups, their order, colours and collapsed state are saved in workspaces and Last session (§17.1). Reopen closed tab (Ctrl+Shift+T) puts a tab back in its group, and closing a whole group can be reopened as a group.
- The all-tabs list shows tabs under their group headings.

### 4.2 Windows

- One process hosts all windows; all share caches, the tag index and the worker connections.
- Ctrl+N opens a new window; closing the last window ends the UI process but not a busy Worker.
- Window position, size and monitor are saved per window, with a safe fallback if a monitor has gone.
- Snap layouts, Mica backdrop, and a custom title bar with the tab strip in it.

### 4.3 Icons (D33)

Leviathan uses a full-colour icon set in the style of Windows 11 File Explorer rather than monochrome glyphs: yellow folders, distinct coloured icons for known folders (Desktop, Documents, Downloads, Pictures, Videos, Music), drives, NAS servers, shares, cloud providers, tags and file-type groups, and colour accents on command-bar actions (e.g. red Delete, blue Copy/Paste). Rules:

- Icons are vector (SVG or a colour icon font), crisp at 100–300% scaling, with light- and dark-theme variants where contrast needs it; high-contrast mode falls back to the system monochrome glyphs.
- File and folder icons in the file view still come from the Windows shell (so app-specific file icons appear); the colour set covers Leviathan's own chrome: command bar, tree, tabs, status bar, menus, the status hub and dialogs.
- Icons come from a permissively licensed set (e.g. Fluent UI System Icons colour variants), recorded in `THIRD-PARTY-NOTICES.md`; one `IconCatalog` maps command and node kinds to icons so a theme can swap them.

## 5. Navigation

### 5.1 Navigation buttons

| Button | Shortcut | Extras |
| --- | --- | --- |
| Back | Alt+Left, mouse button 4, Backspace | Right-click or long-press shows the history list |
| Forward | Alt+Right, mouse button 5 | Right-click shows the forward list |
| Up | Alt+Up | Right-click lists every ancestor |
| Reload | F5, Ctrl+R | Shift+F5 also clears the thumbnail and metadata cache for the folder |

After Back or Up, the folder you came from is selected and scrolled into view.

### 5.2 Address bar

Breadcrumb mode is the default. Each path segment is a button; a chevron after each segment opens a drop-down of its subfolders (type to filter).

- **Click** a segment to go there. **Middle-click** opens it in a new tab.
- **Drag files onto a segment** to copy/move there.
- **Edit mode:** click empty space, press Ctrl+L or Alt+D, or start typing. The full path appears selected.
- **Copy and paste paths.** Ctrl+C in edit mode copies the path; right-click offers *Copy path*, *Copy as UNC* (mapped drive to \\\\server\\share), *Copy as quoted*, *Copy as file:// URI*.
- **Accepts:** drive paths, UNC paths, `sftp://`, `ftp://`, `nfs://`, environment variables (`%USERPROFILE%`), shell folders (`shell:Downloads`), relative paths (`..\\other`), and a file path (opens the folder and selects the file).
- **Autocomplete** from the file system, history and favorites as you type, with Tab to accept.
- **Long paths** (> 260 characters) supported throughout using `\\\\?\\` prefixes internally.
- **Overflow:** when the path is too wide, leading segments collapse into a … menu.
- **Status:** a lock icon for paths needing elevation, a cloud icon for cloud folders, a warning icon for offline shares.
- **Favorite star:** a star at the right end of the address bar, outlined when the current folder is not a favorite and filled when it is. Clicking it pins or unpins the folder (same as Ctrl+D); right-click offers rename, icon and colour for the favorite.

### 5.3 Other navigation

- Type-ahead in the file view jumps to the first matching name.
- Ctrl+P opens a "go to" palette that fuzzy-searches favorites, recent folders, open tabs and commands.

## 6. Command bar

The command bar has three zones: fixed actions on the left, context actions in the middle that change with the selection, and view and options controls on the right. Every command is also in the command palette (Ctrl+Shift+P) and can be bound to a key.

Clipboard and context actions are **icon buttons with hover tooltips** that give the name and shortcut ("Copy (Ctrl+C)"), to keep the bar compact. Only New, the layout and sort pickers and the filter box carry text labels. A setting can show labels under the icons.

### 6.1 Fixed actions

**New ▾** (Ctrl+Shift+N creates a folder directly). The menu is built from three sources:

1. **Built-in:** Folder, Shortcut, Text document (.txt), Rich text (.rtf), Bitmap image (.bmp), Compressed (zipped) folder. These match Windows' own New menu.
2. **Registered by installed apps:** read from the `ShellNew` registry entries, the same source Explorer uses. Typically Word, Excel, PowerPoint, Access, Publisher, OneNote and LibreOffice documents, when installed.
3. **User templates:** any file in Leviathan's templates folder (e.g. `.md`, `.json`, `.ps1`, `.html`, `.gitignore`, `.editorconfig`). A "Manage templates…" item opens it.

New items are created with an inline rename box focused.

**Clipboard group:** Cut, Copy, Paste, Rename (F2), Delete (Del; Shift+Del for permanent), and Undo / Redo. Paste shows a split button with *Paste*, *Paste as shortcut*, *Paste as hard link*, *Paste as symbolic link*, *Paste into new folder*. Paste is disabled with a tooltip when the clipboard has nothing usable.

### 6.2 Context actions

| Selection | Actions shown |
| --- | --- |
| Nothing selected | Select all, Select none, Invert selection, Select by pattern (`*.mkv`), Select by tag, New tab here, Open terminal here, Space map of this folder |
| One folder | Open in new tab, Open in split pane, Add to favorites, Space map, Compress, Properties |
| Several items | Bulk rename, Compress, Compute total size, Compare (2 items), Move to new folder, Tag |
| Video | Play, Triage mode from here, Contact sheet, Extract frame, Show ffprobe info |
| Image | Open, Set as wallpaper, Rotate left/right (lossless where possible), Convert format |
| Audio | Play, Show tags |
| Document / PDF / text | Open, Open with…, Print, Preview |
| Archive | Extract here, Extract to "name\\", Browse as folder, Test archive |
| Executable / script / installer | Run, Run as administrator, Show checksum |
| Shortcut / link | Open target location, Edit target |
| Disk image (.iso, .vhdx) | Mount, Burn |
| Network share root | Disconnect, Reconnect, Map as drive |
| Cloud file | Always keep on this device, Free up space, View online |
| Soft-delete folder | Restore, Purge now |

Actions that don't fit in the bar go into an overflow … menu. Users can pin or hide any context action per type.

### 6.3 Right side

- **View ▾:** Details, List, Tiles, Large thumbnails, Gallery; density; show hidden / system files; show extensions; preview pane toggle; split pane toggle.
- **Sort ▾ / Group ▾:** quick access to sort and group fields.
- **Filter box:** filters the current folder as you type (not a search). Supports `*.mp4`, `size:>1GB`, `tag:red`, `duration:<2m`.
- **Options ⚙:** opens Settings (§17), and a menu with Folder options, Operations queue, Soft-delete bin, Diagnostics.

* **Panes:** a three-button group (single pane, split side by side, split top and bottom) changes the current tab's layout, so a split can be opened and closed back to one pane with one click. The same group sits beside the address bar on the mockups.
* **Preview pane toggle** (Alt+P) next to the pane group.

## 7. Sidebar tree

The sidebar is one virtualized tree with collapsible top-level sections, in this default order (reorderable, each can be hidden):

| Section | Contents | Notes |
| --- | --- | --- |
| **Favorites** | User-pinned folders, files and searches | Drag any folder here, or Ctrl+D. Custom name, icon and color. Ctrl+Alt+1–9 jump to the first nine |
| **Common folders** | Desktop, Documents, Downloads, Pictures, Videos, Music, Home, Recycle Bin, Settings (opens Windows Settings) | Resolved via Known Folder IDs, so relocated folders (e.g. Documents on D:) work |
| **Tags** | One entry per color tag | Shows every tagged item across locations (from the tag index) |
| **Local** | Fixed disks, removable drives, optical, mounted VHDs, WSL distributions | Each drive shows a free-space bar and label |
| **Network** | Saved servers, discovered devices, mapped drives, SFTP/FTP/NFS connections | See 7.1 |
| **Internet** | OneDrive (personal and business), Google Drive, Dropbox, Box, iCloud, Synology Drive, Proton Drive, and other sync clients | Detected from the Windows cloud-provider registry, as Files does; shows sync status |
| **Registry** | HKCR, HKCU, HKLM, HKU, HKCC | Optional, off by default, read-only (§7.3) |

Every node has an icon (known folders get their own: Desktop, Documents, Downloads, Pictures, Videos, Recycle Bin; drives, NAS servers, shares and cloud drives each have a distinct icon) and an expand arrow when it can have children. Drives, servers and shares expand like folders, so the tree can be used to drill down anywhere. Drives also show a thin usage bar and "312 GB free of 1 TB" under their name.

### 7.1 Network section

- **Saved servers** come first: servers you have connected to, with their shares. Each has a health dot (Online / Slow / Offline / Auth required).
- **Discovered devices** appear under a collapsed "Nearby" node, found by WS-Discovery and mDNS (`_smb._tcp`, which Synology and QNAP advertise). Discovery runs in the background and never delays the tree.
- **Add connection…** dialog for SMB, SFTP, FTP(S) and NFS: host, port, path, credentials (stored in Windows Credential Manager), optional "map as drive letter".
- Mapped drives appear here and under Local, with the UNC path in the tooltip.

### 7.2 Resiliency rules for the tree

1. Network nodes never expand automatically at startup; the last expanded state is restored only for nodes that are reachable.
2. Expanding a node shows a spinner child immediately; children arrive asynchronously with a 5 s timeout. On timeout the node shows "Not responding — Retry".
3. Child folders of a network node are listed without checking whether each has subfolders (the expand arrow is shown optimistically and removed if empty). This avoids one extra network round-trip per folder.
4. The tree follows the current folder ("sync with view" setting) without enumerating every level on network paths.
5. Drag and drop onto any tree node copies/moves there, with spring-loading: hovering for 700 ms expands it.

**Look-ahead loading.** When a node is expanded, its children are listed and, in the background, so is the next level down (the children's children), one level only. Expanding a child is then instant, without ever walking the whole file structure. The look-ahead runs at low priority, stops when you navigate away, is cached with the folder's modified time, and on network shares is limited to one request at a time per share.

### 7.3 Registry explorer (D41)

An optional node that lets the tree and the file view browse the Windows registry, read-only. It is **off by default**: **Settings → Files and folders → Show Registry in the sidebar** adds a **Registry** section after Internet, and turning it off removes it (open registry tabs stay open until closed).

**Model.** Keys are folders and values are items, so every existing control works without a special case.

| Registry | Leviathan |
| --- | --- |
| Root: `HKEY_CLASSES_ROOT`, `HKEY_CURRENT_USER`, `HKEY_LOCAL_MACHINE`, `HKEY_USERS`, `HKEY_CURRENT_CONFIG` | The five children of the Registry node, shown as `HKCR`, `HKCU`, `HKLM`, `HKU`, `HKCC` with the long name in the tooltip |
| A key | A folder; subkeys are its subfolders |
| A value | An item in the key's list. Columns: **Name** (the default value is "(Default)"), **Type** (`REG_SZ`, `REG_EXPAND_SZ`, `REG_MULTI_SZ`, `REG_DWORD`, `REG_QWORD`, `REG_BINARY`, `REG_NONE`, others by number), **Data** (rendered: text, decimal and hex for numbers, up to 64 bytes of hex for binary, then "…"), **Size** (bytes), **Modified** (keys only, the key's last-write time) |

- **Locations.** A registry location is a `LocationPath` of a new kind (`Registry`), written `registry:\HKCU\Software\Microsoft`; the contract version is bumped (§2.3). The address bar accepts that form and the common ones (`HKLM\SOFTWARE\Classes`, `HKEY_CURRENT_USER\Software`, `Computer\HKEY_LOCAL_MACHINE\…` as copied from Regedit); the breadcrumb, history, back / forward, up, tabs, split panes (§15.1), columns (§8.8) and workspaces (§17.1) work as for folders.
- **Read-only.** There is no cut, paste, delete, rename, new, drag target or edit anywhere in the registry: those commands are disabled with a tooltip "The registry is read-only". Nothing in Leviathan writes to it, and the provider has no write method to call (the interface it implements is the read-only subset of `ILocationProvider`).
- **What you can do:** open and browse; sort, filter and group the value list; **Copy path**, **Copy name**, **Copy data** (value data as text; binary as hex); **Open in Regedit** (jumps to the key by setting Regedit's last-key and launching it; this runs Regedit, not Leviathan, and may ask for elevation); **Export key to .reg…** (a read of the key and its subkeys written to a `.reg` file the user chooses, as a worker job with progress and cancel; the output matches Regedit's format); **Add to Favorites**. The preview pane shows the selected value's data in full: text, a hex dump for binary, a list for multi-string, with the key path and type above it.
- **Permissions.** Reading uses the current user's rights; Leviathan never elevates for the registry. A key or subtree the user cannot read shows a lock overlay and the "Access denied" state of §8.6; the rest of the tree is unaffected. `HKLM\SECURITY`, `SAM` and similar are shown, locked.
- **Both views.** The native 64-bit view is used. `WOW6432Node` appears as the key it is. A setting adds a "32-bit view" toggle later; it is not in this version.
- **Scale and stability.** A key can have tens of thousands of subkeys or values (e.g. `HKCR`). Keys and values are enumerated in streaming batches with the same batching, cancellation and 16–50 ms UI cadence as folders (§3.2); a key being changed while it is listed is tolerated (a value that disappears is skipped). Registry calls are fast and local but still run off the UI thread, with a timeout. A reparse-like loop is impossible in the registry except through symbolic links (`REG_LINK`), which are shown as items and not followed.
- **No indexing.** Registry locations are not part of search, the folder-size service, thumbnails, tags, soft delete, the listing cache or folder watching beyond a poll of the key's last-write time every 2 s while the key is shown (using `RegNotifyChangeKeyValue` when possible). The filter box (§6.3) works on the visible list.
- **Out of scope in this version:** editing, remote registries, offline hives (`reg load`), searching the registry for text, and a 32-bit view toggle. Each is a candidate for later and each changes the "read-only" rule, so each needs its own decision.

**Budgets and tests.** Opening a key with 10,000 subkeys shows the first rows within 100 ms; expanding a node in the tree never blocks the UI. Tests use an `IRegistry` interface with an in-memory fake in `tests/Leviathan.Testing` for the unit tests (paths, parsing of every address form, value rendering for every type, ordering, locked keys, huge keys, vanishing values), and integration tests against a scratch key under `HKCU\Software\LeviathanTests` that the test creates and deletes (never anything else).

## 8. File view

### 8.1 Layouts

| Layout | Shortcut | Description |
| --- | --- | --- |
| Details | Ctrl+Shift+6 | Rows with configurable columns; the default |
| List | Ctrl+Shift+5 | Compact multi-column names |
| Tiles | Ctrl+Shift+4 | Medium thumbnail with 2–3 lines of metadata (e.g. duration and resolution for video) |
| Thumbnails | Ctrl+Shift+2 | Grid; Ctrl+mouse wheel resizes from 64 to 512 px |
| Gallery | Ctrl+Shift+1 | Large justified thumbnails for photo and video folders, with hover scrub |
| Columns | Ctrl+Shift+3 | Miller columns: one folder per column, the path reads left to right (§8.8) |

Layout, sort, grouping and columns are remembered per folder, with inheritance: a setting can apply to "this folder", "this folder and subfolders" or "all folders of this type" (Videos, Pictures, Music, Documents, General, detected from the content).

### 8.2 Columns (Details view)

Right-click the header for a quick list of columns to show or hide; drag to reorder; double-click a divider to auto-fit. **View → Columns…** (also at the bottom of the header's right-click menu) opens a column chooser: every available column grouped as below, with a search box, tick boxes, drag to reorder, default width, and "Reset to default" / "Apply to all folders of this type". The chosen set is saved with the folder's view settings (§8.1). Available columns:

- **File system:** Name, Extension, Type, Size, Size on disk, Date modified, Date created, Date accessed, Attributes, Owner, Path, Link target, Item count (folders, computed lazily)
- **Leviathan:** Tags, Soft-deleted date, Checksum (when computed), Duplicate group
- **Media (ffprobe, D16):** Duration, Resolution, Frame rate, Video codec, Audio codec, Bitrate, Container, HDR, Audio channels, Audio language(s), Subtitle tracks
- **Image (EXIF):** Dimensions, Date taken, Camera, Lens, ISO, GPS present
- **Audio:** Artist, Album, Title, Track, Year, Genre
- **Document:** Title, Authors, Page count
- **Windows property system:** any `System.*` property, by search

Metadata columns fill in lazily for visible rows only. Until then they show a faint placeholder. Sorting by a column that is not yet filled in starts a background fill for the whole folder, with a progress bar in the column header.

### 8.3 Sort, group and filter

- Sort by any column, with secondary sort (Shift+click a header). Natural sort for names (file2 before file10).
- Folders first, mixed, or last.
- Group by any column, with collapsible groups (e.g. group by Tag, by Date modified buckets, by Resolution).
- Filter box (§6.3) narrows the view without searching subfolders.

### 8.4 Selection and interaction

- Single click selects, double-click or Enter opens (setting: single-click to open).
- Rubber-band selection, Ctrl/Shift multi-select, checkboxes (optional), Space to toggle in keyboard mode.
- Inline rename with F2: the name is selected without the extension; Tab moves to the next item's rename.
- Drag and drop between tabs, windows, panes, the tree, the breadcrumb and other apps. Default: move on the same volume, copy across volumes; Shift forces move, Ctrl forces copy, Alt creates a shortcut. A tooltip always says which one will happen.
- Launch uses the Windows file association; "Open with…" lists registered apps.
- Folder sizes in the Size column, filled in by the folder-size service (§12.7) and never blocking.

### 8.5 Right-click menu

The menu has Leviathan's own items first (Open, Open in new tab, Open with, Cut, Copy, Paste, Rename, Delete, Tag ▸, Compress / Extract, Copy path, Properties), then the context actions from §6.2. Third-party shell extension items (7-Zip, Git, VS Code, etc.) are loaded asynchronously from ShellHost into a "More options ▸" group, so a slow extension never delays the menu. Shift+right-click shows them inline, like Explorer's classic menu.

### 8.6 Empty, loading and error states

- Loading: skeleton rows appear immediately, then stream real items.
- Empty folder: "This folder is empty" plus New and Paste buttons.
- Access denied: explains why, with "Open as administrator" (which relaunches only that tab in an elevated window).
- Offline share: last cached listing, greyed, with a Retry banner (§3.3).

### 8.7 Drive and share summary

Opening a drive (or a NAS share root) shows a summary card above its normal file list:

- Name, type (local disk, removable, NAS share), file system and drive type (SSD / HDD).
- A usage bar broken down by file type, from the folder-size service, with used, free and total, and a warning color under 15% free.
- Facts: health (SMART for local disks, share health for NAS), search status (e.g. "Indexed by Everything"), folder-size freshness, Recycle Bin or soft-delete size, tagged item count.
- Actions: Open space map, Find duplicates, Empty Recycle Bin, Open terminal here, Properties.
- The list below shows each folder with its size and a "share of drive" bar, sorted by size by default.
- The card can be hidden; that choice is remembered per drive.

### 8.8 Column view (Miller columns) (D38)

A fourth way to look at a folder, for tracing a deep path at a glance. Each column lists **one folder's contents**; selecting a folder in a column shows that folder's contents in a new column to its right, so the path from the root to the current item reads left to right.

**Behaviour**

- **Columns layout** (`Ctrl+Shift+3`, View ▾ → Columns) sits beside Details, List, Tiles, Thumbnails and Gallery in §8.1. It is chosen per folder like any other layout and inherits the same way. The tab's current folder is the **rightmost column that holds a folder you are in**; the columns to its left are its ancestors, each with the next one down highlighted.
- **Selecting.** Click, or Up / Down, selects an item in a column. Selecting a **folder** opens its contents in the next column (replacing any columns beyond it). Selecting a **file** adds no column: the preview pane (§9) shows it, as for any layout. The last column never shows a built-in preview; the preview pane is the one place for that.
- **Opening.** Enter, or a double-click, on a folder makes it the tab's folder: the address bar, history, title and status bar follow, and the columns re-anchor so the opened folder is the rightmost. Right / Left arrows move focus to the next / previous column (Right on a folder selects its first child, as in Finder); Alt+Up and the breadcrumb work as in every layout.
- **Look-ahead.** Because the next column is listed as soon as a folder is selected, the existing look-ahead rule (§7.2) is not needed here; instead moving the selection with the keyboard waits 150 ms before listing, so holding Down in a column of 5,000 folders does not list 5,000 folders.
- **Same rules as every list.** Every column is a normal virtualized list with the folder's sort, grouping, filter, hidden-file setting, tags, selection model, drag and drop, context menu and shortcuts. Cut, copy, paste, rename and delete act on the selection in the focused column. Multi-select works within one column.
- **Network folders behave exactly like local ones**: one folder per column, listed in streaming batches (§3.1, §3.3), cached and watched as usual (§7.2). A column whose share is slow shows the loading state of §8.6 inside that column only; a dead share shows its error inside that column and never blocks the others. No column ever lists more than one folder, and never lists a whole tree.

**Sizing and how many columns show**

- Each column has a **width** the user can change by dragging the divider on its right edge; double-click a divider to fit the widest name in that column (measured from the visible rows only). Widths are in pixels.
- A column is never narrower than the **minimum width** (default 160 px) or wider than the **maximum width** (default 480 px); both are settings (Files and folders → Column view). New columns open at the **default width** (default 240 px), or at the width the user last gave a column at that depth in this folder.
- The number of columns that fit follows from the width of the file area: `visible = floor((area − dividers) / width)` for the columns at their current widths, always showing at least one. When there are more columns than fit, the view **scrolls horizontally** and keeps the rightmost columns (the ones that hold the current selection) in view, scrolling smoothly as the selection moves. Resizing the window or the preview pane, or splitting the tab (§15.1), re-flows the columns by the same rule without re-listing anything.
- A column that has been scrolled out to the left is kept in memory (its listing is retained, bounded by the same 200,000-item listing cache as every folder, §3.1) so that scrolling back is instant.

**Rendering**

- Each row is the same as the Details view's name cell: icon (§4.3), name, tag dots, a chevron at the right of a folder row, and a sync / health badge where it applies. Size and date are not columns here; they appear in the tooltip, the preview pane and the status bar. An optional second line (setting) shows size and modified date.
- The selected row in a column that is not the focused one is shown with a quiet highlight so the path stays readable.
- Column headers show the folder's name; right-clicking a header offers Open in new tab, Open as the tab's folder, Copy path, and Close columns to the right.
- Empty folders show "Empty" in the column; unreadable ones show the error of §8.6.

**Budgets and tests**

- First rows of a new column appear within 100 ms of selecting a local folder with 10,000 items, and within 400 ms on the reference NAS (§3.1). Selecting a folder never blocks the UI; a slower listing than the next selection is cancelled.
- View-model tests cover: path-to-columns mapping, selecting a folder / file, replacing trailing columns, re-anchoring on open, width clamping, the visible-count formula, the 150 ms keyboard debounce (with `ManualTimeProvider`), cancellation when selection moves on, and a dead share in one column leaving the others usable. A UI smoke test covers resizing and horizontal scroll.

## 9. Preview and details pane

The right-hand pane (toggle Alt+P) has two stacked parts: the **preview** on top and the **details** below, separated by a draggable splitter. Either part can be collapsed. Space opens a large floating preview of the selection (Quick Look style) and Space again closes it.

- **Collapse and resize:** the pane collapses with a button in its header or Alt+P, and its width is set by dragging the splitter on its left edge.
- **Video size:** drag the handle under the video to make it taller or shorter, or use the S / M / L buttons in the pane header (L fills the pane width). Size is remembered per window.
- **Playback controls are icons:** back 5 min, 1 min, 30 s, play/pause, forward 30 s, 1 min, 5 min, and skim, each with a tooltip giving its shortcut.
- **In split view** the pane previews the selection in whichever file pane has focus (the header says "Preview · left pane"), with Move (F6) and Copy (F5) to the other pane right under the video, so a file can be checked before it is moved.

### 9.1 Preview renderers

| Type | Renderer |
| --- | --- |
| Video | libmpv or LibVLC (D10) with the skim controls of §10.2 |
| Audio | Same engine, waveform plus cover art |
| Images (JPEG, PNG, GIF, WebP, HEIC, AVIF, RAW, SVG, PSD thumbnail) | Windows imaging (WIC) with zoom, pan, EXIF orientation; animated GIF/WebP play |
| PDF | Windows.Data.Pdf, page thumbnails, text select |
| Text and code | Read-only editor with syntax highlighting, first 2 MB, encoding detection |
| Markdown, HTML | Rendered in a sandboxed WebView2 with scripts and network disabled |
| Office documents | Registered shell preview handler, hosted out of process in ShellHost |
| Archives | File list inside the archive |
| Fonts | Sample text at several sizes |
| Folders | Item count, total size (lazy), and a mini space map |
| Multiple items | Count, total size, type breakdown, and a strip of thumbnails |

Previews load after the selection has rested for 150 ms (so arrowing through files stays fast), are cancelled when the selection changes, and are capped by size and time so a huge file never stalls the pane.

### 9.2 Details

Details show every piece of metadata Leviathan can read, grouped and collapsible:

- **General:** name, path (click to copy), type, size and size on disk, dates, attributes, owner
- **Tags:** color tags with an inline editor
- **Media:** a summary line ("1h 42m · 3840×2160 · HEVC 10-bit HDR10 · 23.976 fps · 18.4 Mb/s") then one block per stream (video, each audio track with language and channels, each subtitle), chapters, and container tags
- **Image:** EXIF, IPTC and XMP, with a small map link when GPS is present
- **Audio:** ID3 / Vorbis tags, cover art
- **Document:** title, author, page count, word count
- **Windows properties:** every populated `System.*` property, in an "All properties" expander
- **Integrity:** compute MD5 / SHA-1 / SHA-256 / xxHash on demand, with copy buttons

Editable fields (tags, rating, title, comments, audio tags) are edited in place and written through the Windows property system or the format's own writer.

### 9.3 ffmpeg integration

- ffprobe runs in Leviathan.Media with `-show_format -show_streams -show_chapters -of json`, a 10 s timeout per file, and low process priority.
- For network files it reads only what it needs (container headers), not the whole file.
- Results are cached in SQLite, keyed by full path + size + last-modified time; a change in any of those invalidates the entry.
- A file that times out or crashes ffprobe is marked "unreadable" and is not retried until it changes.
- A bundled GPL ffmpeg build (full codec set; fine for a personal project) is used by default; Settings can point at a different ffmpeg folder.

## 10. Video triage

The aim is under 10 seconds per video to decide keep or discard, without touching the mouse. Four tools cover different speeds of review; each can be turned on or off in Settings (D7).

### 10.1 Triage mode

Start it with T, or "Triage mode from here" on a selection or folder. The window switches to a full-window player with a slim side list of the queue.

- **Queue:** the selected videos, or every video in the folder (optionally recursive), in the current sort order. A filter can limit it to "untagged only", so you can resume where you left off.
- **Instant switching:** the next file is pre-opened and buffered in a second, hidden player instance. On a NAS, the first few MB of the next two files are read ahead.
- **Decisions apply immediately** and auto-advance (setting: delay 0–1 s, or don't advance). Discard is a soft delete (D8), so nothing is lost; Ctrl+Z undoes the last decision and goes back to that file.
- **Start point:** each file starts at a configurable offset (e.g. 10%) to skip intros; set to 0 to start at the beginning.
- **Progress bar** across the top: "48 / 312 · 12 kept · 30 discarded · 6 skipped", plus space reclaimed so far.
- **Overlay:** file name, size, duration, resolution and codec, fading out after 2 s; I toggles it.
- **Summary** when the queue ends, with "Review discarded" (restore any) and "Purge discarded now".

**Working at scale.** Expected load: 30,000+ untriaged videos in a library of about 500,000 files, triaged around 1,000 at a time over a week, with new files arriving all the time. Files are 200 MB–8 GB, some near 100 GB.

- **Triage inboxes:** a saved, named queue over a folder (e.g. `\\\\nas\\inbox`, recursive) that persists across sessions. Decisions live in the tag index, so "untriaged" (no Keep or Discard yet) is an instant filter even over 30,000 files.
- **New arrivals** are picked up when the inbox is opened and by a low-priority scheduled check, appended to the queue, and marked New.
- **Sessions:** start a batch of N files (default 1,000), ordered oldest, newest, largest or random. Progress shows both the batch and the whole inbox ("612 / 1,000 this session · 4,212 of 31,880 triaged").
- **Large files never get copied for viewing.** Playback streams from the share and seeking reads only the byte ranges it needs. Pre-buffering the next file reads its header and index (for MP4 files that keep the index at the end, the tail too), about 8 MB. Contact sheets seek to keyframes, so a 100 GB file costs about the same as a 1 GB file.
- **Keep with move:** when the target folder is on the same share, the move is a rename and is instant; otherwise it becomes a normal queued job.

### 10.2 Default keys (all remappable)

| Key | Action |
| --- | --- |
| Space | Play / pause |
| Right / Left | Jump forward / back 30 s |
| Shift+Right / Shift+Left | Jump forward / back 1 min |
| Ctrl+Right / Ctrl+Left | Jump forward / back 5 min |
| Alt+Right / Alt+Left | Fine seek 5 s |
| . / , | Next / previous frame (paused) |
| Ctrl+1 … Ctrl+9 | Jump to 10% … 90% |
| Home / End | Start / last 10 s |
| J / K / L | Reverse-skim / pause / forward with increasing speed (1×, 2×, 4×, 8×) |
| \[ / \] | Playback speed down / up (0.5× to 4×, audio pitch-corrected) |
| S | Toggle skim playback (10.3) |
| Enter or G | **Keep**: green tag, or green tag + move to a chosen folder (setting), next file |
| Delete or X | **Discard**: soft delete, next file |
| 1 … 9 | Toggle a color tag (no advance) |
| Down or Tab | Skip: no decision, next file |
| Up or Shift+Tab | Previous file |
| C | Show contact sheet for this file |
| R | Rename inline |
| M | Move to a preset folder (choose from up to 9 targets) |
| Ctrl+Z | Undo last decision |
| Esc | Leave triage mode, back to the folder with the current file selected |

### 10.3 Skim playback

Skim plays a short burst (default 3 s) at evenly spaced points (default every 5% of the duration), so a 1-hour video is sampled in about a minute. Available in triage mode and in the preview pane. Bursts run back-to-back with no seek gap because the next point is decoded ahead.

### 10.4 Contact sheet

A grid of frames (3×3 to 6×6) taken evenly across the video, generated in Leviathan.Media using keyframe seeking (fast, no full decode) and cached as one JPEG. Shown in the preview pane, the floating preview or triage mode. Click a frame to start playback there. Contact sheets can also be generated for a whole folder in the background and exported.

### 10.5 Hover scrub

In Tiles, Thumbnails and Gallery layouts, moving the mouse across a video thumbnail scrubs through it: the horizontal position maps to time. Uses a sprite strip of 16–24 frames generated on first hover (cached alongside the thumbnail), with a thin progress line. Hovering still for 1 s can optionally start muted live playback.

### 10.6 Preview pane timeline

In the normal preview pane, the seek bar has a filmstrip of thumbnails above it and shows a frame preview when hovered. The same jump keys work when the preview has focus.

## 11. Color tagging

Tags are named colors; a file can have several. They are stored with the file and indexed locally (D11), so they survive renames and moves and are still fast to search.

### 11.1 Model

- Default set of 7 tags: Red, Orange, Yellow, Green ("Keep"), Blue, Purple, Gray. Each can be renamed, recolored, reordered, or added to (up to 32).
- Keys 1–9 toggle the first nine tags on the selection, in the file view and in triage mode. Ctrl+0 clears all tags.
- Tags show as colored dots after the name (Details), a corner badge (thumbnails), and an optional row tint.
- The Tags section of the sidebar lists everything with a given tag, across all indexed locations.

### 11.2 Storage

| Layer | What | Why |
| --- | --- | --- |
| Alternate data stream `:leviathan.tags` | Small JSON with tag IDs and the time set | Travels with the file on NTFS and on SMB shares that support streams (Synology and QNAP do by default) |
| SQLite tag index | Path, volume serial + file ID, tag IDs | Instant "show all red files" and tag filters without reading every file |
| Fallback for no-stream file systems | Index only, keyed by path + size + modified time | FAT32/exFAT drives, SFTP, FTP, NFS |

Rules:

1. Writing a tag restores the file's original modified time afterwards, so tagging never changes "Date modified" or triggers backup software.
2. The copy engine copies alternate streams, so tags follow copies and moves made in Leviathan.
3. When a folder is opened, tags are read from streams for visible items and reconciled with the index (a file moved by another app keeps its tags; the index catches up).
4. Compatibility: Leviathan reads the Files app's `:files` stream on import, so tags you already set in Files are kept. Optionally it also writes the Windows "Tags" property for formats that support it (JPEG, MP4, Office), so tags are visible in Explorer.
5. A "Tags may be lost" warning appears when copying tagged files to a destination that can't hold streams.

## 12. File operations engine

All copy, move, delete, rename-many, extract and sync jobs run in Leviathan.Worker from a persistent queue (D12). The UI only submits jobs and displays progress, so closing a window or a UI crash never interrupts a transfer.

### 12.1 How a copy works

1. **Plan.** The job enumerates sources and writes the full item list to the SQLite journal before copying anything. Free space at the destination is checked up front.
2. **Pick the fastest path per item:**
   - Same volume move: rename (`MoveFileEx`), instant.
   - Both ends on the same SMB server: `CopyFile2`, which lets the NAS copy server-side at disk speed without data crossing the network.
   - Otherwise: Leviathan's chunked copier (1–8 MB buffers, sized by link speed; unbuffered I/O for files over 256 MB).
3. **Write safely.** Data goes to `name.ext.leviathan-part` and is renamed into place only when complete, so a half-copied file never looks finished.
4. **Resume.** The journal records completed bytes per file. After a network drop, worker restart or reboot, the copy continues from the last confirmed chunk.
5. **Preserve:** timestamps, attributes, alternate data streams (tags), and optionally ACLs.
6. **Verify (optional, default on for moves to or from a NAS):** compare xxHash3 checksums of source and destination (in full up to 4 GB; sampled above that by default, see §3.5). On a cross-volume **move, the source is deleted only after the copy is verified.**

### 12.2 Network failures

- Transient errors (network name no longer available, semaphore timeout, unexpected network error) pause the job, not fail it. The job shows "Waiting for \\\\nas — retrying in 8 s" and resumes automatically when the share returns (default: keep trying for 30 minutes).
- Permanent errors (access denied, disk full, invalid name for the destination file system) ask once with "Skip / Retry / Skip all like this"; the job can also be set to skip and report at the end.
- Names that are invalid on the destination (e.g. `:` or trailing dots copied to SMB) are offered an automatic safe rename.

### 12.3 Queue

Every operation (copy, move, delete, purge, extract, compress, sync, bulk rename) becomes a **job** in one persistent queue in Leviathan.Worker. A scheduler takes queued jobs in priority order and runs as many at once as the limits allow.

- **Concurrency is configurable and live:** a global limit of running jobs (default 3, range 1–16), plus a limit per destination device (default 1 per NAS volume or spinning disk, 2 per SSD). A job waits in the queue until both limits have room. Changing a limit takes effect immediately, without restarting jobs.
- **Threads per job:** large files are copied one at a time with big buffers; folders of many small files can use parallel file copies within a job (default 1 for NAS/HDD, 4 for SSD).
- **Background lane:** folder-size calculation (§12.7), metadata probing and thumbnails run in a separate low-priority lane with its own thread budget (default 2), so they never take a slot from your operations. The lane pauses automatically while a job is copying to or from the same NAS.
- **Priorities:** High, Normal, Low. "Run now" starts a job immediately, above the limit.
- **New jobs:** paste and drop add a job to the end of the queue by default. A setting can make new jobs start paused, so you can stage a batch of moves and then release them together.
- **Bandwidth limit** per job or per destination, with an optional schedule (e.g. unlimited at night).
- **Persistence:** the queue survives UI restarts, worker restarts and reboots. On the next start, queued and interrupted jobs continue (setting: or wait for you to resume them).
- **Reports:** every finished job keeps a report (copied / skipped / failed, with reasons) for 7 days.

Job states: Queued → Running → Completed, with Paused, Waiting (share offline, retrying), Failed (some items failed, others done) and Cancelled.

### 12.4 Conflicts

One conflict dialog for the whole job, not one per file. Options: Replace, Skip, Keep both (adds " (2)"), Replace if newer, Replace if larger, Replace if different (checksum), and **Compare**, which shows both files side by side with thumbnails and metadata (useful for two versions of a video). "Apply to all remaining conflicts" is a checkbox, plus per-file overrides in a list.

### 12.5 Delete and soft delete (D8)

| Location | Delete | Shift+Delete |
| --- | --- | --- |
| Local NTFS drive | Windows Recycle Bin | Permanent, after confirmation |
| Network share (SMB, NFS) | Soft delete | Permanent, after confirmation |
| SFTP / FTP | Soft delete if the server allows rename, else permanent with confirmation | Permanent, after confirmation |
| Removable FAT/exFAT | Permanent with confirmation (Windows does the same) | Same |

Soft delete moves the item into the share's own recycle folder when there is one: Synology `#recycle` (and QNAP `@Recycle`), detected when the share is first opened. The NAS's own recycle-bin rules and web UI then cover these files too. Where no recycle folder exists, Leviathan uses its own hidden `.leviathan-trash` folder at the root of the share (or the highest folder you can write to). Either way it is a rename on the same share, so it is instant even for a 100 GB file. Leviathan keeps its own manifest of original path and deletion time, so Restore works in both cases.

- The **Soft-delete bin** view lists all soft-deleted items across shares, with Restore and Purge.
- Automatic purge after a configurable age (default 30 days) or when the bin passes a size limit; purges run in the worker and are reported.
- Ctrl+Z restores the last delete, wherever it went.
- Leviathan hides `.leviathan-trash` folders by default; other tools will see them as hidden folders.

### 12.6 Activity strip and Operations panel

**Activity strip.** A band of thin progress lines sits directly under the address bar, across the full width of every window. Each running job and each active background task draws its own line, 1 or 2 px high (setting), stacked one above another. **Every job gets its own color** when it is queued, taken in turn from a palette of 8 distinct colors and kept for the job's whole life. The same color marks that job's row in the Operations panel, its line in the strip and its entry in the status bar, so two copies running at once are easy to tell apart. The job type is shown by an icon, not by color.

| Palette rule | Detail |
| --- | --- |
| Assignment | Next unused color in a fixed palette of 8, in order; a color is reused only after its job finishes |
| More than 8 active jobs | Colors repeat, with the job's queue number shown in the tooltip and panel |
| Background tasks (folder sizes, metadata, indexing) | One shared neutral gray, so they never compete with your jobs for attention |
| Errors | Red is reserved for failures and never assigned to a job |
| Accessibility | Palette chosen to stay distinct in light and dark themes and for common color-vision deficiencies; each line also has a tooltip with the job name |

- **States:** a solid line fills left to right as the job progresses. While the total is still unknown (enumerating), the line shows a moving shimmer. Paused shows a dashed line; Waiting for a share pulses; Failed items turn the line's tip red until the report is opened.
- **Hover** shows a tooltip per line: job name, percent, bytes, speed, ETA. **Click** opens the Operations panel scrolled to that job.
- **Many jobs:** up to 4 lines stack (8 px at 2 px each). Beyond that, the extra jobs merge into one combined line in that last slot, with a "+3" badge.
- When nothing is running, the strip takes no space.
- Settings choose which categories draw lines (e.g. hide metadata probing) and the color for each type.

**Operations panel** (Ctrl+Shift+O, or click the strip or the status-bar operations area). It is the Operations tab of the status hub (§16.1): by default it flies up from the status bar, and it can be docked or torn off into its own window.

- **Groups:** Running, Queued (in run order), Completed and Failed (last 7 days).
- **Each row:** color swatch, type icon, description ("Move 312 items to \\\\nas\\media\\keep"), progress bar, bytes and items done / total, speed, ETA, state. Expanding a row shows the current file, the file list with per-file status, and errors.
- **Row actions:** Pause / Resume, Cancel, Remove from queue (queued jobs), Move up / down / to top / to bottom (or drag to reorder), Set priority, Run now, Retry failed items, Show report, Open source / destination, Copy details. Works on a multi-selection.
- **Panel toolbar:** Pause all, Resume all, concurrency limits (editable live), bandwidth limit, Clear completed.
- **Speed graph** for the selected job, and a total queue ETA.

**Panel positions,** like a browser's developer tools, chosen from buttons in the panel header:

| Position | What you see |
| --- | --- |
| Fly-up from the status bar (default) | The status hub (§16.1) rises over the bottom of the file view on the Operations tab and drops back when you click away, unless pinned open. When it is closed you see only the thin progress lines under the address bar and the status-bar chip ("2 jobs · 1 queued · 112 MB/s · 2 min left") that opens it |
| Docked bottom | Panel under the file view, height draggable |
| Docked right | Panel beside the file view, width draggable |
| Popped out | Its own window, which can go on another monitor; closing it docks it back |

**Speed sparklines:** every running job shows a sparkline of its speed over the last 2 minutes next to the current number, in the job's color. The status-bar chip shows one for total speed, so stalls and drops on the NAS link stand out at a glance.

### 12.7 Folder-size service

Folder sizes are calculated lazily in the background lane, stored in the local database, and kept current as folder contents change. The Size column, the preview pane, the status bar and the space map (§14) all read from the same store.

**What is stored per folder:** size of the files directly in it, file count, recursive total size, total file and folder counts, size on disk, the folder's last-modified time when measured, when it was computed, and a state (Fresh, Stale, Computing, Partial, Error). Folders are keyed by volume serial + file ID on NTFS (so renames and moves keep their sizes), and by path elsewhere.

**When it computes (lazy):**

1. A folder is on screen with the Size column showing, is selected, is shown in the preview pane, or is opened in the space map.
2. The service works bottom-up: subfolder totals first, then the parent's total = its own files + its children's totals. Every subfolder's size is stored along the way, so opening a child later is instant.
3. Partial results show straight away with a "≥" prefix ("≥ 412 GB") and fill in as subfolders finish.
4. Priority: what you are looking at now, then selected items, then the rest of the folder. Work that goes off screen is paused, not discarded, and resumes from where it stopped.

**Keeping it current:**

- **Changes made by Leviathan** (copy, move, delete, triage): the worker knows the exact bytes added or removed, and applies that delta to the folder and every ancestor at once. No rescan.
- **Local NTFS:** the change journal (when the indexer is installed) or folder watchers report changes; affected folders are recounted and the difference is passed up the ancestor chain.
- **Network shares:** change notifications from NAS boxes are unreliable, so a folder is checked when visited. A changed folder last-modified time marks it Stale and triggers a recount of that folder only. Opted-in shares are re-validated on a low-priority schedule. A folder's last-modified time does not change when a file deeper down is edited in place, so sizes on a share always show their age ("as of 3 days ago"), with Refresh to recount.
- **Stale values stay visible** (shown in italic) until the new value arrives, so columns never blank out.

**Rules:** junctions and symbolic links are not followed (prevents loops and double counting); hard links count once; cloud placeholder files report logical size and size on disk separately; one enumeration per NAS share at a time by default; a folder that errors (access denied) shows the partial total with a warning icon.

## 13. Search and indexing

Search combines three sources (D14) and shows results as they arrive, in a normal file view tab that supports every layout, column, tag and action.

| Source | Scope | Speed | How |
| --- | --- | --- | --- |
| Local index | All local NTFS volumes | Instant (< 50 ms for name search) | Queries Everything (voidtools) through its SDK when it is installed and running; it already does MFT and change-journal indexing, free. Otherwise Leviathan.Indexer does the same (optional service, admin install) |
| NAS index | Folders you opt in (e.g. `\\\\nas\\media`) | Instant once built | Background crawl at low priority, re-crawled on a schedule and on change detection; stored in SQLite |
| Live search | Anything else, or when no index covers the path | Streams as found | Parallel recursive enumeration in the worker, cancellable |

**Why the NAS index stays Leviathan's own.** Everything can index a NAS share, but only by crawling it over SMB on a schedule, the same way Leviathan does. It does not see changes made on the NAS by other devices until the next rescan. Its Everything Server, which does track remote changes, runs only on Windows, so it cannot run on the Synology. Leviathan's folder-size service (§12.7) already crawls the NAS, so the same pass fills the search index at no extra cost, and search results can carry tags, folder sizes and media metadata. If Everything has a share indexed too, Leviathan merges its results rather than duplicating them.

### 13.1 Query syntax

Plain words match names. Filters can be typed or picked from a filter bar:

- `ext:mkv,mp4` · `size:>2GB` · `modified:lastweek` · `created:2025` · `tag:red` · `type:video`
- Media fields from the metadata cache: `duration:>30m` · `res:>=2160` · `codec:hevc`
- `content:"invoice"` searches text inside documents (via Windows Search when the location is indexed by Windows, otherwise a live text scan)
- Wildcards (`*`, `?`), quoted phrases, `-exclude`, and `regex:`

### 13.2 Saved searches

Any search can be saved and pinned to Favorites. A saved search behaves like a folder that updates itself (e.g. "Untriaged videos on NAS" = `type:video -tag:green path:\\\\nas\\inbox`).

## 14. Space usage visualizer

"Space map" (Ctrl+Shift+M, or from the context menu of any folder, drive or share) opens a WinDirStat-style view in a new tab.

- **Layout:** a sortable tree of folders with size, percentage bar, file count and last modified on the left; a **squarified treemap** on the right where every file is a rectangle sized by its bytes and colored by file type; a legend of extensions by total size underneath.
- **Linked selection:** selecting in the tree highlights the rectangle and vice versa. Double-click a rectangle to zoom into its folder; Backspace zooms out.
- **Scanning** runs in the worker, streaming results so the map fills in progressively. On local NTFS volumes with the indexer installed, a whole-drive map appears in seconds from the index. Network scans run with limited parallelism and can be paused.
- **Caching:** the last scan of each location is saved with its date, so reopening is instant; "Rescan" refreshes only changed folders.
- **Actions** on anything in the map: open, reveal in a tab, tag, delete (soft delete on NAS), compress, find duplicates in this folder.
- **Extras:** color by age instead of type (finds stale data); filter to files over a size; show hard links and cloud placeholders separately so space isn't double-counted.
- **Mini map:** the preview pane shows a small treemap for a selected folder once its size is known.
- **Status hub Space tab** (§16.1): a compact treemap of the current folder (from the folder-size store and the last scan), with the largest items listed beside it; clicking a rectangle selects that item in the file view, and "Open space map" opens the full view.

## 15. Power features

### 15.1 Split pane (D13)

- Ctrl+\\ splits the current tab vertically; Ctrl+Shift+\\ splits horizontally. Each pane has its own path, history, layout and selection; the address bar and command bar follow the focused pane.
- F5 copies and F6 moves the selection to the other pane (through the normal queue). Ctrl+Shift+S swaps the panes; Alt+= makes the other pane show the same folder.
- Split state, including whether it is horizontal or vertical, is saved with the tab, Last session and workspaces (§17.1).

* Split and single-pane buttons sit in the command bar (§6.3); closing the split keeps the focused pane.
* The preview pane stays available in split view and follows the focused pane (§9).

### 15.2 Duplicate finder

- Scope: one or more folders, drives or shares; filters by type and minimum size.
- **Exact duplicates:** group by size, then a partial hash of the first and last 1 MB, then a full xxHash3 only for candidates still matching. This avoids reading most files over the network.
- **Similar media:** perceptual hashes for images, and for videos a hash of frames taken at fixed percentages plus duration. Catches re-encodes and resized copies. Runs in Leviathan.Media; results are cached.
- Results are groups with thumbnails and metadata side by side. Auto-select rules: keep the highest resolution, largest, newest, oldest, or the copy in a preferred folder. Actions: soft delete, move, replace with hard link (same volume), tag.

### 15.3 Bulk rename

- Opened from a multi-selection (F2 with several items selected). Live preview table: old name, new name, conflicts highlighted.
- Operations stack in order: find/replace (plain or regex), insert, remove characters, change case, trim, number sequence (start, step, padding), and tokens: `{name}`, `{ext}`, `{date:yyyy-MM-dd}`, `{taken}`, `{duration}`, `{resolution}`, `{codec}`, `{parent}`, `{counter}`.
- Saved presets; a single undo reverts the whole batch.

### 15.4 Archives and hashing

- Browse zip, 7z, rar, tar, gz, iso as if they were folders (read-only for rar), with preview.
- Extract here / to folder / to chosen location, as worker jobs with progress. Create zip or 7z with level and optional password.
- Checksums (MD5, SHA-1, SHA-256, SHA-512, xxHash3, CRC32) for one or many files; verify against a `.sha256` / `.md5` file; export a checksum file.

### 15.5 Folder compare and sync

- Pick two folders (e.g. local and NAS, or each pane of a split tab). Compare by name + size + date, or by checksum.
- Results show left-only, right-only, newer left, newer right, and identical, with filters and per-item action overrides.
- Sync modes: Mirror left → right, Mirror right → left, Two-way (newer wins, conflicts flagged), Update (copy new and newer only, never delete).
- Deletions on the target go to soft delete / Recycle Bin. A dry-run preview is always shown first.
- Saved sync pairs can be re-run with one click and appear in Favorites.

### 15.6 Other suggested features

- **Copy path, Open terminal here and Open in editor** are built-in commands everywhere; see §15.7.
- **Command palette** (Ctrl+Shift+P) for every command, with its shortcut shown.
- **Git status** overlay icons in repositories (optional; ported from Files).
- **Quick move/copy targets:** up to 9 preset destinations bound to keys, usable everywhere (not only triage).
- **Elevated operations:** when an action needs admin rights, only that action runs elevated through a small helper, not the whole app.
- **Folder notes:** a short note attached to a folder, shown in the details pane.

### 15.7 Always-available tools (D40)

Three commands that are needed so often they are built in, always present and never hidden by file type. Each is in **all three places**: the right-click menu (§8.5), the command bar (§6) and the command palette (§15.6, Ctrl+Shift+P). One implementation backs the three, so a rule or a setting changed once applies everywhere, and each shows the same name, icon and shortcut.

| Command | Shortcut | What it does |
| --- | --- | --- |
| **Copy path** | Ctrl+Shift+C | Puts the path of the selection (or of the current folder when nothing is selected) on the clipboard; a ▾ offers the variants |
| **Open terminal here** | Ctrl+Alt+T | Opens the terminal drawer (§16.2) at the selected folder, the selected file's folder, or the current folder |
| **Open in editor** | Ctrl+Alt+E | Opens the selected file in the editor, or a selected folder in Visual Studio Code |

**Copy path variants.** The default is the full path, quoted when it contains spaces. The ▾ menu and the palette add: *Full path (as is)*, *Quoted*, *Name only*, *Name without extension*, *Parent folder*, *UNC form* (for a mapped drive, the `\\server\share\…` path behind it), *Forward slashes*, *`file:///` URL*, and *For each item, one per line* (for a multi-selection; the default for several items is one path per line). Long paths are copied without the `\\?\` prefix. For a registry location (§7.3) the path is `registry:\HKLM\…` and a *Regedit form* is added. The setting "Default copy format" picks which variant Ctrl+Shift+C uses. A brief "Copied" confirmation appears in the status bar; the copy is also recorded in the Activity tab (§16.1).

**Open terminal here.**
- Before the terminal drawer exists in a build, or when it is switched off, the command falls back to opening Windows Terminal in its own window; once the drawer exists, the drawer is the default and **Shift+click / Shift+Ctrl+Alt+T** opens the external window instead. The setting "Open terminal here uses" chooses *Drawer* (default) or *External window*.
- The folder is the selected folder, else the folder of the selected file, else the current folder. For a network or other location with no usable working directory the drawer's rule of §16.2 applies. Not offered (disabled with a tooltip) for locations that are not folders on a file system, such as the registry, search results and archives.
- Previously planned shell choices (PowerShell, cmd, WSL) remain a setting of the terminal (§16.2).

**Open in editor.**
- **The editor** is chosen in Settings → General → *Open in editor*: *Default app for the file type* (the default; the same app Enter would use), *Visual Studio Code* (found through its install location or `code` on PATH), or *Custom command* (a program and arguments with `{path}`, `{folder}` and `{name}` placeholders, validated before it can be saved).
- For a **file**, *Default app* launches the app registered for the file type; if there is none, *Open with…* appears. For *Visual Studio Code*, the file opens in the last-used VS Code window.
- For a **folder**, *Default app* has no editor to offer, so the command opens the folder in Visual Studio Code when it is installed and otherwise is disabled with the tooltip "Choose an editor in Settings". For a multi-selection of files, each file is opened (a confirmation above 10).
- Files that Windows treats as programs (`.exe`, `.bat`, scripts) are opened for editing, not run; `.lnk` opens its target's folder. Nothing runs through the shell host, so a crash of a launched app cannot affect Leviathan. A launch that fails shows the reason in the status bar and logs it.
- A file on a network share is passed by its UNC path; editors that cannot open it show their own error.

**Resiliency and tests.** Launching happens off the UI thread through the existing `ILauncher` abstraction (Core, `Launching.cs`), with an in-memory fake. Unit tests cover: every copy variant for local, UNC, mapped, long and odd names; the folder rule for the terminal; the editor choice for file, folder, multi-selection and a missing VS Code; that the three places (menu, bar, palette) resolve to the same command ids; and the keyboard shortcuts. UAT playbooks (CLAUDE.md) cover the real clipboard, Windows Terminal and VS Code against the fixture folders.

## 16. Status bar

Left to right; each part can be hidden, and clicking a part acts on it.

| Part | Example | Click |
| --- | --- | --- |
| Item count | 1,284 items (37 folders, 1,247 files) · 12 hidden | Toggle hidden items |
| Selection | 23 selected · 41.7 GB | Select none |
| Selection media total | Total duration 6h 12m (when videos are selected and probed) |  |
| Filter state | Filtered: `*.mkv` (312 of 1,284) | Clear filter |
| Tag summary | Colored dots with counts in this folder | Filter by that tag |
| Location health | `\\\\nas\\media` · SMB 3.1.1 · 12 ms | Show connection details |
| Free space | 2.1 TB free of 16 TB, with a bar | Open space map |
| Background work | Probing metadata 140/1,284 · Indexing | Show background tasks |
| Operations | 2 jobs · 4.3 GB of 18 GB · 112 MB/s · 2 min left | Open Operations panel |
| Layout switcher | Details / Tiles / Thumbnails icons, thumbnail size slider |  |

### 16.1 Status hub (fly-up) (D34, D35)

An expand chevron at the right end of the status bar (and Ctrl+Shift+O, or clicking the operations chip or background-work part) opens the **status hub**: a panel that flies up from the status bar over the bottom of the file view, about 40% of the window high (height draggable and remembered). It closes with Esc or a click outside, or stays open with its pin. Like the Operations panel (§12.6), it can be docked bottom or right, or popped out into its own window. It has six tabs (the sixth, Terminal, is added in the terminal-drawer phase; see §16.2):

| Tab | Contents |
| --- | --- |
| **Operations** | The Operations panel of §12.6: running, queued and completed jobs, row actions, speed sparklines, queue ETA |
| **Activity** | A full log of what you did this session, newest first: navigated to, selected (count and size), opened, renamed, created, copied / moved / deleted (with item counts, linking to the job), tagged, changed a setting, errors shown. Filter by kind, search, jump to a location, open the related job, undo where undo exists, copy or export (text/JSON). Earlier sessions are kept for 7 days. Built on the diagnostics action trail (§20); file names are kept locally only and are never sent anywhere |
| **Background** | Every background task with progress and controls: folder-size calculation (current folder, queue length, rate), indexing and NAS crawls, metadata probing, thumbnail and contact-sheet generation, offline-cache refresh. Each can be paused, resumed or given priority; worker and process status (Worker, Media, ShellHost, Indexer: running, idle, restarts) |
| **Quick settings** | The live knobs, applied at once: background threads / worker processes, running jobs (global and per NAS volume), bandwidth limit and schedule, folder-size calculation on/off for network shares, thumbnail generation on network files, polling interval; a link to the full settings sheet |
| **Space** | A compact treemap of the current folder (§14) |
| **Terminal** | A docked Windows Terminal running cmd, opened at the current folder (§16.2) |

- The hub opens on the tab you used last; clicking a status-bar part opens the matching tab (operations chip → Operations, background work → Background, free space → Space).
- A badge on the chevron shows failures waiting to be seen.

### 16.2 Terminal drawer (D39)

The **Terminal** tab of the status hub is a command prompt that is always one hotkey away and already in the folder you are looking at.

- **Open it** with **Ctrl+\`** (toggles the hub on the Terminal tab; Esc returns focus to the file view without closing the terminal), with **Open terminal here** (§15.7), or by clicking the Terminal tab. Because the hub is a fly-up over the bottom of the file view (§16.1), the terminal inherits its behaviour: height draggable and remembered, a pin to keep it open, docking at the bottom or the right, and a pop-out into its own window.
- **What it hosts.** A **docked Windows Terminal** running **cmd.exe** to start. Terminal sessions keep running while the hub is closed or on another tab; reopening shows them as they were, with their scrollback.
- **Starting folder.** A new terminal starts in the folder of the tab that was active when it was opened. The terminal does **not** follow later navigation on its own: an explicit **Go to current folder** button (and a palette command) sends `cd /d "<folder>"` to the focused terminal. Cmd cannot use a UNC path as its working directory, so for a share the terminal uses `pushd "\\server\share\folder"`, which maps a temporary drive letter, and a note in the terminal header says so. For locations that are not folders on a file system (the registry, search results, archives) the terminal opens in the user's home folder.
- **Several terminals.** A small tab strip inside the Terminal tab holds more than one session; **+** adds one (at the current folder), **×** closes one. Closing the last one leaves the tab empty with "Open terminal here" offered.
- **Shell.** cmd is the default. A setting offers *Windows PowerShell*, *PowerShell 7*, *WSL (default distribution)* and *Windows Terminal's default profile*; the choice applies to new terminals. A profile whose program is missing is not offered.
- **Out of process, and never in the UI thread.** Terminals live in a separate process so that a hung shell, a runaway program or a crash cannot take down the app (§3.4): the host process is a child of `Leviathan.ShellHost` (or a dedicated `Leviathan.Terminal` process, decided in the first slice) supervised by `ProcessSupervisor`. A crash restarts only the terminal tab, which shows "The terminal host stopped. Restart" and keeps the other tabs working. Shells started by the user are the user's: closing Leviathan asks before ending sessions that still have a child process running.
- **Feasibility comes first.** Embedding the Windows Terminal window inside another app's window is not an officially supported scenario. The first slice of the terminal phase is therefore a **spike** that proves, on this machine, whether Windows Terminal can be docked reliably (resize, focus, keyboard, DPI, dark / light, multiple monitors). If it cannot, the fallback is a terminal that Leviathan draws itself on top of a Windows pseudo console (ConPTY) with the Windows Terminal rendering component where its licence and packaging allow, or a docked-to-edge Windows Terminal window kept aligned beneath the app's window. The outcome and the reason are written to `docs/decisions.md` before any other slice starts; the user-visible behaviour above does not change.
- **Not recorded.** Characters typed into a terminal are never logged; the Activity tab records only that a terminal was opened, where, and which shell.
- **Budgets and tests.** Opening the drawer shows a prompt within 600 ms (shell start included); the hub and the file view stay responsive while a program floods the terminal with output (checked with the stall watchdog, §3.2). Unit tests cover command building (quoting, `pushd` for UNC, home fallback), the shell list and the session list; integration tests start the real cmd in a temp folder and read `cd` output back; chaos tests kill the terminal host while a program runs.

## 17. Settings, workspaces and keyboard

### 17.1 Workspaces (D19, D37)

A **workspace** is a saved arrangement of the application: the windows, and in each window its tabs, how each tab is split, the layout of every pane, and where things sit. Workspaces replace the startup profiles of earlier versions of this spec: the same idea, one name, and one place where the application's setup is remembered. A workspace is loaded at startup, or at any time. Opening a workspace is how "Project Alpha" brings up its local, network and cloud folders at once.

**What a workspace remembers**

| Level | Saved |
| --- | --- |
| Workspace | Name, optional icon and colour, description, created / saved times, the schema version |
| Window | Position, size, monitor, maximized state; which tab is selected; sidebar width, collapsed state and which sections and nodes are expanded; preview pane open / width; status hub open / pinned / height / dock side / selected tab; tab groups (name, colour, collapsed) |
| Tab | The location, its back / forward history, label, pin, lock, group, the layout (Details / List / Tiles / Thumbnails / Gallery / Columns), sort, grouping, filter, column set and widths, scroll position and selection (best effort) |
| Split | Whether the tab is split, **horizontal or vertical** (side by side or top and bottom), the divider position, which pane has focus, and for each pane its own location, history, layout, sort and selection (§15.1) |
| Column view | The widths of the columns by depth and the selected path (§8.8) |
| Terminal | Whether the terminal drawer is open and the starting folder of each terminal; not their contents or history (§16.2) |

Locations are saved as paths (local, UNC, mapped drive letters with their UNC form, cloud sync-client folders, and the registry when shown), never as credentials. A saved share is reconnected by Windows' own stored credentials; Leviathan stores no passwords.

**Last session** is the built-in workspace. It is updated continuously (every change, debounced to every 2 s) and on exit, so it also survives a crash, and it cannot be deleted or renamed. It is the default thing loaded at startup.

**Named workspaces** are created with **Save current windows as workspace…**: a name (unique ignoring case), an optional colour and icon, and a choice of **all windows** or **this window only**. A workspace can be renamed, duplicated, updated from what is open now (**Save over**), deleted, and exported to / imported from a `.leviathan-workspace.json` file (paths are portable between machines only where the drive letters and servers match; the importer lists any location that does not resolve on this machine before loading it).

**Opening**

- **At startup.** Settings → General → *Startup* is *Last session* (default), a specific workspace, *Nothing (one empty window)*, or *Ask* (a picker, with a "remember" checkbox). The command line `Leviathan.exe --workspace "Project Alpha"` opens one by name and is how a shortcut can start a workspace directly.
- **At any time**, from the title bar menu, the Workspaces list in the command palette (Ctrl+Shift+P) or Ctrl+Alt+W (a picker). Three modes: **Replace** (close the current windows, open the workspace's), **Open in new windows** (add its windows to what is open), and **Add tabs to this window** (merge its tabs into the current window, in a new tab group named for the workspace). Replace always opens the new windows before closing the old ones, so the app never has zero windows (G1 rule).
- A workspace that is open is shown with a check mark; an edited workspace shows a dot until saved over. Nothing is saved automatically into a named workspace; only Last session is automatic.

**Resilience**

- Tabs pointing to unreachable shares, drives that are not present or folders that no longer exist are restored in an **offline / missing** state (the location is kept, the tab shows the error of §3.3 or §8.6 and retries in the background) and never delay startup or opening. Cloud folders whose sync client is not running open as ordinary folders.
- A workspace file that is damaged or from a newer build is skipped with a message, never half-loaded; the last good copy is kept as `.bak`, and every write is atomic (temp file, flush, rename; §17.2).
- Opening is staged: windows and tabs appear first with placeholders, then each tab's listing streams in (§3.2). Opening a workspace with 50 tabs does not list 50 folders at once: only the selected tab of each window is listed immediately, the rest on first view, and network tabs after local ones.
- **Migration.** Files written by earlier builds (`last-session.json`, `profiles.json`) are read and converted on first start into workspaces, keeping their windows, tabs and split state; the old files are left in place as `.migrated` backups. A newer schema version is never silently downgraded.

**Tests.** Round trips for every level of the table above; split orientation and ratios; the three open modes against the window manager; migration from the old files (kept as fixtures); damaged, truncated and future-version files; missing and offline locations; export and import with unresolvable locations; the `--workspace` argument; a 50-tab workspace that opens without a UI stall over 50 ms.

### 17.2 Settings pages

| Page | Main settings |
| --- | --- |
| General | Startup (Last session, a workspace, or ask), single vs double click, confirmations, default "new tab" location, Open in editor (§15.7), default copy format, Open terminal here uses |
| Appearance | Theme (light / dark / system), density (Compact / Comfortable), accent, Mica on/off, icon size, font |
| Files and folders | Show hidden / system files, extensions, folder sizes, natural sort, folder-view inheritance, column view widths (§8.8), Show Registry in the sidebar (§7.3) |
| Tabs and windows | Tab behaviours from §4.1, Ctrl+Tab order, close-last-tab behaviour |
| Network | Timeouts, retry policy, polling intervals, discovery on/off, offline cache size, saved connections |
| Operations | Parallelism per destination, verify policy, bandwidth limits and schedule, conflict defaults |
| Delete | Soft-delete retention days and size cap, confirmations |
| Media and preview | Video engine (libmpv / LibVLC), hardware decoding, ffmpeg path, preview size limits, which triage tools are enabled, skim burst length and interval, start offset, contact sheet grid |
| Tags | Tag names, colors, order, keys; write Windows "Tags" property on/off |
| Search and index | Indexer service status, NAS folders to index, schedule |
| Keyboard | Every command with its shortcut; search, rebind, reset; import/export |
| Terminal | Shell (cmd, PowerShell, PowerShell 7, WSL), hotkey, confirm on close with running programs (§16.2) |
| Advanced | Diagnostics and log level, cache locations and sizes, reset, export/import all settings |

Settings are a JSON file, written atomically, with an "Open settings file" option for power users. Every setting takes effect without a restart.

**Where settings open (D36):** Options ⚙ (and Ctrl+,) opens settings as a sheet that rises from the bottom of the window, about 70% of its height, over the content: the page list on the left, the page on the right, search across all settings at the top, Esc or a click outside to close. It can be expanded to the full window. The status hub's Quick settings tab (§16.1) covers the settings people change while working.

### 17.3 Core keyboard map

| Area | Keys |
| --- | --- |
| Tabs | Ctrl+T new, Ctrl+W close, Ctrl+Shift+T reopen, Ctrl+Shift+K duplicate, Ctrl+Tab next, Ctrl+1–9 go to |
| Navigation | Alt+Left/Right back/forward, Alt+Up parent, Ctrl+L address bar, F5 reload, Ctrl+P go to |
| Files | Ctrl+C/X/V, F2 rename, Del delete, Shift+Del permanent, Ctrl+Shift+N new folder, Ctrl+Z/Y undo/redo, Alt+Enter properties |
| View | Ctrl+Shift+1–6 layouts (3 = Columns, §8.8), Alt+P preview pane, Ctrl+\\ split, Ctrl+H hidden files, Space quick preview |
| Selection | Ctrl+A all, Ctrl+I invert, Ctrl+Shift+A none, 1–9 tags |
| Tools | T triage, Ctrl+Shift+M space map, Ctrl+F search, Ctrl+Shift+P command palette, F5/F6 copy/move to other pane (when split), Ctrl+Shift+C copy path, Ctrl+Alt+T terminal here, Ctrl+Alt+E open in editor, Ctrl+` terminal drawer, Ctrl+Alt+W workspaces (§15.7, §16.2, §17.1) |

When a key is shared (F5 is reload in a single pane, copy-to-other-pane in a split), the split-pane meaning wins only while split; the setting can change this.

## 18. Reuse from the Files fork

The fork (brett-baker/Files) targets .NET 10 and Windows App SDK, so most interop code can be ported with light changes. It is dual-licensed: check each file's header (MIT or MPL-2.0) before porting. MPL-2.0 files can be reused, but changes to those files must stay under MPL-2.0. Keep a `THIRD-PARTY-NOTICES` file listing every ported file and its license.

| Area in Files | Path | Plan |
| --- | --- | --- |
| CsWin32 setup and COM helpers | `src/Files.App.CsWin32` | Port almost as-is into `Leviathan.Interop` |
| Shell context menus (including the worker pool) | `src/Files.App/Utils/Shell/ContextMenu*.cs` | Port the logic; host it in ShellHost instead of in-process |
| Shell preview handlers | `src/Files.App/Utils/Shell/PreviewHandler.cs` | Port; host out of process |
| Cloud drive detection (OneDrive, Google Drive, Dropbox, Box, Synology Drive and more) | `src/Files.App/Utils/Cloud` | Port as the Internet section's provider |
| File tags in alternate data streams | `src/Files.App/Utils/FileTags` | Use as reference; read its `:files` stream to import existing tags |
| Win32 enumeration | `src/Files.App/Utils/Storage/Enumerators` | Port, then add streaming batches and cancellation |
| Bulk shell operations | `src/Files.App.Storage/Windows/WindowsBulkOperations.cs` | Use for Recycle Bin deletes and special shell items only |
| FTP storage | `src/Files.App.Storage/Ftp` | Reference for the FTP provider |
| Open With menu, file associations, launching | `src/Files.App/Utils/Shell` (`OpenWithMenu`, `LaunchHelper`, `FileAssociationHelpers`) | Port |
| Git integration | `src/Files.App/Utils/Git` | Optional, later phase |
| 7-Zip binaries and archive browsing | `src/Files.App` (`7z*.dll`) | Reuse the same approach |
| Quick Look / PowerToys Peek / Seer providers | `src/Files.App/Services/PreviewPopupProviders` | Not needed: Leviathan has its own floating preview |
| App instance server | `src/Files.App.Server` | Reference for IPC and single-instance; Leviathan replaces it with its own worker contracts |

Not reused: Files' UI layer (views, view models, layout modes) and its storage abstraction, since Leviathan's streaming, provider-based design differs. Its UI remains a good reference for behaviour and edge cases.

## 19. Testing strategy

Every project has unit tests in xUnit, and the file-system, network and process-failure behaviour that makes Leviathan resilient is covered by integration and chaos tests. A change that breaks a §3.1 budget fails the build.

### 19.1 Tools

| Purpose | Choice | Note |
| --- | --- | --- |
| Test framework | xUnit v3 | Parallel by default; `[Theory]` for data-driven cases |
| Assertions | Shouldly (or AwesomeAssertions) | FluentAssertions 8+ needs a paid licence, so avoid it |
| Test doubles | NSubstitute |  |
| File system for unit tests | System.IO.Abstractions + its TestingHelpers `MockFileSystem` | All managed file access goes through `IFileSystem` |
| Property-based tests | FsCheck (xUnit integration) | Queue scheduler, folder-size deltas, bulk rename |
| Snapshot tests | Verify | ffprobe parsing, crash-report JSON, settings migration |
| UI automation | FlaUI | Smoke tests against the real app |
| Network faults | Toxiproxy in front of a Samba test container | Latency, resets, timeouts, bandwidth caps |
| Benchmarks | BenchmarkDotNet | Enumeration, sort, copy throughput, startup |
| Coverage | coverlet | Reported per project in CI |

### 19.2 Where System.IO.Abstractions fits, and where it doesn't

It is the right base for everything that uses `System.IO` (settings, templates, the journal's files, tag sidecars). It cannot model the Win32 paths Leviathan depends on: `FindFirstFileExW` enumeration, `CopyFile2` and server-side copy, alternate data streams, NTFS file IDs, SMB latency and disconnects. So Leviathan defines its own small interfaces over those, each with a real Win32 implementation and an in-memory fake built on `MockFileSystem`:

- `IDirectoryEnumerator` (streaming batches), `IFileCopier` (chunked, resumable), `IAlternateStreamStore`, `IFileIdentity` (volume serial + file ID), `IVolumeInfo`, `IShareHealthProbe`, `IClock`, `IProcessSupervisor`
- A **fault-injecting decorator** wraps any of them: add latency, throw "network name no longer available" after N bytes, access denied on a path pattern, disk full at a threshold, or hang until cancelled. The same decorator drives unit tests and the chaos runs.

### 19.3 Test projects and what they prove

| Project | Kind | Proves |
| --- | --- | --- |
| `Leviathan.Core.Tests` | Unit | Commands, path parsing (UNC, long paths, `shell:`), sorting (natural sort), filters, query syntax |
| `Leviathan.Worker.Tests` | Unit + property | Job state machine, scheduler limits and priorities, journal replay, conflict rules, soft-delete manifest, verify policy |
| `Leviathan.Data.Tests` | Unit (real SQLite in a temp folder) | Tag index, metadata cache invalidation, folder-size store, migrations |
| `Leviathan.Providers.*.Tests` | Unit + integration | Each provider against fakes, then against a real target (temp NTFS folder, Samba, SFTP and NFS containers) |
| `Leviathan.Media.Tests` | Integration | ffprobe parsing on a fixture set including corrupt and truncated files; contact-sheet generation |
| `Leviathan.Integration.Tests` | Integration | Real processes talking over pipes: copy end to end, tags survive copy, folder sizes update after moves |
| `Leviathan.Chaos.Tests` | Chaos (nightly) | Kill the worker at random points during 1,000 copies and confirm no corrupt or half-named files; drop the network mid-copy via Toxiproxy and confirm resume |
| `Leviathan.UI.Tests` | UI smoke (FlaUI) | Launch, open tabs, navigate, tear off a tab, copy and paste, triage keys; the UI-stall watchdog fails the test on any stall over 50 ms |
| `Leviathan.Benchmarks` | Performance | The §3.1 budgets, compared against the last main-branch run |

### 19.4 Rules and CI

- Unit tests never touch the real disk or network; integration tests use only temp folders and containers, never your real shares. An optional, manually triggered "lab" run can target a dedicated test share on the NAS.
- Coverage targets: Core, Worker and Data at least 85% of lines; every job state transition and journal recovery path covered.
- Every fixed bug gets a regression test, named after the crash signature (§20) when there is one.
- GitHub Actions on Windows: unit tests on every push; integration on pull requests; chaos, UI and benchmarks nightly.

## 20. Diagnostics and crash reports

Every crash, hang or reported problem produces a self-contained bundle that Claude (with access to the repository) can diagnose and fix from without asking follow-up questions.

### 20.1 Groundwork in the build

- **Deterministic builds with SourceLink and embedded portable PDBs**, so every stack frame carries a source file, a line number and the exact git commit. The version string includes the commit SHA.
- **Structured logs** in every process as JSON lines (OpenTelemetry log records), rolling for 7 days, with a trace ID that follows an action across processes (UI → Worker → Media), plus job ID, tab ID and location.
- **Always-on flight recorder:** each process keeps its last 2,000 events at debug level in memory, at negligible cost, and writes them into any crash bundle.
- **Action trail:** the last 50 user commands (navigate, copy, tag, triage key…) with timestamps and targets, recorded so a debug build can replay them.

### 20.2 What gets captured

| Trigger | Captured by |
| --- | --- |
| Unhandled .NET exception (UI, worker threads, unobserved tasks, WinUI dispatcher) | In-process handlers write the bundle, then the process exits |
| Native crash (shell extension, ffmpeg, libmpv / LibVLC) | The supervising process notices the exit and writes a minidump through Windows Error Reporting's local-dump setting |
| UI hang over 5 s | Watchdog thread captures the UI thread's stack and a minidump, then lets the app continue |
| Worker hang (no progress heartbeat for 60 s) | Supervisor captures a dump, restarts the worker; the journal resumes the job |
| Problem without a crash | Help › Report a problem (Ctrl+Shift+F12) writes the same bundle on demand |

### 20.3 The bundle

One folder per incident, e.g. `%LOCALAPPDATA%\\Leviathan\\crashes\\2026-11-01T14-03-22Z_worker_7f3a\\`:

| File | Contents |
| --- | --- |
| `report.md` | The summary written for Claude: start here |
| `report.json` | The same facts, machine-readable, with a versioned schema |
| `events.jsonl` | Flight-recorder events from all processes, merged in time order |
| `actions.json` | The action trail, replayable in a debug build |
| `environment.json` | App version and commit, Windows build, .NET and Windows App SDK versions, GPU and drivers, video engine, ffmpeg version, network shares and their health |
| `settings.json` | Current settings (credentials are never stored, so nothing to redact) |
| `minidump.dmp` | When captured; full-memory dumps only if turned on in Settings |

`report.md` always has the same sections, in this order:

1. **Summary:** one line, e.g. "Worker crashed: IOException (ERROR\_NETNAME\_DELETED, 0x80070040) in ChunkedCopier.WriteChunkAsync while copying to \\\\nas\\media".
2. **Crash signature:** a short hash of exception type + top 5 frames, plus how many times it has happened and when first seen.
3. **Exception chain:** every inner exception with type, message, HRESULT and Win32 error name.
4. **Stack trace with source links:** each frame as a repo path and line (`src/Leviathan.Worker/Copy/ChunkedCopier.cs:212`) and a GitHub permalink at the build's commit.
5. **What was happening:** the last 20 user actions, the active jobs with their state, the tab and location involved, and the share's health at the time.
6. **Relevant log lines:** the 50 events around the failure, with trace IDs.
7. **Environment:** the short form of `environment.json`.
8. **Starting points:** the source files implicated by the stack and by the component that logged the error.
9. **How to reproduce:** the replay command for the action trail, when one applies.

### 20.4 Getting it to Claude

- The crash dialog and the Diagnostics view have **Copy for Claude**, which puts `report.md` on the clipboard, and **Open folder** for the full bundle.
- A command-line tool, `leviathan-diag`, prints the latest report (`leviathan-diag last`), lists crashes grouped by signature (`leviathan-diag list`) and replays an action trail (`leviathan-diag replay <id>`). Claude Code can run these directly in the repository.
- Crashes are grouped by signature in the Diagnostics view, so repeats of a known bug are counted, not reported again.

## 21. Phased roadmap

Each phase ends in a usable build and a gate that must pass before the next starts. NAS resiliency (Phase 2) is proven before media features are layered on top, because everything later depends on it.

&#91;embedded content: roadmap · 6 phases, each with an exit gate\]

**Phases 6 to 10 (extensions, after the original plan is solid).** They are built only after Phase 5's gate, in this order, one phase at a time, with the same gates and UAT playbooks:

| Phase | Adds | Spec |
| --- | --- | --- |
| 6 | Workspaces (profiles folded in) | §17.1, D37 |
| 7 | Column view | §8.8, D38 |
| 8 | Terminal drawer | §16.2, D39 |
| 9 | Always-available tools | §15.7, D40 |
| 10 | Registry explorer (read-only) | §7.3, D41 |

Considered and set aside for now (not specified, may return): a window-level dual pane with independent tab sets per side, smart folders spanning drives (saved searches in §13.2 stay the only virtual folders), deep Git integration (§15.6 keeps the optional status icons), and extra hash / properties presentation beyond §15.4.

No dates yet: phase sizes depend on how much time you put in each week. Phase 1 is the first point where Leviathan can replace Explorer for local work; Phase 3 delivers the triage workflow.

**Later option: a NAS agent.** Container Manager is already running on the DS2419+. A small Leviathan agent in a container there could do the NAS-heavy work at disk speed instead of over the 1 Gb/s link: checksums and duplicate hashing, folder-size crawls, contact sheets, and instant change detection (Linux file notifications) for the folder-size service and the NAS search index. It would talk to the PC over TCP with TLS and a pairing key, using the same contracts as the local workers. Not in v1; worth deciding after Phase 4.

## 22. Open questions

- [x] Name: Leviathan
- [x] Local search: Everything when installed, own indexer as fallback (D29)
- [x] Network: 2.5 GbE PC card held to 1 Gb/s by the switch (§3.5)
- [x] Queue defaults: 3 running jobs, 1 per NAS volume (D31)
- [x] Strip colors: one color per job (D30)
- [x] NAS: DS2419+, DSM 7.2.2, 1 Gb/s links (§3.5)
- [x] NFS: Windows Client for NFS (D25)
- [x] Soft-delete location: NAS `#recycle` when present, else the app's own folder (D24)
- [x] Triage volume: 30,000+ untriaged, \~1,000 per week, 500K-file library, 200 MB–100 GB files (§10.1)
- [x] Keep action: optional move to a folder (D26)
- [x] ffmpeg: GPL build (D27)
- [x] Distribution: unpackaged build for now (D28)

No open questions remain. The spec is ready for review before Phase 0 starts.
