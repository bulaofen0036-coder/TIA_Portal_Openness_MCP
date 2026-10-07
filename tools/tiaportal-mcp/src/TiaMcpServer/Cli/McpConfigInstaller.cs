using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// One-click MCP registration: writes the `tia-portal` server entry into an AI host's
    /// config file (Claude Desktop / Claude Code / Cursor / VS Code), pointing at the exe
    /// that matches the machine's TIA version — no REPLACE_ME, no manual JSON editing.
    /// Merges into existing config (keeps other servers and unrelated keys), backs up the
    /// old file first. Shipped inside the engine so the bundle needs no extra tool.
    /// </summary>
    public static class McpConfigInstaller
    {
        public const string ServerKey = "tia-portal";

        // Keep Chinese path segments human-readable instead of \uXXXX escapes.
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Config file schema family.</summary>
        public enum HostStyle
        {
            /// <summary>Root key "mcpServers", entry {command,args} (Claude Desktop / Claude Code / Cursor).</summary>
            McpServers,
            /// <summary>Root key "servers", entry {type:"stdio",command,args} (VS Code mcp.json).</summary>
            VsCode,
            /// <summary>TOML [mcp_servers.*] sections (OpenAI Codex ~/.codex/config.toml).</summary>
            CodexToml,
        }

        public class Host
        {
            public string Name;
            public string ConfigPath;
            public HostStyle Style;
            public Host(string name, string path, HostStyle style)
            {
                Name = name; ConfigPath = path; Style = style;
            }
        }

        public static List<Host> KnownHosts()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new List<Host>
            {
                new Host("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json"), HostStyle.McpServers),
                new Host("Claude Code",    Path.Combine(userProfile, ".claude.json"),                     HostStyle.McpServers),
                new Host("Cursor",         Path.Combine(userProfile, ".cursor", "mcp.json"),              HostStyle.McpServers),
                new Host("VS Code",        Path.Combine(appData, "Code", "User", "mcp.json"),             HostStyle.VsCode),
                // The commercial GUI's config wizard has written these four for a while; the
                // engine's own `config` did not — so the zero-install path (just run 配置MCP.bat),
                // the one meant to have the LOWEST barrier, supported fewer clients than the GUI.
                new Host("Codex",          Path.Combine(userProfile, ".codex", "config.toml"),            HostStyle.CodexToml),
                new Host("Gemini CLI",     Path.Combine(userProfile, ".gemini", "settings.json"),         HostStyle.McpServers),
                new Host("Windsurf",       Path.Combine(userProfile, ".codeium", "windsurf", "mcp_config.json"), HostStyle.McpServers),
                new Host("Cline",          Path.Combine(appData, "Code", "User", "globalStorage",
                                                        "saoudrizwan.claude-dev", "settings",
                                                        "cline_mcp_settings.json"),                       HostStyle.McpServers),
            };
        }

        /// <summary>Full path of the currently running engine exe.</summary>
        public static string OwnExePath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
            catch { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        /// <summary>
        /// The exe the config should point at for <paramref name="tiaMajorVersion"/>: this exe
        /// when it matches, otherwise the sibling built for that version (falls back to this
        /// exe — it self-routes at startup anyway, this just avoids the extra hop).
        /// </summary>
        public static string ExeForVersion(int tiaMajorVersion)
        {
            if (tiaMajorVersion == Siemens.EngineRouter.CompiledTiaMajorVersion) return OwnExePath();
            return Siemens.EngineRouter.FindSiblingExe(tiaMajorVersion) ?? OwnExePath();
        }

        public static JsonObject BuildServerEntry(string exePath, int tiaMajorVersion, HostStyle style, bool full = false)
        {
            var entry = new JsonObject();
            if (style == HostStyle.VsCode) entry["type"] = "stdio";
            entry["command"] = exePath;
            entry["args"] = new JsonArray("--tia-major-version", tiaMajorVersion.ToString());
            // The engine defaults to the lite roster on its own, so the normal config
            // needs no env at all. Only the opt-out is worth writing — and it is an opt-out with
            // consequences: the full roster exceeds what VS Code/Copilot (128) and Windsurf (100) load.
            if (full) entry["env"] = new JsonObject { ["TIA_MCP_PROFILE"] = "full" };
            return entry;
        }

        /// <summary>Pretty single-server snippet for hosts we don't write automatically.</summary>
        public static string Snippet(string exePath, int tiaMajorVersion, HostStyle style = HostStyle.McpServers, bool full = false)
        {
            if (style == HostStyle.CodexToml) return CodexTomlSection(exePath, tiaMajorVersion, full);
            string rootKey = style == HostStyle.VsCode ? "servers" : "mcpServers";
            var root = new JsonObject
            {
                [rootKey] = new JsonObject { [ServerKey] = BuildServerEntry(exePath, tiaMajorVersion, style, full) }
            };
            return root.ToJsonString(JsonOpts);
        }

        /// <summary>
        /// Upserts the tia-portal server into one host config. Returns a human-readable status line.
        /// Throws on hard I/O / parse failure so the caller can report it.
        /// </summary>
        public static string Apply(string configPath, string exePath, int tiaMajorVersion, HostStyle style = HostStyle.McpServers, bool full = false)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            if (style == HostStyle.CodexToml) return ApplyCodexToml(configPath, exePath, tiaMajorVersion, full);

            JsonObject root;
            if (File.Exists(configPath))
            {
                var text = File.ReadAllText(configPath);
                root = string.IsNullOrWhiteSpace(text)
                    ? new JsonObject()
                    : JsonNode.Parse(text) as JsonObject ?? throw new InvalidDataException("existing config is not a JSON object");
                File.Copy(configPath, configPath + ".bak", overwrite: true);
            }
            else
            {
                root = new JsonObject();
            }

            string rootKey = style == HostStyle.VsCode ? "servers" : "mcpServers";
            if (root[rootKey] is not JsonObject servers)
            {
                servers = new JsonObject();
                root[rootKey] = servers;
            }

            bool existed = servers.ContainsKey(ServerKey);
            servers[ServerKey] = BuildServerEntry(exePath, tiaMajorVersion, style, full);

            AtomicWriteAllText(configPath, root.ToJsonString(JsonOpts));
            return (existed ? "updated" : "wrote") + " " + ServerKey + " -> " + configPath;
        }

        /// <summary>
        /// The engine path a host is currently configured to launch, or null when the host has no
        /// tia-portal entry. Used by `doctor`: "registered" is not the same as "working" — a config
        /// carried over from another machine (or from a moved bundle) still contains the entry while
        /// pointing at an exe that no longer exists, and the host then just fails to start it.
        /// </summary>
        public static string? RegisteredCommand(Host host)
        {
            try
            {
                if (!File.Exists(host.ConfigPath)) return null;
                string text = File.ReadAllText(host.ConfigPath);

                if (host.Style == HostStyle.CodexToml)
                {
                    bool inSection = false;
                    foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                    {
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("[", StringComparison.Ordinal))
                        {
                            inSection = trimmed.StartsWith("[mcp_servers." + ServerKey + "]", StringComparison.Ordinal);
                            continue;
                        }
                        if (!inSection) continue;
                        var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^command\s*=\s*(['""])(.*)\1\s*$");
                        if (m.Success) return m.Groups[2].Value;
                    }
                    return null;
                }

                if (JsonNode.Parse(text) is not JsonObject root) return null;
                string rootKey = host.Style == HostStyle.VsCode ? "servers" : "mcpServers";
                if (root[rootKey] is not JsonObject servers) return null;
                if (servers[ServerKey] is not JsonObject entry) return null;
                return entry["command"]?.GetValue<string>();
            }
            catch { return null; }
        }

        /// <summary>The TOML section Codex needs; standalone so `config --print` can show it too.</summary>
        private static string CodexTomlSection(string exePath, int tiaMajorVersion, bool full)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[mcp_servers." + ServerKey + "]");
            sb.AppendLine("command = " + TomlString(exePath));
            sb.AppendLine("args = [\"--tia-major-version\", \"" + tiaMajorVersion + "\"]");
            // TIA needs far longer to come up than Codex's 10s default; without this Codex kills
            // the server mid-startup and reports it as a crash.
            sb.AppendLine("startup_timeout_sec = 120");
            if (full)
            {
                sb.AppendLine();
                sb.AppendLine("[mcp_servers." + ServerKey + ".env]");
                sb.AppendLine("TIA_MCP_PROFILE = \"full\"");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Codex config is TOML. Rather than take on a TOML dependency, drop our own
        /// [mcp_servers.tia-portal] section (and its sub-sections) line by line and append a fresh
        /// one — a [a.b] section is legal anywhere in the file, so everything the user wrote for
        /// other servers survives untouched.
        /// </summary>
        private static string ApplyCodexToml(string configPath, string exePath, int tiaMajorVersion, bool full)
        {
            string text = "";
            bool existed = false;
            if (File.Exists(configPath))
            {
                File.Copy(configPath, configPath + ".bak", overwrite: true);
                var kept = new List<string>();
                bool inTia = false;
                foreach (var line in File.ReadAllText(configPath).Replace("\r\n", "\n").Split('\n'))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    {
                        inTia = trimmed.StartsWith("[mcp_servers." + ServerKey + "]", StringComparison.Ordinal)
                             || trimmed.StartsWith("[mcp_servers." + ServerKey + ".", StringComparison.Ordinal);
                        if (inTia) existed = true;
                    }
                    if (!inTia) kept.Add(line);
                }
                text = string.Join(Environment.NewLine, kept).TrimEnd();
            }

            var sb = new StringBuilder(text);
            if (sb.Length > 0) { sb.AppendLine(); sb.AppendLine(); }
            sb.Append(CodexTomlSection(exePath, tiaMajorVersion, full));
            AtomicWriteAllText(configPath, sb.ToString());
            return (existed ? "updated" : "wrote") + " " + ServerKey + " -> " + configPath;
        }

        /// <summary>Windows path as a TOML literal string (no backslash escaping inside '...').</summary>
        private static string TomlString(string s)
        {
            if (s.IndexOf('\'') < 0 && s.IndexOf('\n') < 0 && s.IndexOf('\r') < 0) return "'" + s + "'";
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";
        }

        /// <summary>Temp file + replace: a crash mid-write must not truncate the user's config.</summary>
        private static void AtomicWriteAllText(string path, string content)
        {
            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }
}
