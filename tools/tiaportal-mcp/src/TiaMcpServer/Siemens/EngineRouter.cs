using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Version-aware self-routing (Siemens-free — safe to JIT before any Openness assembly
    /// resolves). The bundle ships one exe per TIA major version because the IL is hard-bound
    /// to that version's assembly identities, so running the V21 exe on a V20 machine crashes
    /// at the first Siemens type load. Instead of crashing, Main asks this class to re-exec
    /// the sibling exe that matches the effective TIA version, inheriting stdio so MCP/CLI
    /// callers never notice (GitHub issue #8).
    /// </summary>
    public static class EngineRouter
    {
        /// <summary>TIA major version this binary's IL is bound to.</summary>
        public const int CompiledTiaMajorVersion =
#if TIA_V20
            20;
#else
            21;
#endif

        private const string RedirectGuardVar = "TIAMCP_NO_REDIRECT";

        /// <summary>
        /// Locate the sibling exe built for <paramref name="version"/> next to this one.
        /// Understands both bundle layouts: source-tree (bin\ / bin-v20\ Release\net48) and
        /// plugin runtime (runtime\v20\ / runtime\v21\). Returns null when not found.
        /// </summary>
        public static string? FindSiblingExe(int version)
        {
            try
            {
                string own = Process.GetCurrentProcess().MainModule.FileName;
                string exeName = Path.GetFileName(own);
                string dir = Path.GetDirectoryName(own) ?? "";
                var candidates = new List<string>();

                var m = Regex.Match(dir, @"^(.*)[\\/]bin(-v20)?[\\/]Release[\\/]net48$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string binDir = version == 20 ? "bin-v20" : "bin";
                    candidates.Add(Path.Combine(m.Groups[1].Value, binDir, "Release", "net48", exeName));
                }

                var parent = new DirectoryInfo(dir);
                if (parent.Parent != null && Regex.IsMatch(parent.Name, @"^v\d+$", RegexOptions.IgnoreCase))
                {
                    candidates.Add(Path.Combine(parent.Parent.FullName, "v" + version, exeName));
                }

                foreach (var c in candidates)
                {
                    if (File.Exists(c) &&
                        !string.Equals(Path.GetFullPath(c), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
                    {
                        return c;
                    }
                }
            }
            catch
            {
                // best-effort only; caller falls back to a warning
            }
            return null;
        }

        /// <summary>
        /// Re-exec the sibling exe for <paramref name="version"/> with identical args.
        /// stdio handles are inherited, so MCP stdio and CLI output pass through unchanged.
        /// Returns false when no sibling exists or the redirect guard is set (loop protection).
        /// </summary>
        public static bool TryRedirect(int version, string[] args, Action<string> log, out int exitCode)
        {
            exitCode = 0;
            if (Environment.GetEnvironmentVariable(RedirectGuardVar) == "1")
            {
                log("EngineRouter: redirect guard set; staying in this exe.");
                return false;
            }

            string? sibling = FindSiblingExe(version);
            if (sibling == null) return false;

            log($"EngineRouter: TIA V{version} requested but this exe is built for V{CompiledTiaMajorVersion}; rerouting to {sibling}");
            var psi = new ProcessStartInfo
            {
                FileName = sibling,
                Arguments = QuoteArgs(args),
                UseShellExecute = false,
            };
            psi.EnvironmentVariables[RedirectGuardVar] = "1";
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                exitCode = p.ExitCode;
            }
            return true;
        }

        /// <summary>Windows-correct argument re-quoting (spaces, quotes, trailing backslashes).</summary>
        public static string QuoteArgs(string[] args)
        {
            var sb = new StringBuilder();
            foreach (var a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
                {
                    sb.Append(a);
                    continue;
                }
                sb.Append('"');
                int backslashes = 0;
                foreach (char c in a)
                {
                    if (c == '\\') { backslashes++; continue; }
                    if (c == '"')
                    {
                        sb.Append('\\', backslashes * 2 + 1).Append('"');
                        backslashes = 0;
                        continue;
                    }
                    sb.Append('\\', backslashes).Append(c);
                    backslashes = 0;
                }
                sb.Append('\\', backslashes * 2).Append('"');
            }
            return sb.ToString();
        }
    }
}
