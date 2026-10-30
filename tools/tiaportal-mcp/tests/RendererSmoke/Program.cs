using System;
using System.IO;
using TiaMcpServer.ModelContextProtocol;
using TiaMcpServer.Siemens;

// Offline regression for the SimaticML -> readable logic renderer used by DescribeBlockLogic.
// Needs no TIA Portal and no Openness DLLs. Run from the repository root:
//   dotnet run --project tools/tiaportal-mcp/tests/RendererSmoke/RendererSmoke.csproj
internal static class Program
{
    private static int Main()
    {
        var root = Directory.GetCurrentDirectory();
        var cases = new[]
        {
            new Case(
                Path.Combine(root, "templates", "mcp-full-e2e-verify", "plc", "blocks", "MCPVerify_FC_LAD_v2.xml"),
                "LAD",
                new[] { "程序段", "#O_EQ", "#O_NEG" }),
            new Case(
                Path.Combine(root, "templates", "mcp-full-e2e-verify", "plc", "blocks", "MCPVerify_FC_SCL_E2E.xml"),
                "SCL",
                new[] { "#OutBool :=", "IF NOT #Enable", "END_REPEAT" })
        };

        var failed = 0;
        // Compile-time guard: V19 must never advertise itself as V21 (EngineRouter would reroute).
        if (EngineRouter.CompiledTiaMajorVersion != 19)
        {
            Console.Error.WriteLine($"FAIL: EngineRouter.CompiledTiaMajorVersion={EngineRouter.CompiledTiaMajorVersion}, expected 19");
            failed++;
        }
        foreach (var test in cases)
        {
            if (!File.Exists(test.Path))
            {
                Console.Error.WriteLine($"FAIL: fixture not found: {test.Path}");
                failed++;
                continue;
            }

            var text = LadTextRenderer.Render(File.ReadAllText(test.Path));
            Console.WriteLine($"===== {test.Language}: {Path.GetFileName(test.Path)} | {text.Length} chars =====");
            Console.WriteLine(text);

            if (text.StartsWith("Could not parse", StringComparison.Ordinal) || text.Length < 80)
            {
                Console.Error.WriteLine("FAIL: renderer produced no usable output");
                failed++;
                continue;
            }

            foreach (var expected in test.MustContain)
            {
                if (!text.Contains(expected, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine($"FAIL: missing expected text '{expected}'");
                    failed++;
                }
            }
        }

        Console.WriteLine(failed == 0 ? "ALL RENDER CHECKS PASSED" : $"{failed} CHECK(S) FAILED");
        return failed == 0 ? 0 : 1;
    }

    private sealed record Case(string Path, string Language, string[] MustContain);
}
