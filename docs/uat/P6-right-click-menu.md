# UAT: Right-click menu (icon strip, New, Paste special, Open with, Image)

**Goal:** the right-click menu looks and works like Windows 11's: icon buttons along the top, a New menu on empty space, Paste special, the shell's Open with list, and an Image submenu for pictures.
**Built in:** commits `2d5e880`, `9348cd0`, `aa44e9c` and `0d5ccae`.
**Time:** about 15 minutes.

## Setup

1. Build the fixtures (never test with real data):
   ```powershell
   .\tools\New-UatFixtures.ps1
   ```
   Fixture root: `D:\Playbooks\uat`. This playbook uses `11-preview` (it has `picture.png`, 640 x 400, an orange disc on the left and the words "Leviathan UAT" at the bottom, and `photo.jpg`, the same picture as a JPEG) and `01-basic`.
2. Start the app with scratch settings, so your real settings, favourites and saved servers are untouched:
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Copy the `11-preview` folder to `D:\Playbooks\scratch\pics` (File Explorer is fine) and open that copy in Leviathan, so the originals stay as they are. Open it in a tab.

## Steps

### A. Icon strip

| # | Do | Expect |
|---|----|--------|
| 1 | Right-click `picture.png`. | A row of icon buttons at the top of the menu: Cut, Copy, Rename, Delete. Hovering one shows its name and shortcut. |
| 2 | Click Copy in the strip. | Menu closes; the status bar says "Copied picture.png." |
| 3 | Right-click empty space in the folder. | The strip shows one button, Paste, and it is enabled. |
| 4 | Click Paste. | A copy of the picture appears with a numbered name. |
| 5 | Copy a different file's *text* only: select some text in Notepad, Ctrl+C, then right-click empty space in Leviathan. | The Paste button is greyed (text alone is not a file paste). |

### B. New menu on empty space

| # | Do | Expect |
|---|----|--------|
| 6 | Right-click empty space, point at New. | A submenu: Folder, Text document, then the templates Windows knows (Word, Excel and so on, if installed). It opens at once. |
| 7 | Choose Text document. | "New Text Document.txt" is created and its name is in edit mode. Type `hello` and Enter. |
| 8 | Repeat with Folder. | "New folder" is created and in edit mode; if the name is taken, it is numbered. |

### C. Paste special

| # | Do | Expect |
|---|----|--------|
| 9 | In Notepad copy the words `hello from notepad`. Right-click empty space, Paste special, Paste text as new file. | "Pasted text.txt" appears, in edit mode. Open it: it holds exactly that text. |
| 10 | Copy a picture to the clipboard (open `picture.png` in Paint, Select all, Ctrl+C). Right-click empty space, Paste special, Paste image as new file. | "Pasted image.png" appears and shows the picture. |
| 11 | Copy two files in Leviathan, then Paste special, Paste into new folder. | A new folder is made and the files are copied into it. |
| 12 | With an empty clipboard, look at Paste special. | The whole submenu is greyed. |

### D. Open with

| # | Do | Expect |
|---|----|--------|
| 13 | Right-click a `.txt` file in `01-basic`. | Under Open there is "Open with". |
| 14 | Point at it (give it a second). | The list of apps Windows offers for that type (Notepad at least), ending with "Choose another app". |
| 15 | Choose Notepad. | The file opens in Notepad. |
| 16 | Open the menu on a file inside a zip (browse `sample.zip` in `11-preview`). | "Open with…" still appears; it opens the Windows chooser. |

### E. Image submenu

| # | Do | Expect |
|---|----|--------|
| 17 | Right-click `picture.png`, point at Image. | Rotate left, Rotate right, Flip horizontally, Flip vertically, Resize, Convert to. |
| 18 | Choose Rotate right. | `picture (rotated right).png` appears, selected, and the status bar says what was saved. Open it: it is 400 wide and 640 tall, the disc is at the top. |
| 19 | Choose Rotate left on the same file. | `picture (rotated left).png`: the disc is at the bottom. |
| 20 | Flip horizontally. | The disc is on the right and the text is mirrored. |
| 21 | Resize, 50%. | `picture (50%).png` is 320 x 200. |
| 22 | Convert to, JPEG on `picture.png`. | `picture.jpg` appears (if the name is taken, `picture (2).jpg`). |
| 23 | Repeat Rotate right on `picture.png`. | A second file, `picture (rotated right) (2).png`; the first is not overwritten. |
| 24 | Select `picture.png` and `photo.jpg` together (Ctrl+click), Image, Rotate left. | Two new files; both are selected afterwards. |
| 25 | Select a picture and a `.txt` together and right-click. | No Image submenu enabled for the mix. |
| 26 | Check the originals. | `picture.png` and `photo.jpg` are byte-for-byte what they were (size and date unchanged). |

## Look at (tests cannot judge these)

- Does the strip look like Windows 11's: spacing, icon size, hover colour, Paste greyed convincingly?
- Is the Open with list as quick to appear as Explorer's? (It arrives a moment after the menu opens.)
- Wording: "Paste text as new file", "Convert to", the status bar messages.
- Does the menu feel as fast as before with the extra entries? Open it on a 10,000-item folder (`06-ten-thousand`) and note any delay.

## Known edge cases

- Images are always saved as copies; there is no "replace the original" option yet.
- JPEG edits are re-encoded (quality 0.92) and lose EXIF and colour profile. WebP, HEIC and AVIF can be edited but are saved as PNG. Animated GIFs keep only the first frame.
- "Open with" is found by its English title, so on a Windows in another language the shell's list may stay under "More options" while the own "Open with…" line opens the chooser.
- The shell's other items stay under "More options"; they are not renamed "App extensions".
- There is no Share button in the strip, no Paste as shortcut or link, and no Set as wallpaper yet.
- Not seen on screen by the developer before this playbook: the strip and nested submenus are the likeliest to look wrong.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\scratch", "D:\Playbooks\data"
```
