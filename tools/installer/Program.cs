using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SpellBrigadeModsSetup
{
    /// <summary>
    /// One-click setup for a player: finds the game through Steam, backs up the save folder, installs
    /// the .NET 6 runtime (MelonLoader 0.7.x needs it) and MelonLoader 0.7.3 if missing, then copies the
    /// mod DLLs embedded in this exe into &lt;game&gt;/Mods. Downloads are pinned by SHA-256.
    /// Usage: SpellBrigadeModsSetup.exe [game folder] [--uninstall]
    /// </summary>
    internal static class Program
    {
        private const string AppId = "2904000";
        private const string GameExe = "TheSpellBrigade.exe";
        private const string GameProcess = "TheSpellBrigade";

        private const string MlVersion = "0.7.3";
        private const string MlUrl = "https://github.com/LavaGang/MelonLoader/releases/download/v0.7.3/MelonLoader.x64.zip";
        private const string MlSha256 = "5b2b2f3d1cd42b59ec886c5bdc2663edae87a0097a4f4a8f58c0965a99dda416";

        private const string RuntimeUrl = "https://builds.dotnet.microsoft.com/dotnet/Runtime/6.0.36/dotnet-runtime-6.0.36-win-x64.exe";
        private const string RuntimeSha256 = "6bdad7bc4c41fe93d4ae7b0312b1d017cfe369d28e7e2e421f5b675f9feefe84";

        private const string BackedUpFlag = "--backed-up";
        private const string UninstallFlag = "--uninstall";

        private static bool _pause = true;

        private static int Main(string[] args)
        {
            try
            {
                return Run(args);
            }
            catch (Exception e)
            {
                Error("Setup failed: " + e.Message);
                Console.WriteLine();
                Console.WriteLine(e);
                return 1;
            }
            finally
            {
                if (_pause)
                {
                    Console.WriteLine();
                    Console.Write("Press Enter to close.");
                    Console.ReadLine();
                }
            }
        }

        private static int Run(string[] args)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            bool uninstall = args.Contains(UninstallFlag);
            bool backedUp = args.Contains(BackedUpFlag);
            string argDir = args.FirstOrDefault(a => !a.StartsWith("--"));

            var mods = EmbeddedMods();
            string expectedBuild = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "GameBuild")?.Value;

            Console.WriteLine("Spell Brigade mods setup");
            Console.WriteLine("  mods: " + string.Join(", ", mods.Select(m => m.Name)));
            if (!string.IsNullOrEmpty(expectedBuild)) Console.WriteLine("  built for game build " + expectedBuild);
            Console.WriteLine();

            // 1. Game folder
            string game = FindGame(argDir, out string manifest);
            Info("Game folder: " + game);

            WaitForGameClosed(game);

            if (uninstall)
            {
                if (!EnsureWritable(game, args)) return 0;
                foreach (var m in mods)
                {
                    string path = Path.Combine(game, "Mods", m.Name);
                    if (File.Exists(path)) { File.Delete(path); Ok("Removed Mods\\" + m.Name); }
                }
                Info("MelonLoader and the .NET runtime were left in place.");
                return 0;
            }

            // 2. Game build
            string build = manifest != null ? Match(File.ReadAllText(manifest), "buildid") : null;
            if (!string.IsNullOrEmpty(expectedBuild) && build != null && build != expectedBuild)
            {
                Warn($"Your game build is {build}; these mods were built for {expectedBuild}.");
                Warn("A game update can break them. Make sure Steam has finished updating the game.");
                if (!AskYes("Continue anyway?")) return 1;
            }

            // 3. Save backup (as the invoking user, before any elevation)
            if (!backedUp) BackupSaves();

            // 4. Elevate if the game folder is not writable
            if (!EnsureWritable(game, args)) return 0;

            // 5. .NET 6 runtime
            EnsureRuntime();

            // 6. MelonLoader
            bool firstMlInstall = EnsureMelonLoader(game);

            // 7. Mods
            string modsDir = Path.Combine(game, "Mods");
            Directory.CreateDirectory(modsDir);
            foreach (var m in mods)
            {
                File.WriteAllBytes(Path.Combine(modsDir, m.Name), m.Bytes);
                Ok("Installed Mods\\" + m.Name);
            }

            Console.WriteLine();
            Ok("All done. Start the game from Steam.");
            if (firstMlInstall)
                Info("The first launch with MelonLoader takes several minutes (it generates files), and the main menu takes about 2 more. Let it finish.");
            Info("Check: " + Path.Combine(game, "MelonLoader", "Latest.log") + " should show a 'hooks N/N' line for each mod.");
            Info("To remove the mods later, run this setup with " + UninstallFlag + ".");
            return 0;
        }

        // ---- game folder -------------------------------------------------------------------------

        private static string FindGame(string argDir, out string manifest)
        {
            manifest = null;
            if (!string.IsNullOrEmpty(argDir) && File.Exists(Path.Combine(argDir, GameExe)))
            {
                manifest = ManifestFor(argDir);
                return Path.GetFullPath(argDir);
            }

            string here = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(here, GameExe)))
            {
                manifest = ManifestFor(here);
                return Path.GetFullPath(here);
            }

            foreach (string lib in SteamLibraries())
            {
                string acf = Path.Combine(lib, "steamapps", "appmanifest_" + AppId + ".acf");
                if (!File.Exists(acf)) continue;
                string dir = Match(File.ReadAllText(acf), "installdir");
                if (dir == null) continue;
                string game = Path.Combine(lib, "steamapps", "common", dir);
                if (File.Exists(Path.Combine(game, GameExe)))
                {
                    manifest = acf;
                    return game;
                }
            }

            Warn("Could not find The Spell Brigade through Steam.");
            while (true)
            {
                Console.Write("Paste the game folder (the one with " + GameExe + "): ");
                string input = (Console.ReadLine() ?? "").Trim().Trim('"');
                if (input.Length == 0) throw new Exception("No game folder given.");
                if (File.Exists(Path.Combine(input, GameExe)))
                {
                    manifest = ManifestFor(input);
                    return Path.GetFullPath(input);
                }
                Warn(GameExe + " is not in that folder.");
            }
        }

        private static string ManifestFor(string game)
        {
            // <lib>/steamapps/common/<game> -> <lib>/steamapps/appmanifest_<id>.acf
            string steamapps = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(game).TrimEnd('\\')));
            string acf = steamapps == null ? null : Path.Combine(steamapps, "appmanifest_" + AppId + ".acf");
            return acf != null && File.Exists(acf) ? acf : null;
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var roots = new List<string>();
            void Add(string p)
            {
                if (string.IsNullOrEmpty(p)) return;
                p = p.Replace('/', '\\').TrimEnd('\\');
                if (Directory.Exists(p) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p);
            }

            Add(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
            Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
            Add(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string);
            Add(@"C:\Program Files (x86)\Steam");

            foreach (string root in roots.ToList())
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (System.Text.RegularExpressions.Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            return roots;
        }

        private static string Match(string vdf, string key)
        {
            var m = Regex.Match(vdf, "\"" + key + "\"\\s+\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : null;
        }

        private static void WaitForGameClosed(string game)
        {
            while (GameRunningFrom(game))
            {
                Warn("The game is running. Close it, then press Enter.");
                if (Console.ReadLine() == null) throw new Exception("The game is still running.");
            }
        }

        private static bool GameRunningFrom(string game)
        {
            string dir = Path.GetFullPath(game).TrimEnd('\\');
            foreach (var p in Process.GetProcessesByName(GameProcess))
            {
                try
                {
                    if (string.Equals(Path.GetDirectoryName(p.MainModule.FileName), dir, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    return true; // path unreadable (e.g. game runs elevated): assume it is this copy
                }
            }
            return false;
        }

        // ---- saves -------------------------------------------------------------------------------

        private static void BackupSaves()
        {
            string local = Environment.GetEnvironmentVariable("USERPROFILE") ?? "";
            string saves = Path.Combine(local, "AppData", "LocalLow", "BoltBlasterGames", "TheSpellBrigade");
            if (!Directory.Exists(saves))
            {
                Info("No save folder yet; nothing to back up.");
                return;
            }
            string dest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "SpellBrigadeSaveBackups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            CopyTree(saves, dest);
            Ok("Saves backed up to " + dest);
            Info("If your profile ever looks reset, copy that folder's files back into " + saves + ".");
        }

        private static void CopyTree(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src)) CopyTree(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // ---- elevation ---------------------------------------------------------------------------

        /// <summary>True if this process can write to the game folder; otherwise relaunches elevated and returns false.</summary>
        private static bool EnsureWritable(string game, string[] args)
        {
            string probe = Path.Combine(game, ".sbm-write-test");
            try
            {
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new Exception("Cannot write to " + game + " even as administrator.");
            }

            Info("The game folder needs administrator rights; Windows will ask for permission.");
            var relaunch = args.Where(a => a.StartsWith("--")).Concat(new[] { BackedUpFlag }).Distinct()
                .Prepend("\"" + game + "\"");
            try
            {
                var p = Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly().Location, string.Join(" ", relaunch))
                {
                    Verb = "runas",
                    UseShellExecute = true,
                });
                p?.WaitForExit();
                _pause = false;
                return false;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw new Exception("Administrator permission was refused.");
            }
        }

        // ---- .NET 6 ------------------------------------------------------------------------------

        private static bool HasRuntime6()
        {
            string pf = Environment.GetEnvironmentVariable("ProgramW6432")
                        ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string fx = Path.Combine(pf, "dotnet", "shared", "Microsoft.NETCore.App");
            return Directory.Exists(fx) && Directory.GetDirectories(fx, "6.0.*").Length > 0;
        }

        private static void EnsureRuntime()
        {
            if (HasRuntime6())
            {
                Ok(".NET 6 runtime already installed");
                return;
            }
            Info("Installing the .NET 6 runtime (MelonLoader needs it)...");
            string exe = Download(RuntimeUrl, RuntimeSha256, "dotnet-runtime-6-win-x64.exe");
            var p = Process.Start(new ProcessStartInfo(exe, "/install /passive /norestart") { UseShellExecute = true });
            p.WaitForExit();
            if (p.ExitCode != 0 && p.ExitCode != 3010)
                throw new Exception(".NET 6 runtime installer exited with code " + p.ExitCode + ".");
            if (!HasRuntime6()) throw new Exception(".NET 6 runtime installer finished but the runtime is not there.");
            Ok(".NET 6 runtime installed");
        }

        // ---- MelonLoader -------------------------------------------------------------------------

        /// <summary>Returns true if MelonLoader was (re)installed now.</summary>
        private static bool EnsureMelonLoader(string game)
        {
            string dll = Path.Combine(game, "MelonLoader", "net6", "MelonLoader.dll");
            string proxy = Path.Combine(game, "version.dll");
            string have = File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).FileVersion : null;
            if (have != null && have.StartsWith(MlVersion) && File.Exists(proxy))
            {
                Ok("MelonLoader " + MlVersion + " already installed");
                return false;
            }
            if (have != null) Warn("Replacing MelonLoader " + have + " with " + MlVersion + ".");

            Info("Installing MelonLoader " + MlVersion + "...");
            string zip = Download(MlUrl, MlSha256, "MelonLoader.x64.zip");
            string root = Path.GetFullPath(game).TrimEnd('\\') + "\\";
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("Unsafe path in MelonLoader zip: " + entry.FullName);
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
            Ok("MelonLoader " + MlVersion + " installed");
            return true;
        }

        // ---- downloads ---------------------------------------------------------------------------

        private static string Download(string url, string sha256, string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "SpellBrigadeModsSetup");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);
            if (!(File.Exists(path) && Sha256(path) == sha256))
            {
                Info("  downloading " + url);
                using (var wc = new WebClient()) wc.DownloadFile(url, path);
            }
            string got = Sha256(path);
            if (got != sha256)
            {
                File.Delete(path);
                throw new Exception($"Download of {name} failed its checksum (got {got}).");
            }
            return path;
        }

        private static string Sha256(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // ---- embedded mods -----------------------------------------------------------------------

        private sealed class Mod
        {
            public string Name;
            public byte[] Bytes;
        }

        private static List<Mod> EmbeddedMods()
        {
            var asm = typeof(Program).Assembly;
            var mods = new List<Mod>();
            foreach (string res in asm.GetManifestResourceNames().Where(r => r.StartsWith("Mods/")).OrderBy(r => r))
            {
                using (var s = asm.GetManifestResourceStream(res))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    mods.Add(new Mod { Name = res.Substring("Mods/".Length), Bytes = ms.ToArray() });
                }
            }
            if (mods.Count == 0) throw new Exception("This setup has no mods inside it (packaging error).");
            return mods;
        }

        // ---- console -----------------------------------------------------------------------------

        private static bool AskYes(string q)
        {
            Console.Write(q + " [y/N] ");
            return (Console.ReadLine() ?? "").Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
        }

        private static void Ok(string s) => Say(ConsoleColor.Green, "[ok] " + s);
        private static void Info(string s) => Say(ConsoleColor.Gray, "     " + s);
        private static void Warn(string s) => Say(ConsoleColor.Yellow, "[!]  " + s);
        private static void Error(string s) => Say(ConsoleColor.Red, "[x]  " + s);

        private static void Say(ConsoleColor c, string s)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = c;
            Console.WriteLine(s);
            Console.ForegroundColor = old;
        }
    }
}
