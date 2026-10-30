using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.WatchAndForceTables;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TiaMcpServer.Siemens
{
    /// <summary>
    /// Partial: 日常对象编辑 —— 读 / 增 / 改 / 删单个 PLC 变量、建变量表 / 监控表、删监控表、给 FB 建背景 DB、删设备、
    /// 改块名块号 / 变量表名 / 数据类型名 / 监控表名 / 设备名、编译硬件组态、归档工程。
    ///
    /// 全部是 V20 / V21 PublicAPI 里现成的成员。写属性一律走 IEngineeringObject.SetAttribute：
    /// CLR setter 在 V20 有的没有（监控表 Name），SetAttribute 两边都能编，TIA 不让改时会抛，照实报。
    /// 删除类与 DeletePlcBlock 一个规格：dryRun 默认 true，预览把影响摆出来，实做后读回确认。
    /// </summary>
    public partial class Portal
    {
        private const int TagListDefaultLimit = 300;

        private sealed class TagHit
        {
            public PlcTag Tag = null!;
            public PlcTagTable Table = null!;
            public string TablePath = "";
        }

        private PlcSoftware RequirePlc(string tool, string softwarePath)
        {
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"{tool}: no project is open. Connect + OpenProject (or AttachToOpenProject) first.");
            }
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"{tool}: PLC software not found at '{softwarePath}'." + AvailablePlcPathsSuffix());
            }
            return plc;
        }

        /// <summary>全部变量表（含子组），带组限定路径。</summary>
        private static List<(PlcTagTable Table, string Path)> AllTagTables(PlcSoftware plc)
        {
            var list = new List<(PlcTagTable, string)>();
            void Walk(PlcTagTableGroup g, string prefix)
            {
                foreach (var t in g.TagTables)
                    list.Add((t, prefix.Length == 0 ? t.Name : prefix + "/" + t.Name));
                foreach (PlcTagTableGroup sub in g.Groups)
                    Walk(sub, prefix.Length == 0 ? sub.Name : prefix + "/" + sub.Name);
            }
            Walk(plc.TagTableGroup, "");
            return list;
        }

        private static (PlcTagTable Table, string Path)? FindTagTableByName(List<(PlcTagTable Table, string Path)> tables, string wanted)
        {
            var w = (wanted ?? "").Replace('\\', '/').Trim('/');
            var exact = tables.Where(t => string.Equals(t.Path, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var byName = tables.Where(t => string.Equals(t.Table.Name, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 1) return byName[0];
            if (byName.Count > 1)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"tag table name '{wanted}' is ambiguous: " + string.Join(", ", byName.Select(t => t.Path)) + " — give the group-qualified path");
            }
            return null;
        }

        private static List<TagHit> FindTagsByName(List<(PlcTagTable Table, string Path)> tables, string name)
        {
            var hits = new List<TagHit>();
            foreach (var (table, path) in tables)
            {
                var t = table.Tags.Find(name);
                if (t != null) hits.Add(new TagHit { Tag = t, Table = table, TablePath = path });
            }
            return hits;
        }

        private static JsonObject ReadComment(MultilingualText? text)
        {
            var o = new JsonObject();
            if (text == null) return o;
            try
            {
                foreach (var item in text.Items)
                {
                    var culture = item.Language?.Culture?.Name ?? "";
                    var s = item.Text ?? "";
                    if (culture.Length > 0 && s.Length > 0) o[culture] = s;
                }
            }
            catch (Exception ex)
            {
                o["_error"] = ex.Message;
            }
            return o;
        }

        private static JsonObject TagToJson(PlcTag tag, string tablePath)
        {
            var o = new JsonObject
            {
                ["name"] = tag.Name,
                ["table"] = tablePath,
                ["dataType"] = tag.DataTypeName,
                ["address"] = tag.LogicalAddress,
                ["comment"] = ReadComment(tag.Comment),
            };
            try
            {
                o["externalAccessible"] = tag.ExternalAccessible;
                o["externalVisible"] = tag.ExternalVisible;
                o["externalWritable"] = tag.ExternalWritable;
            }
            catch { /* 安全变量等少数情形读不出，不影响主字段 */ }
            return o;
        }

        // ─────────────────────────────────────────────────────────────── GetPlcTags
        public JsonObject GetPlcTags(string softwarePath, string tagTableName, string nameFilter, string addressFilter, bool includeConstants, int maxTags)
        {
            var plc = RequirePlc("GetPlcTags", softwarePath);
            var tables = AllTagTables(plc);
            if (!string.IsNullOrWhiteSpace(tagTableName))
            {
                var hit = FindTagTableByName(tables, tagTableName);
                if (hit == null)
                {
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"GetPlcTags: no tag table '{tagTableName}' in '{softwarePath}'. Available: " + string.Join(", ", tables.Select(t => t.Path)));
                }
                tables = new List<(PlcTagTable, string)> { hit.Value };
            }
            int limit = maxTags <= 0 ? TagListDefaultLimit : Math.Min(maxTags, 5000);
            var nf = (nameFilter ?? "").Trim();
            var arr = new JsonArray();
            var consts = new JsonArray();
            int matched = 0, total = 0;
            foreach (var (table, path) in tables)
            {
                foreach (var tag in table.Tags)
                {
                    total++;
                    if (nf.Length > 0 && tag.Name.IndexOf(nf, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!EverydayEditSpec.AddressMatches(tag.LogicalAddress, addressFilter)) continue;
                    matched++;
                    if (arr.Count < limit) arr.Add(TagToJson(tag, path));
                }
                if (includeConstants)
                {
                    foreach (var c in table.UserConstants)
                    {
                        if (nf.Length > 0 && c.Name.IndexOf(nf, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        consts.Add(new JsonObject
                        {
                            ["name"] = c.Name,
                            ["table"] = path,
                            ["dataType"] = c.DataTypeName,
                            ["value"] = c.Value,
                            ["comment"] = ReadComment(c.Comment),
                        });
                    }
                }
            }
            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["tables"] = new JsonArray(tables.Select(t => (JsonNode)JsonValue.Create(t.Path)!).ToArray()),
                ["totalTags"] = total,
                ["matchedTags"] = matched,
                ["returnedTags"] = arr.Count,
                ["truncated"] = matched > arr.Count,
                ["tags"] = arr,
            };
            if (includeConstants) result["userConstants"] = consts;
            return result;
        }

        // ─────────────────────────────────────────────────────────────── SetPlcTag
        /// <summary>
        /// 写之前先校验注释语言：校验若放在改名 / 改类型 / 改地址之后，语言不对时报 InvalidParams，
        /// 调用方以为什么都没改，其实前面几项已经生效了。
        /// <paramref name="probe"/> = 任一同工程对象的注释（工程语言对所有对象相同）；null = 无从预检，留给写入时再判。
        /// </summary>
        private static void PrecheckCommentLanguage(MultilingualText? probe, string? comment, string language)
        {
            if (probe == null || string.IsNullOrEmpty(comment)) return;
            ResolveCommentItem(probe, language, out _);
        }

        private static MultilingualTextItem ResolveCommentItem(MultilingualText text, string language, out string usedCulture)
        {
            var items = text.Items.ToList();
            var cultures = items.Select(i => i.Language?.Culture?.Name ?? "").Where(c => c.Length > 0).ToList();
            MultilingualTextItem? target;
            if (string.IsNullOrWhiteSpace(language))
            {
                // 默认中文；工程里没有中文就用第一种语言（工程语言由 TIA「项目语言」决定，引擎不替用户加语言）。
                target = items.FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name, "zh-CN", StringComparison.OrdinalIgnoreCase))
                         ?? items.FirstOrDefault();
            }
            else
            {
                target = items.FirstOrDefault(i => string.Equals(i.Language?.Culture?.Name, language.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (target == null)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"comment language '{language}' is not a project language. Project languages: " + (cultures.Count > 0 ? string.Join(", ", cultures) : "(none)")
                    + ". Add the language in TIA (Project languages) first, or pick one of these.");
            }
            usedCulture = target.Language?.Culture?.Name ?? "";
            return target;
        }

        public JsonObject SetPlcTag(string softwarePath, string tagName, string tagTableName, string newName, string dataType, string address, string comment, string commentLanguage)
        {
            var p = EverydayEditSpec.NameProblem(tagName, "tagName");
            if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "SetPlcTag: " + p);
            if (!string.IsNullOrEmpty(newName))
            {
                p = EverydayEditSpec.NameProblem(newName, "newName");
                if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "SetPlcTag: " + p);
            }
            var plc = RequirePlc("SetPlcTag", softwarePath);
            var tables = AllTagTables(plc);
            var hits = FindTagsByName(tables, tagName);
            (PlcTagTable Table, string Path)? wantedTable = string.IsNullOrWhiteSpace(tagTableName) ? null : FindTagTableByName(tables, tagTableName);
            if (!string.IsNullOrWhiteSpace(tagTableName) && wantedTable == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"SetPlcTag: no tag table '{tagTableName}' in '{softwarePath}'. Available: " + string.Join(", ", tables.Select(t => t.Path)));
            }
            PrecheckCommentLanguage(hits.Count == 1 ? hits[0].Tag.Comment : tables.SelectMany(t => t.Table.Tags).FirstOrDefault()?.Comment, comment, commentLanguage);

            bool created = false;
            TagHit hit;
            if (hits.Count == 0)
            {
                // 新建：PLC 变量名全 PLC 唯一；必须给表、类型、地址（PLC 变量没有地址就不是 PLC 变量）。
                if (wantedTable == null)
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"SetPlcTag: tag '{tagName}' does not exist; to create it give tagTableName (one of: {string.Join(", ", tables.Select(t => t.Path))}), dataType and address.");
                if (string.IsNullOrWhiteSpace(dataType) || string.IsNullOrWhiteSpace(address))
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"SetPlcTag: tag '{tagName}' does not exist; creating it needs both dataType (e.g. 'Bool') and address (e.g. '%Q0.0').");
                if (!string.IsNullOrEmpty(newName))
                    throw new PortalException(PortalErrorCode.InvalidParams, "SetPlcTag: newName only applies to an existing tag; to create, put the name in tagName.");
                var t = wantedTable.Value.Table.Tags.Create(tagName, dataType.Trim(), address.Trim());
                hit = new TagHit { Tag = t, Table = wantedTable.Value.Table, TablePath = wantedTable.Value.Path };
                created = true;
            }
            else if (hits.Count > 1)
            {
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"SetPlcTag: tag '{tagName}' exists in several tables ({string.Join(", ", hits.Select(h => h.TablePath))}) — TIA should not allow this; fix it in TIA first.");
            }
            else
            {
                hit = hits[0];
                if (wantedTable != null && wantedTable.Value.Path != hit.TablePath)
                {
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"SetPlcTag: tag '{tagName}' lives in table '{hit.TablePath}', not '{tagTableName}'. Moving a tag between tables is not supported here; "
                        + "leave tagTableName empty to edit it where it is.");
                }
            }

            var before = created ? null : TagToJson(hit.Tag, hit.TablePath);
            var changes = new List<string>();
            if (!created && !string.IsNullOrWhiteSpace(newName) && newName != hit.Tag.Name
                && !string.Equals(newName, hit.Tag.Name, StringComparison.OrdinalIgnoreCase))
            {
                // 只改大小写不算重名（Find 不分大小写，会找回它自己）。
                var clash = FindTagsByName(tables, newName);
                if (clash.Count > 0)
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"SetPlcTag: a tag named '{newName}' already exists (table '{clash[0].TablePath}'). Tag names are unique per PLC. Nothing was changed.");
            }
            string? usedCulture = null;
            try
            {
                if (!created)
                {
                    if (!string.IsNullOrWhiteSpace(newName) && newName != hit.Tag.Name)
                    {
                        hit.Tag.SetAttribute("Name", newName);
                        changes.Add("name");
                    }
                    if (!string.IsNullOrWhiteSpace(dataType) && !string.Equals(dataType.Trim(), hit.Tag.DataTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        hit.Tag.SetAttribute("DataTypeName", dataType.Trim());
                        changes.Add("dataType");
                    }
                    if (!string.IsNullOrWhiteSpace(address) && EverydayEditSpec.NormalizeAddress(address) != EverydayEditSpec.NormalizeAddress(hit.Tag.LogicalAddress))
                    {
                        hit.Tag.SetAttribute("LogicalAddress", address.Trim());
                        changes.Add("address");
                    }
                }
                if (comment != null && comment.Length > 0)
                {
                    ResolveCommentItem(hit.Tag.Comment, commentLanguage, out var c).SetAttribute("Text", comment);
                    usedCulture = c;
                    changes.Add("comment");
                }
            }
            catch (Exception ex) when (created || changes.Count > 0)
            {
                string current = tagName;
                try { current = hit.Tag.Name; } catch { }
                throw new PortalException(PortalErrorCode.OpennessError,
                    EverydayEditSpec.PartialApplyMessage("SetPlcTag", "tag", tagName, current, created, changes, ex.Message), inner: ex);
            }

            // 读回：从 PLC 重新找一遍（改名后旧名找不到才对）。
            var finalName = !created && !string.IsNullOrWhiteSpace(newName) ? newName : tagName;
            var tables2 = AllTagTables(plc);
            var again = FindTagsByName(tables2, finalName);
            bool verified = again.Count == 1;
            JsonObject? after = verified ? TagToJson(again[0].Tag, again[0].TablePath) : null;
            if (verified)
            {
                if (!string.IsNullOrWhiteSpace(dataType) && !string.Equals(after!["dataType"]?.ToString(), dataType.Trim(), StringComparison.OrdinalIgnoreCase)) verified = false;
                if (!string.IsNullOrWhiteSpace(address) && EverydayEditSpec.NormalizeAddress(after!["address"]?.ToString()) != EverydayEditSpec.NormalizeAddress(address)) verified = false;
                if (usedCulture != null && after!["comment"]?[usedCulture]?.ToString() != comment) verified = false;
            }
            if (!created && !string.Equals(finalName, tagName, StringComparison.OrdinalIgnoreCase) && FindTagsByName(tables2, tagName).Count > 0) verified = false;

            // 地址重叠只提示不拦：TIA 自己也允许（黄色警告），IO 迁移时常常要先占后放。
            var warnings = new JsonArray();
            var addr = after?["address"]?.ToString();
            if (!string.IsNullOrEmpty(addr))
            {
                var same = tables2.SelectMany(t => t.Table.Tags.Select(x => (x, t.Path)))
                    .Where(x => x.x.Name != finalName && EverydayEditSpec.NormalizeAddress(x.x.LogicalAddress) == EverydayEditSpec.NormalizeAddress(addr))
                    .Select(x => x.x.Name + "（" + x.Path + "）").ToList();
                if (same.Count > 0) warnings.Add($"地址 {addr} 还被这些变量用着：{string.Join("、", same)}。TIA 允许重叠，但多半是迁移没收尾 —— 确认后改掉或删掉旧的。");
            }
            foreach (var w in EverydayEditSpec.TagEditWarnings(created, changes)) warnings.Add(w);
            return new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["created"] = created,
                ["changed"] = created || changes.Count > 0,
                ["changes"] = ToJsonArray(changes),
                ["commentLanguage"] = usedCulture,
                ["before"] = before,
                ["after"] = after,
                ["verified"] = verified,
                ["warnings"] = warnings,
            };
        }

        // ─────────────────────────────────────────────────────────────── DeletePlcTag
        public JsonObject DeletePlcTag(string softwarePath, string tagName, string tagTableName, bool dryRun)
        {
            var p = EverydayEditSpec.NameProblem(tagName, "tagName");
            if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcTag: " + p);
            var plc = RequirePlc("DeletePlcTag", softwarePath);
            var tables = AllTagTables(plc);
            var hits = FindTagsByName(tables, tagName);
            if (!string.IsNullOrWhiteSpace(tagTableName))
            {
                var t = FindTagTableByName(tables, tagTableName);
                if (t == null)
                    throw new PortalException(PortalErrorCode.NotFound,
                        $"DeletePlcTag: no tag table '{tagTableName}' in '{softwarePath}'. Available: " + string.Join(", ", tables.Select(x => x.Path)));
                hits = hits.Where(h => h.TablePath == t.Value.Path).ToList();
            }
            if (hits.Count == 0)
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcTag: no tag named '{tagName}'" + (string.IsNullOrWhiteSpace(tagTableName) ? "" : $" in table '{tagTableName}'") + $" on '{softwarePath}'. Use GetPlcTags to list tags.");
            if (hits.Count > 1)
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"DeletePlcTag: '{tagName}' found in several tables ({string.Join(", ", hits.Select(h => h.TablePath))}); give tagTableName.");
            var hit = hits[0];
            var info = TagToJson(hit.Tag, hit.TablePath);
            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["tag"] = info,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false,
                ["warnings"] = new JsonArray
                {
                    "删除后程序里按符号引用它的地方会变成绝对地址或报错（编译看得到）；HMI 按符号名绑定 PLC 变量，断链要到画面上才暴露。",
                    "变量的注释删了就没了 —— 需要留底先 ExportPlcTagTable。"
                },
            };
            if (dryRun) return result;
            hit.Tag.Delete();
            bool absent = FindTagsByName(AllTagTables(plc), tagName).Count == 0;
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
                throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcTag: Delete() returned but '{tagName}' is still present");
            return result;
        }

        // ─────────────────────────────────────────────────────────────── CreatePlcTagTable / CreatePlcWatchTable
        private static PlcTagTableGroup? FindTagTableGroup(PlcSoftware plc, string? groupPath)
        {
            PlcTagTableGroup cur = plc.TagTableGroup;
            foreach (var n in EverydayEditSpec.GroupPath(EverydayEditSpec.KindTagTable, groupPath, cur.Name))
            {
                PlcTagTableGroup? next = cur.Groups.FirstOrDefault(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        private static PlcWatchAndForceTableGroup? FindWatchGroup(PlcSoftware plc, string? groupPath)
        {
            PlcWatchAndForceTableGroup cur = plc.WatchAndForceTableGroup;
            foreach (var n in EverydayEditSpec.GroupPath(EverydayEditSpec.KindWatchTable, groupPath, cur.Name))
            {
                PlcWatchAndForceTableGroup? next = cur.Groups.FirstOrDefault(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        public JsonObject CreatePlcTagTable(string softwarePath, string tableName, string groupPath)
        {
            var p = EverydayEditSpec.NameProblem(tableName, "tableName");
            if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "CreatePlcTagTable: " + p);
            var plc = RequirePlc("CreatePlcTagTable", softwarePath);
            var all = AllTagTables(plc);
            var clash = all.FirstOrDefault(t => string.Equals(t.Table.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (clash.Table != null)
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"CreatePlcTagTable: a tag table named '{clash.Table.Name}' already exists ('{clash.Path}'). Tag table names are unique per PLC. Nothing was changed.");
            var group = FindTagTableGroup(plc, groupPath)
                ?? throw new PortalException(PortalErrorCode.NotFound,
                    $"CreatePlcTagTable: tag table group '{groupPath}' not found. Create the group in TIA first, or leave groupPath empty for the root.");
            group.TagTables.Create(tableName.Trim());
            var back = AllTagTables(plc).FirstOrDefault(t => string.Equals(t.Table.Name, tableName.Trim(), StringComparison.Ordinal));
            return new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["tableName"] = tableName.Trim(),
                ["tablePath"] = back.Path,
                ["verified"] = back.Table != null,
            };
        }

        public JsonObject CreatePlcWatchTable(string softwarePath, string tableName, string groupPath)
        {
            var p = EverydayEditSpec.NameProblem(tableName, "tableName");
            if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "CreatePlcWatchTable: " + p);
            var plc = RequirePlc("CreatePlcWatchTable", softwarePath);
            var clash = AllWatchTables(plc).FirstOrDefault(t => string.Equals(t.Table.Name, tableName, StringComparison.OrdinalIgnoreCase));
            if (clash.Table != null)
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"CreatePlcWatchTable: a watch table named '{clash.Table.Name}' already exists ('{clash.Path}'). Nothing was changed.");
            var group = FindWatchGroup(plc, groupPath)
                ?? throw new PortalException(PortalErrorCode.NotFound,
                    $"CreatePlcWatchTable: watch table group '{groupPath}' not found. Create the group in TIA first, or leave groupPath empty for the root.");
            group.WatchTables.Create(tableName.Trim());
            var back = AllWatchTables(plc).FirstOrDefault(t => string.Equals(t.Table.Name, tableName.Trim(), StringComparison.Ordinal));
            return new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["tableName"] = tableName.Trim(),
                ["tablePath"] = back.Path,
                ["verified"] = back.Table != null,
            };
        }

        // ─────────────────────────────────────────────────────────────── DeletePlcWatchTable
        public JsonObject DeletePlcWatchTable(string softwarePath, string watchTableName, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(watchTableName))
                throw new PortalException(PortalErrorCode.InvalidParams, "DeletePlcWatchTable: watchTableName is empty");
            var plc = RequirePlc("DeletePlcWatchTable", softwarePath);
            var all = AllWatchTables(plc);
            var w = watchTableName.Replace('\\', '/').Trim('/');
            var hits = all.Where(x => string.Equals(x.Path, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) hits = all.Where(x => string.Equals(x.Table.Name, w, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0)
                throw new PortalException(PortalErrorCode.NotFound,
                    $"DeletePlcWatchTable: watch table '{watchTableName}' not found. Available: " + (all.Count > 0 ? string.Join(", ", all.Select(x => x.Path)) : "(none)"));
            if (hits.Count > 1)
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"DeletePlcWatchTable: '{watchTableName}' is ambiguous: {string.Join(", ", hits.Select(x => x.Path))} — give the group-qualified path.");
            var hit = hits[0];
            int entries = -1;
            try { if (TryGetPropertyValue(hit.Table, "Entries") is IEnumerable e) entries = e.Cast<object>().Count(); } catch { }
            var result = new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["watchTable"] = hit.Path,
                ["entryCount"] = entries,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false,
                ["warnings"] = new JsonArray { "监控表只影响调试，不影响程序；但表里的条目、修改值、注释删了就没了 —— 要留底先 ExportPlcWatchTable。" },
            };
            if (dryRun) return result;
            hit.Table.Delete();
            bool absent = !AllWatchTables(plc).Any(x => string.Equals(x.Path, hit.Path, StringComparison.OrdinalIgnoreCase));
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
                throw new PortalException(PortalErrorCode.OpennessError, $"DeletePlcWatchTable: Delete() returned but '{hit.Path}' is still present");
            return result;
        }

        // ─────────────────────────────────────────────────────────────── CreateInstanceDB
        public JsonObject CreateInstanceDB(string softwarePath, string fbName, string dbName, int number, string groupPath)
        {
            if (string.IsNullOrWhiteSpace(fbName))
                throw new PortalException(PortalErrorCode.InvalidParams, "CreateInstanceDB: fbName is empty");
            var plc = RequirePlc("CreateInstanceDB", softwarePath);
            var fb = FindBlockLoose(softwarePath, fbName);
            if (fb == null)
                throw new PortalException(PortalErrorCode.NotFound, $"CreateInstanceDB: block '{fbName}' not found in '{softwarePath}'. Use GetBlocks to list blocks.");
            if (!(fb is FB))
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"CreateInstanceDB: '{fbName}' is a {fb.GetType().Name}, not an FB — only function blocks have instance DBs.");
            var name = string.IsNullOrWhiteSpace(dbName) ? EverydayEditSpec.DefaultInstanceDbName(fb.Name) : dbName.Trim();
            var p = EverydayEditSpec.NameProblem(name, "dbName");
            if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "CreateInstanceDB: " + p);
            if (FindBlockLoose(softwarePath, name) != null)
                throw new PortalException(PortalErrorCode.InvalidParams, $"CreateInstanceDB: a block named '{name}' already exists. Pick another dbName.");

            PlcBlockGroup target = plc.BlockGroup;
            if (!string.IsNullOrWhiteSpace(groupPath))
            {
                foreach (var n in EverydayEditSpec.GroupPath(EverydayEditSpec.KindBlock, groupPath, plc.BlockGroup.Name))
                {
                    var next = target.Groups.FirstOrDefault(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase));
                    if (next == null)
                        throw new PortalException(PortalErrorCode.NotFound,
                            $"CreateInstanceDB: block group '{groupPath}' not found (stuck at '{n}'). Create it with CreatePlcBlockGroup, or leave groupPath empty for the root.");
                    target = next;
                }
            }
            bool auto = number <= 0;
            var db = target.Blocks.CreateInstanceDB(name, auto, auto ? 1 : number, fb.Name);
            var back = FindBlockLoose(softwarePath, name) as InstanceDB;
            bool verified = back != null && string.Equals(back.InstanceOfName, fb.Name, StringComparison.Ordinal);
            return new JsonObject
            {
                ["softwarePath"] = softwarePath,
                ["fbName"] = fb.Name,
                ["dbName"] = name,
                ["dbNumber"] = back?.Number ?? db.Number,
                ["autoNumber"] = auto,
                ["blockPath"] = back != null ? GetBlockPath(back) : null,
                ["instanceOfName"] = back?.InstanceOfName,
                ["verified"] = verified,
            };
        }

        // ─────────────────────────────────────────────────────────────── DeleteDevice
        /// <summary>
        /// 设备的「头」设备项名：CPU / 接口模块（Classification ≠ None）或带软件容器的设备项（HMI 面板、PC 站应用）。
        /// IDE 项目树里显示的就是它们的名字（PLC_1），而 Device.Name 常是看不见的站名（S7-1500/ET200MP station_1）。
        /// </summary>
        private List<string> HeadItemNames(Device device)
        {
            var heads = new List<string>();
            void Walk(DeviceItemComposition c, int depth)
            {
                foreach (DeviceItem di in c)
                {
                    bool head = false;
                    try { head = di.Classification != DeviceItemClassifications.None; } catch { }
                    if (!head) head = TryGetServiceByTypeSuffix(di, "SoftwareContainer") != null;
                    if (head && !string.IsNullOrEmpty(di.Name) && !heads.Contains(di.Name)) heads.Add(di.Name);
                    if (depth < 2) Walk(di.DeviceItems, depth + 1);
                }
            }
            Walk(device.DeviceItems, 0);
            return heads;
        }

        private string DeviceNamesForMessage()
        {
            var names = EnumerateAllDevices().Select(d => d.Name).ToList();
            return names.Count > 0 ? string.Join(", ", names) : "(none)";
        }

        public JsonObject DeleteDevice(string devicePath, bool dryRun)
        {
            if (string.IsNullOrWhiteSpace(devicePath))
                throw new PortalException(PortalErrorCode.InvalidParams, "DeleteDevice: devicePath is empty");
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "DeleteDevice: no project is open. Connect + OpenProject (or AttachToOpenProject) first.");
            var device = GetDevice(devicePath)
                         ?? throw new PortalException(PortalErrorCode.NotFound,
                             $"DeleteDevice: device '{devicePath}' not found. Devices: " + DeviceNamesForMessage());
            var refusal = EverydayEditSpec.WholeDeviceRefusal("DeleteDevice", devicePath, device.Name, HeadItemNames(device));
            if (refusal != null) throw new PortalException(PortalErrorCode.InvalidParams, refusal);
            var name = device.Name;
            var items = new JsonArray();
            var software = new JsonArray();
            void Walk(DeviceItemComposition c, int depth)
            {
                foreach (DeviceItem di in c)
                {
                    if (items.Count < 100 && depth <= 1) items.Add(di.Name);
                    var sc = TryGetServiceByTypeSuffix(di, "SoftwareContainer");
                    var sw = sc == null ? null : TryGetPropertyValue(sc, "Software");
                    if (sw != null)
                    {
                        var o = new JsonObject { ["name"] = TryGetPropertyValue(sw, "Name")?.ToString(), ["type"] = sw.GetType().Name };
                        if (sw is PlcSoftware ps)
                        {
                            int blocks = 0;
                            void Count(PlcBlockGroup g) { blocks += g.Blocks.Count; foreach (var s in g.Groups) Count(s); }
                            try { Count(ps.BlockGroup); o["blockCount"] = blocks; } catch { }
                        }
                        software.Add(o);
                    }
                    if (depth < 3) Walk(di.DeviceItems, depth + 1);
                }
            }
            Walk(device.DeviceItems, 0);
            string? typeId = null;
            try { typeId = device.TypeIdentifier; } catch { }
            var warnings = new JsonArray();
            if (software.Count > 0)
                warnings.Add("⚠️ 这台设备里有软件（见 software）—— 删设备会连同它的整套程序 / 画面一起删掉，无法撤销。要留程序先 ExportBlocks / ArchiveProject。");
            warnings.Add("网络连接、IO 系统分配、S7 连接、HMI 连接里指向这台设备的部分会一并断开。");
            var result = new JsonObject
            {
                ["requestedDevicePath"] = devicePath,
                ["deviceName"] = name,
                ["typeIdentifier"] = typeId,
                ["topLevelItems"] = items,
                ["software"] = software,
                ["dryRun"] = dryRun,
                ["deleted"] = false,
                ["verifiedAbsent"] = false,
                ["warnings"] = warnings,
            };
            if (dryRun) return result;
            device.Delete();
            bool absent = !EnumerateAllDevices().Any(d => string.Equals(d.Name, name, StringComparison.Ordinal));
            result["deleted"] = true;
            result["verifiedAbsent"] = absent;
            if (!absent)
                throw new PortalException(PortalErrorCode.OpennessError, $"DeleteDevice: Delete() returned but device '{name}' is still present");
            return result;
        }

        // ─────────────────────────────────────────────────────────────── RenamePlcObject
        public JsonObject RenamePlcObject(string kind, string softwarePath, string objectPath, string newName, int newNumber)
        {
            var k = EverydayEditSpec.RenameKind(kind)
                    ?? throw new PortalException(PortalErrorCode.InvalidParams,
                        $"RenamePlcObject: unknown kind '{kind}'. Use block | type | tagtable | watchtable | device.");
            if (string.IsNullOrWhiteSpace(objectPath))
                throw new PortalException(PortalErrorCode.InvalidParams, "RenamePlcObject: objectPath is empty");
            bool wantName = !string.IsNullOrWhiteSpace(newName);
            if (wantName)
            {
                var p = EverydayEditSpec.NameProblem(newName, "newName");
                if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "RenamePlcObject: " + p);
            }
            if (newNumber > 0 && k != EverydayEditSpec.KindBlock)
                throw new PortalException(PortalErrorCode.InvalidParams, "RenamePlcObject: newNumber only applies to kind=block.");
            if (!wantName && newNumber <= 0)
                throw new PortalException(PortalErrorCode.InvalidParams, "RenamePlcObject: give newName and/or newNumber (blocks).");

            IEngineeringObject target;
            string oldName;
            Func<string, bool> stillFindable;   // 按名字能不能找到（读回用）
            Func<string, string?> otherOwner;   // 同名对象（重名预检）
            int? oldNumber = null;
            switch (k)
            {
                case EverydayEditSpec.KindBlock:
                {
                    RequirePlc("RenamePlcObject", softwarePath);
                    var b = FindBlockLoose(softwarePath, objectPath)
                            ?? throw new PortalException(PortalErrorCode.NotFound, $"RenamePlcObject: block '{objectPath}' not found in '{softwarePath}'.");
                    target = b; oldName = b.Name; oldNumber = b.Number;
                    stillFindable = n => FindBlockLoose(softwarePath, n) != null;
                    otherOwner = n => { var o = FindBlockLoose(softwarePath, n); return o != null && o.Name != b.Name ? GetBlockPath(o) : null; };
                    break;
                }
                case EverydayEditSpec.KindType:
                {
                    RequirePlc("RenamePlcObject", softwarePath);
                    var t = FindTypeLoose(softwarePath, objectPath)
                            ?? throw new PortalException(PortalErrorCode.NotFound, $"RenamePlcObject: PLC data type '{objectPath}' not found in '{softwarePath}'.");
                    target = t; oldName = t.Name;
                    stillFindable = n => FindTypeLoose(softwarePath, n) != null;
                    otherOwner = n => { var o = FindTypeLoose(softwarePath, n); return o != null && o.Name != t.Name ? o.Name : null; };
                    break;
                }
                case EverydayEditSpec.KindTagTable:
                {
                    var plc = RequirePlc("RenamePlcObject", softwarePath);
                    var all = AllTagTables(plc);
                    var hit = FindTagTableByName(all, objectPath)
                              ?? throw new PortalException(PortalErrorCode.NotFound,
                                  $"RenamePlcObject: tag table '{objectPath}' not found. Available: " + string.Join(", ", all.Select(x => x.Path)));
                    target = hit.Table; oldName = hit.Table.Name;
                    var oldPath = hit.Path;
                    stillFindable = n => AllTagTables(plc).Any(x => string.Equals(x.Table.Name, n, StringComparison.OrdinalIgnoreCase));
                    otherOwner = n => AllTagTables(plc).Where(x => x.Path != oldPath && string.Equals(x.Table.Name, n, StringComparison.OrdinalIgnoreCase)).Select(x => x.Path).FirstOrDefault();
                    break;
                }
                case EverydayEditSpec.KindWatchTable:
                {
                    var plc = RequirePlc("RenamePlcObject", softwarePath);
                    var all = AllWatchTables(plc);
                    var w = (objectPath ?? "").Replace('\\', '/').Trim('/');
                    var hit = all.FirstOrDefault(x => string.Equals(x.Path, w, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Table.Name, w, StringComparison.OrdinalIgnoreCase));
                    if (hit.Table == null)
                        throw new PortalException(PortalErrorCode.NotFound,
                            $"RenamePlcObject: watch table '{objectPath}' not found. Available: " + (all.Count > 0 ? string.Join(", ", all.Select(x => x.Path)) : "(none)"));
                    target = hit.Table; oldName = hit.Table.Name;
                    var oldPath = hit.Path;
                    stillFindable = n => AllWatchTables(plc).Any(x => string.Equals(x.Table.Name, n, StringComparison.OrdinalIgnoreCase));
                    otherOwner = n => AllWatchTables(plc).Where(x => x.Path != oldPath && string.Equals(x.Table.Name, n, StringComparison.OrdinalIgnoreCase)).Select(x => x.Path).FirstOrDefault();
                    break;
                }
                default:
                {
                    if (IsProjectNull())
                        throw new PortalException(PortalErrorCode.InvalidState, "RenamePlcObject: no project is open. Connect + OpenProject (or AttachToOpenProject) first.");
                    var d = GetDevice(objectPath)
                            ?? throw new PortalException(PortalErrorCode.NotFound,
                                $"RenamePlcObject: device '{objectPath}' not found. Devices: " + DeviceNamesForMessage());
                    var refusal = EverydayEditSpec.WholeDeviceRefusal("RenamePlcObject(kind=device)", objectPath, d.Name, HeadItemNames(d));
                    if (refusal != null) throw new PortalException(PortalErrorCode.InvalidParams, refusal);
                    target = d; oldName = d.Name;
                    var oldDeviceName = d.Name;
                    stillFindable = n => EnumerateAllDevices().Any(x => string.Equals(x.Name, n, StringComparison.Ordinal));
                    otherOwner = n => EnumerateAllDevices().Where(x => x.Name != oldDeviceName && string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase)).Select(x => x.Name).FirstOrDefault();
                    break;
                }
            }

            if (wantName && !string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                var clash = string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase) ? null : otherOwner(newName);
                if (clash != null)
                    throw new PortalException(PortalErrorCode.InvalidParams, $"RenamePlcObject: '{newName}' is already used by '{clash}'. Nothing was changed.");
            }
            if (newNumber > 0 && target is PlcBlock blk && blk.Number != newNumber)
            {
                // 自动编号开着时写 Number 会被拒，而 AutoNumber 经 Openness 只读 ——
                // 关自动编号只能人在 TIA 里点。在任何改动之前如实拒绝，别改了名字再在改号上失败。
                if (blk.AutoNumber)
                    throw new PortalException(PortalErrorCode.InvalidState,
                        $"RenamePlcObject: block '{oldName}' uses automatic numbering, and Openness does not allow switching that off (AutoNumber is read-only). "
                        + "In TIA: block Properties → General → untick 'Number automatically' (自动编号), save, then call again. Nothing was changed.");
                var clashBlock = NumberClash(softwarePath, blk, newNumber);
                if (clashBlock != null)
                    throw new PortalException(PortalErrorCode.InvalidParams,
                        $"RenamePlcObject: number {newNumber} is already used by '{clashBlock}' (same block family). Nothing was changed.");
            }

            var changes = new List<string>();
            try
            {
                if (wantName && !string.Equals(newName, oldName, StringComparison.Ordinal))
                {
                    target.SetAttribute("Name", newName);
                    changes.Add("name");
                }
                if (newNumber > 0 && target is PlcBlock b2 && b2.Number != newNumber)
                {
                    target.SetAttribute("Number", newNumber);
                    changes.Add("number");
                }
            }
            catch (Exception ex) when (ex is not PortalException)
            {
                // 只在真跑在 V20 上时这么说：V21 上同样的 "not supported" 另有原因，说成「V20 不支持」是误导。
                var v20 = k == EverydayEditSpec.KindWatchTable && Engineering.TiaMajorVersion == 20 && ex.Message.IndexOf("not supported", StringComparison.OrdinalIgnoreCase) >= 0
                    ? " TIA V20 Openness does not allow renaming watch tables (V21 does); rename it in the TIA UI."
                    : "";
                throw new PortalException(PortalErrorCode.OpennessError,
                    $"RenamePlcObject: TIA refused the change ({ex.Message}).{v20}" + (changes.Count > 0 ? " Already applied: " + string.Join(",", changes) : " Nothing was changed."), inner: ex);
            }

            var finalName = wantName ? newName : oldName;
            bool verified = stillFindable(finalName) && (!wantName || string.Equals(finalName, oldName, StringComparison.OrdinalIgnoreCase) || !stillFindable(oldName));
            int? finalNumber = null;
            if (k == EverydayEditSpec.KindBlock)
            {
                var back = FindBlockLoose(softwarePath, finalName);
                finalNumber = back?.Number;
                if (newNumber > 0 && finalNumber != newNumber) verified = false;
            }
            var result = new JsonObject
            {
                ["kind"] = k,
                ["softwarePath"] = k == EverydayEditSpec.KindDevice ? null : softwarePath,
                ["oldName"] = oldName,
                ["newName"] = finalName,
                ["oldNumber"] = oldNumber,
                ["newNumber"] = finalNumber,
                ["changes"] = ToJsonArray(changes),
                ["changed"] = changes.Count > 0,
                ["verified"] = verified,
            };
            var warnings = EverydayEditSpec.RenameWarnings(k, changes, target is DataBlock);
            if (target is Device && changes.Contains("name"))
            {
                var viaHead = EverydayEditSpec.RenamedViaHeadItemNote(objectPath ?? "", oldName);
                if (viaHead != null) warnings.Add(viaHead);
            }
            result["warnings"] = ToJsonArray(warnings);
            return result;
        }

        private static List<(PlcWatchTable Table, string Path)> AllWatchTables(PlcSoftware plc)
        {
            var list = new List<(PlcWatchTable, string)>();
            void Walk(PlcWatchAndForceTableGroup g, string prefix)
            {
                foreach (var t in g.WatchTables)
                    list.Add((t, prefix.Length == 0 ? t.Name : prefix + "/" + t.Name));
                foreach (PlcWatchAndForceTableGroup sub in g.Groups)
                    Walk(sub, prefix.Length == 0 ? sub.Name : prefix + "/" + sub.Name);
            }
            Walk(plc.WatchAndForceTableGroup, "");
            return list;
        }

        /// <summary>
        /// 块：先按给的路径找（GetBlock 只在指定组里找，裸名 = 根组）；裸名找不到再全 PLC 按名找（块名全 PLC 唯一）。
        /// 建在子组里的块用 GetBlock(裸名) 读回是 null —— 重名预检也会因此漏掉子组里的同名块。
        /// </summary>
        private PlcBlock? FindBlockLoose(string softwarePath, string blockPath)
        {
            var b = GetBlock(softwarePath, blockPath);
            if (b != null || (blockPath ?? "").Contains("/")) return b;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;
            PlcBlock? Walk(PlcBlockGroup g)
            {
                var hit = g.Blocks.FirstOrDefault(x => string.Equals(x.Name, blockPath, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
                foreach (var s in g.Groups) { var r = Walk(s); if (r != null) return r; }
                return null;
            }
            return Walk(plc.BlockGroup);
        }

        /// <summary>数据类型：同 FindBlockLoose 的规则。</summary>
        private global::Siemens.Engineering.SW.Types.PlcType? FindTypeLoose(string softwarePath, string typePath)
        {
            var t = GetType(softwarePath, typePath);
            if (t != null || (typePath ?? "").Contains("/")) return t;
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;
            global::Siemens.Engineering.SW.Types.PlcType? Walk(global::Siemens.Engineering.SW.Types.PlcTypeGroup g)
            {
                var hit = g.Types.FirstOrDefault(x => string.Equals(x.Name, typePath, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
                foreach (global::Siemens.Engineering.SW.Types.PlcTypeGroup s in g.Groups) { var r = Walk(s); if (r != null) return r; }
                return null;
            }
            return Walk(plc.TypeGroup);
        }

        /// <summary>同一编号空间里（OB / FB / FC / 所有 DB）是否已有这个号，返回占用者路径。按名字排除自己（Openness 代理对象不能按引用比）。</summary>
        private string? NumberClash(string softwarePath, PlcBlock self, int number)
        {
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return null;
            string Family(PlcBlock b) => b is DataBlock ? "DB" : b.GetType().Name;
            var fam = Family(self);
            string? hit = null;
            void Walk(PlcBlockGroup g)
            {
                foreach (var b in g.Blocks)
                    if (hit == null && b.Name != self.Name && b.Number == number && Family(b) == fam) hit = GetBlockPath(b);
                foreach (var s in g.Groups) Walk(s);
            }
            Walk(plc.BlockGroup);
            return hit;
        }

        // ─────────────────────────────────────────────────────────────── CompileHardware
        /// <summary>
        /// 编译一台设备的硬件组态。ICompilable 从设备本身取，取不到再从它的设备项（CPU 等，深两层）取；
        /// 走过的每一层都记下来，取不到就把这串报出去。
        /// </summary>
        public (global::Siemens.Engineering.Compiler.CompilerResult Result, string Target) CompileHardware(string devicePath)
        {
            if (string.IsNullOrWhiteSpace(devicePath))
                throw new PortalException(PortalErrorCode.InvalidParams, "CompileHardware: devicePath is empty");
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "CompileHardware: no project is open. Connect + OpenProject (or AttachToOpenProject) first.");
            var device = GetDevice(devicePath)
                         ?? throw new PortalException(PortalErrorCode.NotFound,
                             $"CompileHardware: device '{devicePath}' not found. Devices: " + DeviceNamesForMessage());
            var probed = new List<string>();
            global::Siemens.Engineering.Compiler.ICompilable? Probe(IEngineeringServiceProvider p, string label)
            {
                try
                {
                    var s = p.GetService<global::Siemens.Engineering.Compiler.ICompilable>();
                    probed.Add(label + (s == null ? "(no)" : "(OK)"));
                    return s;
                }
                catch (Exception ex) { probed.Add(label + "(threw " + ex.GetType().Name + ")"); return null; }
            }
            var svc = Probe(device, "Device " + device.Name);
            string target = "Device " + device.Name;
            if (svc == null)
            {
                foreach (DeviceItem di in device.DeviceItems)
                {
                    svc = Probe(di, "DeviceItem " + di.Name);
                    if (svc != null) { target = "DeviceItem " + di.Name; break; }
                    foreach (DeviceItem sub in di.DeviceItems)
                    {
                        svc = Probe(sub, "DeviceItem " + di.Name + "/" + sub.Name);
                        if (svc != null) { target = "DeviceItem " + di.Name + "/" + sub.Name; break; }
                    }
                    if (svc != null) break;
                }
            }
            if (svc == null)
                throw new PortalException(PortalErrorCode.InvalidState,
                    $"CompileHardware: device '{device.Name}' offers no ICompilable at the device or its first two item levels. Probed: {string.Join(" -> ", probed)}");
            return (svc.Compile(), target);
        }

        // ─────────────────────────────────────────────────────────────── ArchiveProject
        public JsonObject ArchiveProject(string targetDirectory, string archiveName, string mode)
        {
            if (IsProjectNull())
                throw new PortalException(PortalErrorCode.InvalidState, "ArchiveProject: no project is open.");
            if (!(_project is Project project))
                throw new PortalException(PortalErrorCode.InvalidState, "ArchiveProject: the open project is not a local project (multiuser sessions cannot be archived this way).");
            var modeName = EverydayEditSpec.ArchiveMode(mode)
                           ?? throw new PortalException(PortalErrorCode.InvalidParams,
                               $"ArchiveProject: unknown mode '{mode}'. Use compressed (default) | none | discard | discardcompressed.");
            if (string.IsNullOrWhiteSpace(targetDirectory))
                throw new PortalException(PortalErrorCode.InvalidParams, "ArchiveProject: targetDirectory is empty");
            bool modified;
            try { modified = project.IsModified; }
            catch (Exception ex) { throw new PortalException(PortalErrorCode.InvalidState, $"ArchiveProject: cannot read IsModified ({ex.Message}); SaveProject first and retry.", inner: ex); }
            if (modified)
                throw new PortalException(PortalErrorCode.InvalidState,
                    "ArchiveProject: the project has unsaved changes. Archive packs the saved state — call SaveProject first (the engine does not save on the user's behalf), then retry.");
            var name = string.IsNullOrWhiteSpace(archiveName) ? project.Name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmm") : archiveName.Trim();
            var p = EverydayEditSpec.NameProblem(name, "archiveName");
            if (p != null) throw new PortalException(PortalErrorCode.InvalidParams, "ArchiveProject: " + p);
            var dir = new DirectoryInfo(targetDirectory);
            if (!dir.Exists) dir.Create();
            // Archive 的 targetName 就是最终文件名，TIA 不补扩展名（传 'x' 就生成无扩展名的 x，
            // 双击打不开、TIA「恢复」也不认）。按工程文件扩展名补 .zapNN。
            string projExt = "";
            try { projExt = Path.GetExtension(project.Path?.FullName ?? ""); } catch { }
            var fileName = EverydayEditSpec.ArchiveFileName(name, projExt);
            var target = new FileInfo(Path.Combine(dir.FullName, fileName));
            if (target.Exists)
                throw new PortalException(PortalErrorCode.InvalidParams,
                    $"ArchiveProject: '{target.FullName}' already exists. Pick another archiveName (the engine does not overwrite archives).");
            var modeVal = (ProjectArchivationMode)Enum.Parse(typeof(ProjectArchivationMode), modeName);
            var started = DateTime.Now;
            project.Archive(dir, fileName, modeVal);
            target.Refresh();
            var file = target.Exists ? target : null;
            return new JsonObject
            {
                ["projectName"] = project.Name,
                ["mode"] = modeName,
                ["archivePath"] = file?.FullName,
                ["sizeBytes"] = file?.Length,
                ["seconds"] = Math.Round((DateTime.Now - started).TotalSeconds, 1),
                ["verified"] = file != null && file.Length > 0,
            };
        }
    }
}
