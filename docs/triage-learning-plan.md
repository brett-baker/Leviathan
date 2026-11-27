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
- [ ] 3. Background feature collection per decided file: parsed file name, ffprobe facts, folder, and the Stash scene when the
      file matches one (from the enabled servers in Settings).
- [ ] 4. The "Learning" bar: examples so far against what is needed (both kinds), counts of keeps and discards.
- [ ] 5. The model and the hint: gradient-boosted trees or logistic regression on the features, judged on later decisions, the
      hint with confidence and top reasons, a recent accuracy figure, a setting to switch it off.

## Open points

- Existing Stash scenes are not used as keeps (33,360 scenes and no discards would teach "keep everything").
- Reasons are stored by id, so renaming one keeps the discards it was given to.
