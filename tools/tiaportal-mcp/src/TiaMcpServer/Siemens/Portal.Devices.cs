using Microsoft.Extensions.Logging;
using Siemens.Engineering;
using Siemens.Engineering.Cax;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Online;
using Siemens.Engineering.Online.Configurations;
using Siemens.Engineering.SW.Alarm;
using Siemens.Engineering.SW.OpcUa;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Multiuser;
using Siemens.Engineering.Safety;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Types;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TiaMcpServer.ModelContextProtocol;

namespace TiaMcpServer.Siemens
{
    // Partial: devices. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region devices

        public string GetProjectTree()
        {
            _logger?.LogInformation("Getting project tree...");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            StringBuilder sb = new();

            sb.AppendLine($"{_project?.Name}");

            var ancestorStates = new List<bool>();
            var sections = new List<Action>();
            
            if (_project?.Devices != null && _project.Devices.Count > 0)
            {
                sections.Add(() => GetProjectTreeDevices(sb, _project.Devices, ancestorStates));
            }
            
            if (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0)
            {
                sections.Add(() => GetProjectTreeGroups(sb, _project.DeviceGroups, ancestorStates));
            }
            
            if (_project?.UngroupedDevicesGroup != null)
            {
                sections.Add(() => GetProjectTreeUngroupedDeviceGroup(sb, _project.UngroupedDevicesGroup, ancestorStates));
            }
            
            for (int i = 0; i < sections.Count; i++)
            {
                var isLastSection = i == sections.Count - 1;
                if (i == 0)
                {
                    sections[i]();
                }
                else
                {
                    sections[i]();
                }
            }

            return sb.ToString();
        }

        

        public List<Device> GetDevices(string regexName = "")
        {
            _logger?.LogInformation("Getting devices...");

            if (IsProjectNull())
            {
                return [];
            }

            var list = new List<Device>();

            if (_project?.Devices != null)
            {
                foreach (Device device in _project.Devices)
                {
                    list.Add(device);
                }

                foreach (var group in _project.DeviceGroups)
                {
                    GetDevicesRecursive(group, list, regexName);
                }

                //foreach (var group in _project.UngroupedDevicesGroup)
                //{
                //    GetDevicesRecursive(_project.UngroupedDevicesGroup, list, regexName);
                //}
            }

            return list;
        }

        public Device? GetDevice(string devicePath)
        {
            _logger?.LogInformation($"Getting device by path: {devicePath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceByPath(devicePath);
        }

        public Device AddDevice(string orderNumber, string version, string deviceName)
        {
            _logger?.LogInformation($"Adding device: {deviceName}, OrderNumber={orderNumber}, Version={version}");
            if (IsProjectNull()) throw new PortalException(PortalErrorCode.InvalidState, "No project is open. If a project is already open in the TIA Portal UI, call AttachToOpenProject(projectName); otherwise call OpenProject(path) for a local .apXX project, or CreateProject to start a new one. (Connect is attempted automatically.)");

            string? lastVariantError = null;
            try
            {
                // Openness CreateWithItem expects a TypeIdentifier, not split order/version.
                // Example: OrderNumber:6ES7 513-1AM03-0AB0/V3.0
                var project = (_project as Project);
                if (project == null) throw new PortalException(PortalErrorCode.InvalidState, "Current project is not a local Project instance");

                var orderRaw = orderNumber ?? "";
                var verRaw = version ?? "";

                var orderVariants = new List<string>
                {
                    orderRaw,
                    NormalizeOrderNumber(orderRaw),
                    TryFormatMlfbWithSpaces(orderRaw),
                    TryFormatMlfbWithSpaces(NormalizeOrderNumber(orderRaw))
                }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var v = verRaw.Trim();
                var vNoV = v.StartsWith("V", StringComparison.OrdinalIgnoreCase) ? v.Substring(1) : v;
                var versionVariants = new List<string>
                {
                    v,
                    "V" + vNoV,
                    vNoV,
                    "" // let TIA pick default/latest if supported
                };

                // append common ".0" expansions (V3.0 -> V3.0.0 etc.)
                foreach (var baseV in new[] { v, "V" + vNoV, vNoV })
                {
                    if (string.IsNullOrWhiteSpace(baseV)) continue;
                    if (!baseV.Contains('.')) continue;

                    versionVariants.Add(baseV + ".0");
                    versionVariants.Add(baseV + ".0.0");
                }

                // Heuristic: if user provides TIA20.* for Unified panels, try bump to 21.* as well.
                // (Common when copying order/version from older screenshots.)
                try
                {
                    var parts = vNoV.Split('.');
                    if (parts.Length > 0 && int.TryParse(parts[0], out var maj) && maj == 20)
                    {
                        versionVariants.Add("21.0.0.0");
                        versionVariants.Add("21.0.0.1");
                        versionVariants.Add("V21.0.0.0");
                    }
                }
                catch { }

                versionVariants = versionVariants
                    .Where(x => x != null) // keep empty-string variant
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var typeIdentifierVariants = new List<string>();
                foreach (var o in orderVariants)
                {
                    foreach (var ver in versionVariants)
                    {
                        if (o.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase))
                        {
                            typeIdentifierVariants.Add(o);
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(ver))
                            typeIdentifierVariants.Add($"OrderNumber:{o}/{ver}");
                    }
                }

                typeIdentifierVariants = typeIdentifierVariants
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var typeIdentifier in typeIdentifierVariants)
                {
                    foreach (var itemName in new[] { deviceName, "Device_1", "Station_1" }.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var dev = project.Devices.CreateWithItem(typeIdentifier, itemName, deviceName);
                            if (dev is Device d) return d;
                        }
                        catch (Exception exTry)
                        {
                            // try next variant
                            lastVariantError = FormatExceptionDetail(exTry);
                        }
                    }
                }

                throw new PortalException(PortalErrorCode.OpennessError,
                    lastVariantError ?? $"AddDevice failed: no device created for OrderNumber={orderNumber} Version={version}");
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PortalException(PortalErrorCode.OpennessError, FormatExceptionDetail(ex), null, ex);
            }
        }

        public (Device? Device, string? MlfbUsed, string? VersionUsed, List<string> Attempts, string? Error) AddDeviceWithFallback(
            string preferredMlfb,
            string preferredVersion,
            string deviceName,
            string family)
        {
            var attempts = new List<string>();
            string? lastError = null;

            // Minimal built-in list (keep small; user can pass preferred MLFB first)
            var known = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["S7-1500"] = new List<string>
                {
                    "6ES7513-1AM03-0AB0",
                    "6ES7516-3AN03-0AB0",
                    "6ES7515-2AM02-0AB0",
                    "6ES7513-1AL03-0AB0",
                    "6ES7512-1AK02-0AB0",
                },
                ["WinCCUnifiedPC"] = new List<string>
                {
                    // Unified PC Runtime (actual availability depends on installed packages/HSP)
                    "6AV2123-3GB32-0AW0",
                    "6AV2154-0BS01-0AA0",
                    "6AV2154-0BP01-0AA0",
                },
                ["S7-1200"] = new List<string>
                {
                    // CPU 1211C AC/DC/Rly
                    "6ES7211-1BE40-0XB0",
                }
            };

            var mlfbs = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredMlfb)) mlfbs.Add(preferredMlfb);
            if (known.TryGetValue(family ?? "", out var list)) mlfbs.AddRange(list);
            mlfbs = mlfbs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            var versions = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferredVersion)) versions.Add(preferredVersion);
            if (string.Equals(family, "S7-1500", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("V3.0");
                versions.Add("V3.1");
                versions.Add("V2.9");
            }
            if (string.Equals(family, "WinCCUnifiedPC", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("20.0.0.0");
                versions.Add("21.0.0.0");
            }
            if (string.Equals(family, "S7-1200", StringComparison.OrdinalIgnoreCase))
            {
                versions.Add("V4.7");
                versions.Add("4.7");
                versions.Add("V4.6");
                versions.Add("4.6");
                versions.Add("V4.5");
                versions.Add("4.5");
            }
            versions.Add("21.0.0.0");
            versions.Add("V21.0.0.0");
            versions.Add("");
            versions = versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var mlfb in mlfbs)
            {
                foreach (var ver in versions)
                {
                    try
                    {
                        var d = AddDevice(mlfb, ver, deviceName);
                        attempts.Add($"{mlfb} {ver} -> OK");
                        return (d, mlfb, ver, attempts, null);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                        attempts.Add($"{mlfb} {ver} -> FAIL: {ex.Message}");
                    }
                }
            }

            return (null, null, null, attempts, lastError ?? "All attempts failed");
        }

        public List<GsdDeviceCandidate> SearchInstalledGsdDevices(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<GsdDeviceCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new PortalException(PortalErrorCode.InvalidParams, "Keyword is empty");

            try
            {
                var catalog = _portal == null ? null : TryGetPropertyValue(_portal, "HardwareCatalog");
                if (catalog != null)
                {
                    foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                    {
                        foreach (var entry in FindHardwareCatalogEntries(catalog, filter))
                        {
                            var candidate = CatalogEntryToCandidate(entry, normalizedKeyword);
                            if (candidate == null) continue;

                            var key = candidate.TypeIdentifierNormalized
                                      ?? candidate.TypeIdentifier
                                      ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                            if (!seen.Add(key)) continue;
                            results.Add(candidate);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "HardwareCatalog search failed during GSD device search");
            }

            try
            {
                foreach (var candidate in SearchGsdmlFiles(normalizedKeyword))
                {
                    var key = $"GSDML|{candidate.GsdmlPath}|{candidate.DapId}|{candidate.DapName}";
                    if (!seen.Add(key)) continue;
                    results.Add(candidate);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "GSDML scan failed during GSD device search");
            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, normalizedKeyword, null);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.Source)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public List<HardwareCatalogCandidate> SearchHardwareCatalog(string keyword, int limit = 50)
        {
            var normalizedKeyword = (keyword ?? string.Empty).Trim();
            var results = new List<HardwareCatalogCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(normalizedKeyword))
                throw new PortalException(PortalErrorCode.InvalidParams, "Keyword is empty");

            try
            {
                var catalog = _portal == null ? null : TryGetPropertyValue(_portal, "HardwareCatalog");
                if (catalog == null)
                    throw new PortalException(PortalErrorCode.InvalidState, "TIA Portal HardwareCatalog is not available. Connect to TIA Portal first.");

                foreach (var filter in BuildHardwareCatalogFilters(normalizedKeyword))
                {
                    foreach (var entry in FindHardwareCatalogEntries(catalog, filter))
                    {
                        var candidate = CatalogEntryToHardwareCandidate(entry, normalizedKeyword);
                        if (candidate == null) continue;

                        var key = candidate.TypeIdentifierNormalized
                                  ?? candidate.TypeIdentifier
                                  ?? $"{candidate.ArticleNumber}|{candidate.Description}|{candidate.CatalogPath}";
                        if (!seen.Add(key)) continue;
                        results.Add(candidate);
                    }
                }
            }
            catch (PortalException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "HardwareCatalog search failed");
            }

            return results
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogCandidate(c, normalizedKeyword);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ThenBy(c => c.ArticleNumber)
                .ThenBy(c => c.Description)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        public (Device? Device, HardwareCatalogCandidate? Candidate, List<HardwareCatalogCandidate> Candidates, List<string> Attempts, string? Error)
            AddHardwareCatalogDeviceWithProbe(string keyword, string deviceName, string preferredText = "")
        {
            var attempts = new List<string>();
            var candidates = SearchHardwareCatalog(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreHardwareCatalogCandidate(c, keyword, preferredText);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No hardware catalog candidates found for '{keyword}'");
            }

            if (IsProjectNull())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = _project as Project;
            if (project == null)
            {
                return (null, null, candidates, attempts, "Current project is not a local Project instance.");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                var typeIdentifier = candidate.TypeIdentifier!.Trim();
                try
                {
                    var itemName = MakeEngineeringName(deviceName);
                    var dev = project.Devices.CreateWithItem(typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = FormatExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => c.Insertable == true))
                lastError = "Hardware catalog returned no insertable TypeIdentifier candidates.";

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        public (Device? Device, GsdDeviceCandidate? Candidate, List<GsdDeviceCandidate> Candidates, List<string> Attempts, string? Error)
            AddGsdDeviceWithProbe(string keyword, string deviceName, string preferredDap = "")
        {
            var attempts = new List<string>();
            var candidates = SearchInstalledGsdDevices(keyword, 100)
                .Select(c =>
                {
                    c.Score = ScoreGsdCandidate(c, keyword, preferredDap);
                    return c;
                })
                .OrderByDescending(c => c.Score ?? 0)
                .ToList();

            if (candidates.Count == 0)
            {
                return (null, null, candidates, attempts, $"No installed GSD/catalog candidates found for '{keyword}'");
            }

            if (IsProjectNull())
            {
                return (null, null, candidates, attempts, "No project is open. Open or attach to a project before adding the device.");
            }

            var project = _project as Project;
            if (project == null)
            {
                return (null, null, candidates, attempts, "Current project is not a local Project instance.");
            }

            string? lastError = null;
            foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                var typeIdentifier = candidate.TypeIdentifier!.Trim();
                try
                {
                    var itemName = MakeEngineeringName(deviceName);
                    var dev = project.Devices.CreateWithItem(typeIdentifier, itemName, deviceName);
                    if (dev is Device d)
                    {
                        attempts.Add($"{typeIdentifier} -> OK");
                        return (d, candidate, candidates, attempts, null);
                    }

                    lastError = "CreateWithItem returned null";
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
                catch (Exception ex)
                {
                    lastError = FormatExceptionDetail(ex);
                    attempts.Add($"{typeIdentifier} -> FAIL: {lastError}");
                }
            }

            if (!candidates.Any(c => !string.IsNullOrWhiteSpace(c.TypeIdentifier)))
            {
                lastError = "Only GSDML file metadata was found; HardwareCatalog did not return an insertable TypeIdentifier.";
            }

            return (null, null, candidates, attempts, lastError ?? "All insert attempts failed");
        }

        private static IEnumerable<string> BuildHardwareCatalogFilters(string keyword)
        {
            var raw = (keyword ?? string.Empty).Trim();
            var tokens = raw.Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            return new[] { raw }
                .Concat(tokens.Where(t => t.Length >= 3))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x));
        }

        private static IEnumerable<object> FindHardwareCatalogEntries(object catalog, string filter)
        {
            var find = catalog.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "Find"
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (find == null) yield break;

            var value = find.Invoke(catalog, new object[] { filter });
            if (value is not IEnumerable enumerable) yield break;

            foreach (var item in enumerable)
            {
                if (item != null) yield return item;
            }
        }

        private static GsdDeviceCandidate? CatalogEntryToCandidate(object entry, string keyword)
        {
            var c = new GsdDeviceCandidate
            {
                Source = "HardwareCatalog",
                Keyword = keyword,
                ArticleNumber = TryGetPropertyValue(entry, "ArticleNumber")?.ToString(),
                CatalogPath = TryGetPropertyValue(entry, "CatalogPath")?.ToString(),
                Description = TryGetPropertyValue(entry, "Description")?.ToString(),
                TypeIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString(),
                TypeIdentifierNormalized = TryGetPropertyValue(entry, "TypeIdentifierNormalized")?.ToString(),
                TypeName = TryGetPropertyValue(entry, "TypeName")?.ToString(),
                Version = TryGetPropertyValue(entry, "Version")?.ToString()
            };

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static HardwareCatalogCandidate? CatalogEntryToHardwareCandidate(object entry, string keyword)
        {
            var c = new HardwareCatalogCandidate
            {
                Source = "HardwareCatalog",
                Keyword = keyword,
                ArticleNumber = TryGetPropertyValue(entry, "ArticleNumber")?.ToString(),
                CatalogPath = TryGetPropertyValue(entry, "CatalogPath")?.ToString(),
                Description = TryGetPropertyValue(entry, "Description")?.ToString(),
                TypeIdentifier = TryGetPropertyValue(entry, "TypeIdentifier")?.ToString(),
                TypeIdentifierNormalized = TryGetPropertyValue(entry, "TypeIdentifierNormalized")?.ToString(),
                TypeName = TryGetPropertyValue(entry, "TypeName")?.ToString(),
                Version = TryGetPropertyValue(entry, "Version")?.ToString()
            };
            c.Insertable = !string.IsNullOrWhiteSpace(c.TypeIdentifier);

            var haystack = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            if (string.IsNullOrWhiteSpace(haystack)) return null;
            if (!ContainsAllKeywordTokens(haystack, keyword) && !ContainsAnyKeywordToken(haystack, keyword)) return null;
            return c;
        }

        private static IEnumerable<GsdDeviceCandidate> SearchGsdmlFiles(string keyword)
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Siemens",
                "Automation",
                $"Portal V{Engineering.TiaMajorVersion}",
                "data",
                "xdd",
                "gsd");

            if (!Directory.Exists(root)) yield break;

            foreach (var path in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileNameWithoutExtension(path);
                string textPrefix = fileName;
                if (!ContainsAnyKeywordToken(textPrefix, keyword))
                {
                    try
                    {
                        var raw = File.ReadAllText(path, Encoding.UTF8);
                        if (!ContainsAnyKeywordToken(raw, keyword)) continue;
                    }
                    catch
                    {
                        continue;
                    }
                }

                XDocument doc;
                try { doc = XDocument.Load(path); }
                catch { continue; }

                var vendor = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProfileHeader")?.Attribute("VendorName")?.Value;
                var family = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Family");
                var mainFamily = family?.Attribute("MainFamily")?.Value;
                var productFamily = family?.Attribute("ProductFamily")?.Value;
                var orderNumber = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "OrderNumber")?.Attribute("Value")?.Value;

                foreach (var dap in doc.Descendants().Where(e => e.Name.LocalName == "DeviceAccessPointItem"))
                {
                    var dapId = dap.Attribute("ID")?.Value;
                    var dapName = dap.Attribute("DNS_CompatibleName")?.Value ?? dap.Attribute("Name")?.Value;
                    var textId = dap.Attribute("TextId")?.Value;
                    var infoText = FindGsdText(doc, textId) ?? dapName ?? fileName;
                    var haystack = string.Join(" ", new[] { vendor, mainFamily, productFamily, orderNumber, dapId, dapName, infoText, fileName });
                    if (!ContainsAnyKeywordToken(haystack, keyword)) continue;

                    yield return new GsdDeviceCandidate
                    {
                        Source = "GSDML",
                        Keyword = keyword,
                        Vendor = vendor,
                        MainFamily = mainFamily,
                        ProductFamily = productFamily,
                        DapId = dapId,
                        DapName = dapName,
                        ArticleNumber = orderNumber,
                        Description = infoText,
                        GsdmlPath = path,
                        Score = ScoreGsdCandidateText(haystack, keyword, null)
                    };
                }
            }
        }

        private static string? FindGsdText(XDocument doc, string? textId)
        {
            if (string.IsNullOrWhiteSpace(textId)) return null;
            var text = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Text" && string.Equals(e.Attribute("TextId")?.Value, textId, StringComparison.OrdinalIgnoreCase));
            return text?.Attribute("Value")?.Value;
        }

        private static int ScoreGsdCandidate(GsdDeviceCandidate c, string keyword, string? preferredDap)
        {
            var text = string.Join(" ", new[]
            {
                c.Vendor, c.ProductFamily, c.MainFamily, c.DapId, c.DapName, c.ArticleNumber, c.CatalogPath,
                c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version, c.GsdmlPath
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return ScoreGsdCandidateText(text, keyword, preferredDap)
                   + (string.Equals(c.Source, "HardwareCatalog", StringComparison.OrdinalIgnoreCase) ? 30 : 0)
                   + (!string.IsNullOrWhiteSpace(c.TypeIdentifier) ? 50 : 0);
        }

        private static int ScoreHardwareCatalogCandidate(HardwareCatalogCandidate c, string keyword, string? preferredText = null)
        {
            var text = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            var preferred = preferredText ?? string.Empty;
            var score = ScoreGsdCandidateText(text, keyword, null)
                   + (string.IsNullOrWhiteSpace(preferred) ? 0 : ScoreGsdCandidateText(text, preferred, null))
                   + (c.Insertable == true ? 50 : 0)
                   + (!string.IsNullOrWhiteSpace(c.ArticleNumber) ? 10 : 0);

            if (!string.IsNullOrWhiteSpace(preferred))
            {
                var normalizedPreferred = NormalizeCatalogSearchText(preferred);
                var normalizedArticle = NormalizeCatalogSearchText(c.ArticleNumber ?? "");
                var normalizedTypeIdentifier = NormalizeCatalogSearchText(c.TypeIdentifier ?? "");
                if (!string.IsNullOrWhiteSpace(normalizedArticle) && normalizedPreferred.Contains(normalizedArticle))
                    score += 100;
                if (!string.IsNullOrWhiteSpace(normalizedTypeIdentifier) && normalizedTypeIdentifier.Contains(normalizedPreferred))
                    score += 50;
            }

            var asksSiplus = ContainsAnyKeywordToken("SIPLUS", keyword + " " + preferred);
            var asksPortrait = ContainsAnyKeywordToken("Portrait 立式", keyword + " " + preferred);
            if (!asksSiplus && ContainsAnyKeywordToken(text, "SIPLUS"))
                score -= 40;
            if (!asksPortrait && ContainsAnyKeywordToken(text, "Portrait 立式"))
                score -= 25;

            return score;
        }

        private static string NormalizeCatalogSearchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(char.ToUpperInvariant(ch));
            }
            return sb.ToString();
        }

        private static int ScoreHardwareCatalogCandidateLegacy(HardwareCatalogCandidate c, string keyword, string? preferredText = null)
        {
            var text = string.Join(" ", new[]
            {
                c.ArticleNumber, c.CatalogPath, c.Description, c.TypeIdentifier, c.TypeIdentifierNormalized, c.TypeName, c.Version
            }.Where(x => !string.IsNullOrWhiteSpace(x)));

            return ScoreGsdCandidateText(text, keyword, null)
                   + (string.IsNullOrWhiteSpace(preferredText) ? 0 : ScoreGsdCandidateText(text, preferredText!, null))
                   + (c.Insertable == true ? 50 : 0)
                   + (!string.IsNullOrWhiteSpace(c.ArticleNumber) ? 10 : 0);
        }

        private static int ScoreGsdCandidateText(string text, string keyword, string? preferredDap)
        {
            var score = 0;
            var haystack = text ?? string.Empty;
            foreach (var token in SplitSearchTokens(keyword))
            {
                if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) score += token.Length >= 5 ? 20 : 10;
            }

            if (!string.IsNullOrWhiteSpace(preferredDap) &&
                haystack.IndexOf(preferredDap!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 40;
            }

            if (haystack.IndexOf(keyword ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0) score += 50;
            return score;
        }

        private static bool ContainsAnyKeywordToken(string text, string keyword)
        {
            return SplitSearchTokens(keyword).Any(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool ContainsAllKeywordTokens(string text, string keyword)
        {
            var tokens = SplitSearchTokens(keyword).ToList();
            return tokens.Count > 0 && tokens.All(t => (text ?? string.Empty).IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static IEnumerable<string> SplitSearchTokens(string keyword)
        {
            return (keyword ?? string.Empty)
                .Split(new[] { ' ', '\t', ',', ';', '/', '\\', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length >= 2)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static string MakeEngineeringName(string value)
        {
            var name = Regex.Replace(value ?? "Device_1", @"[^\w]", "_");
            if (string.IsNullOrWhiteSpace(name)) name = "Device_1";
            if (char.IsDigit(name[0])) name = "Device_" + name;
            return name;
        }

        private static string NormalizeOrderNumber(string s)
        {
            return (s ?? string.Empty).Replace(" ", "").Trim();
        }

        private static string TryFormatMlfbWithSpaces(string value)
        {
            var n = NormalizeOrderNumber(value);
            if (n.Length < 8) return n;

            if (n.StartsWith("6ES7", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, 4) + " " + n.Substring(4);

            if (n.StartsWith("6AV2", StringComparison.OrdinalIgnoreCase))
                return n.Substring(0, 4) + " " + n.Substring(4);

            return n;
        }
        // NOTE: Demo orchestration helpers removed.
        // In V21, the stable path is: generate/import standard block XML -> ImportBlock/ImportBlocksFromDirectory -> CompileSoftware.

        public DeviceItem? GetDeviceItem(string deviceItemPath)
        {
            _logger?.LogInformation($"Getting device item by path: {deviceItemPath}");

            if (IsProjectNull())
            {
                return null;
            }

            // Retrieve the device by its path
            return GetDeviceItemByPath(deviceItemPath);

        }

        public string GetDeviceItemTree(string deviceItemPath, int maxDepth = 4)
        {
            _logger?.LogInformation($"Getting device item tree by path: {deviceItemPath}, depth={maxDepth}");

            if (IsProjectNull())
            {
                return string.Empty;
            }

            var root = GetDeviceItemByPath(deviceItemPath);
            if (root == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"{root.Name} [DeviceItem]");
            BuildDeviceItemTree(sb, root, new List<bool>(), 0, Math.Max(0, maxDepth));
            return sb.ToString();
        }

        public List<ModelContextProtocol.NetworkAttribute>? GetDeviceItemNetworkInfo(string deviceItemPath)
        {
            if (IsProjectNull()) return null;
            var di = GetDeviceItemByPath(deviceItemPath);
            if (di == null) return null;

            // Heuristic: filter attribute names that likely contain network addressing / interface identity.
            var keys = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address"
            };

            var list = new List<ModelContextProtocol.NetworkAttribute>();
            try
            {
                foreach (var info in di.GetAttributeInfos())
                {
                    var n = info.Name ?? "";
                    var lower = n.ToLowerInvariant();
                    if (!keys.Any(k => lower.Contains(k))) continue;

                    object vObj;
                    try { vObj = di.GetAttribute(info.Name); }
                    catch { continue; }

                    var v = vObj?.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;

                    list.Add(new ModelContextProtocol.NetworkAttribute
                    {
                        Name = info.Name,
                        Value = v,
                        DataType = TryGetPropertyValue(info, "DataType", "Type")?.ToString(),
                        IsWritable = IsAttributeWritable(info)
                    });
                }
            }
            catch
            {
                // best-effort
            }

            return list;
        }

        // Read-only: export a device's hardware configuration to an AutomationML (CAx) file.
        // The .aml contains the configured IP address, subnet/mask, PN device name and topology -
        // information not surfaced by GetDeviceItemNetworkInfo (which omits the node Address).
        public ModelContextProtocol.CaxExportResult ExportDeviceConfigurationAml(string devicePath, string exportPath)
        {
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var device = Guard.RequireNotNull(GetDevice(devicePath), "Device", devicePath);

            // Resolve final file path: treat a directory or extension-less path as a folder, else use as-is.
            string filePath;
            if (Directory.Exists(exportPath) || string.IsNullOrEmpty(Path.GetExtension(exportPath)))
            {
                var safeName = string.Concat((device.Name ?? "device").Split(Path.GetInvalidFileNameChars()));
                filePath = Path.Combine(exportPath, $"{safeName}.aml");
            }
            else
            {
                filePath = exportPath;
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(filePath)) File.Delete(filePath);

            var cax = _project!.GetService<CaxProvider>();
            if (cax == null)
            {
                throw new PortalException(PortalErrorCode.InvalidState, "CAx/AML export service is not available for this project");
            }

            var result = cax.Export(device, new FileInfo(filePath));
            var messageLines = FlattenTransferMessages(result?.Messages);
            var state = result?.State.ToString() ?? "Unknown";
            var ok = result != null && result.State != TransferResultState.Error;

            if (!ok && !File.Exists(filePath))
            {
                throw new PortalException(PortalErrorCode.ExportFailed,
                    $"CAx/AML export failed (state={state}): " + (messageLines.Count > 0 ? string.Join(" | ", messageLines) : "no detail returned"));
            }

            return new ModelContextProtocol.CaxExportResult
            {
                DeviceName = device.Name ?? devicePath,
                FilePath = filePath,
                Success = ok,
                State = state,
                ErrorCount = result?.ErrorCount ?? 0,
                WarningCount = result?.WarningCount ?? 0,
                Messages = messageLines
            };
        }

        private static List<string> FlattenTransferMessages(IEnumerable<TransferResultMessage>? messages)
        {
            var lines = new List<string>();
            void Walk(IEnumerable<TransferResultMessage>? ms)
            {
                if (ms == null) return;
                foreach (var m in ms)
                {
                    if (m == null) continue;
                    lines.Add($"[{m.State}] {m.Message}");
                    try { Walk(m.Messages); } catch { }
                }
            }
            Walk(messages);
            return lines;
        }

        public ResponseMessage SetDeviceItemAttribute(string deviceItemPath, string attributeName, string value)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["deviceItemPath"] = deviceItemPath,
                ["attributeName"] = attributeName
            };

            try
            {
                if (IsProjectNull())
                {
                    meta["error"] = "Project is null";
                    return new ResponseMessage { Message = "Project is null", Meta = meta };
                }

                var di = GetDeviceItemByPath(deviceItemPath);
                if (di == null)
                {
                    meta["error"] = "Device item not found";
                    return new ResponseMessage { Message = "Device item not found", Meta = meta };
                }

                var info = di.GetAttributeInfos().FirstOrDefault(x => string.Equals(x.Name, attributeName, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    meta["error"] = "Attribute not found";
                    meta["availableAttributes"] = ToJsonArray(di.GetAttributeInfos().Select(x => x.Name ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)));
                    return new ResponseMessage { Message = "Attribute not found", Meta = meta };
                }

                object? oldValue = null;
                try { oldValue = di.GetAttribute(info.Name); } catch { }
                meta["oldValue"] = oldValue?.ToString() ?? string.Empty;
                meta["attributeDataType"] = TryGetPropertyValue(info, "DataType", "Type")?.ToString() ?? string.Empty;
                meta["attributeWritable"] = IsAttributeWritable(info);

                object typedValue = CoerceAttributeValue(value, oldValue, info);
                di.SetAttribute(info.Name, typedValue);

                object? newValue = null;
                try { newValue = di.GetAttribute(info.Name); } catch { }
                meta["newValue"] = newValue?.ToString() ?? string.Empty;
                meta["success"] = true;
                return new ResponseMessage { Message = $"Device item attribute '{attributeName}' set", Meta = meta };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                return new ResponseMessage { Message = $"Failed setting device item attribute '{attributeName}'", Meta = meta };
            }
        }

        // ----- Network topology / IP read-out (Openness as the source of truth) -----
        // Replaces the old "probe S7 / hand-parse exported AML" workaround for finding a PLC's IP.

        private static string? ReadNodeAddress(object node)
            => TryGetPropertyValue(node, "Address")?.ToString()
               ?? TryGetPropertyValue(node, "IpAddress")?.ToString()
               ?? TryGetPropertyValue(node, "IPAddress")?.ToString()
               ?? TryGetEngineeringAttribute(node, "Address")?.ToString()
               ?? TryGetEngineeringAttribute(node, "IpAddress")?.ToString();

        private JsonArray BuildDeviceNodesJson(Device device)
        {
            var arr = new JsonArray();
            foreach (var root in device.DeviceItems)
            {
                foreach (var n in FindNetworkNodes(root))
                {
                    var subnet = TryGetPropertyValue(n.Node, "ConnectedSubnet");
                    arr.Add(new JsonObject
                    {
                        ["nodeName"] = TryGetName(n.Node) ?? "<unnamed>",
                        ["address"] = ReadNodeAddress(n.Node) ?? string.Empty,
                        ["nodeType"] = TryGetPropertyValue(n.Node, "NodeType")?.ToString() ?? string.Empty,
                        ["connectedSubnet"] = TryGetName(subnet) ?? subnet?.ToString() ?? string.Empty,
                        ["interfacePath"] = n.Path,
                        ["isIndustrialEthernet"] = IsIndustrialEthernetNode(n.Node)
                    });
                }
            }
            return arr;
        }

        private static string FirstAddress(JsonArray nodes, bool ieOnly)
        {
            foreach (var node in nodes)
            {
                if (node is not JsonObject jo) continue;
                if (ieOnly && !(jo["isIndustrialEthernet"]?.GetValue<bool>() ?? false)) continue;
                var a = jo["address"]?.ToString();
                if (!string.IsNullOrWhiteSpace(a)) return a!;
            }
            return string.Empty;
        }

        // Read a device's configured IP straight from Openness (PROFINET node Address), no S7 probe, no AML export.
        public JsonObject GetDeviceIpAddress(string devicePath)
        {
            if (IsProjectNull()) return new JsonObject { ["found"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new JsonObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var nodes = BuildDeviceNodesJson(device);
            var primary = FirstAddress(nodes, ieOnly: true);
            if (primary.Length == 0) primary = FirstAddress(nodes, ieOnly: false);

            return new JsonObject
            {
                ["found"] = nodes.Count > 0,
                ["device"] = device.Name ?? devicePath,
                ["ipAddress"] = primary,
                ["nodeCount"] = nodes.Count,
                ["nodes"] = nodes
            };
        }

        // One-shot project topology: every device with its network nodes (IP / subnet / type).
        public JsonObject GetProjectTopology()
        {
            if (IsProjectNull()) return new JsonObject { ["message"] = "No project open." };
            var devices = new JsonArray();
            foreach (Device device in _project!.Devices)
            {
                devices.Add(new JsonObject
                {
                    ["device"] = device.Name ?? string.Empty,
                    ["nodes"] = BuildDeviceNodesJson(device)
                });
            }
            return new JsonObject
            {
                ["projectName"] = _project!.Name ?? string.Empty,
                ["deviceCount"] = devices.Count,
                ["devices"] = devices,
                ["note"] = "Devices placed inside device groups are not enumerated here (mirrors Project.Devices)."
            };
        }

        // ----- PUT/GET access (dependency check for S7 reads) -----

        private static string NormalizeAttrName(string? n)
            => new string((n ?? string.Empty).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // Locate the CPU DeviceItem + attribute controlling "Permit access with PUT/GET communication".
        // The exact Openness attribute name varies by CPU/firmware, so match any attribute whose
        // normalized (lowercase, alphanumeric-only) name contains "putget".
        // NOTE: S7-1200 (e.g. 1211C V4.6) does not expose this attribute via Openness at all — neither
        // enumerated by GetAttributeInfos() nor gettable by name — so this returns (null, null) there.
        // Use DumpDeviceAttributes to see exactly what a given CPU exposes.
        private (DeviceItem? item, string? attrName) FindPutGetAttribute(Device device)
        {
            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in TraverseDeviceItems(root, root.Name))
                {
                    var it = tup.Item1;
                    string[] names;
                    try { names = it.GetAttributeInfos().Select(x => x.Name ?? string.Empty).ToArray(); }
                    catch { continue; }
                    var match = names.FirstOrDefault(n => NormalizeAttrName(n).Contains("putget"));
                    if (!string.IsNullOrEmpty(match)) return (it, match);
                }
            }
            return (null, null);
        }

        private static bool AttrValueIsEnabled(object? v)
            => v is bool b ? b : (v?.ToString()?.Equals("True", StringComparison.OrdinalIgnoreCase) ?? false);

        // Read whether a CPU permits remote PUT/GET access (precondition for ReadPlcLiveValuesS7 on DB areas).
        public JsonObject GetPutGetAccess(string devicePath)
        {
            if (IsProjectNull()) return new JsonObject { ["found"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new JsonObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var (item, attrName) = FindPutGetAttribute(device);
            if (item == null || attrName == null)
                return new JsonObject
                {
                    ["found"] = false,
                    ["device"] = device.Name,
                    ["message"] = "PUT/GET access is not exposed as an Openness attribute on this CPU/firmware " +
                                  "(confirmed e.g. on S7-1200 1211C V4.6). Check/set it manually in TIA: " +
                                  "CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication'. " +
                                  "Note: M/I/Q S7 reads still work when the channel is permitted; a successful M-area read implies PUT/GET is enabled."
                };

            object? val = null;
            try { val = item.GetAttribute(attrName); } catch { }
            return new JsonObject
            {
                ["found"] = true,
                ["device"] = device.Name,
                ["deviceItem"] = item.Name,
                ["attributeName"] = attrName,
                ["enabled"] = AttrValueIsEnabled(val),
                ["rawValue"] = val?.ToString() ?? string.Empty
            };
        }

        // Enable/disable remote PUT/GET access. NOTE: hardware-config change — a hardware DownloadToPlc is
        // required for it to take effect on the live CPU.
        public JsonObject SetPutGetAccess(string devicePath, bool enable)
        {
            if (IsProjectNull()) return new JsonObject { ["ok"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new JsonObject { ["ok"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var (item, attrName) = FindPutGetAttribute(device);
            if (item == null || attrName == null)
                return new JsonObject
                {
                    ["ok"] = false,
                    ["device"] = device.Name,
                    ["message"] = "PUT/GET access cannot be set via Openness on this CPU/firmware (not an exposed attribute; " +
                                  "confirmed e.g. on S7-1200 1211C V4.6). Set it manually in TIA: " +
                                  "CPU > Protection & Security > Connection mechanisms > 'Permit access with PUT/GET communication', then download hardware."
                };

            object? before = null; try { before = item.GetAttribute(attrName); } catch { }
            try { item.SetAttribute(attrName, enable); }
            catch (Exception ex)
            {
                return new JsonObject { ["ok"] = false, ["device"] = device.Name, ["attributeName"] = attrName, ["message"] = $"SetAttribute failed: {ex.Message}" };
            }
            object? after = null; try { after = item.GetAttribute(attrName); } catch { }

            return new JsonObject
            {
                ["ok"] = AttrValueIsEnabled(after) == enable,
                ["device"] = device.Name,
                ["deviceItem"] = item.Name,
                ["attributeName"] = attrName,
                ["before"] = before?.ToString() ?? string.Empty,
                ["after"] = after?.ToString() ?? string.Empty,
                ["note"] = "Hardware-config change — run DownloadToPlc (hardware) for it to take effect on the CPU."
            };
        }

        // Read-only inventory of every Openness attribute exposed on a device's DeviceItems (CPU, modules,
        // interfaces, ports...). For each attribute: name, access mode (read-only vs read/write), current value,
        // value type. Use this ONCE to learn what a given CPU/firmware actually exposes, then drive subsequent
        // hardware reads/writes from that ground truth instead of guessing attribute names.
        // NOTE: GetAttributeInfos() does not enumerate EVERY gettable attribute on all CPUs (e.g. the PUT/GET
        // flag on some S7-1200), so absence here is "not enumerated", not a hard guarantee of "no interface".
        public JsonObject DumpDeviceAttributes(string devicePath, string? nameFilter = null, int maxItems = 500)
        {
            if (IsProjectNull()) return new JsonObject { ["found"] = false, ["message"] = "No project open." };
            var device = GetDevice(devicePath);
            if (device == null) return new JsonObject { ["found"] = false, ["device"] = devicePath, ["message"] = $"Device not found: '{devicePath}'." };

            var filter = NormalizeAttrName(nameFilter);
            var hasFilter = !string.IsNullOrEmpty(filter);

            var itemsArr = new JsonArray();
            int itemCount = 0;
            int totalAttrs = 0, writableAttrs = 0;

            foreach (var root in device.DeviceItems)
            {
                foreach (var tup in TraverseDeviceItems(root, root.Name))
                {
                    if (itemCount >= maxItems) break;
                    var it = tup.Item1;

                    System.Collections.Generic.IList<EngineeringAttributeInfo>? infos = null;
                    try { infos = it.GetAttributeInfos(); } catch { }
                    if (infos == null || infos.Count == 0) continue;

                    var attrsArr = new JsonArray();
                    foreach (var info in infos)
                    {
                        var name = info?.Name ?? string.Empty;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (hasFilter && !NormalizeAttrName(name).Contains(filter)) continue;

                        var access = TryGetAttributeInfoAccess(info!);
                        object? val = null; bool readErr = false;
                        try { val = it.GetAttribute(name); } catch { readErr = true; }

                        var isWritable = access?.IndexOf("write", StringComparison.OrdinalIgnoreCase) >= 0
                                         || access?.IndexOf("readwrite", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (isWritable) writableAttrs++;
                        totalAttrs++;

                        attrsArr.Add(new JsonObject
                        {
                            ["name"] = name,
                            ["access"] = access ?? string.Empty,
                            ["value"] = val?.ToString() ?? (readErr ? "<read-error>" : string.Empty),
                            ["valueType"] = val?.GetType().Name ?? string.Empty
                        });
                    }

                    if (attrsArr.Count == 0) continue;
                    itemsArr.Add(new JsonObject
                    {
                        ["item"] = it.Name,
                        ["path"] = tup.Item2,
                        ["attributeCount"] = attrsArr.Count,
                        ["attributes"] = attrsArr
                    });
                    itemCount++;
                }
            }

            return new JsonObject
            {
                ["found"] = true,
                ["device"] = device.Name,
                ["nameFilter"] = nameFilter ?? string.Empty,
                ["itemCount"] = itemsArr.Count,
                ["totalAttributes"] = totalAttrs,
                ["writableAttributes"] = writableAttrs,
                ["items"] = itemsArr
            };
        }

        // EngineeringAttributeInfo exposes its access mode under SDK-version-dependent property names;
        // reflect defensively so we don't hard-depend on one.
        private static string? TryGetAttributeInfoAccess(object info)
        {
            foreach (var pn in new[] { "AccessMode", "Access", "ReadOnly", "IsReadOnly" })
            {
                try
                {
                    var p = info.GetType().GetProperty(pn);
                    if (p != null)
                    {
                        var v = p.GetValue(info);
                        if (v != null) return pn + "=" + v;
                    }
                }
                catch { }
            }
            return null;
        }

        public string ProbeConnectDeviceNodesToSubnet(string plcRootPath, string hmiRootPath, string subnetName)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNodes = FindNetworkNodes(plcRoot).ToList();
            var hmiNodes = FindNetworkNodes(hmiRoot).ToList();
            sb.AppendLine("PLC item service scan:");
            foreach (var line in DescribeNetworkServiceScan(plcRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("HMI item service scan:");
            foreach (var line in DescribeNetworkServiceScan(hmiRoot)) sb.AppendLine("  " + line);
            sb.AppendLine("PLC nodes:");
            foreach (var n in plcNodes) sb.AppendLine("  " + FormatNodeInfo(n));
            sb.AppendLine("HMI nodes:");
            foreach (var n in hmiNodes) sb.AppendLine("  " + FormatNodeInfo(n));

            var plcNode = plcNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = hmiNodes.FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return sb.ToString();

            object? subnet = null;
            try
            {
                subnet = TryGetPropertyValue(plcNode.Node, "ConnectedSubnet");
                if (subnet == null)
                {
                    var create = plcNode.Node.GetType().GetMethod("CreateAndConnectToSubnet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    subnet = create?.Invoke(plcNode.Node, new object[] { subnetName });
                    sb.AppendLine("PLC CreateAndConnectToSubnet: " + (subnet == null ? "NULL" : "OK " + TryGetName(subnet)));
                }
                else
                {
                    sb.AppendLine("PLC already connected to subnet: " + (TryGetName(subnet) ?? subnet.ToString()));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("PLC subnet create/connect error: " + FormatExceptionDetail(ex));
            }

            if (subnet != null)
            {
                try
                {
                    var connect = hmiNode.Node.GetType().GetMethod("ConnectToSubnet", BindingFlags.Public | BindingFlags.Instance);
                    connect?.Invoke(hmiNode.Node, new object[] { subnet });
                    sb.AppendLine("HMI ConnectToSubnet: OK");
                }
                catch (Exception ex)
                {
                    sb.AppendLine("HMI ConnectToSubnet error: " + FormatExceptionDetail(ex));
                }
            }

            sb.AppendLine("Readback PLC node: " + FormatNodeInfo(plcNode));
            sb.AppendLine("Readback HMI node: " + FormatNodeInfo(hmiNode));

            try
            {
                var sw = GetSoftwareContainer("HMI_RT_1")?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("HMI Connections after subnet:");
                if (connections is IEnumerable en)
                {
                    var any = false;
                    foreach (var c in en)
                    {
                        any = true;
                        sb.AppendLine("  " + (TryGetName(c) ?? c?.ToString() ?? "<null>"));
                    }
                    if (!any) sb.AppendLine("  <empty>");
                }
                else
                {
                    sb.AppendLine("  <not found>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("HMI connection readback error: " + FormatExceptionDetail(ex));
            }

            return sb.ToString();
        }

        public ResponseMessage EnsureSubnet(string anchorDeviceItemPath, string subnetType, string subnetName)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["anchorDeviceItemPath"] = anchorDeviceItemPath,
                ["subnetType"] = subnetType,
                ["subnetName"] = subnetName
            };

            try
            {
                if (IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                if (!IsSupportedProfinetSubnetType(subnetType))
                {
                    meta["error"] = "Only IndustrialEthernet/PROFINET subnet types are supported by this safe primitive.";
                    return new ResponseMessage { Message = "Unsupported subnet type", Meta = meta };
                }

                var anchorRoot = GetDeviceItemByPath(anchorDeviceItemPath);
                if (anchorRoot == null)
                {
                    meta["error"] = "Anchor device item not found";
                    return new ResponseMessage { Message = "Anchor device item not found", Meta = meta };
                }

                var existing = FindConnectedSubnetByName(subnetName);
                if (existing != null)
                {
                    meta["success"] = true;
                    meta["created"] = false;
                    meta["readback"] = BuildSubnetReadbackJson(subnetName);
                    return new ResponseMessage { Message = "Subnet already exists and was read back", Meta = meta };
                }

                var node = FindNetworkNodes(anchorRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
                if (node.Node == null)
                {
                    meta["error"] = "No Industrial Ethernet/PROFINET network node found under anchor path.";
                    meta["readback"] = BuildSubnetReadbackJson(subnetName);
                    return new ResponseMessage { Message = "No suitable network node found", Meta = meta };
                }

                object? subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                if (subnet == null)
                {
                    var create = node.Node.GetType().GetMethod("CreateAndConnectToSubnet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                    subnet = create?.Invoke(node.Node, new object[] { subnetName });
                    meta["created"] = subnet != null;
                }
                else
                {
                    meta["created"] = false;
                    meta["reusedExistingNodeSubnet"] = TryGetName(subnet) ?? subnet.ToString();
                }

                meta["selectedNode"] = FormatNodeInfo(node);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                var ok = subnet != null && SubnetReadbackContains(subnetName);
                meta["success"] = ok;
                return new ResponseMessage
                {
                    Message = ok ? "Subnet ensured and read back" : "Subnet ensure did not produce readback evidence",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                return new ResponseMessage { Message = "Failed ensuring subnet", Meta = meta };
            }
        }

        public ResponseMessage AttachDeviceNodeToSubnet(string deviceItemPath, int interfaceIndex, string subnetName, string anchorDeviceItemPath = "")
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["deviceItemPath"] = deviceItemPath,
                ["interfaceIndex"] = interfaceIndex,
                ["subnetName"] = subnetName,
                ["anchorDeviceItemPath"] = anchorDeviceItemPath
            };

            try
            {
                if (IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                var targetRoot = GetDeviceItemByPath(deviceItemPath);
                if (targetRoot == null)
                {
                    meta["error"] = "Device item not found";
                    return new ResponseMessage { Message = "Device item not found", Meta = meta };
                }

                object? subnet = FindConnectedSubnetByName(subnetName);
                if (subnet == null && !string.IsNullOrWhiteSpace(anchorDeviceItemPath))
                {
                    var ensure = EnsureSubnet(anchorDeviceItemPath, "PROFINET", subnetName);
                    meta["ensureSubnet"] = ensure.Meta?.DeepClone();
                    subnet = FindConnectedSubnetByName(subnetName);
                }

                if (subnet == null)
                {
                    meta["error"] = "Subnet was not found. Call EnsureSubnet with a valid anchor first or pass anchorDeviceItemPath.";
                    meta["readback"] = BuildSubnetReadbackJson(subnetName);
                    return new ResponseMessage { Message = "Subnet not found", Meta = meta };
                }

                var nodes = FindNetworkNodes(targetRoot)
                    .Where(n => IsIndustrialEthernetNode(n.Node))
                    .ToList();
                meta["candidateNodes"] = ToJsonArray(nodes.Select(FormatNodeInfo));
                if (interfaceIndex < 0 || interfaceIndex >= nodes.Count)
                {
                    meta["error"] = $"interfaceIndex is out of range. Available Industrial Ethernet/PROFINET nodes: {nodes.Count}.";
                    return new ResponseMessage { Message = "Interface index out of range", Meta = meta };
                }

                var selected = nodes[interfaceIndex];
                var connected = TryGetPropertyValue(selected.Node, "ConnectedSubnet");
                var connectedName = TryGetName(connected);
                if (!string.Equals(connectedName, subnetName, StringComparison.OrdinalIgnoreCase))
                {
                    var connect = selected.Node.GetType().GetMethod("ConnectToSubnet", BindingFlags.Public | BindingFlags.Instance);
                    connect?.Invoke(selected.Node, new object[] { subnet });
                }

                meta["selectedNodeBefore"] = FormatNodeInfo(selected);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                var ok = SubnetReadbackContains(subnetName) && BuildSubnetReadbackLines(subnetName).Any(x => x.IndexOf(deviceItemPath, StringComparison.OrdinalIgnoreCase) >= 0 || x.IndexOf(selected.Item.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                meta["success"] = ok;
                return new ResponseMessage
                {
                    Message = ok ? "Device network node attached to subnet and read back" : "Device network node attach did not produce readback evidence",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                meta["readback"] = BuildSubnetReadbackJson(subnetName);
                return new ResponseMessage { Message = "Failed attaching device node to subnet", Meta = meta };
            }
        }

        public ResponseMessage SetCpuCommonSettings(string cpuPath, string settingsJson)
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false,
                ["cpuPath"] = cpuPath
            };

            try
            {
                if (IsProjectNull())
                    return new ResponseMessage { Message = "Project is null", Meta = meta };

                var di = GetDeviceItemByPath(cpuPath);
                if (di == null)
                {
                    meta["error"] = "CPU device item not found";
                    return new ResponseMessage { Message = "CPU device item not found", Meta = meta };
                }

                JsonObject? root;
                try
                {
                    root = JsonNode.Parse(settingsJson) as JsonObject;
                }
                catch (Exception ex)
                {
                    meta["error"] = "Invalid JSON: " + ex.Message;
                    return new ResponseMessage { Message = "Invalid settings JSON", Meta = meta };
                }

                var exact = root?["exactAttributes"] as JsonObject;
                if (exact == null || exact.Count == 0)
                {
                    meta["error"] = "settingsJson must contain exactAttributes. Attribute names must come from GetDeviceItemInfo/GetDeviceItemNetworkInfo readback.";
                    return new ResponseMessage { Message = "No exact attributes supplied", Meta = meta };
                }

                var infos = di.GetAttributeInfos().ToList();
                var applied = new JsonArray();
                var rejected = new JsonArray();

                foreach (var kv in exact)
                {
                    var name = kv.Key;
                    var value = kv.Value?.ToString() ?? string.Empty;
                    var info = infos.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (info == null)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = name,
                            ["reason"] = "Attribute not found on CPU device item. Read exact names first."
                        });
                        continue;
                    }

                    if (IsAttributeWritable(info) != true)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = "Attribute is not writable according to TIA attribute metadata."
                        });
                        continue;
                    }

                    try
                    {
                        object? oldValue = null;
                        try { oldValue = di.GetAttribute(info.Name); } catch { }
                        var typedValue = CoerceAttributeValue(value, oldValue, info);
                        di.SetAttribute(info.Name, typedValue);
                        object? newValue = null;
                        try { newValue = di.GetAttribute(info.Name); } catch { }
                        applied.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["oldValue"] = oldValue?.ToString() ?? string.Empty,
                            ["newValue"] = newValue?.ToString() ?? string.Empty,
                            ["dataType"] = TryGetPropertyValue(info, "DataType", "Type")?.ToString() ?? string.Empty
                        });
                    }
                    catch (Exception ex)
                    {
                        rejected.Add(new JsonObject
                        {
                            ["attribute"] = info.Name,
                            ["reason"] = FormatExceptionDetail(ex)
                        });
                    }
                }

                meta["applied"] = applied;
                meta["rejected"] = rejected;
                meta["readback"] = BuildDeviceItemNetworkReadbackJson(cpuPath);
                meta["success"] = applied.Count > 0 && rejected.Count == 0;
                return new ResponseMessage
                {
                    Message = meta["success"]?.GetValue<bool>() == true
                        ? "CPU common settings applied and read back"
                        : "CPU common settings completed with rejected attributes",
                    Meta = meta
                };
            }
            catch (Exception ex)
            {
                meta["error"] = FormatExceptionDetail(ex);
                return new ResponseMessage { Message = "Failed setting CPU common settings", Meta = meta };
            }
        }

        public string ProbeDeviceNetworkExposure(string deviceItemPath)
        {
            var sb = new StringBuilder();
            var root = GetDeviceItemByPath(deviceItemPath);
            if (root == null)
            {
                return $"DeviceItem not found: {deviceItemPath}";
            }

            sb.AppendLine($"Network exposure probe for: {deviceItemPath}");
            sb.AppendLine("DeviceItem tree:");
            sb.AppendLine(GetDeviceItemTree(deviceItemPath, 5));

            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                sb.AppendLine($"Item: path={item.Path}; kind={item.Kind}; owner={item.DeviceItem.Name}; type={item.Object.GetType().FullName}");

                try
                {
                    var attrs = TryReadInterestingAttributes(item.Object).ToList();
                    if (attrs.Count > 0)
                    {
                        sb.AppendLine("  Attributes:");
                        foreach (var attr in attrs)
                            sb.AppendLine("    " + attr);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Attributes error: " + FormatExceptionDetail(ex));
                }

                try
                {
                    var services = ProbeInterestingServices(item.Object).ToList();
                    if (services.Count > 0)
                    {
                        sb.AppendLine("  Services:");
                        foreach (var svc in services)
                            sb.AppendLine("    " + svc);
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Services error: " + FormatExceptionDetail(ex));
                }
            }

            return sb.ToString();
        }

        public string ProbeCreateHardwareHmiConnection(string plcRootPath, string hmiRootPath, string connectionName, bool createConnection = false, bool deepScan = false)
        {
            var sb = new StringBuilder();
            if (IsProjectNull()) return "Project is null";

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            sb.AppendLine("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            sb.AppendLine("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return sb.ToString();

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            sb.AppendLine("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            sb.AppendLine("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            sb.AppendLine("CreateConnection: " + createConnection);
            sb.AppendLine("DeepScan: " + deepScan);
            if (plcNode.Node == null || hmiNode.Node == null) return sb.ToString();

#if TIA_V20
            // V20 does not expose Siemens.Engineering.HW.CommunicationConnections. The hardware-level
            // HMI connection helper degrades to a no-op (caller still gets the diagnostic prefix).
            var connectionCompositionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var hmiConnectionType = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.HmiConnection, Siemens.Engineering");
            if (connectionCompositionType == null || hmiConnectionType == null)
            {
                sb.AppendLine(Capability.Describe(TiaFeature.HardwareHmiConnection) + " Skipping hardware HMI connection creation.");
                return sb.ToString();
            }
#else
            var connectionCompositionType = typeof(global::Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition);
            var hmiConnectionType = typeof(global::Siemens.Engineering.HW.CommunicationConnections.HmiConnection);
#endif
            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();
            sb.AppendLine("Candidate count: " + candidates.Count);

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                sb.AppendLine("Candidate: " + c.Label + " targetType=" + c.Target.GetType().FullName);
                object? composition = null;
                try
                {
                    composition = TryGetService(c.Target, connectionCompositionType);
                    sb.AppendLine("  ConnectionComposition service: " + (composition == null ? "<none>" : composition.GetType().FullName));
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  ConnectionComposition service error: " + FormatExceptionDetail(ex));
                }

                if (composition == null) continue;

                try
                {
                    sb.AppendLine("  Before count=" + (TryGetPropertyValue(composition, "Count")?.ToString() ?? ""));
                }
                catch { }

                try
                {
                    var create = composition.GetType()
                        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .FirstOrDefault(m => m.Name == "Create" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
                    if (create == null)
                    {
                        sb.AppendLine("  Create<T>(Node,DeviceItem,Node) not found.");
                        continue;
                    }

                    sb.AppendLine("  Create<T> signature: " + create);
                    if (!createConnection)
                    {
                        sb.AppendLine("  Create skipped (scan-only mode).");
                        continue;
                    }

                    var generic = create.MakeGenericMethod(hmiConnectionType);
                    var created = generic.Invoke(composition, new object[] { c.LocalNode, c.PartnerTarget, c.PartnerNode });
                    sb.AppendLine("  Create<HmiConnection>: " + (created == null ? "NULL" : "OK " + created.GetType().FullName));
                    if (created != null)
                    {
                        TrySetProperty(created, "LocalConnectionName", connectionName);
                        sb.AppendLine("  Created readback: " + SummarizeHmiObjectReadback(created, "LocalConnectionName", "LocalAddress", "PartnerAddress", "AccessPoint", "Online", "TimeSynchronizationMode"));
                    }

                    try
                    {
                        sb.AppendLine("  After count=" + (TryGetPropertyValue(composition, "Count")?.ToString() ?? ""));
                    }
                    catch { }

                    break;
                }
                catch (Exception ex)
                {
                    sb.AppendLine("  Create<HmiConnection> error: " + FormatExceptionDetail(ex));
                }
            }

            try
            {
                var sw = GetSoftwareContainer("HMI_RT_1")?.Software;
                var connections = sw == null ? null : TryGetPropertyValue(sw, "Connections");
                sb.AppendLine("Classic HMI Connections after HW probe:");
                if (connections is IEnumerable en)
                {
                    var any = false;
                    foreach (var c in en)
                    {
                        any = true;
                        sb.AppendLine("  " + (TryGetName(c) ?? c?.ToString() ?? "<null>"));
                    }
                    if (!any) sb.AppendLine("  <empty>");
                }
                else
                {
                    sb.AppendLine("  <not found>");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Classic HMI connection readback error: " + FormatExceptionDetail(ex));
            }

            return sb.ToString();
        }

        public List<string> ProbeHardwareHmiConnectionOwnerCandidates(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = new List<string>();
            if (IsProjectNull())
            {
                lines.Add("Project is null");
                return lines;
            }

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            lines.Add("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            lines.Add("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return lines;

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            lines.Add("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            lines.Add("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return lines;

            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();
            lines.Add("DeepScan: " + deepScan);
            lines.Add("Candidate count: " + candidates.Count);

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                lines.Add(c.Label
                    + " | type=" + (c.Target.GetType().FullName ?? c.Target.GetType().Name)
                    + " | name=" + (TryGetName(c.Target) ?? "<unnamed>"));
            }

            return lines;
        }

        public List<string> ProbeHardwareHmiConnectionWhitelistedServices(string plcRootPath, string hmiRootPath, bool deepScan = true)
        {
            var lines = new List<string>();
            if (IsProjectNull())
            {
                lines.Add("Project is null");
                return lines;
            }

            var plcRoot = GetDeviceItemByPath(plcRootPath);
            var hmiRoot = GetDeviceItemByPath(hmiRootPath);
            lines.Add("PLC root: " + plcRootPath + " -> " + (plcRoot?.Name ?? "<not found>"));
            lines.Add("HMI root: " + hmiRootPath + " -> " + (hmiRoot?.Name ?? "<not found>"));
            if (plcRoot == null || hmiRoot == null) return lines;

            var plcNode = FindNetworkNodes(plcRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            var hmiNode = FindNetworkNodes(hmiRoot).FirstOrDefault(n => IsIndustrialEthernetNode(n.Node));
            lines.Add("Selected PLC node: " + (plcNode.Node == null ? "<none>" : FormatNodeInfo(plcNode)));
            lines.Add("Selected HMI node: " + (hmiNode.Node == null ? "<none>" : FormatNodeInfo(hmiNode)));
            if (plcNode.Node == null || hmiNode.Node == null) return lines;

            var candidates = deepScan
                ? BuildHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList()
                : BuildDirectHardwareHmiConnectionCandidates(plcNode, hmiNode).ToList();

#if TIA_V20
            var commConnT = Type.GetType("Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition, Siemens.Engineering");
            var serviceTypes = commConnT != null
                ? new[] { commConnT, typeof(NetworkInterface), typeof(NetworkPort) }
                : new[] { typeof(NetworkInterface), typeof(NetworkPort) };
#else
            var serviceTypes = new[]
            {
                typeof(global::Siemens.Engineering.HW.CommunicationConnections.ConnectionComposition),
                typeof(NetworkInterface),
                typeof(NetworkPort)
            };
#endif

            lines.Add("DeepScan: " + deepScan);
            lines.Add("Candidate count: " + candidates.Count);
            lines.Add("Service whitelist: " + string.Join(", ", serviceTypes.Select(t => t.FullName)));

            foreach (var c in candidates)
            {
                if (c.Target == null) continue;
                if (!IsSafeHardwareHmiServiceProbeTarget(c.Target))
                {
                    lines.Add("Candidate: " + c.Label + " | type=" + c.Target.GetType().FullName + " | SKIP unsafe/high-level target");
                    continue;
                }

                lines.Add("Candidate: " + c.Label + " | type=" + (c.Target.GetType().FullName ?? c.Target.GetType().Name) + " | name=" + (TryGetName(c.Target) ?? "<unnamed>"));
                foreach (var serviceType in serviceTypes)
                {
                    object? service = null;
                    try
                    {
                        service = TryGetService(c.Target, serviceType);
                    }
                    catch (Exception ex)
                    {
                        lines.Add("  " + serviceType.Name + ": ERROR " + FormatExceptionDetail(ex));
                        continue;
                    }

                    if (service == null)
                    {
                        lines.Add("  " + serviceType.Name + ": <none>");
                        continue;
                    }

                    lines.Add("  " + serviceType.Name + ": " + service.GetType().FullName + " | " + SummarizeWhitelistedService(service));
                }
            }

            return lines;
        }

        private IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

            foreach (var c in Expand("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node))
                yield return c;
            foreach (var c in Expand("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;
            foreach (var c in Expand("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;
            foreach (var c in Expand("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node))
                yield return c;

            if (_project != null)
            {
                foreach (var c in Add("Project", _project, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;
                foreach (var c in Add("Project devices", _project.Devices, plcNode.Node, hmiNode.Item, hmiNode.Node))
                    yield return c;

                foreach (var d in _project.Devices)
                {
                    foreach (var c in Add("Device " + d.Name, d, plcNode.Node, hmiNode.Item, hmiNode.Node))
                        yield return c;
                    foreach (var c in Add("DeviceItems " + d.Name, d.DeviceItems, plcNode.Node, hmiNode.Item, hmiNode.Node))
                        yield return c;
                }
            }

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Expand(string label, object? start, object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                var current = start;
                for (var depth = 0; current != null && depth < 8; depth++)
                {
                    foreach (var c in Add(depth == 0 ? label : label + " ancestor[" + depth + "]", current, localNode, partnerTarget, partnerNode))
                        yield return c;

                    var next = TryGetPropertyValue(current, "Parent", "OwnedBy");
                    if (next == null || ReferenceEquals(next, current)) break;
                    current = next;
                }
            }

            IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> Add(string label, object? target, object localNode, DeviceItem partnerTarget, object partnerNode)
            {
                if (target == null) yield break;
                if (!seen.Add(target)) yield break;
                yield return (label, target, localNode, partnerTarget, partnerNode);
            }
        }

        private static IEnumerable<(string Label, object? Target, object LocalNode, DeviceItem PartnerTarget, object PartnerNode)> BuildDirectHardwareHmiConnectionCandidates(NetworkNodeInfo plcNode, NetworkNodeInfo hmiNode)
        {
            yield return ("PLC node", plcNode.Node, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC network interface", plcNode.NetworkInterface, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("PLC device item", plcNode.Item, plcNode.Node, hmiNode.Item, hmiNode.Node);
            yield return ("HMI node", hmiNode.Node, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI network interface", hmiNode.NetworkInterface, hmiNode.Node, plcNode.Item, plcNode.Node);
            yield return ("HMI device item", hmiNode.Item, hmiNode.Node, plcNode.Item, plcNode.Node);
        }

        private static bool IsSafeHardwareHmiServiceProbeTarget(object target)
        {
            var typeName = target.GetType().FullName ?? target.GetType().Name;
            if (typeName.Contains("Project", StringComparison.OrdinalIgnoreCase)) return false;
            if (typeName.Contains("Composition", StringComparison.OrdinalIgnoreCase)) return false;
            if (typeName.Contains("DeviceComposition", StringComparison.OrdinalIgnoreCase)) return false;
            return typeName.Contains("DeviceItem", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("Node", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("NetworkInterface", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("NetworkPort", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("HardwareComponent", StringComparison.OrdinalIgnoreCase)
                   || typeName.Contains("Device", StringComparison.OrdinalIgnoreCase);
        }

        private static string SummarizeWhitelistedService(object service)
        {
            var parts = new List<string>();
            try
            {
                var count = TryGetPropertyValue(service, "Count");
                if (count != null) parts.Add("Count=" + count);
            }
            catch { }

            try
            {
                var nodes = TryGetPropertyValue(service, "Nodes") as IEnumerable;
                if (nodes != null && nodes is not string)
                {
                    var nodeInfos = new List<string>();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        nodeInfos.Add((TryGetName(node) ?? "<unnamed>") + ":" + (TryGetPropertyValue(node, "NodeType")?.ToString() ?? "") + ":" + (TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? "<none>"));
                    }
                    parts.Add("Nodes=[" + string.Join(", ", nodeInfos) + "]");
                }
            }
            catch { }

            try
            {
                var createMethods = service.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, "Create", StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ToString())
                    .Take(8)
                    .ToList();
                if (createMethods.Count > 0) parts.Add("CreateMethods=[" + string.Join(" | ", createMethods) + "]");
            }
            catch { }

            try
            {
                var methods = service.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m =>
                    {
                        var n = m.Name ?? "";
                        return n.IndexOf("Connect", StringComparison.OrdinalIgnoreCase) >= 0
                               || n.IndexOf("Disconnect", StringComparison.OrdinalIgnoreCase) >= 0
                               || n.IndexOf("Subnet", StringComparison.OrdinalIgnoreCase) >= 0;
                    })
                    .Select(m => m.ToString())
                    .Take(8)
                    .ToList();
                if (methods.Count > 0) parts.Add("NetworkMethods=[" + string.Join(" | ", methods) + "]");
            }
            catch { }

            return parts.Count == 0 ? "<no summary>" : string.Join("; ", parts);
        }

        private readonly struct NetworkNodeInfo
        {
            public NetworkNodeInfo(string path, DeviceItem item, object networkInterface, object node)
            {
                Path = path;
                Item = item;
                NetworkInterface = networkInterface;
                Node = node;
            }

            public string Path { get; }
            public DeviceItem Item { get; }
            public object NetworkInterface { get; }
            public object Node { get; }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static IEnumerable<NetworkNodeInfo> FindNetworkNodes(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                object? networkInterface = null;
                try { networkInterface = TryGetNetworkInterfaceService(item.Object) ?? TryGetNetworkInterfaceFromNetworkPort(item.Object); } catch { }
                if (networkInterface == null) continue;

                var nodes = TryGetPropertyValue(networkInterface, "Nodes");
                if (nodes is not IEnumerable enumerable || nodes is string) continue;
                foreach (var node in enumerable)
                {
                    if (node != null) yield return new NetworkNodeInfo(item.Path, item.DeviceItem, networkInterface, node);
                }
            }
        }

        private static IEnumerable<(DeviceItem Item, string Path)> TraverseDeviceItems(DeviceItem root, string path)
        {
            yield return (root, path);
            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in TraverseDeviceItems(child, path + "/" + child.Name))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<string> DescribeNetworkServiceScan(DeviceItem root)
        {
            foreach (var item in TraverseDeviceItemsAndHardware(root, root.Name))
            {
                object? networkInterface = null;
                Exception? networkEx = null;
                try { networkInterface = TryGetNetworkInterfaceService(item.Object) ?? TryGetNetworkInterfaceFromNetworkPort(item.Object); } catch (Exception ex) { networkEx = ex; }

                var line = $"path={item.Path}; item={TryGetName(item.Object) ?? item.DeviceItem.Name}; ownerDeviceItem={item.DeviceItem.Name}; objectKind={item.Kind}; type={item.Object.GetType().FullName}";
                if (networkInterface != null)
                {
                    var nodes = TryGetPropertyValue(networkInterface, "Nodes") as IEnumerable;
                    var nodeCount = 0;
                    if (nodes != null)
                    {
                        foreach (var _ in nodes) nodeCount++;
                    }
                    line += $"; NetworkInterface=YES; nodes={nodeCount}";
                }
                else
                {
                    line += "; NetworkInterface=NO";
                    if (networkEx != null) line += "; err=" + networkEx.Message;
                }

                yield return line;
            }
        }

        private static IEnumerable<(object Object, DeviceItem DeviceItem, string Path, string Kind)> TraverseDeviceItemsAndHardware(DeviceItem root, string path)
        {
            yield return (root, root, path, "DeviceItem");

            foreach (var hardware in root.Items)
            {
                if (hardware != null)
                {
                    yield return (hardware, root, path + "#" + (TryGetName(hardware) ?? hardware.GetType().Name), "HardwareComponent");
                }
            }

            foreach (var child in root.DeviceItems)
            {
                foreach (var nested in TraverseDeviceItemsAndHardware(child, path + "/" + child.Name))
                {
                    yield return nested;
                }
            }
        }

        private static object? TryGetNetworkInterfaceService(object target)
        {
            try
            {
                var mi = target.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;
                return mi.MakeGenericMethod(typeof(NetworkInterface)).Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        private static object? TryGetNetworkInterfaceFromNetworkPort(object target)
        {
            try
            {
                var port = TryGetServiceByTypeSuffix(target, "NetworkPort");
                if (port == null) return null;
                return TryGetPropertyValue(port, "Interface");
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> TryReadInterestingAttributes(object target)
        {
            var result = new List<string>();
            var methods = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            var getInfos = methods.FirstOrDefault(m => m.Name == "GetAttributeInfos" && m.GetParameters().Length == 0);
            var getAttr = methods.FirstOrDefault(m => m.Name == "GetAttribute" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
            if (getInfos == null || getAttr == null)
                return result;

            var infos = getInfos.Invoke(target, Array.Empty<object>()) as IEnumerable;
            if (infos == null)
                return result;

            var interesting = new[]
            {
                "ip", "ipv4", "subnet", "mask", "gateway", "mac", "pn", "profinet", "device", "station", "interface", "name", "address", "partner", "node"
            };

            foreach (var info in infos)
            {
                var name = TryGetPropertyValue(info!, "Name")?.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var lower = name.ToLowerInvariant();
                if (!interesting.Any(k => lower.Contains(k)))
                    continue;

                try
                {
                    var value = getAttr.Invoke(target, new object[] { name });
                    result.Add($"{name}={value ?? ""}");
                }
                catch { }
            }

            return result;
        }

        private static bool IsSupportedProfinetSubnetType(string subnetType)
        {
            var value = (subnetType ?? string.Empty).Trim();
            return value.Length == 0
                   || value.Equals("PROFINET", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("PN", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("IndustrialEthernet", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("Industrial Ethernet", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("PN/IE", StringComparison.OrdinalIgnoreCase);
        }

        private object? FindConnectedSubnetByName(string subnetName)
        {
            if (_project == null || string.IsNullOrWhiteSpace(subnetName))
                return null;

            foreach (var device in _project.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        if (subnet == null) continue;

                        var name = TryGetName(subnet) ?? subnet.ToString() ?? string.Empty;
                        if (string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            return subnet;
                    }
                }
            }

            foreach (var group in _project.DeviceGroups)
            {
                foreach (var subnet in FindConnectedSubnetByNameInGroup(group, subnetName))
                    return subnet;
            }

            return null;
        }

        private static IEnumerable<object> FindConnectedSubnetByNameInGroup(DeviceUserGroup group, string subnetName)
        {
            foreach (var device in group.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        if (subnet == null) continue;

                        var name = TryGetName(subnet) ?? subnet.ToString() ?? string.Empty;
                        if (string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            yield return subnet;
                    }
                }
            }

            foreach (var child in group.Groups)
            {
                foreach (var subnet in FindConnectedSubnetByNameInGroup(child, subnetName))
                    yield return subnet;
            }
        }

        private JsonArray BuildSubnetReadbackJson(string subnetName)
        {
            var arr = new JsonArray();
            foreach (var line in BuildSubnetReadbackLines(subnetName))
                arr.Add(line);
            return arr;
        }

        private List<string> BuildSubnetReadbackLines(string subnetName)
        {
            var lines = new List<string>();
            if (_project == null) return lines;

            void AddFromDevice(Device device)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(FormatNodeInfo(node));
                    }
                }
            }

            foreach (var device in _project.Devices)
                AddFromDevice(device);
            foreach (var group in _project.DeviceGroups)
                AddSubnetReadbackLinesFromGroup(group, subnetName, lines);

            return lines;
        }

        private static void AddSubnetReadbackLinesFromGroup(DeviceUserGroup group, string subnetName, List<string> lines)
        {
            foreach (var device in group.Devices)
            {
                foreach (var root in device.DeviceItems)
                {
                    foreach (var node in FindNetworkNodes(root))
                    {
                        var subnet = TryGetPropertyValue(node.Node, "ConnectedSubnet");
                        var name = TryGetName(subnet) ?? subnet?.ToString() ?? "<none>";
                        if (string.IsNullOrWhiteSpace(subnetName) || string.Equals(name, subnetName, StringComparison.OrdinalIgnoreCase))
                            lines.Add(FormatNodeInfo(node));
                    }
                }
            }

            foreach (var child in group.Groups)
                AddSubnetReadbackLinesFromGroup(child, subnetName, lines);
        }

        private bool SubnetReadbackContains(string subnetName)
        {
            return BuildSubnetReadbackLines(subnetName)
                .Any(x => x.IndexOf("connectedSubnet=" + subnetName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private JsonArray BuildDeviceItemNetworkReadbackJson(string deviceItemPath)
        {
            var arr = new JsonArray();
            var attrs = GetDeviceItemNetworkInfo(deviceItemPath) ?? new List<ModelContextProtocol.NetworkAttribute>();
            foreach (var attr in attrs)
            {
                arr.Add(new JsonObject
                {
                    ["name"] = attr.Name ?? string.Empty,
                    ["value"] = attr.Value ?? string.Empty,
                    ["dataType"] = attr.DataType ?? string.Empty,
                    ["isWritable"] = attr.IsWritable
                });
            }
            return arr;
        }

        private static IEnumerable<string> ProbeInterestingServices(object target)
        {
            var serviceSuffixes = new[]
            {
                "NetworkInterface",
                "NetworkPort",
                "Node",
                "SubnetOwner",
                "TransferArea",
                "InterfaceOperatingMode",
                "CommunicationConnections",
                "CommunicationConnection",
                "AddressController",
                "HardwareObject"
            };

            foreach (var suffix in serviceSuffixes)
            {
                object? svc = null;
                try { svc = TryGetServiceByTypeSuffix(target, suffix); } catch { }
                if (svc == null) continue;

                var details = new List<string>
                {
                    $"service={svc.GetType().FullName}"
                };

                var nodes = TryGetPropertyValue(svc, "Nodes") as IEnumerable;
                if (nodes != null && nodes is not string)
                {
                    var nodeInfos = new List<string>();
                    foreach (var node in nodes)
                    {
                        if (node == null) continue;
                        nodeInfos.Add($"{TryGetName(node) ?? "<unnamed>"}:{TryGetPropertyValue(node, "NodeType") ?? ""}:{TryGetName(TryGetPropertyValue(node, "ConnectedSubnet")) ?? "<none>"}");
                    }
                    details.Add("Nodes=[" + string.Join(", ", nodeInfos) + "]");
                }

                foreach (var propName in new[] { "Name", "Type", "Mode", "Address", "Subnet", "ConnectedSubnet", "NodeType" })
                {
                    try
                    {
                        var value = TryGetPropertyValue(svc, propName);
                        if (value != null)
                            details.Add(propName + "=" + value);
                    }
                    catch { }
                }

                var memberSummaries = DescribeMembers(svc, 80)
                    .Select(m => $"{m.Kind}:{m.Name}:{m.Type}:{m.Signature}")
                    .Take(40)
                    .ToList();
                if (memberSummaries.Count > 0)
                    details.Add("Members=[" + string.Join(" | ", memberSummaries) + "]");

                yield return suffix + " => " + string.Join("; ", details);
            }
        }

        private static string FormatNodeInfo(NetworkNodeInfo info)
        {
            return $"path={info.Path}; item={info.Item.Name}; node={TryGetName(info.Node) ?? "<unnamed>"}; type={TryGetPropertyValue(info.Node, "NodeType")}; connectedSubnet={TryGetName(TryGetPropertyValue(info.Node, "ConnectedSubnet")) ?? TryGetPropertyValue(info.Node, "ConnectedSubnet")?.ToString() ?? "<none>"}";
        }

        private static bool IsIndustrialEthernetNode(object? node)
        {
            if (node == null) return false;
            var type = TryGetPropertyValue(node, "NodeType")?.ToString() ?? "";
            var name = TryGetName(node) ?? "";
            return type.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0
                   || type.IndexOf("Profinet", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("PROFINET", StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("Ethernet", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public ResponseMessage ValidateAutomationContext(string expectedPlcSoftwarePath = "PLC_1", string expectedHmiSoftwarePath = "HMI_RT_1")
        {
            var meta = new JsonObject
            {
                ["timestamp"] = DateTime.Now,
                ["success"] = false
            };

            var problems = new JsonArray();
            var devices = new JsonArray();
            var software = new JsonArray();
            meta["problems"] = problems;
            meta["devices"] = devices;
            meta["software"] = software;

            if (IsProjectNull())
            {
                problems.Add("Project is null. Use Connect + AttachToOpenProject/OpenProject first.");
                return new ResponseMessage { Message = "Automation context invalid", Meta = meta };
            }

            meta["project"] = _project?.Name ?? string.Empty;

            try
            {
                foreach (var d in _project!.Devices)
                {
                    var deviceInfo = new JsonObject
                    {
                        ["name"] = d.Name,
                        ["type"] = d.GetType().FullName ?? d.GetType().Name
                    };
                    devices.Add(deviceInfo);

                    foreach (var di in d.DeviceItems)
                    {
                        TryAddSoftwareInfo(di, software);
                    }
                }
            }
            catch (Exception ex)
            {
                problems.Add($"Device/software scan failed: {FormatExceptionDetail(ex)}");
            }

            if (devices.Count == 0)
            {
                problems.Add("No devices found in current project.");
            }

            if (!string.IsNullOrWhiteSpace(expectedPlcSoftwarePath))
            {
                var plc = GetSoftwareContainer(expectedPlcSoftwarePath)?.Software as PlcSoftware;
                meta["expectedPlcSoftwarePath"] = expectedPlcSoftwarePath;
                meta["expectedPlcFound"] = plc != null;
                if (plc == null) problems.Add($"PLC software not found at '{expectedPlcSoftwarePath}'.");
            }

            if (!string.IsNullOrWhiteSpace(expectedHmiSoftwarePath))
            {
                var hmi = GetSoftwareContainer(expectedHmiSoftwarePath)?.Software;
                meta["expectedHmiSoftwarePath"] = expectedHmiSoftwarePath;
                meta["expectedHmiFound"] = hmi != null && hmi.GetType().FullName?.Contains("Hmi", StringComparison.OrdinalIgnoreCase) == true;
                if (hmi == null) problems.Add($"HMI software not found at '{expectedHmiSoftwarePath}'.");
            }

            try
            {
                meta["projectTree"] = GetProjectTree();
            }
            catch (Exception ex)
            {
                problems.Add($"Project tree failed: {FormatExceptionDetail(ex)}");
            }

            meta["success"] = problems.Count == 0;
            return new ResponseMessage
            {
                Message = problems.Count == 0 ? "Automation context OK" : "Automation context has problems",
                Meta = meta
            };

            void TryAddSoftwareInfo(DeviceItem item, JsonArray target)
            {
                try
                {
                    var sc = item.GetService<SoftwareContainer>();
                    if (sc?.Software != null)
                    {
                        target.Add(new JsonObject
                        {
                            ["deviceItem"] = item.Name,
                            ["softwareName"] = TryGetName(sc.Software) ?? item.Name,
                            ["softwareType"] = sc.Software.GetType().FullName ?? sc.Software.GetType().Name
                        });
                    }
                }
                catch { }

                try
                {
                    foreach (var child in item.DeviceItems)
                    {
                        TryAddSoftwareInfo(child, target);
                    }
                }
                catch { }
            }
        }

        private static void BuildDeviceItemTree(StringBuilder sb, DeviceItem node, List<bool> ancestorStates, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;

            // Hardware components (Items)
            if (node.Items != null && node.Items.Count > 0)
            {
                var items = node.Items.ToList();
                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    var isLast = (i == items.Count - 1) && (node.DeviceItems == null || node.DeviceItems.Count == 0);
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{it.Name} [Hardware Component]");
                }
            }

            // Sub device items
            if (node.DeviceItems != null && node.DeviceItems.Count > 0)
            {
                var children = node.DeviceItems.ToList();
                for (int i = 0; i < children.Count; i++)
                {
                    var child = children[i];
                    var isLast = i == children.Count - 1;
                    sb.AppendLine($"{GetTreePrefixStatic(ancestorStates, isLast)}{child.Name} [DeviceItem]");
                    BuildDeviceItemTree(sb, child, new List<bool>(ancestorStates) { isLast }, depth + 1, maxDepth);
                }
            }
        }

        private static string GetTreePrefixStatic(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }

        #endregion
    }
}
