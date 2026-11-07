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
    // Partial: opcua. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region opcua

        /// <summary>Returns the ServerInterfaceGroup node via reflection chain.</summary>
        private static object? GetOpcUaServerInterfaceGroup(PlcSoftware plc)
        {
            var provider = plc.GetService<OpcUaProvider>();
            if (provider == null) return null;
            var commGroup = TryGetPropertyValue(provider, "CommunicationGroup");
            if (commGroup == null) return null;
            return TryGetPropertyValue(commGroup, "ServerInterfaceGroup");
        }

        public ModelContextProtocol.ResponseJsonReport GetOpcUaConfig(string softwarePath)
        {
            var data = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now.ToString("O") };

            if (IsProjectNull())
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "No project open.", Data = data };

            var plc = GetPlcSoftware(softwarePath);
            if (plc == null)
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"PLC software not found: '{softwarePath}'.", Data = data };

            try
            {
                var provider = plc.GetService<OpcUaProvider>();
                if (provider == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "OpcUaProvider not available for this PLC.", Data = data };

                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null)
                    return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = "ServerInterfaceGroup not accessible.", Data = data };

                data["serverInterfaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "ServerInterfaces"));
                data["simaticInterfaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "SimaticInterfaces"));
                data["referenceNamespaces"] = CollectOpcUaItems(TryGetPropertyValue(sig, "ReferenceNamespaces"));

                return new ModelContextProtocol.ResponseJsonReport { Ok = true, Message = $"OPC UA config read for '{softwarePath}'.", Data = data };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "GetOpcUaConfig failed for {SoftwarePath}", softwarePath);
                return new ModelContextProtocol.ResponseJsonReport { Ok = false, Message = $"Error: {ex.Message}", Data = data };
            }
        }

        private static JsonArray CollectOpcUaItems(object? collection)
        {
            var arr = new JsonArray();
            if (collection is not IEnumerable items || collection is string) return arr;
            foreach (var item in items)
            {
                if (item == null) continue;
                var obj = new JsonObject();
                foreach (var prop in new[] { "Name", "Comment", "Author", "Enabled", "UseStringNodeIds", "GenerateNodes", "GeneratedInterfaceName" })
                {
                    var val = TryGetPropertyValue(item, prop);
                    if (val != null) obj[prop] = JsonValue.Create(val.ToString());
                }
                arr.Add(obj);
            }
            return arr;
        }

        public ResponseMessage SetOpcUaInterfaceEnabled(string softwarePath, string interfaceName, bool enabled, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found in '{softwarePath}'." };

                TrySetProperty(item, "Enabled", enabled);
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' {(enabled ? "enabled" : "disabled")}. Download to PLC to apply.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["interfaceName"] = interfaceName, ["enabled"] = enabled }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "SetOpcUaInterfaceEnabled failed");
                return new ResponseMessage { Message = $"Error: {ex.Message}" };
            }
        }

        public ResponseMessage ExportOpcUaInterface(string softwarePath, string interfaceName, string exportPath, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "SimaticInterface" => "SimaticInterfaces",
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                var item = FindByName(collection, interfaceName);
                if (item == null)
                    return new ResponseMessage { Message = $"{interfaceType} '{interfaceName}' not found." };

                Directory.CreateDirectory(Path.GetDirectoryName(exportPath) ?? ".");
                TryInvokeMethodByName(item, "Export", new FileInfo(exportPath));
                return new ResponseMessage
                {
                    Message = $"{interfaceType} '{interfaceName}' exported to '{exportPath}'.",
                    Meta = new JsonObject { ["exportPath"] = exportPath }
                };
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ExportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Export failed: {ex.Message}" };
            }
        }

        public ResponseMessage ImportOpcUaInterface(string softwarePath, string importPath, string interfaceType = "ServerInterface")
        {
            if (IsProjectNull()) return new ResponseMessage { Message = "No project open." };
            var plc = GetPlcSoftware(softwarePath);
            if (plc == null) return new ResponseMessage { Message = $"PLC software not found: '{softwarePath}'." };

            try
            {
                var sig = GetOpcUaServerInterfaceGroup(plc);
                if (sig == null) return new ResponseMessage { Message = "ServerInterfaceGroup not accessible." };

                string collectionProp = interfaceType switch
                {
                    "ReferenceNamespace" => "ReferenceNamespaces",
                    _ => "ServerInterfaces"
                };

                var collection = TryGetPropertyValue(sig, collectionProp);
                if (collection == null) return new ResponseMessage { Message = $"{collectionProp} collection not accessible." };

                // ServerInterfaceComposition.Create(name) then Import(file)
                // OR find existing and call Import
                var fi = new FileInfo(importPath);
                var interfaceName = Path.GetFileNameWithoutExtension(importPath);
                var existing = FindByName(collection, interfaceName);

                if (existing != null)
                {
                    TryInvokeMethodByName(existing, "Import", fi);
                    return new ResponseMessage { Message = $"Existing {interfaceType} '{interfaceName}' updated from '{importPath}'." };
                }
                else
                {
                    // Try Create then Import
                    var created = TryInvokeMethodByName(collection, "Create", interfaceName);
                    if (created != null)
                        TryInvokeMethodByName(created, "Import", fi);
                    return new ResponseMessage { Message = created != null
                        ? $"{interfaceType} '{interfaceName}' created and imported from '{importPath}'."
                        : $"Could not create {interfaceType} '{interfaceName}'. Try importing via ExportOpcUaInterface first." };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "ImportOpcUaInterface failed");
                return new ResponseMessage { Message = $"Import failed: {ex.Message}" };
            }
        }

        private static object? FindByName(object? collection, string name)
        {
            if (collection is not IEnumerable items || collection is string) return null;
            foreach (var item in items)
            {
                if (item == null) continue;
                if (string.Equals(TryGetPropertyValue(item, "Name")?.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return item;
            }
            return null;
        }

        #endregion
    }
}
