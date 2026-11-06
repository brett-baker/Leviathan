# Backlog: parked on purpose

Things Brett chose to leave for later (2026-11-06, after Phase 2). Nothing here is forgotten or blocked; it was set aside. Details are in
`docs/phase-2.md` ("Still unverified or open") and `docs/decisions.md`.

## Needs Brett at the machine

- **Cable-pull gate:** 50 GB copy to the NAS surviving three cable pulls and a killed UI (procedure in `docs/phase-2.md`).
- **NAS first rows:** a NAS folder with about 10,000 items for `--nas-rows` (the scratch folder has 3, so the 400 ms budget was not
  measured at its size).
- **Look at the Phase 2 UI by eye:** progress bar, panes and headers, mouse back and forward, F5 / F6 / swap, preview Move and Copy
  buttons. Only compiled, tested and started so far.

## Can be done without him

- **UAT playbooks** for E1b, E2, F2 and F3 (E1 and F1 have one).
- **Cold start:** about 1.0 s against the 600 ms budget; needs a lighter `MainWindow` (`x:Load`, lazy chrome icons).
- **Copy speed:** 105 to 106 MB/s against `robocopy` at 118; try deeper read-ahead and bigger buffers.
- **Stalls:** tab tear-off after a new tab (262 ms in one run) and sidebar navigation; the 40,000-file view-switching freezes seen in a Debug build.
- **UI smoke tests:** re-run on a quiet machine, with Brett's say-so first (they use the real mouse and keyboard; they once deleted a real folder before the worker isolation went in).
- **Cleanups:** tick the C3 (Operations panel) plan entry once used; fix the flaky `LiveRefreshTests` and `ConcurrentWriters_DoNotFail`.
- **Small gaps:** `\\server\share` paths without a drive letter are not counted for folder sizes; no draggable gap between panes; no Sign-in button in the second pane; undo of a partly copied folder leaves what arrived; extra mouse shortcuts (Shift+Backspace for Up, middle-click Back for the history list).

## Next phase

- **Phase 3, Media and triage:** starts on Brett's go-ahead (`docs/build-plan.md`). Specs for phases 6 to 10 are written (104d779) and not built.
