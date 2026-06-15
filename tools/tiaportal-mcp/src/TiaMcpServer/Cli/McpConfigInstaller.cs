using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// One-click MCP registration: writes the `tia-portal` server entry into an AI host's
    /// config file (Claude Desktop / Cursor), pointing at THIS exe with the right TIA version —
    /// no REPLACE_ME, no manual JSON editing. Merges into existing config (keeps other servers),
    /// backs up the old file first. Shipped inside the engine so the bundle needs no extra tool.
    /// </summary>
    public static class McpConfigInstaller
    {
        public const string ServerKey = "tia-portal";

        public class Host
        {
            public string Name;
            public string ConfigPath;
            public Host(string name, string path) { Name = name; ConfigPath = path; }
        }

        public static List<Host> KnownHosts()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new List<Host>
            {
                new Host("Claude Desktop", Path.Combine(appData, "Claude", "claude_desktop_config.json")),
                new Host("Cursor",         Path.Combine(userProfile, ".cursor", "mcp.json")),
            };
        }

        /// <summary>Full path of the currently running engine exe.</summary>
        public static string OwnExePath()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName; }
            catch { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        public static JsonObject BuildServerEntry(string exePath, int tiaMajorVersion)
        {
            return new JsonObject
            {
                ["command"] = exePath,
                ["args"] = new JsonArray("--tia-major-version", tiaMajorVersion.ToString()),
            };
        }

        /// <summary>Pretty single-server snippet for hosts we don't write automatically.</summary>
        public static string Snippet(string exePath, int tiaMajorVersion)
        {
            var root = new JsonObject { ["mcpServers"] = new JsonObject { [ServerKey] = BuildServerEntry(exePath, tiaMajorVersion) } };
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Upserts the tia-portal server into one host config. Returns a human-readable status line.
        /// Throws on hard I/O / parse failure so the caller can report it.
        /// </summary>
        public static string Apply(string configPath, string exePath, int tiaMajorVersion)
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

            if (root["mcpServers"] is not JsonObject servers)
            {
                servers = new JsonObject();
                root["mcpServers"] = servers;
            }

            bool existed = servers.ContainsKey(ServerKey);
            servers[ServerKey] = BuildServerEntry(exePath, tiaMajorVersion);

            File.WriteAllText(configPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (existed ? "updated" : "wrote") + " " + ServerKey + " -> " + configPath;
        }
    }
}
