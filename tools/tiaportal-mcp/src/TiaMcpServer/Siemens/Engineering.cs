using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaMcpServer.Siemens
{
    // Manual Siemens.Engineering.dll resolve
    public class Engineering
    {
        public static int TiaMajorVersion { get; set; }

        // Optional explicit override (CLI --tia-portal-location), e.g. D:\app\TIA20\Portal V20.
        // Takes precedence over TiaPortalLocation env var and registry lookup.
        public static string? TiaPortalLocationOverride { get; set; }

        // When true, launch TIA Portal with its full GUI (slower cold start, allows visual inspection).
        // Default false = headless (WithoutUserInterface), which starts much faster. Set via --with-ui.
        // Lives here (not on Portal) because Program.Main must set it without forcing the CLR to load the
        // Portal type — Portal's Siemens.Engineering field types would be needed before Resolver is wired up.
        public static bool LaunchWithUserInterface { get; set; } = false;

        public static Assembly? Resolver(object sender, ResolveEventArgs args)
        {
            var assemblyName = new AssemblyName(args.Name);
            if (!assemblyName.Name.StartsWith("Siemens.Engineering"))
            {
                return null;
            }

            var tiaInstallPath = GetTiaPortalInstallPath();
            if (string.IsNullOrEmpty(tiaInstallPath))
            {
                throw new InvalidOperationException($"Could not find TIA Portal installation path for version {TiaMajorVersion} in the registry.");
            }

            var tiaMajorVersionString = TiaMajorVersion.ToString();
            var searchDirectories = new[]
            {
                Path.Combine(tiaInstallPath, "PublicAPI", $"V{tiaMajorVersionString}"),
                Path.Combine(tiaInstallPath, "Bin", "PublicAPI")
            };

            // IEnumerable without given majorVersionString
            var excludedTiaMajorVersions = new[] { "V13", "V14", "V15", "V16", "V17", "V18", "V19", "V20" }
                                    .Where(v => v != $"V{tiaMajorVersionString}");

            foreach (var dir in searchDirectories)
            {
                var assemblyPath = FindAssemblyRecursive(dir, assemblyName.Name + ".dll", excludedTiaMajorVersions);
                if (assemblyPath != null)
                {
                    return Assembly.LoadFrom(assemblyPath);
                }
            }

            throw new FileNotFoundException($"Could not find DLL '{assemblyName.Name}' for TIA Portal version {TiaMajorVersion} in the installation directories.");
        }

        /// <summary>
        /// Detects the highest installed TIA Portal major version without requiring a CLI flag.
        /// Detection order:
        ///   1. TiaPortalLocation env var — extract version from path (e.g. "Portal V21" → 21)
        ///   2. Registry: HKLM\SOFTWARE\Siemens\Automation\_InstalledSW\TIAP*\TIA_Opns
        ///   3. Filesystem: C:\Program Files\Siemens\Automation\Portal V*
        /// Returns the highest found version, or null if nothing detected.
        /// </summary>
        public static int? DetectTiaMajorVersion()
        {
            var candidates = new List<int>();

            // 0. Explicit override (CLI --tia-portal-location)
            if (!string.IsNullOrWhiteSpace(TiaPortalLocationOverride))
            {
                var m = System.Text.RegularExpressions.Regex.Match(TiaPortalLocationOverride, @"[Vv](\d{2})", System.Text.RegularExpressions.RegexOptions.RightToLeft);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int ovVer))
                    candidates.Add(ovVer);
            }

            // 1. TiaPortalLocation env var
            var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
            if (!string.IsNullOrWhiteSpace(env))
            {
                var match = System.Text.RegularExpressions.Regex.Match(env, @"[Vv](\d{2})", System.Text.RegularExpressions.RegexOptions.RightToLeft);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int envVer))
                    candidates.Add(envVer);
            }

            // 2. Registry scan
            try
            {
                using var regBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var installedSw = regBase.OpenSubKey(@"SOFTWARE\Siemens\Automation\_InstalledSW");
                if (installedSw != null)
                {
                    foreach (var subName in installedSw.GetSubKeyNames())
                    {
                        // Key names follow pattern TIAP21, TIAP20, etc.
                        var numMatch = System.Text.RegularExpressions.Regex.Match(subName, @"TIAP(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (!numMatch.Success || !int.TryParse(numMatch.Groups[1].Value, out int regVer)) continue;

                        using var opnsKey = installedSw.OpenSubKey(subName + @"\TIA_Opns");
                        if (opnsKey?.GetValue("Path") is string path && Directory.Exists(path))
                            candidates.Add(regVer);
                    }
                }
            }
            catch { }

            // 2b. Openness registrations: a V21 install may not write TIA_Opns at all
            //     (see GetTiaPortalInstallPath), but it always registers its PublicAPI assemblies.
            try
            {
                using var regBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var openness = regBase.OpenSubKey(@"SOFTWARE\Siemens\Automation\Openness");
                if (openness != null)
                {
                    foreach (var name in openness.GetSubKeyNames())
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(name, @"^(\d+)\.\d+$");
                        if (m.Success && int.TryParse(m.Groups[1].Value, out int ov)
                            && GetInstallPathFromOpennessRegistry(ov) != null)
                            candidates.Add(ov);
                    }
                }
            }
            catch { }

            // 3. Filesystem scan
            try
            {
                var siemensRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Siemens", "Automation");
                if (Directory.Exists(siemensRoot))
                {
                    foreach (var dir in Directory.GetDirectories(siemensRoot, "Portal V*"))
                    {
                        var dirMatch = System.Text.RegularExpressions.Regex.Match(dir, @"Portal V(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (dirMatch.Success && int.TryParse(dirMatch.Groups[1].Value, out int fsVer))
                            candidates.Add(fsVer);
                    }
                }
            }
            catch { }

            return candidates.Count > 0 ? candidates.Max() : (int?)null;
        }

        /// <summary>
        /// Answer "will Resolver() actually find Siemens.Engineering at load time?" by walking the
        /// exact same install path and search directories it does. A check that only asks the
        /// registry whether TIA is installed says OK on a machine where Openness was never
        /// installed — and the engine then dies with FileLoadException / "Could not find
        /// installation path" on the first real call. Reuses the resolver's own private helpers so
        /// the two cannot drift apart.
        /// </summary>
        public static (bool Ok, string? InstallPath, string? ResolvedDll, string? Problem) ProbeOpennessAssemblies()
        {
            string? installPath;
            try { installPath = GetTiaPortalInstallPath(); }
            catch (Exception ex) { return (false, null, null, "install path lookup threw: " + ex.Message); }

            if (string.IsNullOrEmpty(installPath))
            {
                return (false, null, null,
                    $"no TIA Portal V{TiaMajorVersion} install path (registry TIAP{TiaMajorVersion}\\TIA_Opns, " +
                    $"Openness\\{TiaMajorVersion}.0\\PublicAPI, the TiaPortalLocation env var and --tia-portal-location were all checked)");
            }

            var versionString = TiaMajorVersion.ToString();
            var searchDirectories = new[]
            {
                Path.Combine(installPath, "PublicAPI", $"V{versionString}"),
                Path.Combine(installPath, "Bin", "PublicAPI")
            };
            var excluded = new[] { "V13", "V14", "V15", "V16", "V17", "V18", "V19", "V20" }
                .Where(v => v != $"V{versionString}");

            // V20 ships the monolithic Siemens.Engineering.dll; V21 splits it into
            // Siemens.Engineering.Base/Step7/... — either one proves Openness is present.
            foreach (var dll in new[] { "Siemens.Engineering.dll", "Siemens.Engineering.Base.dll" })
            {
                foreach (var dir in searchDirectories)
                {
                    string? found;
                    try { found = FindAssemblyRecursive(dir, dll, excluded); }
                    catch { continue; }
                    if (found != null) return (true, installPath, found, null);
                }
            }

            return (false, installPath, null,
                $"found the TIA folder but no Siemens.Engineering(.Base).dll under {string.Join(" or ", searchDirectories)}");
        }

        private static string? GetTiaPortalInstallPath()
        {
            // 1. Explicit CLI override (--tia-portal-location). Highest priority — needed when TIA
            //    is installed at a non-default location (e.g. D:\app\TIA20\Portal V20) and the
            //    registry/env var path is wrong or absent.
            if (!string.IsNullOrWhiteSpace(TiaPortalLocationOverride) && Directory.Exists(TiaPortalLocationOverride))
            {
                return TiaPortalLocationOverride;
            }

            // 2. env var (Cursor MCP env or user env) — but it is version-agnostic and on
            //    multi-version machines it typically points at ONE install (e.g. V21), which
            //    used to hijack V20 assembly resolution ("Could not find DLL ... for version 20").
            //    Only trust it when its path names the version we need (or names no version).
            var env = Environment.GetEnvironmentVariable("TiaPortalLocation");
            bool envUsable = !string.IsNullOrWhiteSpace(env) && Directory.Exists(env);
            if (envUsable && PathMatchesVersion(env!, TiaMajorVersion))
            {
                return env;
            }

            // 3. Version-specific registry entry — authoritative on multi-version machines.
            var subKeyName = $@"SOFTWARE\Siemens\Automation\_InstalledSW\TIAP{TiaMajorVersion}\TIA_Opns";

            using (var regBaseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var tiaOpnsKey = regBaseKey.OpenSubKey(subKeyName))
            {
                var regPath = tiaOpnsKey?.GetValue("Path")?.ToString();
                if (!string.IsNullOrWhiteSpace(regPath) && Directory.Exists(regPath))
                {
                    return regPath;
                }
            }

            // 3b. The Openness registration itself. V21 installs do not always write
            //     _InstalledSW\TIAP21\TIA_Opns\Path (seen on V21 Upd2 installed on a non-C: drive),
            //     and without another source the engine died at startup with
            //     "Could not find DLL 'Siemens.Engineering.Base'" - an MCP host only shows
            //     "Connection closed". This is the key the Openness manual documents for loading
            //     the assembly, and the one Siemens' own Openness NuGet targets read.
            var fromOpenness = GetInstallPathFromOpennessRegistry(TiaMajorVersion);
            if (fromOpenness != null)
            {
                return fromOpenness;
            }

            // 4. Last resort: the env var even when its version looks different — better than nothing.
            return envUsable ? env : null;
        }

        /// <summary>
        /// HKLM\SOFTWARE\Siemens\Automation\Openness\{major}.0\PublicAPI\{assemblyVersion}[\net48]
        /// holds "Siemens.Engineering.Base" (V21 split assemblies) or "Siemens.Engineering" (V20 and
        /// older) = the full DLL path. A V20 install registers every compatible assembly version
        /// (17.0.0.0 ... 20.0.0.0), so the portal's own version is preferred.
        /// </summary>
        private static string? GetInstallPathFromOpennessRegistry(int major)
        {
            if (major <= 0) return null;
            try
            {
                using var regBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var publicApi = regBase.OpenSubKey($@"SOFTWARE\Siemens\Automation\Openness\{major}.0\PublicAPI");
                if (publicApi == null) return null;

                var versions = publicApi.GetSubKeyNames()
                    .OrderByDescending(n => n.StartsWith(major + ".", StringComparison.Ordinal))
                    .ThenByDescending(n => n, StringComparer.Ordinal);
                foreach (var ver in versions)
                {
                    using var verKey = publicApi.OpenSubKey(ver);
                    if (verKey == null) continue;

                    var found = DllFromOpennessKey(verKey);
                    if (found == null)
                    {
                        foreach (var framework in verKey.GetSubKeyNames())
                        {
                            using var fwKey = verKey.OpenSubKey(framework);
                            if (fwKey == null) continue;
                            found = DllFromOpennessKey(fwKey);
                            if (found != null) break;
                        }
                    }

                    var root = found == null ? null : InstallRootFromPublicApiDll(found);
                    if (root != null && Directory.Exists(root)) return root;
                }
            }
            catch
            {
                // Registry unreadable: fall through to the remaining sources.
            }
            return null;
        }

        private static string? DllFromOpennessKey(RegistryKey key)
        {
            foreach (var valueName in new[] { "Siemens.Engineering.Base", "Siemens.Engineering" })
            {
                if (key.GetValue(valueName) is string dll && File.Exists(dll)) return dll;
            }
            return null;
        }

        /// <summary>...\Portal V21\PublicAPI\V21\net48\X.dll or ...\Portal V20\PublicAPI\V20\X.dll
        /// → ...\Portal V2x (the directory above PublicAPI).</summary>
        private static string? InstallRootFromPublicApiDll(string dllPath)
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(dllPath) ?? "");
            while (dir != null && !string.Equals(dir.Name, "PublicAPI", StringComparison.OrdinalIgnoreCase))
            {
                dir = dir.Parent;
            }
            return dir?.Parent?.FullName;
        }

        /// <summary>True when the path names no version at all, or names exactly V{version}.</summary>
        private static bool PathMatchesVersion(string path, int version)
        {
            var m = System.Text.RegularExpressions.Regex.Match(path, @"[Vv](\d{2})",
                System.Text.RegularExpressions.RegexOptions.RightToLeft);
            if (!m.Success) return true;
            return int.TryParse(m.Groups[1].Value, out int pv) && pv == version;
        }

        private static string? FindAssemblyRecursive(string directory, string fileName, IEnumerable<string> excludedTiaMajorVersions)
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var filePath = Path.Combine(directory, fileName);
            if (File.Exists(filePath))
            {
                return filePath;
            }

            foreach (var subDir in Directory.GetDirectories(directory))
            {
                var subDirName = new DirectoryInfo(subDir).Name;
                if (excludedTiaMajorVersions.Contains(subDirName))
                {
                    continue;
                }

                var result = FindAssemblyRecursive(subDir, fileName, excludedTiaMajorVersions);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
    }
}
