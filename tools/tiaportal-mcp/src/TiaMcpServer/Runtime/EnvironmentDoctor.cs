using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaMcpServer.Runtime
{
    /// <summary>
    /// The environment checks behind both `tia doctor` (CLI) and the Doctor MCP tool.
    ///
    /// It lives in one place because the two used to be written separately and had drifted: each
    /// only asked the registry whether TIA existed, checked the Openness group, and stopped. The
    /// three failures that actually strand a first-time user on a fresh machine were checked by
    /// neither:
    ///   * Openness was never installed, so Siemens.Engineering cannot be resolved (the registry
    ///     still says TIA is there, so both doctors reported OK and the engine died on first call);
    ///   * the delivery was unzipped straight from a download, so Windows marked every DLL with a
    ///     zone identifier and .NET refuses to load them;
    ///   * .NET Framework 4.8 is missing, which is prerequisite #1 in the README.
    ///
    /// Each check carries both languages: the CLI is invoked from Chinese .bat files by Chinese
    /// engineers, while the MCP tool's output is consumed by a model alongside English tool text.
    /// </summary>
    public static class EnvironmentDoctor
    {
        public sealed class Check
        {
            public string Id = "";
            public bool Ok;
            /// <summary>Informational checks never gate readiness.</summary>
            public bool Gating = true;
            public string NameEn = "", NameZh = "";
            public string DetailEn = "", DetailZh = "";
            public string? FixEn, FixZh;

            public string Name(bool zh) => zh ? NameZh : NameEn;
            public string Detail(bool zh) => zh ? DetailZh : DetailEn;
            public string? Fix(bool zh) => zh ? FixZh : FixEn;
        }

        public static bool PreferChinese =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

        /// <summary>The supported TIA majors. Messages must not promise more than the product delivers.</summary>
        private const string SupportedVersions = "V20 / V21";

        public static List<Check> Run(int compiledTiaMajorVersion, int? detectedTiaMajorVersion)
        {
            var checks = new List<Check>
            {
                TiaInstall(detectedTiaMajorVersion),
                OpennessAssemblies(),
                EngineVersionMatch(compiledTiaMajorVersion, detectedTiaMajorVersion),
                DotNetFramework48(),
                FilesNotBlocked(),
            };
            return checks;
        }

        private static Check TiaInstall(int? detected)
        {
            bool ok = detected != null;
            return new Check
            {
                Id = "tia-install",
                Ok = ok,
                NameEn = "TIA Portal installation",
                NameZh = "TIA Portal 安装",
                DetailEn = ok ? $"detected V{detected}" : "no TIA Portal detected (registry / TiaPortalLocation / default folder)",
                DetailZh = ok ? $"检测到 V{detected}" : "未检测到 TIA Portal（注册表 / TiaPortalLocation 环境变量 / 默认安装目录都查过了）",
                FixEn = ok ? null : $"Install TIA Portal {SupportedVersions} including the Openness option, or set the TiaPortalLocation environment variable to the install folder (e.g. D:\\TIA21\\Portal V21).",
                FixZh = ok ? null : $"安装 TIA Portal {SupportedVersions}（安装时要勾选 Openness 组件），或把用户环境变量 TiaPortalLocation 指向安装根目录（例如 D:\\TIA21\\Portal V21）。",
            };
        }

        private static Check OpennessAssemblies()
        {
            var probe = Siemens.Engineering.ProbeOpennessAssemblies();
            return new Check
            {
                Id = "openness-dll",
                Ok = probe.Ok,
                NameEn = "Openness API assemblies",
                NameZh = "Openness 编程接口 DLL",
                DetailEn = probe.Ok ? "resolvable: " + probe.ResolvedDll : (probe.Problem ?? "not resolvable"),
                DetailZh = probe.Ok ? "可解析：" + probe.ResolvedDll : ("无法解析——" + (probe.Problem ?? "原因未知")),
                FixEn = probe.Ok ? null : "TIA can be installed without Openness. Re-run the TIA Portal setup and add the 'Openness' component, then confirm Siemens.Engineering.dll (V20) or Siemens.Engineering.Base.dll (V21) exists under <install>\\PublicAPI\\V<version>\\.",
                FixZh = probe.Ok ? null : "装了 TIA 不等于装了 Openness。重新运行 TIA Portal 安装程序补装『Openness』组件，然后确认 <安装目录>\\PublicAPI\\V<版本>\\ 下存在 Siemens.Engineering.dll（V20）或 Siemens.Engineering.Base.dll（V21）。",
            };
        }

        private static Check EngineVersionMatch(int compiled, int? detected)
        {
            bool ok = detected == null || detected.Value == compiled
                      || Siemens.EngineRouter.FindSiblingExe(detected.Value) != null;
            return new Check
            {
                Id = "engine-version",
                Ok = ok,
                NameEn = "Engine exe / TIA version",
                NameZh = "引擎 exe 与 TIA 版本匹配",
                DetailEn = $"exe built for V{compiled}" + (detected != null ? $", machine has V{detected}" : ", machine version unknown"),
                DetailZh = $"该 exe 为 V{compiled} 构建" + (detected != null ? $"，本机装的是 V{detected}" : "，本机版本未知"),
                FixEn = ok || detected == null ? null : $"Use runtime\\v{detected}\\TiaMcpServer.exe from the delivery (both versions ship), or keep this one and pass --tia-major-version {compiled}.",
                FixZh = ok || detected == null ? null : $"改用交付包里的 runtime\\v{detected}\\TiaMcpServer.exe（两个版本都随包提供），或继续用当前这个并加参数 --tia-major-version {compiled}。",
            };
        }

        private static Check DotNetFramework48()
        {
            int release = 0;
            try
            {
                using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
                release = (int)(key?.GetValue("Release") ?? 0);
            }
            catch { }

            // 528040 = .NET Framework 4.8 RTM; anything at or above it satisfies net48.
            bool ok = release >= 528040;
            return new Check
            {
                Id = "dotnet48",
                Ok = ok,
                NameEn = ".NET Framework 4.8",
                NameZh = ".NET Framework 4.8",
                DetailEn = ok ? $"present (release {release})" : (release > 0 ? $"too old (release {release}, need >= 528040)" : "not detected"),
                DetailZh = ok ? $"已安装（release {release}）" : (release > 0 ? $"版本过低（release {release}，需要 >= 528040）" : "未检测到"),
                FixEn = ok ? null : "Install the .NET Framework 4.8 runtime (Windows 10 1903+ and Windows 11 ship it built in).",
                FixZh = ok ? null : "安装 .NET Framework 4.8 运行时（Windows 10 1903 及以上、Windows 11 自带）。",
            };
        }

        /// <summary>
        /// Windows tags every file extracted from a downloaded .zip with a Zone.Identifier stream;
        /// .NET then refuses to load the assemblies and the engine fails in ways that look nothing
        /// like "your download is blocked".
        /// </summary>
        private static Check FilesNotBlocked()
        {
            var blocked = new List<string>();
            string dir = "";
            try
            {
                dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.dll").Concat(Directory.GetFiles(dir, "*.exe")))
                    {
                        if (HasZoneIdentifier(f)) blocked.Add(Path.GetFileName(f));
                        if (blocked.Count >= 5) break;
                    }
                }
            }
            catch { }

            bool ok = blocked.Count == 0;
            string list = string.Join(", ", blocked);
            return new Check
            {
                Id = "motw",
                Ok = ok,
                NameEn = "Files not blocked by Windows (MOTW)",
                NameZh = "文件未被 Windows 标记为网络来源 (MOTW)",
                DetailEn = ok ? "no zone identifier on the engine files" : $"blocked files present: {list}{(blocked.Count >= 5 ? ", ..." : "")}",
                DetailZh = ok ? "引擎目录下的文件没有网络来源标记" : $"存在被阻止的文件：{list}{(blocked.Count >= 5 ? " …" : "")}",
                FixEn = ok ? null : $"Unblock the delivery folder in PowerShell:  Get-ChildItem -Recurse '{dir}' | Unblock-File",
                FixZh = ok ? null : $"用 PowerShell 解除阻止：  Get-ChildItem -Recurse '{dir}' | Unblock-File",
            };
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// Alternate data streams have to be opened through the Win32 API: File.Exists() on an
        /// "file.dll:Zone.Identifier" path returns false even when the stream is right there, so a
        /// check written with it silently never fires.
        /// </summary>
        private static bool HasZoneIdentifier(string path)
        {
            const uint GENERIC_READ = 0x80000000;
            const uint FILE_SHARE_READWRITE = 0x00000003;
            const uint OPEN_EXISTING = 3;
            var invalid = new IntPtr(-1);
            IntPtr h = invalid;
            try
            {
                h = CreateFileW(path + ":Zone.Identifier", GENERIC_READ, FILE_SHARE_READWRITE,
                                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                return h != invalid;
            }
            catch { return false; }
            finally { if (h != invalid && h != IntPtr.Zero) CloseHandle(h); }
        }
    }
}
