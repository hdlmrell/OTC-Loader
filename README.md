# OTC Loader
**Release:** [![MLVScan Attestation](https://api.mlvscan.com/public/attestations/att_iwEGpIeriKdDIS_utJQvV88P/badge.svg?style=split-pill)](https://mlvscan.com/attestations/att_iwEGpIeriKdDIS_utJQvV88P)

**OTC Loader** is a lightweight MelonLoader plugin that automatically detects your game branch (IL2CPP or Mono) and disables any incompatible mod DLLs before they can crash MelonLoader. It works for **all mods**, not just OverTheCounter.

OTC Loader is a standalone plugin — install it alongside any mod that ships dual-branch DLLs.

## For mod authors
If your mod ships both IL2CPP and Mono DLLs, you can point your users to install OTC Loader instead of writing your own branch detection. Add it as a Thunderstore dependency or recommend it in your install instructions.

## How it works
1. **Restore pass** — Re-enables any DLLs it previously disabled (`.dll.off` and legacy `.dll.di` formats), so branch switches work automatically without manual cleanup.
2. **Scan pass** — Checks every DLL in your `Mods` folder (including subdirectories). DLLs targeting the wrong branch are renamed to `.dll.off`. DLLs inside `Plugins/` subfolders and infrastructure DLLs (S1API, SwapperPlugin) are always skipped.
3. **Compatibility check** — If a disabled DLL has no compatible counterpart, a detailed log warning identifies the mod and suggests checking for a compatible release or switching game branches.
4. **Interactive review** — On first-time disables, popup prompts let you review each incompatible mod, optionally whitelist individual DLLs, and manage your existing whitelist — all before a recommended restart.
5. **Restart prompt** — A final popup explains which mods were disabled, confirms the game is safe to run, and recommends restarting so the runtime fully unloads cached assemblies.

### Branch detection
OTC Loader uses a two-stage detection strategy:
1. **Filename keywords** — DLLs containing "mono" or "il2cpp" in their filename are classified instantly.
2. **Mono.Cecil fallback** — If the filename is ambiguous, the DLL's type references are inspected via Mono.Cecil. IL2CPP-prefixed namespaces (`Il2CppScheduleOne`, `Il2CppSystem`) are matched first; bare `ScheduleOne` references are then used to identify Mono. DLLs with no recognizable references (native DLLs, locked files, branch-agnostic libraries) return unknown and are left alone.

### Whitelist
When incompatible DLLs are detected for the first time, an interactive prompt walks you through each one — keep it disabled or whitelist it with a single click. If you already have a whitelist, you're given the option to keep or clear it.

You can also manage the whitelist manually by editing the `Whitelist` array in `OTCLoader.config.json` (created automatically next to the DLL on first run).

### Duplicate safety
If both a standalone copy and a bundled copy (e.g. from an older OverTheCounter install) are present, the second instance detects the first already ran and skips execution. No conflicts, no double-processing.

## Installation

### Using a mod manager (recommended)
Install OTC Loader from Thunderstore using **r2modman**, **Vortex**, or **Gale**. The only dependency is MelonLoader.

### Manual installation
Drop `OverTheCounter-Loader.dll` into your game's `Plugins` folder.

## Requirements
- [MelonLoader](https://melonwiki.xyz/) v0.7.0+
- Schedule I by TVGS

## Changelog

### v1.0.5
- Fixed branch detection for MelonPlugins in `Plugins/` subfolders — dual-branch plugins (e.g. MeshVault) are now correctly handled instead of being skipped entirely
- Plugins with only a single variant are left alone to avoid false-positive disables

### v1.0.4
- Packaged for Thunderstore
- Added MLVScan attestation for release builds

### v1.0.1
- Extracted OTC Loader into a standalone project (previously bundled inside the OverTheCounter repo)
- Added interactive whitelist management — review each incompatible mod individually and whitelist with a single click, or keep/clear the existing whitelist via popup prompts
- Improved restart prompt — clearer "SAFE TO RUN" messaging with explicit instructions to restart via mod manager
- Fixed root Mods folder grouping — mods in the root `Mods/` directory are now treated individually instead of being incorrectly grouped together
- Added "compatible version kept" log messages when a matching branch DLL exists
- Duplicate execution prevention — safe to install both standalone and bundled copies simultaneously

### v1.0.0
- Initial release — branch detection, automatic disable/restore, restart prompt, Mono.Cecil fallback inspection, JSON whitelist config

## License
**Creative Commons Attribution-NonCommercial-ShareAlike 4.0 International (CC BY-NC-SA 4.0)**

You are free to:
* **Share** — copy and redistribute the material in any medium or format.
* **Adapt** — remix, transform, and build upon the material.

Under the following terms:
* **Attribution** — You must give appropriate credit to the original author (hdlmrell) and indicate if changes were made. You may not suggest the author endorses you or your use.
* **NonCommercial** — You may not use the material for commercial purposes.
* **ShareAlike** — If you remix, transform, or build upon the material, you must distribute your contributions under the same license as the original.
