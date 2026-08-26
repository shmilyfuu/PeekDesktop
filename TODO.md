# PeekDesktop TODO

Deferred improvements for the independently maintained `shmilyfuu/PeekDesktop` fork.

The current v1.0.0 behavior is intentionally left unchanged until another functional update is planned.

## Next functional update

- [ ] Improve second-instance behavior.
  - Detect when another `PeekDesktop` instance is already running.
  - Instead of silently exiting, notify the existing instance and bring its Settings window to the foreground.
  - Make it obvious which executable location is currently running when multiple portable copies exist.
  - Consider showing the active executable path in Settings / About for easier diagnosis and testing.
  - Preserve the single-instance model; do not allow two PeekDesktop cores to manage windows simultaneously.

## Updates

- [ ] Redesign update checking for this fork.
  - Stop treating `shanselman/PeekDesktop` releases as application updates.
  - Check releases from `shmilyfuu/PeekDesktop` only.
  - Decide whether the first implementation should only report that a new release exists or also install it automatically.
  - Preserve the portable `data` directory during any future update process.
  - Keep update logic compatible with x64 and ARM64 releases.

## Upstream maintenance

- [ ] Review upstream changes manually when useful.
  - Treat `shanselman/PeekDesktop` as a reference source.
  - Evaluate individual fixes/features before porting them.
  - Reimplement or selectively cherry-pick changes only when they fit this fork's current architecture and behavior.
  - Do not automatically synchronize upstream releases into `main`.

## Possible future ideas

- [ ] Optional hotkey to open Settings or toggle peek.
- [ ] Evaluate per-monitor independent peek sessions if there is a real use case; current Fly Away supports targeting the clicked monitor but still uses one global peek session.
- [ ] Optional application exclusion list for Fly Away.
- [ ] Revisit update UI after the fork-specific release checker is implemented.
