# Screen Scout Windows — secondary clicking test release

Download **Screen-Scout-Windows-x64.zip**, extract every file, and run **ScreenScoutTest.exe**. The package includes the Windows x64 runtime. The optional **Add Desktop Shortcut.cmd** creates a separate Screen Scout Test shortcut.

This is the Screen Scout Test version with the secondary-trip pause correction:

- Optional purple secondary click areas, independent randomized waits, curved outbound/return travel and one selected-button click per visit.
- Raw-device observation with filtering of Screen Scout's own tagged input, tolerance for delayed movement, and safe cancellation/return after cursor displacement.
- Preserved regular targets, blocked-area rules, cancellation controls, existing UI and separate test settings.

Validation: 198 automated logic checks, including a 25-hour accelerated scenario; 129 live Windows checks in the harmless test pad; eight UI checks. The long-duration scenario was simulated, not a literal 24-hour desktop run. See `docs/VERIFICATION.md` for the distinction.

The app starts stopped. F8 is the default emergency stop. Disable or extend the session timer for longer sessions. This test build is not code signed. No personal settings, logs, backups, Play Money demo or Live Watch scripts are included.
