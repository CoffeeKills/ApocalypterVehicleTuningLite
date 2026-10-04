# Apocalypter Vehicle Tuning Lite

A small BepInEx 5 mod for **Apocalypter** that makes every vehicle friendlier to
drive with keyboard and mouse: steering response, suspension, and ABS/TCS assists —
nothing else. Four tabs: **Steering**, **Suspension**, **Assists**, **Settings**.

This is the lite companion of [Apocalypter Vehicle Tuning](https://github.com/CoffeeKills/ApocalypterVehicleTuning),
which adds wheel alignment, aero, brakes, grip, drivetrain layouts, gearbox tuning and
a telemetry strip. This repo deliberately ships none of that: it is feature-complete,
not in active development, and safe for mod managers to bundle as-is.

## Quick start

1. Install BepInEx 5.4.x (win_x64) for Apocalypter.
2. Copy `ApocalypterSteeringLite.dll` into `Apocalypter\BepInEx\plugins\`.
3. (Optional) Copy `ApocalypterSteeringLite.png` next to the DLL — it gives the mod
   its icon in the Apocasetter Mods menu.
4. Start the game and press **F7** (or the "Vehicle Tuning Lite" button in the pause
   menu) to open the panel.

Everything starts **off** — the game drives exactly like vanilla until you opt in.
A good starting setup for keyboard and mouse:

- **Steering** → ON, preset **Euro Truck** (or **GTA-style** for arcade feel)
- **Suspension** → ON, preset **Comfort** (or **Off-road** for rough terrain)
- **Assists** → ON, preset **Standard** (ABS + traction control)

Every change applies live and is saved when you close the panel. The panel keeps the
game running while it is open; toggle "Freeze game while open" in the Settings tab for
the old paused behaviour.

## Tabs

- **Steering** — presets (Vanilla, GTA-style, Euro Truck, Sim/Race, Drift, Custom)
  plus response sliders and two editable graphs (lock-at-speed and return-to-centre).
  Click a graph to add a point, drag to move, double-click to remove. The front slip
  clamp and counter-steer boost are what keep keyboard slides catchable.
- **Suspension** — presets (Stock, Comfort, Sport, Off-road, Race, Custom) plus
  spring/ride-height/damping/anti-roll-bar factors, split front/rear if you want.
- **Assists** — presets (Off, Standard, Sport, Off-road, Race, Custom) for ABS and
  traction control with per-assist thresholds.
- **Settings** — panel hotkey (rebind by clicking), freeze toggle, transparency,
  size, width.

## Config

Settings live in `Apocalypter\BepInEx\config\dev.apocalypter.vehicletuninglite.cfg`.
Apocasetter lists the mod with every setting as a live editor. If ApocaLanguage is
installed, the panel translates automatically when a language pack covers its strings.

## Do not run alongside the full mod

The full "Apocalypter Vehicle Tuning" and this lite build patch the same steering
code — install one or the other, never both.

## Build

```
cd plugin
dotnet build -c Release
```

Requires the .NET SDK and the game's DLLs (the .csproj references them by absolute
path under the default Steam location; adjust if yours differs).
