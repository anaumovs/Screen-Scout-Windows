# Verification

This release publishes the application source and behavior verified on November 16, 2026. The publishing snapshot was checked against the saved source hashes and backup: all 27 application source/project/manifest files matched that version before Git's line-ending normalization. The release is rebuilt from this snapshot.

## Automated logic checks

Run `dotnet run --project tests/ScoutChecks -c Release`, or `Scripts/verify.ps1` on Windows. There are **198 passing checks** covering settings compatibility, default-off secondary clicking, interval validation, independent timers, curved routes, red blocked areas, click pairing, saved targets, cancellation, expiry, displacement and unchanged legacy behavior when secondary clicking is off.

The accelerated scenario exercises **25 simulated hours**, 21,752 clicks, 74 injected displacements, 24 input-monitor recovery cycles and 230,626 settling waits. It uses a simulated clock and mouse output with the production controller. It combines regular clicking, secondary trips, automatic breaks, latency and recovery; it does not prove 24-hour Windows or hardware reliability.

## Windows delivery and UI checks

The November 16 verification delivered real input only to the included harmless Click Test Pad, with foreground and destination-process guards:

- **62 secondary checks:** registered raw-device monitoring; own-input filtering; left/right clicks; regular clicking on/off with a 100-second interval; smooth returns; target preservation; no travel clicks; cancellation; and an untagged Windows cursor reposition followed by a smooth return without duplicate clicking.
- **67 existing-behavior checks:** ordinary clicks, movement, breaks, shortcuts, session/final-click behavior and input-monitor recovery.
- **8 UI checks:** visible Advanced navigation, fixed actions, readable disabled controls, compact-window layout and no mouse output during rendering.

Manual-device interruption in the automated native test used a fault-injected device signal alongside real cursor displacement. Physical mouse/touchpad interruption and physical mixed-DPI multi-monitor endurance were not performed in this update. Earlier intermediate candidates failed added own-input checks; the delivered version includes the tag filter and passed the final suites.

To repeat native delivery checks, close all Screen Scout instances, build the package and run `Scripts/verify-external.ps1`. Keep its test pad focused. For the secondary suite, run the packaged application with `--verify-external --secondary-only --report <absolute-report-path>`. These tests move and click the real pointer in the pad; F8 aborts them.

The public repository contains summarized evidence and reproducible tests. Local incident logs, pointer traces, settings, screenshots and user-specific paths are not published.
