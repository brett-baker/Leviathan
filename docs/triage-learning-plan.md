# Triage learning: keep/discard recommendations

What this is: triage learns from the user's own keep and discard decisions and, once it has enough examples, shows a hint
("Keep 69%") on the triage window. A "Learning" bar shows progress until then. The user can give a **reject reason** (a short
title of their own; a description only for the learning) with a discard. Everything stays on the user's PC.

Why a log of our own: the triage journal is cleared once a kept file has moved, and Stash lost the scenes that were deleted,
so neither holds the discards. Examples build up from now on.

## Slices

- [x] 1. Decision history (`triage-history.db`, append-only, never compacted) and the reject-reason list
      (`reject-reasons.json`, starts empty). The session carries a reason with a discard; the recorder writes every decision,
      reason, size and time to the history.
- [x] 2. Reason chips in the triage window, always visible; click = discard with that reason; Shift+1 to Shift+9; add, edit and
      remove reasons from the window.
- [x] 2b. Learning is optional (Settings, Triage, Learning; off by default). Off: no history, no chip bar, no Shift+number
      discard. A list of servers that can help it learn, added from presets (Stash today, more kinds later), each with a name,
      address, API key (Windows Credential Manager, never the settings file), an on/off switch and a Test connection button.
- [x] 3. Background feature collection per decided file (`triage-facts.db`): the words of the file name, folder, year and
      resolution; ffprobe facts (from the media cache, or a probe while the file still exists); and the Stash scene (performers,
      tags, studio, date, rating) matched by file name and size from each enabled server. Collected the moment a file is decided,
      then a backfill a minute after start-up and when learning or a server is switched on. A server that cannot be reached leaves
      the file incomplete, to be retried; a server added later is asked about older files. Settings shows how many files have facts.
- [x] 4. The "Learning" bar in the triage window (keeps and discards that have facts, against 100 of each; half the bar per kind) and an accuracy report in Settings: a baseline (naive Bayes) learns from the earlier 80% of decisions and guesses the latest 20%, compared with always guessing the commoner answer.
- [x] 5. The model and the hint: logistic regression on the features (`LogisticModel`), judged against the naive Bayes baseline on
      the latest 20% of decisions and used only if it is more accurate; the hint ("Keep 69%") with up to three reasons in a tooltip and the recent accuracy;
      shown only when the bar is full and the model beats always guessing by more than 5 points; a "Show a Keep or Discard hint" switch in Settings.
      Gradient-boosted trees were not built: add them only if the report shows logistic regression falling short.

## Open points

- Existing Stash scenes are not used as keeps (33,360 scenes and no discards would teach "keep everything").
- Reasons are stored by id, so renaming one keeps the discards it was given to.
