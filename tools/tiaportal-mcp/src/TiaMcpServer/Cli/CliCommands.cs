using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Cli
{
    /// <summary>
    /// Thin CLI front-end: maps verbs (gen/patch/compile/export/import/describe/prewarm/schema/
    /// version/help) onto the existing McpServer engine statics. No new Openness logic lives here —
    /// it only loads input, calls the engine, formats output, and returns an exit code.
    /// </summary>
    public static class CliCommands
    {
        private static readonly string[] Verbs =
            { "gen", "patch", "compile", "export", "import", "describe", "prewarm", "config", "schema", "version", "help", "--help", "-h" };

        public static bool IsVerb(string s) => Array.IndexOf(Verbs, s.ToLowerInvariant()) >= 0;

        public static int Run(string[] args)
        {
            var verb = args[0].ToLowerInvariant();
            try
            {
                switch (verb)
                {
                    case "gen": return Gen(args);
                    case "patch": return Patch(args);
                    case "compile": return Compile(args);
                    case "export": return Export(args);
                    case "import": return Import(args);
                    case "describe": return Describe(args);
                    case "prewarm": return Prewarm(args);
                    case "config": return Config(args);
                    case "schema": Console.WriteLine(SchemaText); return 0;
                    case "version": Console.WriteLine("tia " + AssemblyVersion()); return 0;
                    default: PrintUsage(); return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 2;
            }
        }

        // ---- verbs ----

        private static int Gen(string[] args)
        {
            var json = SpecLoader.LoadAsJson(Positional(args));
            var resp = McpServer.ScaffoldProject(json, Flag(args, "--dry-run"));
            return Report(resp, Flag(args, "--json"));
        }

        private static int Patch(string[] args)
        {
            var json = SpecLoader.LoadAsJson(Positional(args));
            var resp = McpServer.PatchProject(json, Flag(args, "--dry-run"), Flag(args, "--no-overwrite"));
            return Report(resp, Flag(args, "--json"));
        }

        private static int Compile(string[] args)
        {
            EnsureConnectedOpen(Positional(args));
            var plc = Opt(args, "--plc") ?? "PLC_1";
            var c = McpServer.CompileAndDiagnosePlc(plc);
            bool clean = (c.ErrorCount ?? 0) == 0;
            if (Flag(args, "--json")) Console.WriteLine(Json(c));
            else Console.WriteLine($"compile {plc}: state={c.State} errors={c.ErrorCount} warnings={c.WarningCount}");
            return clean ? 0 : 1;
        }

        private static int Describe(string[] args)
        {
            EnsureConnectedOpen(Positional(args));
            var tree = McpServer.GetProjectTree();
            if (Flag(args, "--json")) { Console.WriteLine(Json(tree)); }
            else
            {
                Console.WriteLine(tree.Message ?? "(project tree)");
                var plc = Opt(args, "--plc");
                if (!string.IsNullOrWhiteSpace(plc))
                    Console.WriteLine(McpServer.GetBlocks(plc!, "").Message);
            }
            return 0;
        }

        private static int Export(string[] args)
        {
            EnsureConnectedOpen(Positional(args));
            var plc = Opt(args, "--plc") ?? "PLC_1";
            var outDir = Opt(args, "--out") ?? throw new ArgumentException("export requires --out <dir>");
            var block = Opt(args, "--block") ?? throw new ArgumentException("export requires --block <path> (single block; bulk export not yet wired)");
            Directory.CreateDirectory(outDir);
            bool scl = Flag(args, "--scl");
            if (scl) McpServer.ExportAsDocuments(plc, block, outDir);
            else McpServer.ExportBlock(plc, block, outDir);
            Console.WriteLine($"exported {block} ({(scl ? "SCL/documents" : "XML")}) -> {outDir}");
            return 0;
        }

        private static int Import(string[] args)
        {
            EnsureConnectedOpen(Positional(args));
            var plc = Opt(args, "--plc") ?? "PLC_1";
            var dir = Opt(args, "--from") ?? throw new ArgumentException("import requires --from <dir>");
            bool overwrite = !Flag(args, "--no-overwrite");
            int n = 0;

            var xml = Directory.GetFiles(dir, "*.xml");
            if (xml.Length > 0)
            {
                var r = McpServer.ImportBlocksFromDirectory(plc, "", dir, "", overwrite);
                Console.WriteLine(r.Message);
                n += xml.Length;
            }
            var docs = Directory.GetFiles(dir, "*.s7dcl");
            foreach (var f in docs)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                try { McpServer.ImportFromDocuments(plc, "", dir, name, overwrite ? "Override" : "None"); Console.WriteLine($"  imported {name}"); n++; }
                catch (Exception ex) { Console.Error.WriteLine($"  skip {name}: {ex.Message}"); }
            }
            if (n == 0) Console.Error.WriteLine($"no .xml or .s7dcl files found under {dir}");
            return n > 0 ? 0 : 1;
        }

        private static int Prewarm(string[] args)
        {
            if (Flag(args, "--stop"))
            {
                if (!McpServer.Portal.IsConnected()) McpServer.Connect(); // attach to the running headless instance
                McpServer.Disconnect();                          // Dispose it
                Console.WriteLine("prewarm: stopped (headless instance disposed).");
                return 0;
            }

            Console.WriteLine("prewarm: cold-starting headless TIA and holding it open. Press Ctrl+C to stop.");
            McpServer.Connect();
            Console.WriteLine($"prewarm: ready ({McpServer.GetState().Message}). Subsequent `tia` commands will attach in ~1s.");

            var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            while (!stop.IsSet)
            {
                stop.Wait(60000);
                if (!stop.IsSet) { try { _ = McpServer.GetState(); } catch { } } // heartbeat
            }
            try { McpServer.Disconnect(); } catch { }
            Console.WriteLine("prewarm: stopped.");
            return 0;
        }

        // One-click MCP registration into AI hosts (Claude Desktop / Cursor), no manual JSON editing.
        private static int Config(string[] args)
        {
            string exe = McpConfigInstaller.OwnExePath();
            int ver = int.TryParse(Opt(args, "--tia-major-version"), out var v) && v > 0
                ? v
                : (TiaMcpServer.Siemens.Engineering.DetectTiaMajorVersion() ?? 21);

            if (Flag(args, "--print"))
            {
                Console.WriteLine("Paste this into your MCP host config (mcpServers):");
                Console.WriteLine(McpConfigInstaller.Snippet(exe, ver));
                return 0;
            }

            string only = Opt(args, "--host"); // claude | cursor | (default: all)
            int done = 0, failed = 0;
            foreach (var h in McpConfigInstaller.KnownHosts())
            {
                if (!string.IsNullOrEmpty(only) &&
                    h.Name.IndexOf(only, StringComparison.OrdinalIgnoreCase) < 0) continue;
                try { Console.WriteLine("  [ok]     " + McpConfigInstaller.Apply(h.ConfigPath, exe, ver)); done++; }
                catch (Exception ex) { Console.Error.WriteLine("  [failed] " + h.Name + ": " + ex.Message); failed++; }
            }

            Console.WriteLine(done > 0
                ? $"Configured {done} host(s) for TIA V{ver}. Restart the AI client to load it. (original config backed up as *.bak)"
                : "No host config written. Targeted host not found, or use `config --print` to copy the snippet manually.");
            Console.WriteLine("For other hosts (e.g. Claude Code), run `config --print` and paste the snippet.");
            return failed > 0 && done == 0 ? 1 : 0;
        }

        // ---- helpers ----

        private static void EnsureConnectedOpen(string projectPath)
        {
            // Openness resolves a relative project path against the exe directory, not the shell's
            // working dir — confusing failures. Resolve against CWD so `tia describe foo.ap21` works.
            if (!McpServer.Portal.IsConnected()) McpServer.Connect();
            McpServer.OpenProject(Path.GetFullPath(projectPath));
        }

        private static int Report(ResponseScaffold resp, bool asJson)
        {
            if (asJson) { Console.WriteLine(Json(resp)); return resp.Ok ? 0 : 1; }
            foreach (var s in resp.Steps)
                Console.WriteLine($"  [{s.Status,-7}] {s.Step}{(string.IsNullOrEmpty(s.Detail) ? "" : " — " + s.Detail)}");
            Console.WriteLine(resp.Message);
            return resp.Ok ? 0 : 1;
        }

        private static string Json(object o) =>
            JsonSerializer.Serialize(o, new JsonSerializerOptions { WriteIndented = true });

        // First non-flag argument after the verb (the project/spec path).
        private static string Positional(string[] args)
        {
            for (int i = 1; i < args.Length; i++)
                if (!args[i].StartsWith("-")) return args[i];
            throw new ArgumentException($"`tia {args[0]}` requires a path argument. Run `tia help` for usage.");
        }

        private static string? Opt(string[] args, string name)
        {
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }

        private static bool Flag(string[] args, string name) =>
            args.Skip(1).Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static string AssemblyVersion() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";

        private static void PrintUsage() => Console.WriteLine(UsageText);

        private const string UsageText =
@"tia — drive TIA Portal from a single spec. (Same engine as the MCP server.)

USAGE
  tia gen      <spec.yaml|json> [--dry-run] [--json]      Build a project from a spec
  tia patch    <spec.yaml|json> [--dry-run] [--json] [--no-overwrite]
                                                          Upsert spec into an EXISTING project (spec.projectPath)
  tia compile  <project.apXX> [--plc NAME] [--json]       Compile + diagnose a PLC
  tia describe <project.apXX> [--plc NAME] [--json]       Print project tree (and PLC blocks)
  tia export   <project.apXX> --plc NAME --out DIR --block PATH [--scl]
  tia import   <project.apXX> --plc NAME --from DIR [--no-overwrite]
  tia prewarm  [--stop]                                   Hold a headless instance open (~1s attach after)
  tia config   [--host claude|cursor] [--print]           One-click: register this MCP into Claude Desktop / Cursor
  tia schema                                              Print the spec field reference
  tia version

GLOBAL FLAGS (also accepted): --with-ui, --tia-portal-location PATH, --tia-major-version N
Exit code: 0 = success, 1 = completed with failed steps, 2 = error.";

        private const string SchemaText =
@"PROJECT SPEC (YAML or JSON). JSON is canonical; YAML is for humans.
Used by `tia gen` (build from zero) and `tia patch` (upsert into existing).

  projectName     string  gen: required. Project name.
  projectPath     string  patch: required. Path to the .apXX to open.
  directoryPath   string  gen: output folder (default %TEMP%).
  plcName         string  default PLC_1.
  plcFamily       string  default S7-1500.
  plcMlfb         string  exact order number (optional).
  hmiName         string  omit to skip all HMI.
  hmiFamily       string  default WinCCUnifiedPC.
  hmiSoftwarePath string  blank = auto-probe.
  connectionName  string  default HMI_Connection_1.
  udt[]           objects same shape as BuildPlcUdt / PlcBuildAndImport.
  globalDb[]      objects same shape as BuildPlcGlobalDb.
  tagTable[]      objects same shape as BuildPlcTagTable.
  sclSourceFiles[] strings .scl external-source file paths.
  ladDocs[]       {importPath, name}  S7DCL document import.
  hmiScreens[]    {screenName, width, height, designJson(object)}.
  hmiTags[]       {tagTableName?, tagName, hmiDataType?, plcTag?, address?}.
  compile         bool   default true.
  save            bool   default true.

NOTES
  * Set width/height to the panel's native resolution or the screen is clipped.
  * Use absolute addresses (%M..) for hmiTags to pass read-back verification.
  * patch --no-overwrite protects hand-edited LAD code blocks (imported as None);
    UDT/DB/tag tables always re-sync to the spec.";
    }
}
