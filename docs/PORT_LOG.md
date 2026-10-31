# Port log — Color Splash

## Task 0: Baseline, tools, log — 2026-10-31
- Files: created `Tools/cc.ps1`, `Tools/smoke.ps1`, `docs/PORT_LOG.md`
- JS sources ported: none (tooling task)
- Compile: `Tools/cc.ps1` → exit 0
- Builds: Windows Succeeded (`[Inkwave] StandaloneWindows64 build Succeeded: Builds/Windows/ColorSplash.exe`, 178.2 MB, 0 errors)
- Smoke: `t0` → PASS (`Builds/Smoke/t0/summary.txt`), fps avg 45.6 (baseline)
- Captures: none (smoke screenshots in `Builds/Smoke/t0/`)
- Phone: not required for this task
- Known gaps / deviations from the JS: the first two baseline runs failed on the then-open boomerang crash (bots drew `boomerang`); after Task 1's fix the baseline was re-run and PASSed. Tools accept the plan's argument forms; `-Subs` must be called in-session (`& .\Tools\smoke.ps1 -Subs a,b`), because `powershell -File` flattens the array into one string.
- Commit: pending
