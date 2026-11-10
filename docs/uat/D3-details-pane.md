# UAT: D3a to D3c Details pane

**Goal:** see the facts of one file in the pane (general, media, properties, checksums) and fix its title, comments and rating in place.
**Built in:** commits `8e77f37` (D3a), `0e21c5f` (D3b), `eaf6215` (D3c), spec §9.2.
**Time:** about 20 minutes.

## Setup

1. Build the fixtures (needs ffmpeg for the clips; use `pwsh`):
   ```powershell
   pwsh .\tools\New-UatFixtures.ps1
   ```
   Uses `11-preview` (photo.jpg, notes.txt, letter.docx, sample.zip) and `09-media` (short-3s.mp4, medium-45s.mkv, hdr10-5s.mkv, song-20s.flac, broken.mp4).
2. Start the app with scratch settings and show the pane (**Alt+P**):
   ```powershell
   $env:LEVIATHAN_DATA_DIR = "D:\Playbooks\data"
   & .\src\Leviathan.App\bin\x64\Debug\net10.0-windows10.0.26100.0\Leviathan.exe
   ```
3. Rebuild the fixtures before running the D3c steps again; they change `photo.jpg`.

## Steps

The Details part fills about 250 ms after the selection settles; arrowing through files reads nothing.

| # | Do | Expect |
|---|----|--------|
| 1 | Open `11-preview`, click `notes.txt`. | **General**: name, type, size, dates, attributes, location. No Media section. |
| 2 | Open `09-media`, click `medium-45s.mkv`. | General, then **Media**: container, duration 0:45, video stream (codec h264, 320×180, frame rate), audio stream, bit rates. A "Reading media information…" line shows first. |
| 3 | Click `hdr10-5s.mkv`. | The Media section mentions HDR10 / BT.2020 / PQ for the video stream. |
| 4 | Click `broken.mp4`. | "Media information is unreadable" with a reason; the window keeps working. |
| 5 | Click `song-20s.flac`. | Media shows the audio stream; an **Audio** properties section appears if Windows reads tags (the fixture has few). |
| 6 | Click `photo.jpg` in `11-preview`. | An **Image** section: dimensions 640×400 and the resolution; camera fields are absent (the fixture has no EXIF camera data). |
| 7 | Click `letter.docx`. | A **Document** section (title/author if any, pages or words may be empty for a hand-made file). |
| 8 | Expand **All properties** on any file. | A list of the other known properties (item type, owner, accessed date, and so on). |
| 9 | Select `medium-45s.mkv`, press **Compute MD5, SHA-1, SHA-256** under Integrity. | "Reading…" with progress, then three hashes with **Copy MD5** / **Copy SHA-1** / **Copy SHA-256** buttons. Click one and paste somewhere: the full hash. |
| 10 | Compare: `Get-FileHash D:\Playbooks\uat\09-media\medium-45s.mkv -Algorithm MD5`. | Same MD5 as the pane. |
| 11 | Start a hash, then click another file before it finishes. | The reading stops; nothing from the old file appears in the new file's details. |
| 12 | Select two files, then a folder. | Two files: a hint to select one file. A folder: General only, no Media or Integrity. |
| 13 | `photo.jpg`: open **Edit title, comments, rating**. Type a title `UAT title` and a comment `UAT comment`, click the 4th star. | **Save** becomes enabled as soon as you change something. |
| 14 | Click **Save**. | "Saved." under the button and "Saved the properties of photo.jpg." in the status bar. The file's **Modified** date in the list does not change. |
| 15 | Click another file and back on `photo.jpg`. Open the edit expander. | The title, comment and 4 stars are still there (read back from the file). The Image/All properties part shows "Rating 4 of 5". |
| 16 | In Explorer, open the file's Properties → Details. | Title, Comments and a 4-star rating match. |
| 17 | Change the rating to none (click the 4th star again or the clear control) and Save. | Rating goes away in the details and in Explorer. |
| 18 | Click `notes.txt`, open the edit expander, set a rating of 3, Save. | A message such as "This file did not take the change: …" in red/plain text under the button; Save stays enabled; the file is unchanged. |
| 19 | Click `song-20s.flac`, try to save a title. | Either it is saved or the same "did not take the change" message; no crash. |

## Look at (tests cannot judge these)

- Is the order of sections right (General, Media, Image/Audio/Document, Edit, Integrity)? Too long for a small window?
- Do the "Reading…" and "unreadable" lines read calmly?
- Is the star control easy to hit, and clear about "no rating"?
- Whether it is obvious that the date is put back after an edit (it is deliberate).

## Known edge cases

- **xxHash** and **IPTC/XMP** beyond what Windows reads are not built; the pane shows only what the Windows property system gives.
- Only title, comments and rating are editable; keywords, artist, album and photo fields are not.
- What can be written depends on the file type's shell handler. The edit works on JPEG; text files and (usually) FLAC refuse.
- The fixtures have no GPS data, so the map link is not exercised.
- The real shell write is verified only by hand here: there is no automated test for it.

## Result

Pass / fail / notes, and anything to tweak.

## Clean up

```powershell
Remove-Item -Recurse -Force "D:\Playbooks\data"
```
