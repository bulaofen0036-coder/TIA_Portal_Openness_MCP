using System;
using System.IO;
using System.Text.RegularExpressions;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>.s7dcl 文件里声明的第一个块：种类 + 名字。</summary>
    internal sealed class S7DclDeclaredBlock
    {
        public S7DclDeclaredBlock(string keyword, string name)
        {
            Keyword = keyword;
            Name = name;
        }

        /// <summary>ORGANIZATION_BLOCK / FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / TYPE。</summary>
        public string Keyword { get; }
        public string Name { get; }
        public bool IsOrganizationBlock => Keyword == "ORGANIZATION_BLOCK";
    }

    /// <summary>
    /// 从 .s7dcl 文本里读出"真正声明的块名"。
    ///
    /// ImportFromDocuments 以前拿**文件名**当块名去读回：OB100.s7dcl 里声明的是 "Startup"，
    /// 于是一次成功的导入被报成 "NOT found after import"（issue #30 的用户报告，V21 实测复现）；
    /// 导入前后的块号保持也按文件名找块，同样找不到。
    ///
    /// 文件头形如（V21 ExportAsDocuments 实测）：
    ///   { S7_EditorMode := "SCL"; S7_Optimized := "TRUE"; S7_Version := "0.1" }
    ///   ORGANIZATION_BLOCK "Startup"
    /// 块前可以有属性块 { ... } 和注释（// 行注释、(* *)、/* */），这些都跳过。
    /// </summary>
    internal static class S7DclHeader
    {
        private static readonly Regex Declaration = new Regex(
            @"\G\s*(?<kw>ORGANIZATION_BLOCK|FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\b\s*(?:""(?<q>[^""]+)""|(?<b>[A-Za-z_][A-Za-z0-9_]*))",
            RegexOptions.CultureInvariant);

        public static S7DclDeclaredBlock? ReadDeclaredBlock(string? text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var s = text!;
            int i = 0;
            if (s.Length > 0 && s[0] == '﻿') i = 1;

            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }

                if (s[i] == '{') { i = SkipPast(s, i + 1, "}"); continue; }
                if (StartsWith(s, i, "//")) { i = SkipPast(s, i + 2, "\n"); continue; }
                if (StartsWith(s, i, "(*")) { i = SkipPast(s, i + 2, "*)"); continue; }
                if (StartsWith(s, i, "/*")) { i = SkipPast(s, i + 2, "*/"); continue; }

                var m = Declaration.Match(s, i);
                if (!m.Success) return null;
                var name = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["b"].Value;
                return new S7DclDeclaredBlock(m.Groups["kw"].Value, name);
            }
            return null;
        }

        /// <summary>读 importPath/fileNameWithoutExtension.s7dcl 的声明；文件不存在或读不出就返回 null。</summary>
        public static S7DclDeclaredBlock? ReadDeclaredBlock(string importPath, string fileNameWithoutExtension)
        {
            try
            {
                var path = Path.Combine(importPath ?? "", (fileNameWithoutExtension ?? "") + ".s7dcl");
                return File.Exists(path) ? ReadDeclaredBlock(File.ReadAllText(path)) : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool StartsWith(string s, int i, string token) =>
            string.CompareOrdinal(s, i, token, 0, token.Length) == 0;

        private static int SkipPast(string s, int from, string terminator)
        {
            var end = s.IndexOf(terminator, from, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + terminator.Length;
        }
    }
}
