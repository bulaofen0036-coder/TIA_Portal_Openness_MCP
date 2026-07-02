using System;
using System.Collections.Generic;
using System.IO;
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

        public static JsonObject BuildServerEntry(string exePath, int tiaMajorVersion, HostStyle style)
        {
            var entry = new JsonObject();
            if (style == HostStyle.VsCode) entry["type"] = "stdio";
            entry["command"] = exePath;
            entry["args"] = new JsonArray("--tia-major-version", tiaMajorVersion.ToString());
            return entry;
        }

        /// <summary>Pretty single-server snippet for hosts we don't write automatically.</summary>
        public static string Snippet(string exePath, int tiaMajorVersion, HostStyle style = HostStyle.McpServers)
        {
            string rootKey = style == HostStyle.VsCode ? "servers" : "mcpServers";
            var root = new JsonObject
            {
                [rootKey] = new JsonObject { [ServerKey] = BuildServerEntry(exePath, tiaMajorVersion, style) }
            };
            return root.ToJsonString(JsonOpts);
        }

        /// <summary>
        /// Upserts the tia-portal server into one host config. Returns a human-readable status line.
        /// Throws on hard I/O / parse failure so the caller can report it.
        /// </summary>
        public static string Apply(string configPath, string exePath, int tiaMajorVersion, HostStyle style = HostStyle.McpServers)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));

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
            servers[ServerKey] = BuildServerEntry(exePath, tiaMajorVersion, style);

            File.WriteAllText(configPath, root.ToJsonString(JsonOpts));
            return (existed ? "updated" : "wrote") + " " + ServerKey + " -> " + configPath;
        }
    }
}
