using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using MelonLoader;
using MelonLoader.Utils;
using Mono.Cecil;
using Newtonsoft.Json;

[assembly: MelonInfo(typeof(OverTheCounter.Loader.LoaderPlugin), "OTC Loader", "1.0.1", "hdlmrell", null)]
[assembly: MelonColor(100, 200, 180, 255)]

namespace OverTheCounter.Loader
{
    internal enum Branch { Mono, Il2Cpp }

    [Serializable]
    internal class LoaderConfig
    {
        /// <summary>DLL filenames (case-insensitive) that the loader will never disable.</summary>
        // string[] instead of List<string>: List<T> references System.Collections v6.0.0.0, which
        // doesn't exist in Mono's CLR and prevents the plugin from loading on Mono games.
        public string[] Whitelist = Array.Empty<string>();
    }

    /// <summary>
    /// MelonPlugin that runs before any mods load and disables wrong-branch DLLs.
    /// Detects branch via filename keywords ("mono"/"il2cpp"), with Mono.Cecil inspection as fallback.
    /// Restores previously-disabled DLLs first so branch switches work automatically.
    /// Replaces the functionality of SwapperPlugin, which has a critical bug on fresh installs.
    /// </summary>
    public class LoaderPlugin : MelonPlugin
    {
        // Use .off instead of .di so SwapperPlugin's restore pass can't undo our work.
        // SwapperPlugin restores by stripping ".di" from filenames — ".off" is immune to that.
        // We still restore legacy .di files in pass 1 for users who previously had SwapperPlugin working.
        private const string DisabledExt = ".off";
        private const string ConfigFileName = "OTCLoader.config.json";

        // Never touch these — infrastructure or ourselves.
        // string[] instead of HashSet<string>: same reason as LoaderConfig.Whitelist above.
        private static readonly string[] BuiltinBlacklist =
        {
            "OverTheCounter-Loader.dll",
            "SwapperPlugin.dll",
        };

        // MB_OKCANCEL = 0x01, MB_ICONWARNING = 0x30, IDOK = 1
        private const uint MB_OKCANCEL = 0x00000001;
        private const uint MB_YESNO = 0x00000004;
        private const uint MB_ICONQUESTION = 0x00000020;
        private const uint MB_ICONWARNING = 0x00000030;
        private const uint MB_ICONINFORMATION = 0x00000040;

        private const int IDOK = 1;
        private const int IDCANCEL = 2;
        private const int IDYES = 6;
        private const int IDNO = 7;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

        private static readonly MelonLogger.Instance Logger = new MelonLogger.Instance("OTC Loader");
        private static LoaderConfig _config = new LoaderConfig();
        private static string _configPath = "";

        /// <summary>
        /// Runs before any mods load. Restores previously-disabled DLLs, then disables
        /// any DLL that targets the wrong game branch (IL2CPP vs. Mono).
        /// </summary>
        public override void OnPreInitialization()
        {
            // Prevent duplicate execution if both standalone Loader and full OTC package are installed.
            const string sentinel = "OTC_LOADER_INITIALIZED";
            if (AppDomain.CurrentDomain.GetData(sentinel) != null)
            {
                Logger.Msg("Another OTC Loader instance already ran — skipping this copy.");
                return;
            }
            AppDomain.CurrentDomain.SetData(sentinel, true);

            string modsPath = MelonEnvironment.ModsDirectory;
            if (!Directory.Exists(modsPath)) return;

            LoadConfig();

            Branch gameBranch = MelonUtils.IsGameIl2Cpp() ? Branch.Il2Cpp : Branch.Mono;
            string branchName = gameBranch == Branch.Il2Cpp ? "IL2CPP" : "Mono";
            string wrongBranchName = gameBranch == Branch.Il2Cpp ? "Mono" : "IL2CPP";

            // ── Pass 1: Restore all previously-disabled DLLs ─────────────────────
            // Runs first so branch switches are handled automatically.
            // Handles:
            //   .dll.off      — our format
            //   .dll.off.di   — our .off file that SwapperPlugin subsequently disabled (runs after us alphabetically)
            //   .dll.di       — legacy SwapperPlugin format
            // Track filenames we restore from OUR format so Pass 2 can suppress the "Disabled" log —
            // these were already disabled last run, no need to tell the user again.
            string[] alreadyDisabled = new string[256];
            int alreadyDisabledCount = 0;

            foreach (string diFile in Directory.GetFiles(modsPath, "*", SearchOption.AllDirectories))
            {
                bool isOurs = diFile.EndsWith(".dll" + DisabledExt, StringComparison.OrdinalIgnoreCase);
                bool isOursPlusDi = diFile.EndsWith(".dll" + DisabledExt + ".di", StringComparison.OrdinalIgnoreCase);
                bool isLegacy = diFile.EndsWith(".dll.di", StringComparison.OrdinalIgnoreCase);
                if (!isOurs && !isOursPlusDi && !isLegacy) continue;

                // Strip the full suffix back to the original .dll name.
                // isOursPlusDi must be checked before isLegacy because .dll.off.di ends with .di too.
                string ext = isOursPlusDi ? (DisabledExt + ".di") : (isLegacy ? ".di" : DisabledExt);
                string original = diFile.Substring(0, diFile.Length - ext.Length);
                try
                {
                    if (!File.Exists(original))
                    {
                        File.Move(diFile, original);
                        // Record that this file was already disabled by us (not first-time)
                        if ((isOurs || isOursPlusDi) && alreadyDisabledCount < alreadyDisabled.Length)
                            alreadyDisabled[alreadyDisabledCount++] = Path.GetFileName(original);
                    }
                    else
                        File.Delete(diFile); // stale .off/.di alongside an existing .dll — clean it up
                }
                catch (Exception ex)
                {
                    Logger.Warning("Could not restore " + Path.GetFileName(diFile) + ": " + ex.Message);
                }
            }

            // ── Pass 2: Evaluate every DLL and disable wrong-branch ones ──────────
            string[] allDlls = Directory.GetFiles(modsPath, "*.dll", SearchOption.AllDirectories);

            // Pre-compute skip flags and branches using only arrays (no List/Dictionary/LINQ).
            // List<T>/Dictionary<K,V> reference System.Collections v6.0.0.0, which Mono lacks.
            bool[] skip = new bool[allDlls.Length];
            Branch?[] branches = new Branch?[allDlls.Length];

            for (int i = 0; i < allDlls.Length; i++)
            {
                string filename = Path.GetFileName(allDlls[i]);
                string dir = Path.GetDirectoryName(allDlls[i]);

                // DLLs inside a Plugins/ subfolder are MelonPlugins with their own loading — skip them.
                skip[i] = Path.GetFileName(dir).Equals("Plugins", StringComparison.OrdinalIgnoreCase)
                           || IsBlacklisted(filename);

                if (!skip[i])
                    branches[i] = DetectBranch(allDlls[i]);
            }

            int disabled = 0;
            string[] firstTimeDisabled = new string[allDlls.Length]; // DLL paths disabled for the first time this session
            int firstTimeCount = 0;
            string[] warnedDirs          = new string[allDlls.Length]; // at most one entry per unique dir
            string[] warnedModNames      = new string[allDlls.Length]; // human-readable name for each
            string[] warnedDisabledLists = new string[allDlls.Length]; // disabled DLL filenames per entry
            int warnedCount = 0;

            for (int i = 0; i < allDlls.Length; i++)
            {
                if (skip[i] || branches[i] == null || branches[i] == gameBranch) continue;

                string dll = allDlls[i];
                string filename = Path.GetFileName(dll);
                string dir = Path.GetDirectoryName(dll);

                bool wasAlreadyDisabled = false;
                try
                {
                    File.Move(dll, dll + DisabledExt);
                    disabled++;
                    // Only log the first time — if it was already .off last run, stay silent.
                    for (int a = 0; a < alreadyDisabledCount; a++)
                    {
                        if (string.Equals(alreadyDisabled[a], filename, StringComparison.OrdinalIgnoreCase))
                        { wasAlreadyDisabled = true; break; }
                    }
                    if (!wasAlreadyDisabled)
                    {
                        Logger.Msg("Disabled '" + filename + "' — targets " + wrongBranchName + " but game is " + branchName + ".");
                        if (firstTimeCount < firstTimeDisabled.Length)
                            firstTimeDisabled[firstTimeCount++] = dll;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning("Could not disable '" + filename + "': " + ex.Message);
                    continue;
                }

                // ── Pass 3: Warn once per directory that has no compatible DLL ──────
                bool inRootModsFolder = string.Equals(dir, modsPath, StringComparison.OrdinalIgnoreCase);
                bool alreadyWarned = false;
                for (int w = 0; w < warnedCount; w++)
                {
                    // Do not group ROOT mods folder items together — they are separate mods.
                    if (!inRootModsFolder && string.Equals(warnedDirs[w], dir, StringComparison.OrdinalIgnoreCase))
                    {
                        alreadyWarned = true;
                        break;
                    }
                }

                if (!alreadyWarned)
                {
                    bool hasCompat = false;
                    string myBase = StripBranchKeyword(filename);
                    for (int j = 0; j < allDlls.Length; j++)
                    {
                        if (j == i || skip[j]) continue;
                        if (branches[j] != null && branches[j] != gameBranch) continue;
                        // Same directory (only if not root mods folder), or matching base name anywhere in the Mods tree
                        bool sameDir  = !inRootModsFolder && string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase);
                        bool sameName = string.Equals(StripBranchKeyword(Path.GetFileName(allDlls[j])), myBase, StringComparison.OrdinalIgnoreCase);
                        if (sameDir || sameName) { hasCompat = true; break; }
                    }

                    if (hasCompat)
                    {
                        // Only log compat message on first-time disables
                        if (!wasAlreadyDisabled)
                        {
                            string compatName = "";
                            for (int j = 0; j < allDlls.Length; j++)
                            {
                                if (j == i || skip[j]) continue;
                                if (branches[j] != null && branches[j] != gameBranch) continue;
                                bool sameDir  = !inRootModsFolder && string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase);
                                bool sameName = string.Equals(StripBranchKeyword(Path.GetFileName(allDlls[j])), myBase, StringComparison.OrdinalIgnoreCase);
                                if (sameDir || sameName) { compatName = Path.GetFileName(allDlls[j]); break; }
                            }
                            Logger.Msg("  → Compatible version kept: " + compatName);
                        }
                    }
                    else
                    {
                        string modName = Path.GetFileNameWithoutExtension(filename);
                        string disabledList = "";
                        for (int j = 0; j < allDlls.Length; j++)
                        {
                            if (skip[j] || branches[j] == null || branches[j] == gameBranch) continue;
                            if (!string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase)) continue;

                            // If we're evaluating the root Mods directory, only list THIS specific file as the "disabled list"
                            // rather than every disabled dll in the entire root folder.
                            if (inRootModsFolder && !string.Equals(filename, Path.GetFileName(allDlls[j]), StringComparison.OrdinalIgnoreCase)) continue;

                            if (disabledList.Length > 0) disabledList += ", ";
                            disabledList += Path.GetFileName(allDlls[j]);
                        }
                        warnedDirs[warnedCount]          = dir;
                        warnedModNames[warnedCount]      = modName;
                        warnedDisabledLists[warnedCount] = disabledList;
                        warnedCount++;
                    }
                }
            }

            if (warnedCount > 0)
            {
                Logger.Warning("╔══════════════════════════════════════════════════════════╗");
                Logger.Warning("║  INCOMPATIBLE MODS — no " + branchName + "-compatible version found");
                Logger.Warning("║");
                for (int w = 0; w < warnedCount; w++)
                {
                    Logger.Warning("║  • " + warnedModNames[w]);
                    Logger.Warning("║    Disabled: " + warnedDisabledLists[w]);
                }
                Logger.Warning("║");
                Logger.Warning("║  → Check each mod page for a " + branchName + "-compatible release,");
                Logger.Warning("║    or switch your game to the branch those mods support.");
                Logger.Warning("║");
                Logger.Warning("║  → Think this is a mistake? Open the config file and");
                Logger.Warning("║    add the filename to the Whitelist:");
                Logger.Warning("║    " + _configPath);
                Logger.Warning("╚══════════════════════════════════════════════════════════╝");
            }

            if (disabled > 0 || warnedCount > 0)
            {
                string incompatNames = "";
                for (int w = 0; w < warnedCount; w++)
                {
                    if (incompatNames.Length > 0) incompatNames += ", ";
                    incompatNames += warnedModNames[w];
                }
                string incompatSuffix = warnedCount > 0 ? (": " + incompatNames) : "";
                Logger.Msg("Done — " + disabled + " wrong-branch DLL(s) correctly handled, " +
                                warnedCount + " mod(s) have no compatible version" + incompatSuffix + ".");
            }
            else
                Logger.Msg("All DLLs are compatible with " + branchName + ".");

            // ── Pass 4: Prompt interactive flow if DLLs were disabled for the first time ──
            // .NET's assembly resolver may have already cached the wrong-branch DLLs
            // before our plugin ran. A restart ensures the renamed files are invisible.
            if (firstTimeCount > 0)
                InteractiveFlow(firstTimeDisabled, firstTimeCount, gameBranch, allDlls, skip, branches, modsPath);
        }

        /// <summary>
        /// Prompts the user with an interactive flow when wrong-branch DLLs
        /// were disabled for the first time.
        /// </summary>
        private static void InteractiveFlow(string[] firstTimePaths, int count, Branch gameBranch, string[] allDlls, bool[] skip, Branch?[] branches, string modsPath)
        {
            string branchName = gameBranch == Branch.Il2Cpp ? "IL2CPP" : "Mono";
            string wrongBranchName = gameBranch == Branch.Il2Cpp ? "Mono" : "IL2CPP";

            string[] needsReviewPaths = new string[count];
            int needsReviewCount = 0;

            for (int i = 0; i < count; i++)
            {
                string dll = firstTimePaths[i];
                string filename = Path.GetFileName(dll);
                string dir = Path.GetDirectoryName(dll);
                string myBase = StripBranchKeyword(filename);

                bool hasCompat = false;
                for (int j = 0; j < allDlls.Length; j++)
                {
                    if (j == i || skip[j]) continue;
                    if (branches[j] != null && branches[j] != gameBranch) continue;

                    bool sameDir = !string.Equals(dir, modsPath, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(allDlls[j]), dir, StringComparison.OrdinalIgnoreCase);
                    bool sameName = string.Equals(StripBranchKeyword(Path.GetFileName(allDlls[j])), myBase, StringComparison.OrdinalIgnoreCase);
                    if (sameDir || sameName) { hasCompat = true; break; }
                }

                if (!hasCompat)
                    needsReviewPaths[needsReviewCount++] = dll;
            }

            bool configChanged = false;

            try
            {
                if (_config.Whitelist != null && _config.Whitelist.Length > 0)
                {
                    string clearMsg = "OTC Loader — Whitelist Management\n\n"
                        + "Your whitelist contains " + _config.Whitelist.Length + " mod(s).\n"
                        + "Keep the current whitelist or clear it entirely?\n\n"
                        + "[OK]     —  (Recommended) Keep current whitelist\n"
                        + "[Cancel] —  Clear whitelist";

                    if (MessageBox(IntPtr.Zero, clearMsg, "OTC Loader", MB_OKCANCEL | MB_ICONQUESTION) == IDCANCEL)
                    {
                        _config.Whitelist = Array.Empty<string>();
                        configChanged = true;
                        Logger.Msg("User chose to clear the whitelist.");
                    }
                }

                bool reviewEach = false;
                if (needsReviewCount > 0)
                {
                    string summaryList = "";
                    for (int i = 0; i < needsReviewCount; i++)
                    {
                        if (i > 0) summaryList += "\n";
                        summaryList += "• " + Path.GetFileName(needsReviewPaths[i]);
                        if (i >= 9) { summaryList += "\n...and " + (needsReviewCount - 10) + " more."; break; }
                    }

                    string summaryMsg = "OTC Loader — Incompatible Mods Detected\n\n"
                        + "These mods target " + wrongBranchName + " but your game runs " + branchName + ".\n"
                        + "No compatible versions were found, so they were disabled:\n\n"
                        + summaryList + "\n\n"
                        + "Keep all of them disabled, or review each mod to optionally whitelist them?\n\n"
                        + "[OK]     —  (Recommended) Keep all disabled\n"
                        + "[Cancel] —  Review each mod";

                    reviewEach = (MessageBox(IntPtr.Zero, summaryMsg, "OTC Loader", MB_OKCANCEL | MB_ICONWARNING) == IDCANCEL);
                }

                if (reviewEach)
                {
                    for (int i = 0; i < needsReviewCount; i++)
                    {
                        string filename = Path.GetFileName(needsReviewPaths[i]);
                        string reviewMsg = "OTC Loader — Review Mod\n\n"
                            + filename + "\n\n"
                            + "This mod targets " + wrongBranchName + " but your game runs " + branchName + ".\n\n"
                            + "Keep this mod disabled, or add it to the whitelist? (May cause crashes if whitelisted)\n\n"
                            + "[OK]     —  (Recommended) Keep disabled\n"
                            + "[Cancel] —  Whitelist this mod";

                        if (MessageBox(IntPtr.Zero, reviewMsg, "OTC Loader", MB_OKCANCEL | MB_ICONQUESTION) == IDCANCEL)
                        {
                            string[] oldWl = _config.Whitelist ?? Array.Empty<string>();
                            string[] newWl = new string[oldWl.Length + 1];
                            Array.Copy(oldWl, newWl, oldWl.Length);
                            newWl[oldWl.Length] = filename;
                            _config.Whitelist = newWl;
                            configChanged = true;
                            Logger.Msg("User whitelisted: " + filename);
                        }
                    }
                }

                if (configChanged) SaveConfig();
            }
            catch (Exception ex)
            {
                Logger.Warning("Interactive prompt failed (falling back to log-only): " + ex.Message);
            }

            string allModsList = "";
            for (int i = 0; i < count; i++)
            {
                if (allModsList.Length > 0) allModsList += ", ";
                allModsList += Path.GetFileNameWithoutExtension(firstTimePaths[i]);
            }

            Logger.Warning("First-time disable of: " + allModsList);
            Logger.Warning("A restart is recommended so the disabled DLLs are fully unloaded.");

            string restartMsg = "SAFE TO RUN! Your compatible mods will work fine.\n\n"
                + "OTC Loader safely disabled these incompatible mod files:\n"
                + allModsList + "\n\n"
                + "The runtime may have already cached the old files. A restart is recommended.\n\n"
                + "[OK]     —  (Recommended) Close game NOW. Restart via your mod manager.\n"
                + "[Cancel] —  Continue anyway (may cause errors)";

            try
            {
                if (MessageBox(IntPtr.Zero, restartMsg, "OTC Loader — Restart Recommended", MB_OKCANCEL | MB_ICONINFORMATION) == IDOK)
                    Environment.Exit(0);
            }
            catch
            {
                Logger.Warning("╔══════════════════════════════════════════════════════════╗");
                Logger.Warning("║  RESTART RECOMMENDED                                     ║");
                Logger.Warning("║                                                          ║");
                Logger.Warning("║  Incompatible mods were disabled but may have already    ║");
                Logger.Warning("║  been cached. Please close and restart the game.         ║");
                Logger.Warning("║  Affected: " + allModsList);
                Logger.Warning("╚══════════════════════════════════════════════════════════╝");
            }
        }

        // ── Config ───────────────────────────────────────────────────────────────

        private static void LoadConfig()
        {
            _configPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                ConfigFileName);
            string configPath = _configPath;

            if (File.Exists(configPath))
            {
                try
                {
                    _config = JsonConvert.DeserializeObject<LoaderConfig>(File.ReadAllText(configPath)) ?? new LoaderConfig();
                }
                catch (Exception ex)
                {
                    Logger.Warning("Could not read config, using defaults: " + ex.Message);
                    _config = new LoaderConfig();
                }
            }
            else
            {
                _config = new LoaderConfig();
                try
                {
                    File.WriteAllText(configPath,
                        "{\n" +
                        "  \"_readme\": \"Add DLL filenames to Whitelist to prevent OTC Loader from disabling them.\",\n" +
                        "  \"Whitelist\": [\n" +
                        "    \"ExampleMod-IL2Cpp.dll\",\n" +
                        "    \"AnotherExampleMod-IL2Cpp.dll\"\n" +
                        "  ]\n" +
                        "}\n");
                    Logger.Msg("Created default config at " + configPath);
                }
                catch { /* Non-fatal — defaults apply */ }
            }
        }

        private static void SaveConfig()
        {
            if (string.IsNullOrEmpty(_configPath)) return;
            try
            {
                string json = JsonConvert.SerializeObject(_config, Formatting.Indented);
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                Logger.Warning("Could not save config: " + ex.Message);
            }
        }

        /// <summary>
        /// Strips branch keywords (IL2CPP / Mono) and common separators from a DLL name
        /// so that "SteamNetworkLib-IL2Cpp" and "SteamNetworkLib-Mono" compare equal.
        /// </summary>
        private static string StripBranchKeyword(string filename)
        {
            string name = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
            string[] tokens = { "-il2cpp", "_il2cpp", ".il2cpp", "-mono", "_mono", ".mono" };
            foreach (string token in tokens)
            {
                if (name.EndsWith(token))
                    return name.Substring(0, name.Length - token.Length);
            }
            return name;
        }

        private static bool IsBlacklisted(string filename) =>
            Array.Exists(BuiltinBlacklist, b => string.Equals(b, filename, StringComparison.OrdinalIgnoreCase)) ||
            filename.StartsWith("S1API", StringComparison.OrdinalIgnoreCase) || // S1API has its own branch detection — never touch it
            Array.Exists(_config.Whitelist ?? Array.Empty<string>(), w => string.Equals(w, filename, StringComparison.OrdinalIgnoreCase));

        // ── Detection ────────────────────────────────────────────────────────────

        /// <summary>
        /// Detects the target branch of a DLL.
        /// Returns null if the DLL appears branch-agnostic or cannot be determined.
        /// </summary>
        private static Branch? DetectBranch(string path)
        {
            string filename = Path.GetFileName(path).ToLowerInvariant();

            // Fast path: filename keyword
            if (filename.Contains("mono")) return Branch.Mono;
            if (filename.Contains("il2cpp")) return Branch.Il2Cpp;

            // Fallback: Mono.Cecil type reference inspection
            try
            {
                using var asm = AssemblyDefinition.ReadAssembly(path);
                foreach (var module in asm.Modules)
                {
                    foreach (var typeRef in module.GetTypeReferences())
                    {
                        string ns = (typeRef.Namespace ?? "").ToLowerInvariant();
                        if (ns.StartsWith("il2cppscheduleone") || ns.StartsWith("il2cppsystem"))
                            return Branch.Il2Cpp;
                        if (ns == "scheduleone")
                            return Branch.Mono;
                    }
                }
            }
            catch { /* Native DLLs, obfuscated assemblies, or locked files — skip silently */ }

            return null;
        }
    }
}
