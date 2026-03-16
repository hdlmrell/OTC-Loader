# OTC Loader

**OTC Loader** is a lightweight MelonLoader plugin that automatically detects your game branch (IL2CPP or Mono) and disables any incompatible mod DLLs before they can crash MelonLoader. It works for **all mods**, not just OverTheCounter.

OTC Loader is a standalone plugin — install it alongside any mod that ships dual-branch DLLs.

## For mod authors
If your mod ships both IL2CPP and Mono DLLs, you can point your users to install OTC Loader instead of writing your own branch detection. Add it as a dependency or recommend it in your install instructions.

## How it works
1. **Restore pass** — Re-enables any DLLs it previously disabled, so branch switches work automatically.
2. **Scan pass** — Checks every DLL in your `Mods` folder. DLLs targeting the wrong branch are renamed to `.dll.off`.
3. **Compatibility check** — If a disabled DLL has no compatible counterpart, a prominent log warning tells the user which mod needs a different version.
4. **Restart prompt** — On first-time disables, a popup recommends restarting so the runtime fully unloads cached assemblies.

### Branch detection
OTC Loader uses a two-stage detection strategy:
1. **Filename keywords** — DLLs containing "mono" or "il2cpp" in their filename are classified instantly.
2. **Mono.Cecil fallback** — If the filename is ambiguous, the DLL is inspected for type references to `Il2CppScheduleOne`/`Il2CppSystem` (IL2CPP) or `ScheduleOne` (Mono).

### Whitelist
When OTC Loader disables incompatible DLLs for the first time, an interactive prompt lets you review each one and optionally whitelist it with a single click. You can also manage the whitelist manually by editing the `Whitelist` array in `OTCLoader.config.json` (created automatically next to the DLL on first run).

## Installation

### Using a mod manager (recommended)
Install OTC Loader from Thunderstore using **r2modman**, **Vortex**, or **Gale**. The only dependency is MelonLoader.

### Manual installation
Drop `OverTheCounter-Loader.dll` into your game's `Plugins` folder.

## Requirements
- [MelonLoader](https://melonwiki.xyz/) v0.7.0+
- Schedule I by TVGS

## License
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You are free to:
* **Share** — copy and redistribute the material in any medium or format.
* **Adapt** — remix, transform, and build upon the material.

Under the following terms:
* **Attribution** — You must give appropriate credit to the original author (hdlmrell) and indicate if changes were made. You may not suggest the author endorses you or your use.
* **NonCommercial** — You may not use the material for commercial purposes.
* **ShareAlike** — If you remix, transform, or build upon the material, you must distribute your contributions under the same license as the original.
