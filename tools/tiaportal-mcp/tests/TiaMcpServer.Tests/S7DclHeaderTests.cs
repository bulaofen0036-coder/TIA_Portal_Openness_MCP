using System;
using System.IO;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Tests
{
    /// <summary>
    /// ImportFromDocuments 的读回改按 .s7dcl 里声明的块名（issue #30）。
    /// 用户报告：OB100.s7dcl 里声明 "Startup"，导入成功却报 verified=false —— V21 实测复现。
    /// </summary>
    internal static class S7DclHeaderTests
    {
        internal static void Run(Action<bool, string> check)
        {
            // V21 ExportAsDocuments 的真实输出（BOM + 属性块 + ORGANIZATION_BLOCK）
            var fixture = ToolSafetyTests.FindRepoFile(Path.Combine("tools", "tiaportal-mcp", "tests", "TiaMcpServer.Tests", "fixtures", "ob", "OB100_Startup.export.s7dcl"));
            if (fixture == null)
            {
                check(false, "fixture OB100_Startup.export.s7dcl not found");
            }
            else
            {
                var real = S7DclHeader.ReadDeclaredBlock(File.ReadAllText(fixture));
                check(real != null && real.Keyword == "ORGANIZATION_BLOCK" && real.Name == "Spike_Startup" && real.IsOrganizationBlock,
                    "real V21 export: attribute block skipped, ORGANIZATION_BLOCK \"Spike_Startup\" read");
            }

            var startup = S7DclHeader.ReadDeclaredBlock("{ S7_Optimized := \"TRUE\" }\r\nORGANIZATION_BLOCK \"Startup\"\r\n{ S7_Language := \"SCL\" }\r\n");
            check(startup?.Name == "Startup", "file OB100.s7dcl declaring \"Startup\" → the block is Startup, not OB100");

            var commented = S7DclHeader.ReadDeclaredBlock("// file comment\n(* block *)\n/* c-style */\n{ S7_Version := \"0.1\" }\nFUNCTION_BLOCK \"FB_Motor\"\n");
            check(commented?.Keyword == "FUNCTION_BLOCK" && commented.Name == "FB_Motor", "comments and pragmas before the header are skipped");

            var fc = S7DclHeader.ReadDeclaredBlock("FUNCTION \"FC_Scale\" : Real\n");
            check(fc?.Keyword == "FUNCTION" && fc.Name == "FC_Scale" && !fc.IsOrganizationBlock, "FUNCTION is not mistaken for FUNCTION_BLOCK");

            var bare = S7DclHeader.ReadDeclaredBlock("﻿DATA_BLOCK DB_1\n");
            check(bare?.Keyword == "DATA_BLOCK" && bare.Name == "DB_1", "BOM + unquoted name");

            check(S7DclHeader.ReadDeclaredBlock("") == null, "empty text → null (caller falls back to the file name)");
            check(S7DclHeader.ReadDeclaredBlock("VAR_GLOBAL x : Int; END_VAR") == null, "no block header → null");
            check(S7DclHeader.ReadDeclaredBlock("{ unterminated pragma") == null, "unterminated pragma → null, no exception");

            var dir = Path.Combine(Path.GetTempPath(), "s7dcl_header_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "OB100.s7dcl"), "﻿ORGANIZATION_BLOCK \"Startup\"\n");
                check(S7DclHeader.ReadDeclaredBlock(dir, "OB100")?.Name == "Startup", "ReadDeclaredBlock(dir, fileName) reads the file");
                check(S7DclHeader.ReadDeclaredBlock(dir, "Missing") == null, "missing file → null");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
