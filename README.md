# Screen Scout Windows

A portable Windows auto-clicker with curved movement, allowed/blocked regions, a session timer, and optional secondary-area clicking. This release contains the **Screen Scout Test** version with the secondary-trip pause correction.

## Download

**[Download Screen Scout Windows for 64-bit Windows](https://github.com/anaumovs/Screen-Scout-Windows/releases/download/v2.3.2-windows-test.1/Screen-Scout-Windows-x64.zip)**

[Release notes and SHA-256 checksum](https://github.com/anaumovs/Screen-Scout-Windows/releases/tag/v2.3.2-windows-test.1)

1. Download the ZIP above and extract **all** its files into a folder you want to keep.
2. Open **ScreenScoutTest.exe**, or run **Add Desktop Shortcut.cmd** to create a Screen Scout Test shortcut.
3. Keep the extracted files together. The Windows x64 package includes its .NET runtime; no separate installation or administrator access is needed.

The GitHub **Code → Download ZIP** option downloads source code. Use the release download above for the runnable application. The application starts stopped. This test build is not code signed.

## Use

- Position the pointer over your target and press **F + G + H** to capture it and start. Press the chord again to pause.
- **Start** resumes using the saved target. **Retry same position** reconnects input monitoring without recapturing it.
- **Stop** cancels pending work. **F8** is the default emergency stop; it can be changed in Advanced.
- **I + O + P** captures the optional final-click target.
- **Open test pad** provides a harmless local window for trying settings.

### Secondary-area clicking

1. Open **Advanced**, select the intended **Active display**, and choose **Choose purple area…**.
2. Draw a purple rectangle and save it. One purple rectangle is supported per display; it can be moved, resized or deleted.
3. Enable **Secondary-area clicking**, which defaults to off. Choose a minimum and maximum wait; defaults are **5–10 seconds**. Equal values make the wait fixed. The accepted range is **0.2–3600 seconds**.
4. Start the session. Secondary visits work even when regular clicking and random movement are off.

Each visit follows a smooth curved route, clicks once using the selected button, and follows a curved return route. With regular clicking on, it returns to the saved regular target; otherwise it returns to where the visit began. Red blocked areas apply to the complete trip. Green areas continue to restrict large random movement only. A 100-second regular interval does not delay a secondary visit due after 5–10 seconds.

Brief cursor-delivery delays can settle without a false manual-movement pause. A displaced trip can be canceled and safely returned without retrying its click. Actual device movement still pauses the trip. Pause, Stop, emergency stop, unavailable output and timer expiry retain their cancellation behavior.

### Settings and coexistence

Settings are stored locally in `%LOCALAPPDATA%\ScreenScoutTest\ClickerSettingsV2.json`. If an older Screen Scout settings file exists, it is imported once without changing the original. Personal settings and targets are not included in the download. Targets are session-only.

The test app uses separate settings and a separate shortcut. The original and test applications share an input-ownership lock, so close one before opening the other. To return to an existing main installation, close Screen Scout Test and open the original shortcut.

For sessions longer than 24 hours, disable the session timer or set it beyond the desired duration. The PC must remain awake and unlocked. Verification includes **25 simulated hours**, not a 24-hour real-time unattended guarantee. See [verification](docs/VERIFICATION.md).

## Build from source

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
./Scripts/verify.ps1
./Scripts/build.ps1
```

The build script runs the logic checks and produces `dist/Screen-Scout-Windows-x64.zip` plus a SHA-256 checksum. GitHub Actions also builds and packages the project on pushes and pull requests.

The `src/ScoutCore` library contains scheduling, settings and motion logic. `src/ScreenScout` contains the WinForms UI and serialized Windows input adapter. `src/ClickTestPad` is the independent input receiver. `tests/ScoutChecks` contains deterministic controller tests and the accelerated endurance scenario.

This is the secondary-clicking test release, with the existing appearance, shortcuts and mouse-button choices preserved. The later Play Money demo and Live Watch additions are not part of this release. [Source provenance](docs/PROVENANCE.md).
