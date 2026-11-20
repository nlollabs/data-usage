# Data Usage

A tiny Windows app that shows the data usage of your current internet connection in one small window, so you don't have to dig through **Settings > Network & internet > Advanced network settings > Data usage**.

It is a single ~120 KB `.exe` with nothing to install.

## What it shows

- The connection currently providing internet (name, Ethernet / Wi-Fi / cellular, metered or not)
- If you have a **data limit** set in Windows:
  - percent used, with a bar that turns amber at 75% and red at 90%
  - data used, data remaining and the limit (daily, weekly, monthly or one time)
  - when the period started, when it resets, and the time left
  - a marker on the bar showing how far through the period you are
- If there is **no limit**: total usage for the last 24 hours, 7 days or month (your pick)
- Download / upload split
- The top apps by data used

## Using it

- Download `DataUsage.exe` from the [Releases](../../releases) page and run it.
- It refreshes every minute. **F5** refreshes now, **Esc** closes it.
- **Minimize** hides it to the system tray:
  - left click the tray icon to bring the window back
  - right click for a menu (open, refresh, Windows data usage settings, start with Windows, exit)
  - hover for a quick usage tooltip
- **Start with Windows (in tray)** is off by default. Tick it in the window or the tray menu to launch hidden in the tray at sign-in. It adds one entry under your user's `Run` registry key and removes it when unticked.
- **Open in Settings** jumps to the real Windows Data usage page, which is where you set or change the limit.

Windows may show a SmartScreen prompt the first time, because the exe is not code signed.

## Requirements

- Windows 10 or 11
- .NET Framework 4.x (already part of Windows)

## Building

No Visual Studio or SDK needed. The build uses the C# compiler that ships with Windows:

```
src\build.cmd
```

This writes `DataUsage.exe` to the repository root.

## How it works

- Usage totals and the per-app breakdown come from the `Windows.Networking.Connectivity` API (the same data Windows keeps for the Settings page).
- The limit and the running total for the current period come from the Windows Connection Manager (`WcmQueryProperty`), with a fallback to the Data Usage service's registry settings.
- The app only reads these. It never changes your limit or any network setting.

## Known limitations

- The limit details rely on Windows internals that are not officially documented, so a future Windows update could change them.
- Windows resets its counter a little after a period ends. Until it does, the app keeps showing the period Windows is still counting, so the numbers match the Settings page.
- App names are shown as program names (for example `steamwebhelper`) rather than the friendly names Settings uses.
- The **Open in Settings** button steps into the Data usage page by finding its button by its English name. On other display languages it stops at the Network & internet page.
- Usage is measured by your device. Your provider's count may differ.
