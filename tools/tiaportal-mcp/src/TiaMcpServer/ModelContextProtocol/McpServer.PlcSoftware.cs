using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using TiaMcpServer.Siemens;


namespace TiaMcpServer.ModelContextProtocol
{
    // Partial: plc software. Extracted from McpServer.cs (god-file split); behavior unchanged.
    public static partial class McpServer
    {
        #region plc software

        [McpServerTool(Name = "GetSoftwareInfo"), Description("[L1][PLC-Software] Get PLC software properties (language, version, block counts). Requires: Connect + OpenProject. softwarePath comes from GetProjectTree (e.g. 'PLC_1'). Use GetSoftwareTree for the full block hierarchy.")]
        public static ResponseSoftwareInfo GetSoftwareInfo(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var software = Portal.GetPlcSoftware(softwarePath);
                if (software != null)
                {

                    var attributes = Helper.GetAttributeList(software);

                    return new ResponseSoftwareInfo
                    {
                        Message = $"Software info retrieved from '{softwarePath}'",
                        Name = software.Name,
                        Attributes = attributes,
                        Description = software.ToString(),
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Software not found at '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software info from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiProgramInfo"), Description("[L2][HMI] Get HMI software type (Classic/Basic/Unified), version, and list of all screen names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'HMI_RT_1'). Use to confirm HMI type before choosing Classic vs Unified tool variants.")]
        public static ResponseHmiProgramInfo GetHmiProgramInfo(
            [Description("softwarePath: path in the project structure to the HMI software (see GetProjectTree)")] string softwarePath)
        {
            try
            {
                var info = Portal.GetHmiProgramInfo(softwarePath);
                if (info != null)
                {
                    return new ResponseHmiProgramInfo
                    {
                        Message = $"HMI program info retrieved from '{softwarePath}'",
                        Name = info.Value.Name,
                        ProgramType = info.Value.ProgramType,
                        Screens = info.Value.Screens,
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }

                throw new McpException($"HMI program not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving HMI program info from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiSoftware"), Description("[L2][HMI]Describe the HMI software object (members/methods) via reflection. Useful to discover Export/Import/Create APIs.")]
        public static ResponseObjectDescribe DescribeHmiSoftware(
            [Description("softwarePath: path in the project structure to the HMI software (e.g. 'HMI_RT_1')")] string softwarePath,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiSoftware(softwarePath, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiScreen"), Description("[L2][HMI]Describe one HMI screen object (members/methods) by name under an HMI software.")]
        public static ResponseObjectDescribe DescribeHmiScreen(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("screenName: screen name, e.g. 'Main'")] string screenName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiScreen(softwarePath, screenName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiTagTable"), Description("[L2][HMI]Describe one HMI tag table object (members/methods) by name under an HMI software.")]
        public static ResponseObjectDescribe DescribeHmiTagTable(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("tagTableName: tag table name")] string tagTableName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiTagTable(softwarePath, tagTableName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiTag"), Description("[L2][HMI]Describe one HMI tag object (members/methods) by name under an HMI tag table.")]
        public static ResponseObjectDescribe DescribeHmiTag(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("tagTableName: tag table name")] string tagTableName,
            [Description("tagName: tag name")] string tagName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiTag(softwarePath, tagTableName, tagName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeHmiScreenItem"), Description("[L2][HMI]Describe one HMI screen item (widget) by name under an HMI screen.")]
        public static ResponseObjectDescribe DescribeHmiScreenItem(
            [Description("softwarePath: HMI software path, e.g. 'HMI_RT_1'")] string softwarePath,
            [Description("screenName: screen name, e.g. 'Main'")] string screenName,
            [Description("itemName: widget name, e.g. 'BTN_Start'")] string itemName,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeHmiScreenItem(softwarePath, screenName, itemName, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing HMI screen item '{itemName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeObjectProperty"), Description("[L2][Reflection]Describe an object's nested property via reflection (members list). propertyPath supports dotted path.")]
        public static ResponseObjectDescribe DescribeObjectProperty(
            [Description("objectKind: Project|Portal|Device|DeviceItem|Software|Block|Type")] string objectKind,
            [Description("objectPath: object path")] string objectPath,
            [Description("propertyPath: dotted property path, e.g. 'Connections' or 'PressedStateTags'")] string propertyPath,
            [Description("softwarePath: required for Block/Type")] string softwarePath = "",
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeObjectProperty(objectKind, objectPath, propertyPath, softwarePath, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing property '{propertyPath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureStartStopUnifiedHmi"), Description("[L2][HMI-Unified] SHORTCUT for motor start/stop HMI. Ensures HMI_Connection_1 uses the correct PLC driver (1200/1500 vs 300/400 from CPU TypeIdentifier), 4 HMI tags (StartPB/StopPB/EStop/RunOut) with symbolic PLC binding, and a simple styled Main screen. Requires: Connect + OpenProject + PLC + Unified HMI. Call after EnsureUnifiedHmiScreen if you need a fixed screen size. Idempotent.")]
        public static ResponseMessage EnsureStartStopUnifiedHmi(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name (default 'Main')")] string screenName = "Main",
            [Description("tagTableName: target HMI tag table name (default '默认变量表')")] string tagTableName = "默认变量表",
            [Description("plcName: PLC software path / device name for connection + tag mapping (default 'PLC_1')")] string plcName = "PLC_1",
            [Description("connectionName: Unified HMI connection object name (default 'HMI_Connection_1')")] string connectionName = "HMI_Connection_1")
        {
            try
            {
                var res = Portal.EnsureStartStopUnifiedHmi(hmiSoftwarePath, screenName, tagTableName, plcName, connectionName);
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring unified HMI start/stop: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiScreen"), Description("[L2][HMI-Unified] Create or verify a WinCC Unified HMI screen exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. After creating a screen, add tags with EnsureUnifiedHmiTag, add controls with EnsureUnifiedHmiScreenItem, or apply a complete layout with ApplyUnifiedHmiScreenDesignJson.")]
        public static ResponseMessage EnsureUnifiedHmiScreen(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("width: optional screen width, 0 means keep current")] uint width = 0,
            [Description("height: optional screen height, 0 means keep current")] uint height = 0)
        {
            try
            {
                return Portal.EnsureUnifiedHmiScreen(hmiSoftwarePath, screenName, width, height);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiTagTable"), Description("[L2][HMI-Unified] Create or verify a Unified HMI tag table exists. Requires: Connect + OpenProject + Unified HMI. Idempotent. Create tag tables before adding tags with EnsureUnifiedHmiTag. Default tag table name is '默认变量表'.")]
        public static ResponseMessage EnsureUnifiedHmiTagTable(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("tagTableName: target HMI tag table name")] string tagTableName)
        {
            try
            {
                return Portal.EnsureUnifiedHmiTagTable(hmiSoftwarePath, tagTableName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiTag"), Description("[L2][HMI-Unified] Create or verify a Unified HMI external tag. For PLC-backed tags pass plcTag and address in the same call; the address must read back in Address/LogicalAddress, e.g. %DB200.DBX0.0. Requires: Connect + OpenProject + EnsureUnifiedHmiConnection + EnsureUnifiedHmiTagTable.")]
        public static ResponseMessage EnsureUnifiedHmiTag(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("tagTableName: target HMI tag table name")] string tagTableName,
            [Description("tagName: HMI tag name")] string tagName,
            [Description("hmiDataType: HMI data type, e.g. Bool, Int, Real, String")] string hmiDataType = "Bool",
            [Description("plcName: PLC name for symbolic binding")] string plcName = "PLC_1",
            [Description("plcTag: PLC tag name/path; empty means same as tagName")] string plcTag = "",
            [Description("connectionName: HMI connection name; empty keeps current/auto")] string connectionName = "",
            [Description("address: optional absolute PLC address, e.g. %DB200.DBX0.0. When supplied it is written as the verified HMI runtime address while plcTag remains available as the symbolic reference.")] string address = "",
            [Description("requireVerifiedBinding: stable public generation should keep this true. Set false only for intentional internal HMI-only tags used by local validation/probes.")] bool requireVerifiedBinding = true)
        {
            try
            {
                return Portal.EnsureUnifiedHmiTag(hmiSoftwarePath, tagTableName, tagName, hmiDataType, plcName, plcTag, connectionName, address, requireVerifiedBinding);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiConnection"), Description("[L2][HMI-Unified] Create or verify the PLC↔HMI communication connection (HMI_Connection_1 by default). Requires: Connect + OpenProject + both PLC and Unified HMI devices. Must exist before PLC-backed HMI tags can exchange data. Call before EnsureUnifiedHmiTag with plcTag binding.")]
        public static ResponseObjectDescribe EnsureUnifiedHmiConnection(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("connectionName: HMI connection name")] string connectionName = "HMI_Connection_1",
            [Description("plcName: PLC software/device symbolic name")] string plcName = "PLC_1")
        {
            try
            {
                var res = Portal.EnsureUnifiedHmiConnection(hmiSoftwarePath, connectionName, plcName);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI connection '{connectionName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiScreenItem"), Description("[L2][HMI-Unified] Create or verify a single Unified HMI control (button, lamp, IO field, etc.) on a screen. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. itemType: Button, Rectangle (lamp/indicator), IOField (value display/entry), or full CLR type name. For a complete screen layout use ApplyUnifiedHmiScreenDesignJson instead.")]
        public static ResponseMessage EnsureUnifiedHmiScreenItem(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: screen item name")] string itemName,
            [Description("itemType: Button, Rectangle/Lamp, IOField, or full CLR type name")] string itemType = "Button",
            [Description("left: X position")] int left = 0,
            [Description("top: Y position")] int top = 0,
            [Description("width: item width")] uint width = 120,
            [Description("height: item height")] uint height = 40,
            [Description("text: optional button/display text")] string text = "")
        {
            try
            {
                return Portal.EnsureUnifiedHmiScreenItem(hmiSoftwarePath, screenName, itemName, itemType, left, top, width, height, text);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring HMI screen item '{itemName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiScreenDesignJson"), Description("[L2][HMI-Unified] PREFERRED for natural-language HMI design. Apply a complete JSON layout spec to a screen in one call: screen size + multiple controls (Button/Rectangle/IOField) with positions, text, and properties. Requires: Connect + OpenProject + EnsureUnifiedHmiScreen. Better than calling EnsureUnifiedHmiScreenItem multiple times. Use BuildUnifiedHmiLayoutDesignJson to generate the JSON from a grid description.")]
        public static ResponseMessage ApplyUnifiedHmiScreenDesignJson(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("designJson: JSON object with optional screen properties and items array")] string designJson,
            [Description("strict: true fails the tool when any property/text write fails; false keeps legacy best-effort behavior.")] bool strict = true)
        {
            try
            {
                return Portal.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, designJson, strict);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI design to '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiThemeDesignJson"), Description("[L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a theme/palette. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiThemeDesignJson(
            [Description("themeJson: JSON {name?, palette:{Page?,Surface?,Text?,Border?,...}} with TIA ARGB colors like 0xFFF4F6F8.")] string themeJson)
        {
            try
            {
                var root = JsonNode.Parse(themeJson) as JsonObject
                    ?? throw new ArgumentException("themeJson root must be an object.");
                var design = HmiUnifiedThemeLayoutBuilder.BuildThemeDesign(root);
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = "Unified HMI theme design JSON built offline",
                    Data = design,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["offlineOnly"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid Unified HMI theme JSON: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiLayoutDesignJson"), Description("[L2][HMI-Unified][Offline] Build ApplyUnifiedHmiScreenDesignJson-compatible JSON from a grid layout. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiLayoutDesignJson(
            [Description("layoutJson: JSON {grid?,left?,top?,gap?,columns?,cellWidth?,cellHeight?,items:[{name,type?,row?,col?,rowSpan?,colSpan?,text?,properties?}]}.")] string layoutJson)
        {
            try
            {
                var root = JsonNode.Parse(layoutJson) as JsonObject
                    ?? throw new ArgumentException("layoutJson root must be an object.");
                var design = HmiUnifiedThemeLayoutBuilder.BuildLayoutDesign(root);
                return new ResponseJsonReport
                {
                    Ok = true,
                    Message = "Unified HMI layout design JSON built offline",
                    Data = design,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true, ["offlineOnly"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid Unified HMI layout JSON: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiTheme"), Description("[L2][HMI-Unified] Apply a theme/palette to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify with DescribeHmiScreenItem/readback before saving.")]
        public static ResponseMessage ApplyUnifiedHmiTheme(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("themeJson: JSON accepted by BuildUnifiedHmiThemeDesignJson.")] string themeJson)
        {
            try
            {
                var design = BuildUnifiedHmiThemeDesignJson(themeJson).Data?.ToJsonString() ?? "{}";
                return Portal.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI theme: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ApplyUnifiedHmiLayout"), Description("[L2][HMI-Unified] Apply a grid layout to a real Unified HMI screen through ApplyUnifiedHmiScreenDesignJson. Requires a connected TIA project; verify changed items with DescribeHmiScreenItem/readback before saving.")]
        public static ResponseMessage ApplyUnifiedHmiLayout(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("layoutJson: JSON accepted by BuildUnifiedHmiLayoutDesignJson.")] string layoutJson)
        {
            try
            {
                var design = BuildUnifiedHmiLayoutDesignJson(layoutJson).Data?.ToJsonString() ?? "{}";
                return Portal.ApplyUnifiedHmiScreenDesignJson(hmiSoftwarePath, screenName, design);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI layout: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildClassicHmiScreenXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI screen XML document from structured JSON. It does not connect to TIA Portal, import screens, or modify projects. Validate in a temporary Classic HMI project before using on a real project.")]
        public static ResponseXmlBuild BuildClassicHmiScreenXml(
            [Description("designJson: JSON object with Screen/Items. Items support Type=Text/Button/IOField/Lamp/Rectangle plus Name/Left/Top/Width/Height/Text/Properties.")] string designJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(ClassicHmiScreenXmlBuilder.BuildFromJson(designJson), "Classic HMI screen XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI screen XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcUdtXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 PLC UDT/PlcStruct XML document from structured JSON. Input: {members:[{name,datatype,externalWritable?,commentZhCn?}]}. It only returns XML; it does not connect to TIA Portal, import types, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcUdtXml(
            [Description("udtJson: JSON object with members[]. Required member fields: name, datatype. Optional: externalWritable, commentZhCn/comment.")] string udtJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildUdt(udtJson), "PLC UDT XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC UDT builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcTagTableXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 PLC tag table XML document from structured JSON. Input: {tableName,tags:[{name,dataTypeName,logicalAddress}]}. It only returns XML; it does not connect to TIA Portal, import tag tables, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcTagTableXml(
            [Description("tagTableJson: JSON object with tableName/name and tags[]. Required tag fields: name, dataTypeName/datatype, logicalAddress/address.")] string tagTableJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildTagTable(tagTableJson), "PLC tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC tag table builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildPlcGlobalDbXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 PLC GlobalDB XML document from structured JSON. Input: {dbName,dbNumber,staticMembers:[{name,datatype,externalWritable?,commentZhCn?,startValue?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildPlcGlobalDbXml(
            [Description("globalDbJson: JSON object with dbName/name, dbNumber/number, and staticMembers[] or members[].")] string globalDbJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildGlobalDb(globalDbJson), "PLC GlobalDB XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC GlobalDB builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildStructuredTextXml"), Description("[L2][PLC-Builders][Offline] Build a TIA V21 StructuredText/v4 XML fragment from operation JSON. Input: {operations:[{op:'if'|'else'|'endif'|'assignment'|'token'|'blank'|'newline', ...}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildStructuredTextXml(
            [Description("structuredTextJson: JSON object with operations[]. assignment uses target + literalValue/value; if uses condition/variable; token uses text.")] string structuredTextJson,
            [Description("innerOnly: true returns only inner XML for embedding into a block composer; false returns <StructuredText>.")] bool innerOnly = false)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildStructuredText(structuredTextJson, innerOnly), "PLC StructuredText XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid StructuredText builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "BuildFlgNetCallXml"), Description("[L2][PLC-Builders][Offline] NARROW SCOPE: builds ONLY a LAD network that calls one FC with parameters. For general ladder (contacts/coils/SR/compare/Move/math) author S7DCL text and import with ImportBlocksFromDocuments — there is no XML builder for those, and hand-written FlgNet XML is the usual cause of import errors. Build a TIA V21 LAD FlgNet/v5 FC call network XML from structured JSON. Input: {callName,parameters:[{name,section,dataType,sourceKind?,symbolPath?|symbol?|value?}]}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild BuildFlgNetCallXml(
            [Description("flgNetJson: JSON object with callName/name and parameters[]. Global parameters use symbolPath[] or dotted symbol; constants use sourceKind='constant' and value.")] string flgNetJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.BuildFlgNetCall(flgNetJson), "PLC FlgNet call XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid FlgNet call builder input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcFcBlockXml"), Description("[L2][PLC-Builders][Offline] Compose a TIA V21 SCL FC block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs:[{name,datatype}],outputs:[{name,datatype}],structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, or modify projects.")]
        public static ResponseXmlBuild ComposePlcFcBlockXml(
            [Description("fcBlockJson: JSON object with blockName/name, blockNumber/number, inputs[], outputs[], and structuredTextInnerXml or structuredText.operations[].")] string fcBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFcBlock(fcBlockJson), "PLC FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcFbBlockXml"), Description("[L2][PLC-Builders][Offline] Compose a TIA V21 SCL FB block XML from interface JSON and StructuredText content. Input: {blockName,blockNumber,inputs?,outputs?,inouts?,statics?,temps?,structuredTextInnerXml? or structuredText:{operations:[]}}. It only returns XML; it does not connect to TIA Portal, import blocks, write files, create instance DBs, or modify projects.")]
        public static ResponseXmlBuild ComposePlcFbBlockXml(
            [Description("fbBlockJson: JSON object with blockName/name, blockNumber/number, optional inputs/outputs/inouts/statics/temps arrays, and structuredTextInnerXml or structuredText.operations[].")] string fbBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeFbBlock(fbBlockJson), "PLC FB block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC FB composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ComposePlcLadFcBlockXml"), Description("[L2][PLC-Builders][Offline] NARROW SCOPE: every network must be an FC call; this cannot emit contacts/coils/SR/compare/Move/math. For general ladder, author S7DCL text (.s7dcl + .s7res) and import with ImportBlocksFromDocuments instead. Compose a TIA V21 LAD FC block XML containing one or more FlgNet/v5 FC-call networks. Each network is an FC call described as { callJson: { callName, parameters[] }, titleZhCn?, commentZhCn? }. Top-level: blockName, blockNumber, optional inputs/outputs members, optional commentZhCn / titleZhCn. Returns XML only; does not connect to TIA Portal or import. Pair with ImportBlock.")]
        public static ResponseXmlBuild ComposePlcLadFcBlockXml(
            [Description("ladFcBlockJson: JSON object with blockName, blockNumber, networks[] (each with callJson{callName,parameters[]}, optional titleZhCn/commentZhCn), optional inputs[]/outputs[] interface members with commentZhCn, optional commentZhCn/titleZhCn block-level.")] string ladFcBlockJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(PlcBuilderToolJson.ComposeLadFcBlock(ladFcBlockJson), "PLC LAD FC block XML composed offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Invalid PLC LAD FC composer input: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "PlcBuildAndImport"), Description("[L1][PLC-Software] MAIN tool for creating new PLC blocks from natural language. Build one PLC artifact (UDT/tag table/GlobalDB/FC/FB) from structured JSON, then optionally import and compile. Use dryRun=true first to validate. Workflow: describe block in JSON → dryRun → review → dryRun=false to import. Replaces the multi-step Build*Xml + ImportBlock sequence.")]
        public static ResponsePlcProgramImport PlcBuildAndImport(
            [Description("softwarePath: PLC software path, e.g. 'PLC_1'. Required only when dryRun=false.")] string softwarePath,
            [Description("kind: udt|tagtable|globaldb|fc|fb")] string kind,
            [Description("json: structured JSON matching the corresponding BuildPlc* tool.")] string json,
            [Description("typeGroupPath: PLC data type group path for kind=udt.")] string typeGroupPath = "",
            [Description("tagFolderPath: PLC tag table group path for kind=tagtable.")] string tagFolderPath = "",
            [Description("blockGroupPath: PLC block group path for kind=globaldb|fc.")] string blockGroupPath = "",
            [Description("compileAfter: compile PLC after import when dryRun=false.")] bool compileAfter = true,
            [Description("dryRun: true builds XML and returns the import plan without importing/compiling.")] bool dryRun = true)
        {
            var failed = new List<ImportFailure>();
            var importedTypes = new List<string>();
            var importedTagTables = new List<string>();
            var importedBlocks = new List<string>();
            ResponseCompile? compile = null;

            try
            {
                var normalizedKind = NormalizePlcBuildKind(kind);
                var capability = AnalyzePlcBuildCapability(normalizedKind, json);
                var build = BuildPlcArtifact(normalizedKind, json);
                var xml = build["xml"]?.ToString() ?? "";
                var objectName = ResolveBuiltPlcObjectName(xml);
                var tempDir = Path.Combine(Path.GetTempPath(), "tia_mcp_plc_build_import_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
                Directory.CreateDirectory(tempDir);
                var fileName = MakeSafeFileName(string.IsNullOrWhiteSpace(objectName) ? normalizedKind : objectName) + ".xml";
                var xmlPath = Path.Combine(tempDir, fileName);
                File.WriteAllText(xmlPath, xml, System.Text.Encoding.UTF8);

                var classifiedKind = ClassifyPlcXml(xmlPath, out var subKind, out var classifiedObjectName);
                if (classifiedKind == "unknown")
                {
                    failed.Add(new ImportFailure { Path = xmlPath, Error = "Generated XML could not be classified as a supported PLC XML artifact." });
                }

                var discoveredTypes = classifiedKind == "type" ? new List<string> { classifiedObjectName } : new List<string>();
                var discoveredTagTables = classifiedKind == "tagtable" ? new List<string> { classifiedObjectName } : new List<string>();
                var discoveredBlocks = classifiedKind == "block" ? new List<string> { classifiedObjectName } : new List<string>();

                if (!dryRun && failed.Count == 0)
                    compile = PlcBuildAndImportApply(softwarePath, typeGroupPath, tagFolderPath, blockGroupPath, compileAfter, xmlPath, classifiedKind, classifiedObjectName, importedTypes, importedTagTables, importedBlocks, failed);

                var response = BuildPlcProgramImportResponse(
                    tempDir,
                    dryRun,
                    discoveredTypes,
                    discoveredTagTables,
                    new List<string>(),
                    discoveredBlocks,
                    importedTypes,
                    importedTagTables,
                    new List<string>(),
                    importedBlocks,
                    failed,
                    compile);
                response.BuildKind = normalizedKind;
                response.CapabilityDecision = capability.Decision;
                response.CapabilityWarnings = capability.Warnings;
                response.RecommendedNextActions = capability.NextActions;
                response.GeneratedDirectory = tempDir;
                response.WrittenFiles = new[] { xmlPath };
                response.Message = dryRun
                    ? $"PLC build/import dry-run kind={normalizedKind}: generated '{xmlPath}', classified={classifiedKind}/{subKind}, failed={failed.Count}"
                    : $"PLC build/import kind={normalizedKind}: generated '{xmlPath}', importedTypes={importedTypes.Count}, importedTagTables={importedTagTables.Count}, importedBlocks={importedBlocks.Count}, failed={failed.Count}, compileState={compile?.State ?? "-"}";
                response.Meta ??= new JsonObject();
                response.Meta["offlineBuildOk"] = build["ok"]?.GetValue<bool>() == true;
                response.Meta["classifiedKind"] = classifiedKind;
                response.Meta["classifiedSubKind"] = subKind;
                response.Meta["capabilityDecision"] = capability.Decision;
                response.Meta["capabilityWarnings"] = new JsonArray(capability.Warnings.Select(x => (JsonNode)x).ToArray());
                response.Meta["recommendedNextActions"] = new JsonArray(capability.NextActions.Select(x => (JsonNode)x).ToArray());
                return response;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running PlcBuildAndImport: {ex.Message}", ex, McpErrorCode.InvalidParams);
            }
        }

        private static ResponseCompile? PlcBuildAndImportApply(
            string softwarePath,
            string typeGroupPath,
            string tagFolderPath,
            string blockGroupPath,
            bool compileAfter,
            string xmlPath,
            string classifiedKind,
            string classifiedObjectName,
            List<string> importedTypes,
            List<string> importedTagTables,
            List<string> importedBlocks,
            List<ImportFailure> failed)
        {
            if (classifiedKind == "type")
            {
                try { Portal.ImportType(softwarePath, typeGroupPath, xmlPath); importedTypes.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else if (classifiedKind == "tagtable")
            {
                try { Portal.ImportPlcTagTable(softwarePath, tagFolderPath, xmlPath); importedTagTables.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else if (classifiedKind == "block")
            {
                try { Portal.ImportBlock(softwarePath, blockGroupPath, xmlPath); importedBlocks.Add(classifiedObjectName); }
                catch (PortalException pex) { failed.Add(new ImportFailure { Path = xmlPath, Error = pex.Message }); }
            }
            else
            {
                failed.Add(new ImportFailure { Path = xmlPath, Error = "Unsupported classified kind: " + classifiedKind });
            }

            if (!compileAfter || failed.Count != 0)
                return null;

            try
            {
                var result = Portal.CompileSoftware(softwarePath);
                return BuildCompileResponse(softwarePath, result);
            }
            catch (PortalException pex)
            {
                failed.Add(new ImportFailure { Path = softwarePath, Error = $"[{pex.Code}] {pex.Message}" });
                return null;
            }
        }

        private static ResponseXmlBuild BuildOfflineXmlBuilderReport(JsonObject data, string successMessage)
        {
            var ok = data["ok"]?.GetValue<bool>() == true;
            var xml = data["xml"]?.GetValue<string>();

            // Builders use one of two error shapes:
            //   PlcBuilderToolJson:        ["error"] = string?
            //   ClassicHmi*XmlBuilder:     ["errors"] = JsonArray of string
            string[]? errorList = null;
            if (data["errors"] is JsonArray errArr)
            {
                errorList = errArr.Where(e => e != null).Select(e => e!.GetValue<string>()).ToArray();
                if (errorList.Length == 0) errorList = null;
            }
            else
            {
                var singleError = data["error"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(singleError))
                    errorList = new[] { singleError! };
            }

            string[]? warningList = null;
            if (data["warnings"] is JsonArray warnArr)
            {
                warningList = warnArr.Where(w => w != null).Select(w => w!.GetValue<string>()).ToArray();
                if (warningList.Length == 0) warningList = null;
            }

            return new ResponseXmlBuild
            {
                Ok = ok,
                Message = ok ? successMessage : successMessage + " with validation findings",
                Data = data,
                Xml = xml,
                Errors = errorList,
                Warnings = warningList,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok,
                    ["offlineOnly"] = true
                }
            };
        }


        private static string NormalizePlcBuildKind(string kind)
        {
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant().Replace("-", "").Replace("_", "");
            return normalized switch
            {
                "udt" => "udt",
                "type" => "udt",
                "plcstruct" => "udt",
                "tagtable" => "tagtable",
                "plctagtable" => "tagtable",
                "globaldb" => "globaldb",
                "db" => "globaldb",
                "fc" => "fc",
                "function" => "fc",
                "fb" => "fb",
                "functionblock" => "fb",
                _ => throw new ArgumentException("Unsupported PLC build kind. Supported values: udt|tagtable|globaldb|fc|fb.")
            };
        }

        private static JsonObject BuildPlcArtifact(string kind, string json)
        {
            return kind switch
            {
                "udt" => PlcBuilderToolJson.BuildUdt(json),
                "tagtable" => PlcBuilderToolJson.BuildTagTable(json),
                "globaldb" => PlcBuilderToolJson.BuildGlobalDb(json),
                "fc" => PlcBuilderToolJson.ComposeFcBlock(json),
                "fb" => PlcBuilderToolJson.ComposeFbBlock(json),
                _ => throw new ArgumentException("Unsupported PLC build kind: " + kind)
            };
        }

        private sealed class PlcBuildCapabilityDecision
        {
            public string Decision { get; set; } = "xml-dsl";
            public List<string> Warnings { get; } = new();
            public List<string> NextActions { get; } = new();
        }

        private static PlcBuildCapabilityDecision AnalyzePlcBuildCapability(string kind, string json)
        {
            var result = new PlcBuildCapabilityDecision();
            if (kind != "fc" && kind != "fb")
            {
                result.Decision = "declaration-xml";
                result.NextActions.Add("Import generated declaration XML in dependency order, then run CompileAndDiagnosePlc.");
                return result;
            }

            JsonObject? root = null;
            try
            {
                root = JsonNode.Parse(json) as JsonObject;
            }
            catch
            {
                result.Warnings.Add("PLC JSON could not be parsed for capability analysis; builder will still validate the schema.");
                result.NextActions.Add("Fix JSON parsing/schema errors before importing into TIA Portal.");
                return result;
            }

            var structuredText = root?["structuredText"] as JsonObject;
            var operations = structuredText?["operations"] as JsonArray;
            if (operations == null)
            {
                if (root?["structuredTextInnerXml"] != null || root?["structuredTextXml"] != null || root?["sclInnerXml"] != null)
                {
                    result.Decision = "raw-structuredtext-xml";
                    result.Warnings.Add("Raw StructuredText XML was supplied. This path is only safe when cloned from a TIA export or generated by a verified builder.");
                    result.NextActions.Add("Dry-run first, import into a disposable project, then require CompileAndDiagnosePlc errors=0.");
                }
                return result;
            }

            var risky = new List<string>();
            for (var i = 0; i < operations.Count; i++)
            {
                if (operations[i] is not JsonObject op) continue;
                foreach (var name in new[] { "condition", "source", "sym", "name" })
                {
                    if (op[name] is JsonNode n && LooksLikeSclExpression(n.ToString()))
                        risky.Add($"$.structuredText.operations[{i}].{name}='{n}'");
                }
            }

            if (risky.Count > 0)
            {
                result.Decision = "external-scl-recommended";
                result.Warnings.Add("The PLC XML DSL is intentionally narrow. Complex SCL expressions were detected: " + string.Join("; ", risky.Take(8)));
                result.NextActions.Add("Prefer a native .scl/.s7dcl external source and import via ImportFromDocuments/ImportBlocksFromDocuments, or use a verified SCL template from templates/plc/scl-examples.");
                result.NextActions.Add("If you still use XML DSL, split expressions into verified primitive operations and run dryRun=true plus CompileAndDiagnosePlc.");
            }
            else
            {
                result.NextActions.Add("Run dryRun=true first, then import with compileAfter=true and require CompileAndDiagnosePlc errors=0.");
            }

            return result;
        }

        private static bool LooksLikeSclExpression(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var s = value.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal)) return false;
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (string.Equals(s, "TRUE", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "FALSE", StringComparison.OrdinalIgnoreCase)) return true;
            return s.Any(IsComplexSclToken);
        }

        private static bool IsComplexSclToken(char ch)
        {
            return char.IsWhiteSpace(ch) || ch == '(' || ch == ')' || ch == '+' || ch == '-' || ch == '*' || ch == '/' ||
                   ch == '<' || ch == '>' || ch == '=' || ch == ':' || ch == ';' || ch == ',';
        }

        private static string ResolveBuiltPlcObjectName(string xml)
        {
            try
            {
                var doc = XDocument.Parse(xml);
                var obj = doc.Root?.Elements().FirstOrDefault(e =>
                    e.Name.LocalName.StartsWith("SW.Types.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Tags.", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.StartsWith("SW.Blocks.", StringComparison.OrdinalIgnoreCase));
                var attrs = obj?.Element("AttributeList");
                var name = attrs?.Element("Name")?.Value;
                return string.IsNullOrWhiteSpace(name) ? "" : name!.Trim();
            }
            catch
            {
                return "";
            }
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (string.IsNullOrWhiteSpace(name) ? "plc_artifact" : name.Trim())
                .Select(ch => invalid.Contains(ch) ? '_' : ch)
                .ToArray();
            var safe = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(safe) ? "plc_artifact" : safe;
        }

        private static ResponseCompile BuildCompileResponse(string softwarePath, object result)
        {
            var collected = new CompilerMessageCollectResult();
            try
            {
                var messagesValue = result.GetType().GetProperty("Messages")?.GetValue(result);
                collected = CollectCompilerMessages(messagesValue);
            }
            catch
            {
                // best effort only
            }

            var state = result.GetType().GetProperty("State")?.GetValue(result)?.ToString() ?? "";
            var errorCount = ReadIntProperty(result, "ErrorCount");
            var warningCount = ReadIntProperty(result, "WarningCount");
            return new ResponseCompile
            {
                Message = $"Software '{softwarePath}' compiled. State={state} Errors={errorCount} Warnings={warningCount}",
                State = state,
                ErrorCount = errorCount,
                WarningCount = warningCount,
                Messages = collected.Raw,
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = !state.Equals("Error", StringComparison.OrdinalIgnoreCase),
                    ["errorDetailCount"] = collected.Errors.Count,
                    ["warningDetailCount"] = collected.Warnings.Count
                }
            };
        }

        private static int ReadIntProperty(object value, string propertyName)
        {
            var raw = value.GetType().GetProperty(propertyName)?.GetValue(value);
            if (raw is int i) return i;
            return int.TryParse(raw?.ToString(), out var parsed) ? parsed : 0;
        }

        [McpServerTool(Name = "BuildClassicHmiTagTableXml"), Description("[L2][HMI-Classic]Offline-only helper: build a Classic/Basic WinCC HMI tag table XML document from structured JSON. Supports plain HMI tags and symbolic PLC bindings through Connection + ControllerTag/PlcTag. It does not connect to TIA Portal, import tags, or modify projects.")]
        public static ResponseXmlBuild BuildClassicHmiTagTableXml(
            [Description("tableJson: JSON object with Name/TableName and Tags[]. Tag fields: Name, DataType, Length, optional Connection and ControllerTag/PlcTag.")] string tableJson)
        {
            try
            {
                return BuildOfflineXmlBuilderReport(ClassicHmiTagTableXmlBuilder.BuildFromJson(tableJson), "Classic HMI tag table XML built offline");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI tag table XML offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildClassicHmiMinimalPackage"), Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package from structured JSON. It returns tag-table XML, screen XML, import order, and readiness checks that screen item tag references are declared in the tag table. It does not connect to TIA Portal, import files, or modify projects.")]
        public static ResponseJsonReport BuildClassicHmiMinimalPackage(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable. Screen items may reference HMI tags through Tag/HmiTag/ProcessValueTag or Properties.*Tag.")] string packageJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.BuildFromJson(packageJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package built offline" : "Classic HMI minimal package built with validation findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Classic HMI minimal package offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WriteClassicHmiMinimalPackageFiles"), Description("[L2][HMI-Classic]Offline-only helper: build a minimal Classic/Basic HMI package and write tag-table XML, screen XML, and manifest JSON to an output directory. It does not connect to TIA Portal, import files, or modify projects.")]
        public static ResponseJsonReport WriteClassicHmiMinimalPackageFiles(
            [Description("packageJson: JSON object with Name, ScreenDesign, and TagTable.")] string packageJson,
            [Description("outputDirectory: directory where tag-table XML, screen XML, and manifest JSON will be written.")] string outputDirectory)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.WriteFiles(packageJson, outputDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;

                string[]? files = null;
                if (data["files"] is JsonArray arr)
                {
                    files = arr.Where(f => f != null).Select(f => f!.GetValue<string>()).ToArray();
                    if (files.Length == 0) files = null;
                }
                var outDir = data["outputDirectory"]?.GetValue<string>();

                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package files written offline" : "Classic HMI minimal package files written with validation findings",
                    Data = data,
                    OutputPath = outDir,
                    OutputFiles = files,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["fileCount"] = data["fileCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing Classic HMI minimal package files offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackageFiles"), Description("[L2][HMI-Classic]Offline-only helper: validate an already written Classic/Basic HMI minimal package folder or manifest. It reads manifest/XML, checks parseability and HMI tag references, and does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport ValidateClassicHmiMinimalPackageFiles(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFiles(path);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package files validated offline" : "Classic HMI minimal package file validation found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["missingTagCount"] = data["missingTagCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI minimal package files offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ValidateClassicHmiMinimalPackagePlcSync"), Description("[L2][HMI-Classic]Offline-only helper: validate that Classic/Basic HMI tag-table ControllerTag/PlcTag bindings exist in a caller-provided exact PLC symbol list. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport ValidateClassicHmiMinimalPackagePlcSync(
            [Description("path: package output directory or *_manifest.json path to validate.")] string path,
            [Description("plcSymbolsJson: JSON array of exact PLC symbols, or object with Symbols[]. Example: [\"DB1_MotorData.Motor.Start\"].")] string plcSymbolsJson)
        {
            try
            {
                var data = ClassicHmiMinimalPackageBuilder.ValidateFilesWithPlcSymbols(path, plcSymbolsJson);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI minimal package PLC symbol sync validated offline" : "Classic HMI minimal package PLC symbol sync validation found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["missingPlcSymbolCount"] = data["missingPlcSymbolCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error validating Classic HMI PLC symbol sync offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildPlcSymbolManifestFromXmlPath"), Description("[L2][PLC-Builders]Offline-only helper: extract a PLC symbol manifest from PLC tag table and GlobalDB XML files or directories. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildPlcSymbolManifestFromXmlPath(
            [Description("path: XML file or directory containing PLC tag table / GlobalDB XML exports.")] string path)
        {
            try
            {
                var data = PlcSymbolManifestBuilder.BuildFromXmlPath(path);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "PLC symbol manifest built offline" : "PLC symbol manifest built with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["symbolCount"] = data["symbolCount"]?.GetValue<int>() ?? 0
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building PLC symbol manifest offline: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiOfflineValidationSuite"), Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI validation suite covering PLC symbol extraction, HMI package generation, HMI tag references, and PLC-HMI sync positive/negative gates. It writes reports only to the requested report directory.")]
        public static ResponseJsonReport RunClassicHmiOfflineValidationSuite(
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = ClassicHmiOfflineValidationSuite.Run(reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI offline validation suite passed" : "Classic HMI offline validation suite found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI offline validation suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunOfflineReleaseValidationSuite"), Description("[L2][Validation]Offline-only helper: run the release smoke suite covering PLC Builder, Classic HMI, PLC symbol extraction, Unified HMI template layout, HMI action recipes, and online-monitoring safety guardrails. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport RunOfflineReleaseValidationSuite(
            [Description("workspaceRoot: repository/workspace root containing TMP_EXPORT, docs, and tools.")] string workspaceRoot,
            [Description("reportDirectory: directory where suite files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = OfflineReleaseValidationSuite.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Offline release validation suite passed" : "Offline release validation suite found issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running offline release validation suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunV2PlanCompletionAudit"), Description("[L2][Validation]Offline-only strict audit for docs/TIA_MCP_常见操作全覆盖方案_V2_二次优化计划.md. It reports verified hard-gate percentage and blocks 100% claims when real TIA/online evidence is missing.")]
        public static ResponseJsonReport RunV2PlanCompletionAudit(
            [Description("workspaceRoot: repository/workspace root containing docs, tools, and reports.")] string workspaceRoot,
            [Description("reportDirectory: directory where V2 audit reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = V2PlanCompletionAuditor.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = "V2 plan completion audit finished",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running V2 plan completion audit: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseDiagnosticReport"), Description("[L2][Reports]Build an offline diagnostic report from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseDiagnosticReport(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var data = ReleaseDiagnosticReportBuilder.Build(root);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release diagnostic report built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release diagnostic report: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseRunbook"), Description("[L2][Reports]Build an offline first-user runbook from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseRunbook(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var diagnostics = root["diagnostics"] as JsonObject ?? ReleaseDiagnosticReportBuilder.Build(root);
                var data = ReleaseRunbookBuilder.Build(root, diagnostics);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release runbook built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release runbook: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildReleaseManifest"), Description("[L2][Reports]Build an offline machine-readable release manifest from a previously generated OfflineReleaseValidationSuite JSON report. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildReleaseManifest(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(offlineReleaseSuiteJsonPath) || !File.Exists(offlineReleaseSuiteJsonPath))
                    throw new FileNotFoundException("Offline release suite JSON report not found.", offlineReleaseSuiteJsonPath);
                var root = JsonNode.Parse(File.ReadAllText(offlineReleaseSuiteJsonPath)) as JsonObject
                    ?? throw new InvalidOperationException("Offline release suite JSON root must be an object.");
                var diagnostics = root["diagnostics"] as JsonObject ?? ReleaseDiagnosticReportBuilder.Build(root);
                var runbook = root["runbook"] as JsonObject ?? ReleaseRunbookBuilder.Build(root, diagnostics);
                var data = ReleaseManifestBuilder.Build(root, diagnostics, runbook);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release manifest built.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building release manifest: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RebuildReleaseHandoffArtifacts"), Description("[L2][Reports]Rebuild diagnostics, runbook, and manifest files from an existing OfflineReleaseValidationSuite JSON report. Offline-only and does not connect to TIA Portal.")]
        public static ResponseJsonReport RebuildReleaseHandoffArtifacts(
            [Description("offlineReleaseSuiteJsonPath: path to offline_release_validation_suite_*.json.")] string offlineReleaseSuiteJsonPath,
            [Description("outputDirectory: directory where rebuilt handoff artifacts will be written.")] string outputDirectory)
        {
            try
            {
                var data = ReleaseHandoffArtifactBuilder.RebuildFromSuiteJson(offlineReleaseSuiteJsonPath, outputDirectory);
                return new ResponseJsonReport
                {
                    Ok = data["ok"]?.GetValue<bool>() == true,
                    Message = "Release handoff artifacts rebuilt.",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error rebuilding release handoff artifacts: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunClassicHmiTemporaryImportPreflight"), Description("[L2][HMI-Classic]Offline-only helper: run the Classic/Basic HMI temporary-import preflight. It checks TIA V21 environment, Openness group, package files, PLC-HMI sync, and emits an import/readback plan without connecting to TIA Portal or creating projects.")]
        public static ResponseJsonReport RunClassicHmiTemporaryImportPreflight(
            [Description("workspaceRoot: repository/workspace root.")] string workspaceRoot,
            [Description("reportDirectory: directory where preflight files and reports will be written.")] string reportDirectory)
        {
            try
            {
                var data = ClassicHmiTemporaryImportPreflightSuite.Run(workspaceRoot, reportDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Classic HMI temporary import preflight passed" : "Classic HMI temporary import preflight blocked",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running Classic HMI temporary import preflight: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunHmiTemplatePlcSyncPrecheckSuite"), Description("[L2][Validation]Offline-only helper: verify Unified HMI template RequiredTags against real PLC tag/DB-member XML symbols before any HMI binding. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport RunHmiTemplatePlcSyncPrecheckSuite(
            [Description("templateDirectory: directory containing Unified HMI template JSON files.")] string templateDirectory,
            [Description("plcXmlPath: PLC XML file or directory exported from TIA, containing tag tables and/or GlobalDB XML.")] string plcXmlPath,
            [Description("reportDirectory: directory where reports will be written.")] string reportDirectory,
            [Description("mappingFilePath: optional explicit mapping file produced by the mapping skeleton flow.")] string mappingFilePath = "")
        {
            try
            {
                var data = HmiTemplatePlcSyncPrecheckSuite.Run(templateDirectory, plcXmlPath, reportDirectory, mappingFilePath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template PLC sync precheck suite completed" : "HMI template PLC sync precheck suite found blocking issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI template PLC sync precheck suite: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignJson"), Description("[L2][HMI-Unified]Offline-only helper: convert one Unified HMI template JSON file into the execution JSON accepted by ApplyUnifiedHmiScreenDesignJson, with layout QA attached. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignJson(
            [Description("templateFile: full path to a Unified HMI template JSON file")] string templateFile,
            [Description("fallbackWidth: width used only if the template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if the template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
                {
                    var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                    var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                    var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                    return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
                });
                var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
                var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
                var itemCount = (designJson["items"] as JsonArray)?.Count ?? 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design JSON built offline" : "Unified HMI template execution design JSON built with blocking layout findings",
                    Data = new JsonObject
                    {
                        ["format"] = "tia-unified-hmi-template-apply-design-offline-v1",
                        ["timestamp"] = DateTime.Now.ToString("O"),
                        ["offlineOnly"] = true,
                        ["templateFile"] = templateFile,
                        ["ok"] = ok,
                        ["itemCount"] = itemCount,
                        ["layoutQa"] = layout,
                        ["applyDesign"] = designJson
                    },
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["itemCount"] = itemCount
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design JSON: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiTemplateApplyDesignManifest"), Description("[L2][HMI-Unified]Offline-only helper: build a directory-level manifest for Unified HMI templates. It summarizes layout QA and execution-design readiness for every unified_*.json template without returning full apply payloads. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport BuildUnifiedHmiTemplateApplyDesignManifest(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("fallbackWidth: width used only if a template omits Screen.Width")] int fallbackWidth = 800,
            [Description("fallbackHeight: height used only if a template omits Screen.Height")] int fallbackHeight = 480)
        {
            try
            {
                var files = Directory.Exists(templateDirectory)
                    ? Directory.GetFiles(templateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                        .Where(path => Path.GetFileName(path).StartsWith("unified_", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                    : Array.Empty<string>();
                var rows = new JsonArray();
                var referenceAnalysis = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, "", "");
                var referenceRows = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .ToDictionary(
                        x => x["templateName"]?.ToString() ?? "",
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                var referenceRowsByFile = (referenceAnalysis["templates"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Where(x => !string.IsNullOrWhiteSpace(x["file"]?.ToString()))
                    .ToDictionary(
                        x => Path.GetFullPath(x["file"]?.ToString() ?? ""),
                        x => x,
                        StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var templateName = Path.GetFileNameWithoutExtension(file);
                    referenceRowsByFile.TryGetValue(Path.GetFullPath(file), out var referenceRow);
                    if (referenceRow == null)
                    {
                        referenceRows.TryGetValue(templateName, out referenceRow);
                    }
                    rows.Add(BuildUnifiedHmiTemplateApplyDesignManifestRow(file, fallbackWidth, fallbackHeight, referenceRow));
                }

                var failed = rows.OfType<JsonObject>().Count(x => x["ok"]?.GetValue<bool>() != true);
                var totalItems = rows.OfType<JsonObject>().Sum(x => x["itemCount"]?.GetValue<int>() ?? 0);
                var root = new JsonObject
                {
                    ["format"] = "tia-unified-hmi-template-apply-design-manifest-v1",
                    ["timestamp"] = DateTime.Now.ToString("O"),
                    ["offlineOnly"] = true,
                    ["templateDirectory"] = templateDirectory,
                    ["templateCount"] = files.Length,
                    ["failed"] = failed,
                    ["totalItems"] = totalItems,
                    ["ok"] = failed == 0,
                    ["policy"] = new JsonObject
                    {
                        ["fullPayloadTool"] = "BuildUnifiedHmiTemplateApplyDesignJson",
                        ["applyTool"] = "ApplyUnifiedHmiScreenDesignJson",
                        ["rule"] = "Use this manifest as a pre-apply gate; inspect a full payload for any template before writing it to TIA."
                    },
                    ["templates"] = rows
                };

                var ok = failed == 0;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template execution design manifest built offline" : "Unified HMI template execution design manifest has blocking findings",
                    Data = root,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true,
                        ["templateCount"] = files.Length,
                        ["failed"] = failed,
                        ["totalItems"] = totalItems
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI template execution design manifest: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static JsonObject BuildUnifiedHmiTemplateApplyDesignManifestRow(string templateFile, int fallbackWidth, int fallbackHeight, JsonObject? referenceRow)
        {
            var layout = HmiTemplateLayoutAnalyzer.AnalyzeFile(templateFile, path =>
            {
                var templateRoot = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                var expectedItems = (templateRoot?["Items"] as JsonArray ?? templateRoot?["items"] as JsonArray ?? new JsonArray()).Count;
                var design = HmiTemplateDesignJsonBuilder.BuildApplyDesign(path, fallbackWidth, fallbackHeight);
                return design["items"] is JsonArray executionItems && executionItems.Count == expectedItems;
            });
            var designJson = HmiTemplateDesignJsonBuilder.BuildApplyDesign(templateFile, fallbackWidth, fallbackHeight);
            var items = designJson["items"] as JsonArray ?? new JsonArray();
            var errors = layout["errors"] as JsonArray ?? new JsonArray();
            var warnings = layout["warnings"] as JsonArray ?? new JsonArray();
            var ok = string.Equals(layout["status"]?.ToString(), "pass", StringComparison.OrdinalIgnoreCase);
            var eventReadiness = BuildUnifiedHmiTemplateEventReadiness(referenceRow);
            var row = new JsonObject
            {
                ["templateFile"] = templateFile,
                ["templateName"] = layout["templateName"]?.DeepClone(),
                ["ok"] = ok,
                ["status"] = layout["status"]?.DeepClone(),
                ["width"] = designJson["width"]?.DeepClone(),
                ["height"] = designJson["height"]?.DeepClone(),
                ["itemCount"] = items.Count,
                ["errorCount"] = errors.Count,
                ["warningCount"] = warnings.Count,
                ["errors"] = new JsonArray(errors.Select(x => x?.DeepClone()).ToArray()),
                ["warnings"] = new JsonArray(warnings.Select(x => x?.DeepClone()).ToArray()),
                ["layoutDensity"] = layout["layoutDensity"]?.DeepClone(),
                ["applyDesignReady"] = layout["executionJsonChecked"]?.DeepClone(),
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["eventReadiness"] = eventReadiness,
                ["recommendedNextAction"] = ok
                    ? "Inspect full payload with BuildUnifiedHmiTemplateApplyDesignJson, then apply in a temporary TIA project before using a real project."
                    : "Fix blocking layout/template findings before applying to TIA."
            };
            row["eventRecommendedNextAction"] = eventReadiness["recommendedNextAction"]?.DeepClone();
            return row;
        }

        private static JsonObject BuildUnifiedHmiTemplateEventReadiness(JsonObject? referenceRow)
        {
            if (referenceRow == null)
            {
                return new JsonObject
                {
                    ["status"] = "not-analyzed",
                    ["effectiveActionCount"] = 0,
                    ["safeDeterministicActionCount"] = 0,
                    ["apiDiscoveryRequiredCount"] = 0,
                    ["highRiskActionCount"] = 0,
                    ["todoActionCount"] = 0,
                    ["missingTargetCount"] = 0,
                    ["duplicateActionCount"] = 0,
                    ["commandActionCount"] = 0,
                    ["navigationActionCount"] = 0,
                    ["recommendedNextAction"] = "Run HmiTemplateReferenceAnalyzer first; event and binding readiness could not be joined for this template."
                };
            }

            var summary = referenceRow["actionRecipeSummary"] as JsonObject ?? new JsonObject();
            var effectiveRecipes = summary["effectiveRecipes"] as JsonArray ?? new JsonArray();
            var generated = new JsonArray();
            var safeDeterministic = 0;
            var apiDiscovery = 0;
            var todo = 0;
            var blocked = 0;
            var command = 0;
            var navigation = 0;

            foreach (var recipeNode in effectiveRecipes.OfType<JsonObject>())
            {
                var targetTags = (recipeNode["targetTags"] as JsonArray ?? new JsonArray()).Select(x => x?.ToString() ?? "");
                var built = HmiActionScriptRecipeBuilder.Build(
                    recipeNode["recipeKind"]?.ToString() ?? "",
                    recipeNode["event"]?.ToString() ?? "",
                    targetTags,
                    recipeNode["targetScreen"]?.ToString() ?? "",
                    recipeNode["targetPopup"]?.ToString() ?? "");
                generated.Add(built);
                var kind = built["recipeKind"]?.ToString() ?? "";
                var safety = built["safetyLevel"]?.ToString() ?? "";
                if (string.Equals(safety, "command", StringComparison.OrdinalIgnoreCase)) command++;
                if (string.Equals(safety, "navigation", StringComparison.OrdinalIgnoreCase)) navigation++;
                if (built["requiresApiDiscovery"]?.GetValue<bool>() == true) apiDiscovery++;
                if (built["applyBlocked"]?.GetValue<bool>() == true || !string.IsNullOrWhiteSpace(built["applyBlockedReason"]?.ToString())) blocked++;
                if ((built["script"]?.ToString() ?? "").IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0) todo++;
                if (built["ok"]?.GetValue<bool>() == true
                    && built["requiresApiDiscovery"]?.GetValue<bool>() != true
                    && (kind.Equals("set-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("reset-bit", StringComparison.OrdinalIgnoreCase)
                        || kind.Equals("toggle-bit", StringComparison.OrdinalIgnoreCase)))
                {
                    safeDeterministic++;
                }
            }

            var missingTargets = summary["missingTargets"] as JsonArray ?? new JsonArray();
            var duplicateActions = summary["duplicateActions"] as JsonArray ?? new JsonArray();
            var highRisk = GetManifestInt(summary, "highRiskWrites");
            var missingRequiredTags = summary["missingRequiredTags"] as JsonArray ?? new JsonArray();
            var status = "ready-for-temp-project-validation";
            var recommended = "Generate safe deterministic scripts, then verify HMI tags, PLC-side symbols, TIA SyntaxCheck, and readback in a temporary project.";

            if (missingRequiredTags.Count > 0 || missingTargets.Count > 0 || duplicateActions.Count > 0)
            {
                status = "needs-template-fix";
                recommended = "Fix missing action tags, missing target screens/popups, or duplicate actions before applying events.";
            }
            else if (highRisk > 0)
            {
                status = "blocked-by-high-risk-actions";
                recommended = "High-risk value writes require explicit operator confirmation, range validation, SyntaxCheck, and readback before any apply path is enabled.";
            }
            else if (apiDiscovery > 0 || todo > 0)
            {
                status = "needs-api-discovery";
                recommended = "Keep API-discovery actions blocked until the exact WinCC Unified V21 event/navigation/popup API is verified from TIA readback.";
            }

            return new JsonObject
            {
                ["status"] = status,
                ["requiredTagCount"] = GetManifestInt(referenceRow, "requiredTagCount"),
                ["dynamizationCount"] = GetManifestInt(referenceRow, "dynamizationCount"),
                ["actionCount"] = GetManifestInt(referenceRow, "actionCount"),
                ["effectiveActionCount"] = GetManifestInt(summary, "effectiveActionCount"),
                ["safeDeterministicActionCount"] = safeDeterministic,
                ["apiDiscoveryRequiredCount"] = apiDiscovery,
                ["blockedActionCount"] = blocked,
                ["highRiskActionCount"] = highRisk,
                ["todoActionCount"] = todo,
                ["missingRequiredTagCount"] = missingRequiredTags.Count,
                ["missingTargetCount"] = missingTargets.Count,
                ["duplicateActionCount"] = duplicateActions.Count,
                ["commandActionCount"] = command,
                ["navigationActionCount"] = navigation,
                ["recommendedNextAction"] = recommended
            };
        }

        private static int GetManifestInt(JsonObject? obj, string name)
        {
            if (obj == null || obj[name] == null) return 0;
            return int.TryParse(obj[name]?.ToString(), out var value) ? value : 0;
        }

        [McpServerTool(Name = "BindUnifiedHmiButtonPressedTag"), Description("[L2][HMI-Unified]Bind a Unified HMI button PressedStateTags entry to an HMI tag (momentary press behavior, best-effort).")]
        public static ResponseMessage BindUnifiedHmiButtonPressedTag(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("tagName: HMI tag name to write while pressed")] string tagName)
        {
            try
            {
                return Portal.BindUnifiedHmiButtonPressedTag(hmiSoftwarePath, screenName, buttonName, tagName);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error binding button '{buttonName}' to tag '{tagName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ListUnifiedHmiApiTypes"), Description("[L2][HMI-Unified]List loaded WinCC Unified HMI API types/enums by name filter, useful for discovering event and dynamization types.")]
        public static ResponseStringList ListUnifiedHmiApiTypes(
            [Description("nameContains: case-insensitive substring filter, e.g. Dynamization or EventType")] string nameContains = "",
            [Description("limit: max returned type lines")] int limit = 500)
        {
            try
            {
                var items = Portal.ListUnifiedHmiApiTypes(nameContains, limit);
                return new ResponseStringList
                {
                    Message = $"Unified HMI API types listed (filter='{nameContains}')",
                    Items = items,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing Unified HMI API types: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiButtonEventHandler"), Description("[L2][HMI-Unified]Ensure a Unified HMI button event handler exists and return its API shape. eventType must match HmiButtonEventType.")]
        public static ResponseMessage EnsureUnifiedHmiButtonEventHandler(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: HmiButtonEventType value, e.g. Tapped, Down, Up; use ListUnifiedHmiApiTypes('HmiButtonEventType') to inspect")] string eventType)
        {
            try
            {
                return Portal.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring button event handler '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DescribeUnifiedHmiButtonEventScript"), Description("[L2][HMI-Unified]Describe a Unified HMI button event handler Script property and its current object members/attributes.")]
        public static ResponseObjectDescribe DescribeUnifiedHmiButtonEventScript(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: enum value, e.g. Tapped, Down, Up")] string eventType,
            [Description("maxMembers: max member count")] int maxMembers = 200)
        {
            try
            {
                var res = Portal.DescribeUnifiedHmiButtonEventScript(hmiSoftwarePath, screenName, buttonName, eventType, maxMembers);
                res.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (res.Members != null && res.Members.Any()) };
                return res;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error describing button event script '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetUnifiedHmiButtonEventScriptCode"), Description("[L2][HMI-Unified]Set ScriptCode on a Unified HMI button event ScriptDynamization and run SyntaxCheck.")]
        public static ResponseMessage SetUnifiedHmiButtonEventScriptCode(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: enum value, e.g. Tapped, Down, Up")] string eventType,
            [Description("scriptCode: JavaScript code for the event")] string scriptCode,
            [Description("globalDefinitionAreaScriptCode: optional global definitions for the script")] string globalDefinitionAreaScriptCode = "",
            [Description("async: whether the script is async")] bool async = false)
        {
            try
            {
                return Portal.SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, scriptCode, globalDefinitionAreaScriptCode, async);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting button event script '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BuildUnifiedHmiButtonActionScript"), Description("[L2][HMI-Unified]Build a safe Unified HMI button action script from a high-level action recipe without connecting to TIA.")]
        public static ResponseMessage BuildUnifiedHmiButtonActionScript(
            [Description("actionKind: set-bit, reset-bit, toggle-bit, open-popup, goto-screen, confirm-write")] string actionKind,
            [Description("eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released")] string eventType,
            [Description("targetTag: target HMI tag for set/reset/toggle actions")] string targetTag = "",
            [Description("targetScreen: target screen for goto-screen actions")] string targetScreen = "",
            [Description("targetPopup: target popup for open-popup actions")] string targetPopup = "")
        {
            try
            {
                var tags = string.IsNullOrWhiteSpace(targetTag)
                    ? Array.Empty<string>()
                    : new[] { targetTag };
                var recipe = HmiActionScriptRecipeBuilder.Build(actionKind, eventType, tags, targetScreen, targetPopup);
                return new ResponseMessage
                {
                    Message = recipe["ok"]?.GetValue<bool>() == true
                        ? "Unified HMI button action script recipe built."
                        : "Unified HMI button action script recipe has validation errors.",
                    Meta = recipe
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error building Unified HMI button action script: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "RunHmiActionScriptRecipeSafetySelfTest"), Description("[L2][Diagnostics]Offline-only helper: prove deterministic HMI button action scripts are allowed only for safe set/reset/toggle bit recipes, while high-risk writes and unverified navigation/popup recipes are blocked.")]
        public static ResponseJsonReport RunHmiActionScriptRecipeSafetySelfTest()
        {
            try
            {
                var data = HmiActionScriptRecipeBuilder.RunSafetySelfTest();
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI action script recipe safety self-test passed" : "HMI action script recipe safety self-test failed",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error running HMI action script recipe safety self-test: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiButtonAction"), Description("[L2][HMI-Unified]Generate and apply a deterministic Unified HMI button action. Only set-bit/reset-bit/toggle-bit are applied; high-risk or TODO recipes are rejected.")]
        public static ResponseMessage EnsureUnifiedHmiButtonAction(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("buttonName: HMI button item name")] string buttonName,
            [Description("eventType: HmiButtonEventType value, e.g. Down (press), Up (release), Tapped — NOT Pressed/Released")] string eventType,
            [Description("actionKind: set-bit, reset-bit, or toggle-bit")] string actionKind,
            [Description("targetTag: verified target HMI tag")] string targetTag)
        {
            try
            {
                var recipe = HmiActionScriptRecipeBuilder.Build(actionKind, eventType, new[] { targetTag });
                var kind = recipe["recipeKind"]?.ToString() ?? "";
                var script = recipe["script"]?.ToString() ?? "";
                var allowed = new[] { "set-bit", "reset-bit", "toggle-bit" };
                if (!allowed.Contains(kind, StringComparer.OrdinalIgnoreCase))
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Only set-bit/reset-bit/toggle-bit recipes can be applied by this safe high-level tool.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected by safety policy.", Meta = recipe };
                }
                if (recipe["ok"]?.GetValue<bool>() != true || string.IsNullOrWhiteSpace(script) || script.IndexOf("TODO", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    recipe["applyStatus"] = "rejected";
                    recipe["applyReason"] = "Recipe has errors, empty script, or TODO placeholder.";
                    return new ResponseMessage { Message = "Unified HMI button action rejected because the generated script is not directly applicable.", Meta = recipe };
                }

                var ensure = Portal.EnsureUnifiedHmiButtonEventHandler(hmiSoftwarePath, screenName, buttonName, eventType);
                var set = Portal.SetUnifiedHmiButtonEventScriptCode(hmiSoftwarePath, screenName, buttonName, eventType, script, "", false);
                recipe["applyStatus"] = set.Meta?["success"]?.GetValue<bool>() == true ? "applied" : "apply-failed";
                recipe["ensureMessage"] = ensure.Message ?? "";
                recipe["setMessage"] = set.Message ?? "";
                recipe["setMeta"] = set.Meta?.DeepClone();
                return new ResponseMessage
                {
                    Message = "Unified HMI button action applied via generated recipe.",
                    Meta = recipe
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error applying Unified HMI button action '{buttonName}.{eventType}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "EnsureUnifiedHmiDynamization"), Description("[L2][HMI-Unified]Ensure a Unified HMI item property dynamization exists using a concrete dynamization type and return its API shape.")]
        public static ResponseMessage EnsureUnifiedHmiDynamization(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: HMI screen item name")] string itemName,
            [Description("propertyName: target property name, e.g. BackColor or Visible")] string propertyName,
            [Description("dynamizationType: type short name/full name; empty tries common candidates and returns errors if unsupported")] string dynamizationType = "")
        {
            try
            {
                return Portal.EnsureUnifiedHmiDynamization(hmiSoftwarePath, screenName, itemName, propertyName, dynamizationType);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error ensuring dynamization '{itemName}.{propertyName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "BindUnifiedHmiTagDynamization"), Description("[L2][HMI-Unified]Ensure a Unified HMI TagDynamization exists for an item property and bind it to an HMI tag.")]
        public static ResponseMessage BindUnifiedHmiTagDynamization(
            [Description("hmiSoftwarePath: path to HMI software (e.g. 'HMI_RT_1')")] string hmiSoftwarePath,
            [Description("screenName: target screen name")] string screenName,
            [Description("itemName: HMI screen item name")] string itemName,
            [Description("propertyName: target property name, e.g. BackColor or Visible")] string propertyName,
            [Description("tagName: HMI tag name used as the dynamic source")] string tagName,
            [Description("dataType: tag data type, e.g. Bool, Int, Real")] string dataType = "Bool",
            [Description("plcTag: optional PLC tag/path")] string plcTag = "",
            [Description("address: optional absolute address")] string address = "")
        {
            try
            {
                return Portal.BindUnifiedHmiTagDynamization(hmiSoftwarePath, screenName, itemName, propertyName, tagName, dataType, plcTag, address);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error binding tag dynamization '{itemName}.{propertyName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiScreens"), Description("[L2][HMI] List all screen names in an HMI (Classic or Unified). Requires: Connect + OpenProject. softwarePath from GetProjectTree. Use before EnsureUnifiedHmiScreen/ExportHmiScreen to confirm which screens exist.")]
        public static ResponseStringList GetHmiScreens(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetHmiScreens(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI screens listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI screens for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiTagTables"), Description("[L2][HMI]List HMI tag table names (Classic/Unified, best-effort)")]
        public static ResponseStringList GetHmiTagTables(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetHmiTagTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tag tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tag tables for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiTags"), Description("[L2][HMI]List HMI tag names (best-effort). If tagTableName empty, returns tags found at root collection if available.")]
        public static ResponseStringList GetHmiTags(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: optional tag table name to list tags from")] string tagTableName = "")
        {
            try
            {
                var items = Portal.GetHmiTags(softwarePath, tagTableName);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI tags listed for '{softwarePath}' (table='{tagTableName}')",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI tags for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetHmiConnections"), Description("[L2][HMI]List HMI connection names (Classic/Unified, best-effort)")]
        public static ResponseStringList GetHmiConnections(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetHmiConnections(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"HMI connections listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing HMI connections for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiScreen"), Description("[L2][HMI]Export one HMI screen to a file (best-effort; requires Openness export support)")]
        public static ResponseExportFile ExportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("screenName: the screen name to export")] string screenName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\screen.xml)")] string exportPath)
        {
            try
            {
                Portal.ExportHmiScreen(softwarePath, screenName, exportPath);
                return new ResponseExportFile
                {
                    Message = $"HMI screen '{screenName}' exported",
                    ExportPath = exportPath,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI screen '{screenName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI screen '{screenName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiTagTable"), Description("[L2][HMI]Export one HMI tag table to a file (best-effort; requires Openness export support)")]
        public static ResponseExportFile ExportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("tagTableName: the tag table name to export")] string tagTableName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\tagtable.xml)")] string exportPath)
        {
            try
            {
                Portal.ExportHmiTagTable(softwarePath, tagTableName, exportPath);
                return new ResponseExportFile
                {
                    Message = $"HMI tag table '{tagTableName}' exported",
                    ExportPath = exportPath,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI tag table '{tagTableName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI tag table '{tagTableName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiConnection"), Description("[L2][HMI]Export one HMI connection to a file (best-effort; Classic/Unified via reflection)")]
        public static ResponseExportFile ExportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("connectionName: the HMI connection name to export")] string connectionName,
            [Description("exportPath: full file path to write to (e.g. C:\\\\temp\\\\connection.xml)")] string exportPath)
        {
            try
            {
                Portal.ExportHmiConnection(softwarePath, connectionName, exportPath);
                return new ResponseExportFile
                {
                    Message = $"HMI connection '{connectionName}' exported",
                    ExportPath = exportPath,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed exporting HMI connection '{connectionName}' from '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI connection '{connectionName}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportHmiProgram"), Description("[L2][HMI]Batch export HMI screens/tagtables into a directory (best-effort)")]
        public static ResponseBatchExport ExportHmiProgram(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("exportDir: directory to write exported files into")] string exportDir,
            [Description("exportScreens: default true")] bool exportScreens = true,
            [Description("exportTagTables: default true")] bool exportTagTables = true)
        {
            try
            {
                var res = Portal.ExportHmiProgram(softwarePath, exportDir, exportScreens, exportTagTables);
                if (res != null)
                {
                    return new ResponseBatchExport
                    {
                        Message = $"HMI program exported to '{exportDir}'",
                        Exported = res.Value.Exported,
                        Failed = res.Value.Failed,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }
                throw new McpException($"HMI software not found at '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting HMI program: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreen"), Description("[L2][HMI]Import one HMI screen XML file into an HMI program (best-effort; Classic/Unified via reflection)")]
        public static ResponseMessage ImportHmiScreen(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported screen XML")] string importPath)
        {
            try
            {
                Portal.ImportHmiScreen(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI screen imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI screen from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screen: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTable"), Description("[L2][HMI]Import one HMI tag table XML file into an HMI program (best-effort; Classic/Unified via reflection)")]
        public static ResponseMessage ImportHmiTagTable(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("importPath: full file path of exported tag table XML")] string importPath)
        {
            try
            {
                Portal.ImportHmiTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI tag table imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiConnection"), Description("[L2][HMI]Import one HMI connection XML file into an HMI program (best-effort; Classic/Unified via reflection)")]
        public static ResponseMessage ImportHmiConnection(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("importPath: full file path of exported HMI connection XML")] string importPath)
        {
            try
            {
                Portal.ImportHmiConnection(softwarePath, importPath);
                return new ResponseMessage
                {
                    Message = $"HMI connection imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing HMI connection from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI connection: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiScreensFromDirectory"), Description("[L2][HMI]Batch import HMI screen .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportHmiScreensFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional screen group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported screen XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportHmiScreensFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI screens from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI screens from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportHmiTagTablesFromDirectory"), Description("[L2][HMI]Batch import HMI tag table .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportHmiTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the HMI software")] string softwarePath,
            [Description("folderPath: optional tag table group path inside HMI (use empty for root)")] string folderPath,
            [Description("dir: directory containing exported tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportHmiTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} HMI tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing HMI tag tables from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetCrossReferences"), Description("[L2][PLC-Software]Get cross references for a Step7 block/type (best-effort). Requires applicable object and Openness support.")]
        public static ResponseCrossReferences GetCrossReferences(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("objectPath: blockPath or typePath inside the PLC software")] string objectPath,
            [Description("objectKind: Block or Type")] string objectKind = "Block",
            [Description("filter: CrossReferenceFilter enum name (e.g. AllObjects, ObjectsWithReferences, UnusedObjects)")] string filter = "AllObjects")
        {
            try
            {
                var items = Portal.GetCrossReferences(softwarePath, objectPath, objectKind, filter);
                if (items != null)
                {
                    return new ResponseCrossReferences
                    {
                        Message = $"Cross references retrieved for {objectKind} '{objectPath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Cross reference service not available for {objectKind} '{objectPath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving cross references: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcExternalSources"), Description("[L2][PLC-Software]List PLC external source names (best-effort)")]
        public static ResponseStringList GetPlcExternalSources(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcExternalSources(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC external sources listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{Portal.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC external sources: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcTagTables"), Description("[L2][PLC-Software] List all PLC tag table names. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). Use before ExportPlcTagTable to get exact table names, or before ImportPlcTagTable to check for conflicts.")]
        public static ResponseStringList GetPlcTagTables(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcTagTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC tag tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{Portal.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC tag tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcTagTable"), Description("[L2][PLC-Software]Export one PLC tag table (PlcTagTable) to XML file")]
        public static ResponseExportFile ExportPlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("tagTableName: PLC tag table name")] string tagTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
        {
            try
            {
                var ok = Portal.ExportPlcTagTable(softwarePath, tagTableName, exportPath);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC tag table '{tagTableName}' exported",
                        ExportPath = exportPath,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Failed exporting PLC tag table '{tagTableName}' from '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTable"), Description("[L1][PLC-Software]Import one PLC tag table XML file into PLC software (best-effort)")]
        public static ResponseMessage ImportPlcTagTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of PLC tag table XML")] string importPath)
        {
            try
            {
                Portal.ImportPlcTagTable(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"PLC tag table imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC tag table from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcTagTablesFromDirectory"), Description("[L2][PLC-Software]Batch import PLC tag table .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportPlcTagTablesFromDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional tag table group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing PLC tag table XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportPlcTagTablesFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} PLC tag tables from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC tag tables from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcWatchTables"), Description("[L2][PLC-Software]List PLC watch/monitor table names (PlcWatchTable). Read-only.")]
        public static ResponseStringList GetPlcWatchTables(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var items = Portal.GetPlcWatchTables(softwarePath);
                if (items != null)
                {
                    return new ResponseStringList
                    {
                        Message = $"PLC watch tables listed for '{softwarePath}'",
                        Items = items,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"PLC software not found at '{softwarePath}'.{Portal.AvailablePlcPathsSuffix()}", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing PLC watch tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPlcForceTables"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " List all force table names in the PLC software." +
            " Force tables configure which variables are continuously forced to specific values while the CPU is online." +
            " Use SetForceTableEntry to configure entries, then go online for the forces to take effect.")]
        public static ResponseStringList GetPlcForceTables(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                var names = Portal.GetPlcForceTables(softwarePath);
                return new ResponseStringList
                {
                    Items = names ?? new List<string>(),
                    Message = names == null ? $"PLC software '{softwarePath}' not found." : $"{names.Count} force table(s) found.",
                    Meta = new JsonObject { ["softwarePath"] = softwarePath, ["timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing force tables for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetWatchTableModifyValue"), Description(
            "[L2][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+GoOnline]" +
            " Configure a watch table entry to write a value to a PLC variable once (or on a trigger)." +
            " This is an OFFLINE CONFIGURATION step — the value is written to the PLC only when TIA Portal is online and the trigger fires." +
            " Trigger options: Permanent (every cycle), PermanentAtStart (every cycle, at scan start), OnceOnlyAtStart (single write at scan start), PermanentAtEnd, OnceOnlyAtEnd, OnceOnlyAtStop." +
            " Use GoOnline before calling this for the write to reach the PLC." +
            " Does NOT use Force — variable reverts to PLC logic after the modify. To hold a value persistently, use SetForceTableEntry instead." +
            " Example: SetWatchTableModifyValue('PLC_1', 'Debug_WT', 'DB1.DBX0.0', 'TRUE', 'OnceOnlyAtStart')")]
        public static ResponseMessage SetWatchTableModifyValue(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: name of the watch table to configure (created if not existing)")] string tableName,
            [Description("address: variable address, e.g. 'DB1.DBX0.0', '%M0.0', 'MyTag'")] string address,
            [Description("modifyValue: value to write, e.g. 'TRUE', '42', '3.14'")] string modifyValue,
            [Description("trigger: when to apply the write — Permanent | PermanentAtStart | OnceOnlyAtStart | PermanentAtEnd | OnceOnlyAtEnd | OnceOnlyAtStop (default: Permanent)")] string trigger = "Permanent")
        {
            try
            {
                return Portal.EnsureWatchTableEntry(softwarePath, tableName, address, modifyValue, trigger);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting watch table entry: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Force-write capability retained in the Portal layer but intentionally NOT exposed as an MCP tool:
        // forcing overrides live PLC logic and must not be AI-invocable. Online monitoring stays read-only
        // (see RunOnlineMonitoringSafetySelfTest / Test_OnlineMonitoringNoUnsafeToolNames). Use TIA Portal
        // directly for commissioning forces. Removed from the tool surface in 0.0.38.
        public static ResponseMessage SetForceTableEntry(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("tableName: name of the force table to configure (created if not existing)")] string tableName,
            [Description("address: variable address to force, e.g. 'DB1.DBX0.0', '%M0.0'")] string address,
            [Description("forceValue: value to force, e.g. 'TRUE', '42'")] string forceValue)
        {
            try
            {
                return Portal.EnsureForceTableEntry(softwarePath, tableName, address, forceValue);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error setting force table entry: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTable"), Description("[L2][PLC-Software]Export one PLC watch/monitor table (PlcWatchTable) to XML file. Read-only against the TIA project.")]
        public static ResponseExportFile ExportPlcWatchTable(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("watchTableName: PLC watch table name")] string watchTableName,
            [Description("exportPath: full file path to write to")] string exportPath)
        {
            try
            {
                var ok = Portal.ExportPlcWatchTable(softwarePath, watchTableName, exportPath);
                if (ok)
                {
                    return new ResponseExportFile
                    {
                        Message = $"PLC watch table '{watchTableName}' exported",
                        ExportPath = exportPath,
                        Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                    };
                }

                throw new McpException($"Failed exporting PLC watch table '{watchTableName}' from '{softwarePath}'", McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch table: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportPlcWatchTablesToDirectory"), Description("[L2][PLC-Software]Export all PLC watch/monitor tables to XML files. Read-only against the TIA project.")]
        public static ResponseImportBatch ExportPlcWatchTablesToDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("dir: output directory")] string dir,
            [Description("regexName: optional regex filter applied to table name")] string regexName = "")
        {
            try
            {
                var result = Portal.ExportPlcWatchTablesToDirectory(softwarePath, dir, regexName);
                return new ResponseImportBatch
                {
                    Message = $"Exported {result.Imported?.Count() ?? 0} PLC watch tables to '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error exporting PLC watch tables: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ProbePlcMonitorOnlineCapabilities"), Description("[L2][Online-Monitoring]Read-only probe for PLC online/offline/watch/monitor API surfaces. It does not go online/offline, change watch tables, write values, or touch restricted safety APIs.")]
        public static ResponseJsonReport ProbePlcMonitorOnlineCapabilities(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath)
        {
            try
            {
                var result = Portal.ProbePlcMonitorOnlineCapabilities(softwarePath);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing PLC monitor/online capabilities: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ReadPlcWatchTableCurrentValuesReadOnly"), Description("[L2][Online-Monitoring] Read current/monitor value properties from an existing PLC watch table only. It does not create/modify watch tables, write PLC values, go offline, or use force operations.")]
        public static ResponseJsonReport ReadPlcWatchTableCurrentValuesReadOnly(
            [Description("softwarePath: PLC software path resolved from GetProjectTree/ValidateAutomationContext.")] string softwarePath,
            [Description("watchTableName: existing PLC watch table path/name returned by GetPlcWatchTables.")] string watchTableName,
            [Description("maxEntries: maximum entries to inspect.")] int maxEntries = 50)
        {
            try
            {
                var result = Portal.ReadPlcWatchTableCurrentValuesReadOnly(softwarePath, watchTableName, maxEntries);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading PLC watch table values read-only: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanOnlineReadOnlyMonitoring"), Description("[L2][Online-Monitoring] Validate an online-monitoring request shape without connecting to TIA Portal. Read-only preflight only: no go-online/offline, no watch-table modification, no value write, and no force operation.")]
        public static ResponseJsonReport PlanOnlineReadOnlyMonitoring(
            [Description("softwarePath: PLC software path resolved from GetProjectTree/ValidateAutomationContext.")] string softwarePath,
            [Description("tagPathsJson: JSON array of symbolic PLC tag/member paths, for example [\"DB_HMI.MotorRun\",\"DB_HMI.SpeedSet\"]. Do not pass guessed M bits.")] string tagPathsJson,
            [Description("mode: current-values or watch-table-export-plan. Both are read-only planning modes.")] string mode = "current-values")
        {
            try
            {
                var warnings = new JsonArray();
                var acceptedTags = new JsonArray();
                var rejectedTags = new JsonArray();
                var policy = new JsonArray();
                foreach (var policyLine in GetOnlineMonitoringSafetyPolicy())
                {
                    policy.Add(policyLine);
                }
                var normalizedMode = (mode ?? string.Empty).Trim();
                var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "current-values",
                    "watch-table-export-plan"
                };

                if (!allowedModes.Contains(normalizedMode))
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, $"Unsupported mode '{mode}'. Supported values: current-values, watch-table-export-plan.");
                }

                if (string.IsNullOrWhiteSpace(softwarePath))
                {
                    warnings.Add("softwarePath is empty. Resolve the PLC software path from GetProjectTree before real online monitoring.");
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(tagPathsJson);
                }
                catch (Exception ex)
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, "tagPathsJson must be a JSON array of symbolic PLC paths. Parse error: " + ex.Message);
                }

                if (parsed is not JsonArray tagArray)
                {
                    return BuildOnlineMonitoringPlanResponse(false, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, "tagPathsJson must be a JSON array.");
                }

                foreach (var item in tagArray)
                {
                    var tag = item?.GetValue<string>()?.Trim() ?? string.Empty;
                    var rejectReason = GetOnlineMonitoringTagRejectReason(tag);
                    if (rejectReason == null)
                    {
                        acceptedTags.Add(tag);
                    }
                    else
                    {
                        rejectedTags.Add(new JsonObject
                        {
                            ["tagPath"] = tag,
                            ["reason"] = rejectReason
                        });
                    }
                }

                if (acceptedTags.Count == 0)
                {
                    warnings.Add("No accepted tag paths. Real online monitoring requires at least one declared PLC symbol or DB member.");
                }

                var ok = rejectedTags.Count == 0 && acceptedTags.Count > 0;
                var message = ok
                    ? "Online read-only monitoring plan validated. This preflight did not connect to TIA Portal."
                    : "Online read-only monitoring plan rejected. Fix rejected tag paths before any real online workflow.";

                return BuildOnlineMonitoringPlanResponse(ok, softwarePath, normalizedMode, acceptedTags, rejectedTags, warnings, policy, message);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning online read-only monitoring: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildOnlineMonitoringPlanResponse(bool ok, string softwarePath, string mode, JsonArray acceptedTags, JsonArray rejectedTags, JsonArray warnings, JsonArray policy, string message)
        {
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = new JsonObject
                {
                    ["softwarePath"] = softwarePath,
                    ["mode"] = mode,
                    ["readOnly"] = true,
                    ["connectsToTia"] = false,
                    ["goesOnlineOrOffline"] = false,
                    ["modifiesWatchTables"] = false,
                    ["writesPlcValues"] = false,
                    ["usesForce"] = false,
                    ["acceptedTags"] = acceptedTags,
                    ["rejectedTags"] = rejectedTags,
                    ["warnings"] = warnings,
                    ["policy"] = policy
                },
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok
                }
            };
        }

        private static string? GetOnlineMonitoringTagRejectReason(string tagPath)
        {
            if (string.IsNullOrWhiteSpace(tagPath))
            {
                return "Tag path is empty.";
            }

            var forbiddenIntent = new[]
            {
                "force", "write", "modify", "update", "create",
                "delete", "remove", "import", "insert", "download", "activate", "start", "stop",
                "goonline", "gooffline", "watchtable", "forcetable"
            };
            var compact = Regex.Replace(tagPath, @"[\s_\-\.]+", string.Empty);
            var segments = Regex.Split(tagPath, @"[\.\s_\-]+").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            var forbidden = forbiddenIntent.FirstOrDefault(x =>
                compact.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                segments.Any(segment => segment.StartsWith(x, StringComparison.OrdinalIgnoreCase)));
            if (forbidden != null)
            {
                return $"Tag path contains unsafe online/write/force/watch-table intent keyword '{forbidden}'.";
            }

            if (Regex.IsMatch(tagPath, @"^%?[MIQ][BWD]?\d+(\.\d+)?$", RegexOptions.IgnoreCase))
            {
                return "Absolute I/Q/M address is not accepted for HMI/online planning. Use a declared PLC symbol or DB member read back from the project.";
            }

            if (!Regex.IsMatch(tagPath, @"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$"))
            {
                return "Use a symbolic PLC path with at least one member separator, for example DB_HMI.MotorRun.";
            }

            return null;
        }

        [McpServerTool(Name = "PlanOnlineReadOnlyDataProvider"), Description("[L2][Online-Monitoring] Plan the commercial current-value path through an external read-only data provider such as opcua or s7-readonly. This is a preflight only: it does not connect, write PLC values, modify watch tables, go online/offline through TIA, or use force operations.")]
        public static ResponseJsonReport PlanOnlineReadOnlyDataProvider(
            [Description("provider: opcua or s7-readonly. opcua is preferred for commercial symbolic readback.")] string provider,
            [Description("endpoint: OPC UA endpoint URL or PLC endpoint/IP. It is validated only for shape and is not opened.")] string endpoint,
            [Description("tagPathsJson: JSON array of declared symbolic PLC tags/DB members. Guessed M bits and unsafe intent names are rejected.")] string tagPathsJson,
            [Description("optionsJson: optional JSON object such as {\"pollMs\":1000,\"source\":\"watch-table-export\"}.")] string optionsJson = "{}")
        {
            try
            {
                var normalizedProvider = (provider ?? "").Trim().ToLowerInvariant();
                var allowedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "opcua",
                    "s7-readonly"
                };

                var policy = new JsonArray(GetOnlineMonitoringSafetyPolicy().Select(x => JsonValue.Create(x)).ToArray());
                var warnings = new JsonArray();
                var acceptedTags = new JsonArray();
                var rejectedTags = new JsonArray();
                var options = ParseJsonObjectOrEmpty(optionsJson, "optionsJson");

                if (!allowedProviders.Contains(normalizedProvider))
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, $"Unsupported provider '{provider}'. Supported providers: opcua, s7-readonly.");
                }

                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    warnings.Add("endpoint is empty. Real read-only providers require an OPC UA endpoint URL or PLC endpoint/IP before execution.");
                }

                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(tagPathsJson);
                }
                catch (Exception ex)
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, "tagPathsJson must be a JSON array. Parse error: " + ex.Message);
                }

                if (parsed is not JsonArray tagArray)
                {
                    return BuildReadOnlyProviderPlan(false, normalizedProvider, endpoint, acceptedTags, rejectedTags, warnings, policy, options, "tagPathsJson must be a JSON array.");
                }

                foreach (var item in tagArray)
                {
                    var tag = item?.GetValue<string>()?.Trim() ?? "";
                    var rejectReason = GetOnlineMonitoringTagRejectReason(tag);
                    if (rejectReason == null)
                    {
                        acceptedTags.Add(tag);
                    }
                    else
                    {
                        rejectedTags.Add(new JsonObject
                        {
                            ["tagPath"] = tag,
                            ["reason"] = rejectReason
                        });
                    }
                }

                if (normalizedProvider == "s7-readonly")
                {
                    warnings.Add("s7-readonly must be implemented as a read-only adapter with no Write/Force API surface exposed by MCP.");
                }

                var ok = acceptedTags.Count > 0 && rejectedTags.Count == 0;
                return BuildReadOnlyProviderPlan(
                    ok,
                    normalizedProvider,
                    endpoint,
                    acceptedTags,
                    rejectedTags,
                    warnings,
                    policy,
                    options,
                    ok
                        ? "Read-only data provider plan validated. This preflight did not open a network connection."
                        : "Read-only data provider plan rejected. Fix rejected tags/provider settings before any real read workflow.");
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning read-only data provider: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private static ResponseJsonReport BuildReadOnlyProviderPlan(bool ok, string provider, string endpoint, JsonArray acceptedTags, JsonArray rejectedTags, JsonArray warnings, JsonArray policy, JsonObject options, string message)
        {
            return new ResponseJsonReport
            {
                Ok = ok,
                Message = message,
                Data = new JsonObject
                {
                    ["provider"] = provider,
                    ["endpoint"] = endpoint ?? "",
                    ["implementationPath"] = provider.Equals("opcua", StringComparison.OrdinalIgnoreCase)
                        ? "Use OPC UA read/subscribe as the preferred commercial current-value channel."
                        : "Use a strictly read-only S7 adapter for address/symbol reads when OPC UA is unavailable.",
                    ["status"] = "planned-read-only-provider",
                    ["usesTiaOpennessForCurrentValues"] = false,
                    ["usesTiaOpennessForTagDiscovery"] = true,
                    ["readOnly"] = true,
                    ["connectsNow"] = false,
                    ["writesPlcValues"] = false,
                    ["modifiesWatchTables"] = false,
                    ["usesForce"] = false,
                    ["acceptedTags"] = acceptedTags,
                    ["rejectedTags"] = rejectedTags,
                    ["warnings"] = warnings,
                    ["policy"] = policy,
                    ["options"] = options
                },
                Meta = new JsonObject
                {
                    ["timestamp"] = DateTime.Now,
                    ["success"] = ok
                }
            };
        }

        private static JsonObject ParseJsonObjectOrEmpty(string json, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                throw new McpException(parameterName + " must be a JSON object. Parse error: " + ex.Message, ex, McpErrorCode.InvalidParams);
            }
        }

        [McpServerTool(Name = "ProbeGlobalLibrary"), Description("[L2][HMI-Library]Open a TIA global library (.al21) read-only/best-effort and list accessible master copies/types/folders through public/reflection APIs. It does not import library content.")]
        public static ResponseGlobalLibraryProbe ProbeGlobalLibrary(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("maxItems: maximum items per list")] int maxItems = 500)
        {
            try
            {
                var result = Portal.ProbeGlobalLibrary(libraryPath, maxItems);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error probing global library: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportMasterCopyFromGlobalLibrary"), Description("[L2][HMI-Library] Import one MasterCopy from a TIA global library into a real Unified HMI screen and return ScreenItems readback evidence. This modifies the project, must be tried in a temporary project first, and reports failure unless the imported item is visible after readback.")]
        public static ResponseGlobalLibraryImport ImportMasterCopyFromGlobalLibrary(
            [Description("libraryPath: full path to .al21 file or its containing folder")] string libraryPath,
            [Description("masterCopyName: exact or suffix path/name from ProbeGlobalLibrary MasterCopies readback")] string masterCopyName,
            [Description("hmiSoftwarePath: real Unified HMI software path resolved from GetProjectTree, e.g. HMI_RT_1")] string hmiSoftwarePath,
            [Description("screenName: existing target Unified screen name; create it first with EnsureUnifiedHmiScreen if needed")] string screenName,
            [Description("importedItemName: optional expected item name after import; empty means use masterCopyName leaf")] string importedItemName = "",
            [Description("left: optional Left coordinate applied after import when supported")] int left = 0,
            [Description("top: optional Top coordinate applied after import when supported")] int top = 0)
        {
            try
            {
                var result = Portal.ImportMasterCopyFromGlobalLibrary(libraryPath, masterCopyName, hmiSoftwarePath, screenName, importedItemName, left, top);
                result.Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = result.Ok == true };
                return result;
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing global-library master copy: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeGlobalLibraryPackage"), Description("[L2][HMI-Library]Analyze a TIA global library folder offline by file-system structure. It does not connect to TIA Portal, open the library, import content, or modify files.")]
        public static ResponseJsonReport AnalyzeGlobalLibraryPackage(
            [Description("libraryPath: global library folder path or .al* file path")] string libraryPath)
        {
            try
            {
                var data = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                data["timestamp"] = DateTime.Now.ToString("O");
                data["safetyPolicy"] = new JsonObject
                {
                    ["mode"] = "Offline file-system analysis only.",
                    ["tia"] = "TIA Portal is not connected or opened by this analysis.",
                    ["write"] = "No global library content is imported, modified, or written."
                };

                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Global library package offline analysis completed" : "Global library package offline analysis completed with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing global library package: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "PlanGlobalLibraryTemplateReuse"), Description("[L2][HMI-Library] Plan the commercial fallback when direct MasterCopy import is not publicly verifiable: learn reference/global-library template evidence and rebuild screens with native Unified HMI MCP theme/layout/action tools. Offline planning only; it does not import library content or modify projects.")]
        public static ResponseJsonReport PlanGlobalLibraryTemplateReuse(
            [Description("libraryPath: reference global library folder path or .al* file path.")] string libraryPath,
            [Description("templateIntentJson: optional JSON {\"screenType\":\"overview\",\"targetRuntime\":\"Unified\",\"preferredComponents\":[...]}.")] string templateIntentJson = "{}")
        {
            try
            {
                var analysis = GlobalLibraryPackageAnalyzer.Analyze(libraryPath);
                var intent = ParseJsonObjectOrEmpty(templateIntentJson, "templateIntentJson");
                var exists = analysis["exists"]?.GetValue<bool>() == true;
                var hasCoreFiles = analysis["ok"]?.GetValue<bool>() == true;
                var stringHints = analysis["stringHints"] as JsonObject;
                var patternCounts = stringHints?["patternCounts"] as JsonObject;
                var screenHintCount = patternCounts?["Screen"]?.GetValue<int>() ?? 0;
                var templateHintCount = patternCounts?["Template"]?.GetValue<int>() ?? 0;
                var masterCopyHintCount = patternCounts?["MasterCopy"]?.GetValue<int>() ?? 0;

                var data = new JsonObject
                {
                    ["libraryPath"] = libraryPath,
                    ["intent"] = intent,
                    ["offlineAnalysisOk"] = exists,
                    ["hasCoreGlobalLibraryFiles"] = hasCoreFiles,
                    ["strategy"] = "template-learn-and-native-rebuild",
                    ["directMasterCopyImportRequired"] = false,
                    ["directMasterCopyImportStatus"] = "optional-unverified-path",
                    ["commercialFallbackReady"] = exists,
                    ["safety"] = new JsonObject
                    {
                        ["offlineOnly"] = true,
                        ["importsLibraryContent"] = false,
                        ["modifiesProject"] = false,
                        ["requiresReadbackBeforeClaimingDirectImport"] = true
                    },
                    ["templateEvidence"] = new JsonObject
                    {
                        ["screenHintCount"] = screenHintCount,
                        ["templateHintCount"] = templateHintCount,
                        ["masterCopyHintCount"] = masterCopyHintCount
                    },
                    ["recommendedMcpTools"] = new JsonArray(
                        "AnalyzeGlobalLibraryPackage",
                        "ProbeGlobalLibrary",
                        "BuildUnifiedHmiThemeDesignJson",
                        "BuildUnifiedHmiLayoutDesignJson",
                        "BuildUnifiedHmiTemplateApplyDesignJson",
                        "ApplyUnifiedHmiScreenDesignJson",
                        "EnsureUnifiedHmiButtonAction"),
                    ["validationGates"] = new JsonArray(
                        "Template plan has offline package evidence.",
                        "Generated Unified design JSON passes layout QA.",
                        "Applied HMI screen items are read back by DescribeHmiScreenItem.",
                        "Button actions pass SyntaxCheck with zero errors.",
                        "HMI tags bind only to declared PLC symbols/DB members."),
                    ["reconstructionPlan"] = new JsonArray(
                        "Analyze global library/package structure and string hints without importing content.",
                        "Use ProbeGlobalLibrary only as read-only evidence when TIA is available; do not claim direct MasterCopy import unless readback succeeds.",
                        "Map reusable UI intent to Unified HMI native tools: theme, layout, template apply design, and button action recipes.",
                        "Apply generated design with ApplyUnifiedHmiScreenDesignJson and verify with item readback plus action SyntaxCheck.",
                        "Bind controls only to declared PLC symbols or DB members discovered from project exports/readback."),
                    ["analysis"] = analysis
                };

                return new ResponseJsonReport
                {
                    Ok = exists,
                    Message = exists
                        ? "Global library template reuse plan built. Direct MasterCopy import remains optional until real readback is verified."
                        : "Global library template reuse plan blocked because the library path was not found.",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = exists }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error planning global library template reuse: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeHmiTemplateReference"), Description("[L2][HMI-Library]Analyze local Unified HMI JSON templates against reference-project/runtime/global-library hints offline. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport AnalyzeHmiTemplateReference(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory,
            [Description("referenceProjectPath: reference TIA project folder containing HMI runtime export/currentConfiguration")] string referenceProjectPath,
            [Description("referenceGlobalLibraryPath: reference global library folder or .al* file")] string referenceGlobalLibraryPath)
        {
            try
            {
                var data = HmiTemplateReferenceAnalyzer.Analyze(templateDirectory, referenceProjectPath, referenceGlobalLibraryPath);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "HMI template/reference offline analysis completed" : "HMI template/reference offline analysis completed with findings",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing HMI template/reference assets: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "AnalyzeUnifiedHmiTemplateLayout"), Description("[L2][HMI-Library]Offline-only QA for Unified HMI JSON templates. Checks theme metadata, screen bounds, duplicate item names, size issues, layout overlap warnings, density, and execution JSON shape. It does not connect to TIA Portal or modify projects.")]
        public static ResponseJsonReport AnalyzeUnifiedHmiTemplateLayout(
            [Description("templateDirectory: directory containing Unified HMI JSON templates")] string templateDirectory)
        {
            try
            {
                var data = HmiTemplateLayoutAnalyzer.AnalyzeDirectory(templateDirectory);
                var ok = data["ok"]?.GetValue<bool>() == true;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok ? "Unified HMI template layout offline QA completed" : "Unified HMI template layout offline QA found blocking issues",
                    Data = data,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = ok,
                        ["offlineOnly"] = true
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error analyzing Unified HMI template layout: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetTechnologyObjects"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " List all Technology Objects (TOs) in the PLC software: axes, cams, measuring inputs, etc." +
            " Returns each TO's Name, type (OfSystemLibElement), and firmware version (OfSystemLibVersion)." +
            " Use this to discover TO names before ExportTechnologyObject or GetAxisParameters." +
            " TOs are stored as TechnologicalInstanceDB instances in the TechnologicalObjectGroup.")]
        public static ResponseTechnologyObjectList GetTechnologyObjects(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                var items = Portal.GetTechnologyObjects(softwarePath);
                var typed = items.Select(jo => new TechnologyObjectInfo
                {
                    Name = jo["Name"]?.GetValue<string>(),
                    OfSystemLibElement = jo["OfSystemLibElement"]?.GetValue<string>(),
                    OfSystemLibVersion = jo["OfSystemLibVersion"]?.GetValue<string>(),
                    TypeHint = jo["TypeHint"]?.GetValue<string>(),
                }).ToArray();

                return new ResponseTechnologyObjectList
                {
                    Ok = true,
                    SoftwarePath = softwarePath,
                    Count = typed.Length,
                    Items = typed,
                    Message = $"{typed.Length} technology object(s) found in '{softwarePath}'."
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error listing technology objects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ExportTechnologyObject"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " Export a single Technology Object (axis, cam, measuring input, etc.) to an XML file." +
            " The XML can be inspected, modified offline, and re-imported with ImportTechnologyObject." +
            " Use GetTechnologyObjects first to confirm the exact TO name.")]
        public static ResponseMessage ExportTechnologyObject(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("toName: exact name of the technology object, e.g. 'Axis_1'")] string toName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\Axis_1.xml'")] string exportPath)
        {
            try { return Portal.ExportTechnologyObject(softwarePath, toName, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting technology object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportTechnologyObjectsToDirectory"), Description(
            "[L2][Category:PLC-TechnologyObjects][PreCondition:Connect+OpenProject]" +
            " Batch-export all (or regex-filtered) Technology Objects to XML files in a directory." +
            " Each TO is saved as '<TOName>.xml'. Returns lists of exported names and any failures." +
            " Use regexName to filter by TO name, e.g. 'Axis_.*' for all axes.")]
        public static ResponseImportBatch ExportTechnologyObjectsToDirectory(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportDir: directory to write XML files to, e.g. 'C:\\Temp\\TOs'")] string exportDir,
            [Description("regexName: optional regex filter on TO name; empty = export all")] string regexName = "")
        {
            try { return Portal.ExportTechnologyObjectsToDirectory(softwarePath, exportDir, regexName); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error batch-exporting technology objects: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportTechnologyObject"), Description("[L2][PLC-Software]Import one PLC Technology Object XML file into PLC software (best-effort)")]
        public static ResponseMessage ImportTechnologyObject(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional technology object group path (use empty for root)")] string folderPath,
            [Description("importPath: full file path of Technology Object XML")] string importPath)
        {
            try
            {
                Portal.ImportTechnologyObject(softwarePath, folderPath, importPath);
                return new ResponseMessage
                {
                    Message = $"Technology object imported from '{importPath}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing technology object from '{importPath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing technology object: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportTechnologyObjectsFromDirectory"), Description("[L2][PLC-Software]Batch import PLC technology object .xml files from a directory (best-effort)")]
        public static ResponseImportBatch ImportTechnologyObjectsFromDirectory(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("folderPath: optional technology object group path (use empty for root)")] string folderPath,
            [Description("dir: directory containing technology object XML files")] string dir,
            [Description("regexName: optional regex filter applied to filename without extension")] string regexName = "",
            [Description("overwrite: true=Override (default)")] bool overwrite = true)
        {
            try
            {
                var result = Portal.ImportTechnologyObjectsFromDirectory(softwarePath, folderPath, dir, regexName, overwrite);
                return new ResponseImportBatch
                {
                    Message = $"Imported {result.Imported?.Count() ?? 0} technology objects from '{dir}'. Failed={result.Failed?.Count() ?? 0}",
                    Imported = result.Imported,
                    Failed = result.Failed,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = (result.Failed == null || !result.Failed.Any()) }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing technology objects from '{dir}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "WritePlcSclSourceFile"), Description("[L1][PLC-Software][Offline] Write SCL source text to a local .scl external-source file (UTF-8 WITH BOM, so Chinese comments are not imported as mojibake/乱码). This tool does NOT connect to TIA Portal and does NOT import anything — it only writes the file to disk and returns the path plus manual-import instructions. Use it as the robust fallback when XML block import is rejected (e.g. a TIA V20 portal rejecting V21 SimaticML tokens: 'Cannot create SW.Blocks.CompileUnit... token not supported'): the user imports the .scl manually in TIA via project tree → 'External source files' → 'Add new external file', then right-clicks the source → 'Generate blocks from source'. The sclContent must be a complete source, e.g. FUNCTION_BLOCK \"Name\" ... END_FUNCTION_BLOCK.")]
        public static ResponseMessage WritePlcSclSourceFile(
            [Description("sclContent: the full SCL source text (complete FUNCTION_BLOCK / FUNCTION / DATA_BLOCK / TYPE declarations). This is written verbatim.")] string sclContent,
            [Description("outputPath: target .scl file path. If a directory is given (or the path has no extension), the file is named after the first block found in the source. Empty means a temp file under %TEMP%\\tia_mcp_scl.")] string outputPath = "")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sclContent))
                    throw new McpException("sclContent is empty — provide the full SCL source text to write.", McpErrorCode.InvalidParams);

                // Derive a default file name from the first block declaration in the source.
                var nameMatch = Regex.Match(sclContent,
                    "(?:FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|TYPE)\\s+\"?([A-Za-z_][A-Za-z0-9_]*)\"?",
                    RegexOptions.IgnoreCase);
                var defaultName = MakeSafeFileName(nameMatch.Success ? nameMatch.Groups[1].Value : "MCP_Source");

                string finalPath;
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    var dir = Path.Combine(Path.GetTempPath(), "tia_mcp_scl");
                    finalPath = Path.Combine(dir, defaultName + ".scl");
                }
                else if (Directory.Exists(outputPath) ||
                         outputPath.EndsWith("\\", StringComparison.Ordinal) ||
                         outputPath.EndsWith("/", StringComparison.Ordinal))
                {
                    finalPath = Path.Combine(outputPath, defaultName + ".scl");
                }
                else
                {
                    finalPath = string.IsNullOrEmpty(Path.GetExtension(outputPath))
                        ? outputPath + ".scl"
                        : outputPath;
                }

                var parent = Path.GetDirectoryName(finalPath);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                // UTF-8 WITH BOM: TIA reads BOM-less UTF-8 SCL with Chinese comments as mojibake.
                File.WriteAllText(finalPath, sclContent, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

                return new ResponseMessage
                {
                    Message =
                        $"SCL source written to '{finalPath}'. To import in TIA Portal: project tree → " +
                        "'External source files' → 'Add new external file' → select this .scl → " +
                        "right-click the source → 'Generate blocks from source'. " +
                        "(Or call ImportPlcExternalSource then GenerateBlocksFromExternalSource if connected.)",
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = true,
                        ["path"] = finalPath,
                        ["blockName"] = nameMatch.Success ? nameMatch.Groups[1].Value : null,
                        ["bytes"] = new System.IO.FileInfo(finalPath).Length
                    }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error writing SCL source file: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "ImportPlcExternalSource"), Description("[L2][PLC-Software]Import one PLC external source file into a group (best-effort)")]
        public static ResponseMessage ImportPlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("groupPath: external source group path (use empty for root)")] string groupPath,
            [Description("filePath: path to external source file (.scl, etc.)")] string filePath)
        {
            try
            {
                Portal.ImportPlcExternalSource(softwarePath, groupPath, filePath);
                return new ResponseMessage
                {
                    Message = "PLC external source imported",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed importing PLC external source [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error importing PLC external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DeletePlcExternalSource"), Description("[L2][PLC-Software]Delete a PLC external source by name so ImportPlcExternalSource can replace it (idempotent). Name may include or omit .scl.")]
        public static ResponseMessage DeletePlcExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources (e.g. MCPVerify_FC_SCL_v3.scl)")] string externalSourceName)
        {
            try
            {
                Portal.DeletePlcExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"PLC external source '{externalSourceName}' deleted or was not present",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed deleting PLC external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error deleting PLC external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GenerateBlocksFromExternalSource"), Description("[L2][PLC-Software]Generate blocks from a PLC external source by name (best-effort)")]
        public static ResponseMessage GenerateBlocksFromExternalSource(
            [Description("softwarePath: path in the project structure to the PLC software")] string softwarePath,
            [Description("externalSourceName: name from GetPlcExternalSources")] string externalSourceName)
        {
            try
            {
                Portal.GenerateBlocksFromExternalSource(softwarePath, externalSourceName);
                return new ResponseMessage
                {
                    Message = $"Blocks generated from external source '{externalSourceName}'",
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = true }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed generating blocks from external source '{externalSourceName}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error generating blocks from external source: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetOpcUaConfig"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Read the full OPC UA server configuration for a PLC: server interfaces, SIMATIC interfaces, and reference namespaces — each with their Name, Enabled state, and key properties." +
            " Use this to audit what OPC UA interfaces exist before enabling or exporting them." +
            " Enabled=true means the interface is active and will be downloaded to the CPU.")]
        public static ResponseJsonReport GetOpcUaConfig(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try { return Portal.GetOpcUaConfig(softwarePath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error reading OPC UA config: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "SetOpcUaInterfaceEnabled"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Enable or disable an OPC UA server interface, SIMATIC interface, or reference namespace." +
            " Setting Enabled=true activates the interface — download to PLC is required for the change to take effect on the CPU." +
            " interfaceType options: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'." +
            " Workflow: GetOpcUaConfig → SetOpcUaInterfaceEnabled → DownloadToPlc.")]
        public static ResponseMessage SetOpcUaInterfaceEnabled(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name of the interface as shown in GetOpcUaConfig")] string interfaceName,
            [Description("enabled: true to enable, false to disable")] bool enabled,
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
        {
            try { return Portal.SetOpcUaInterfaceEnabled(softwarePath, interfaceName, enabled, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error setting OPC UA interface enabled state: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Export an OPC UA server interface or reference namespace to an XML file." +
            " The exported XML can be inspected, modified, and re-imported." +
            " interfaceType: 'ServerInterface' (default), 'SimaticInterface', 'ReferenceNamespace'.")]
        public static ResponseMessage ExportOpcUaInterface(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("interfaceName: exact name of the interface to export")] string interfaceName,
            [Description("exportPath: full file path for the XML output, e.g. 'C:\\Temp\\OpcUa_Interface.xml'")] string exportPath,
            [Description("interfaceType: 'ServerInterface' (default), 'SimaticInterface', or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
        {
            try { return Portal.ExportOpcUaInterface(softwarePath, interfaceName, exportPath, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting OPC UA interface: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportOpcUaInterface"), Description(
            "[L2][Category:PLC-OpcUA][PreCondition:Connect+OpenProject]" +
            " Import an OPC UA server interface or reference namespace from an XML file." +
            " If an interface with the same name (derived from the file name) already exists, it is updated in place." +
            " Otherwise a new interface is created." +
            " Download to PLC after import to apply changes to the CPU." +
            " interfaceType: 'ServerInterface' (default), 'ReferenceNamespace'.")]
        public static ResponseMessage ImportOpcUaInterface(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XML file")] string importPath,
            [Description("interfaceType: 'ServerInterface' (default) or 'ReferenceNamespace'")] string interfaceType = "ServerInterface")
        {
            try { return Portal.ImportOpcUaInterface(softwarePath, importPath, interfaceType); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing OPC UA interface: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export PLC alarm classes to a file. Alarm classes define severity, acknowledgment behavior, and display colors for alarms." +
            " The exported file can be edited and re-imported to update alarm class configurations." +
            " Use before bulk alarm class updates to create a backup.")]
        public static ResponseMessage ExportAlarmClasses(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the export, e.g. 'C:\\Temp\\AlarmClasses.xml'")] string exportPath)
        {
            try { return Portal.ExportAlarmClasses(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm classes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmClasses"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Import PLC alarm classes from a previously exported file." +
            " Overwrites existing alarm class definitions. Run CompileSoftware after import.")]
        public static ResponseMessage ImportAlarmClasses(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to import from")] string importPath)
        {
            try { return Portal.ImportAlarmClasses(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm classes: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export all PLC alarm text lists to an XLSX (Excel) file." +
            " Text lists contain the text strings shown for each alarm condition." +
            " Supports multi-language projects — all configured languages are exported." +
            " Typical use: export → translate in Excel → ImportAlarmTextLists.")]
        public static ResponseMessage ExportAlarmTextLists(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output, e.g. 'C:\\Temp\\AlarmTexts.xlsx'")] string exportPath)
        {
            try { return Portal.ExportAlarmTextLists(softwarePath, exportPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm text lists: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ImportAlarmTextLists"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Import PLC alarm text lists from an XLSX file." +
            " The file must match the format exported by ExportAlarmTextLists." +
            " Run CompileSoftware after import to validate alarm configuration.")]
        public static ResponseMessage ImportAlarmTextLists(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("importPath: full file path to the XLSX file")] string importPath)
        {
            try { return Portal.ImportAlarmTextLists(softwarePath, importPath); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error importing alarm text lists: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "ExportAlarmInstanceTexts"), Description(
            "[L2][Category:PLC-Alarms][PreCondition:Connect+OpenProject]" +
            " Export PLC alarm instance texts to an XLSX file." +
            " Instance texts are the alarm messages tied to specific FB/FC instances (e.g. Motor_01.AlarmText)." +
            " Options control what additional columns are included in the export." +
            " Typical use: export → fill in alarm descriptions → ImportInstanceTexts (not yet exposed — edit via TIA Portal UI).")]
        public static ResponseMessage ExportAlarmInstanceTexts(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("exportPath: full file path for the XLSX output")] string exportPath,
            [Description("includeInfoText: include the Info Text column (default: true)")] bool includeInfoText = true,
            [Description("includeAdditionalTexts: include Additional Texts columns (default: true)")] bool includeAdditionalTexts = true,
            [Description("includeAlarmClass: include the Alarm Class column (default: true)")] bool includeAlarmClass = true)
        {
            try { return Portal.ExportAlarmInstanceTexts(softwarePath, exportPath, includeInfoText, includeAdditionalTexts, includeAlarmClass); }
            catch (Exception ex) when (ex is not McpException)
            { throw new McpException($"Unexpected error exporting alarm instance texts: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError); }
        }

        [McpServerTool(Name = "CompileSoftware"), Description("[L1][PLC-Software] Compile all blocks in the PLC software. Requires: Connect + OpenProject. Returns basic success/failure. For structured error/warning details use CompileAndDiagnosePlc instead. Must compile before ExportBlock if any blocks are inconsistent. After adding new blocks via import, always compile to catch type/interface mismatches.")]
        public static ResponseCompile CompileSoftware(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath,
            [Description("password: the password to access adminsitration, default: no password")] string password = "")
        {
            try
            {
                var result = WithAutoOffline(() => Portal.CompileSoftware(softwarePath, password));
                var collected = CollectCompilerMessages(result.Messages);

                return new ResponseCompile
                {
                    Message = $"Software '{softwarePath}' compiled. State={result.State} Errors={result.ErrorCount} Warnings={result.WarningCount}",
                    State = result.State.ToString(),
                    ErrorCount = result.ErrorCount,
                    WarningCount = result.WarningCount,
                    Messages = collected.Raw,
                    Meta = new JsonObject
                    {
                        ["timestamp"] = DateTime.Now,
                        ["success"] = !result.State.ToString().Equals("Error", StringComparison.OrdinalIgnoreCase),
                        ["errorDetailCount"] = collected.Errors.Count,
                        ["warningDetailCount"] = collected.Warnings.Count
                    }
                };
            }
            catch (PortalException pex)
            {
                throw new McpException($"Failed compiling software '{softwarePath}' [{pex.Code}]: {pex.Message}", pex, McpErrorCode.InternalError);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error compiling software '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetOnlineState"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Read the current online connection state of a PLC (Offline/Connecting/Online/Incompatible/NotReachable/Protected/Disconnecting)." +
            " Does NOT change state — purely a read operation." +
            " Use before GoOnline to check current state, or after DownloadToPlc to verify the CPU is reachable." +
            " State=Online means the PC is communicating with the physical CPU." +
            " State=Incompatible means online but firmware/config mismatch — download required." +
            " State=NotReachable means network or IP configuration issue." +
            " NOTE: This reports Openness connection state, NOT the CPU operating mode (RUN/STOP)." +
            " The TIA Portal public API does not expose CPU operating mode — check the CPU front panel LEDs or HMI for RUN/STOP status.")]
        public static ResponseOnlineState GetOnlineState(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.GetOnlineState(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error reading online state for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOnline"), Description(
            "[L1][Category:PLC-Online][ONLINE-CONNECT][PreCondition:Connect+OpenProject]" +
            " Establish an online connection from TIA Portal to the physical PLC." +
            " Required before DownloadToPlc to confirm reachability, or for future online monitoring tools." +
            " Returns State=Online on success." +
            " If ipAddress is omitted, uses the IP address configured in the project's hardware configuration." +
            " If ipAddress is provided, overrides the configured IP for this session (useful for commissioning with a different IP)." +
            " Common failures: NotReachable (wrong IP / no cable), Protected (CPU requires authentication — supply password), Incompatible (firmware mismatch).")]
        public static ResponseOnlineState GoOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("ipAddress: optional IP address override, e.g. '192.168.1.10'. Leave empty to use the project's configured IP.")] string ipAddress = "",
            [Description("password: optional CPU access password. Required when the CPU has read/write protection configured. Leave empty for unprotected CPUs.")] string password = "")
        {
            try
            {
                return Portal.GoOnline(
                    softwarePath,
                    string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress,
                    string.IsNullOrWhiteSpace(password) ? null : password);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going online for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOffline"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Disconnect the online session between TIA Portal and the physical PLC." +
            " Safe to call even if not currently online. Always go offline when monitoring or download is complete.")]
        public static ResponseMessage GoOffline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.GoOffline(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error going offline for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GoOfflineAll"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject]" +
            " Take EVERY PLC in the open project offline in one call and report each PLC's before/after online state." +
            " Use this whenever CompileSoftware/Export*/Import* is blocked by 'operation not permitted in online mode':" +
            " a UI-initiated online session or a second online PLC is NOT released by GoOffline on a single softwarePath." +
            " Fully autonomous — never ask the user to toggle online/offline in the TIA UI, and never OCR the toolbar.")]
        public static ResponseJsonReport GoOfflineAll()
        {
            try
            {
                var data = Portal.GoOfflineAll();
                bool all = data["allOffline"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = all,
                    Message = data["message"]?.ToString() ?? "GoOfflineAll completed.",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = all }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GoOfflineAll failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetDeviceIpAddress"), Description(
            "[L1][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " Read a device's configured IP address straight from the TIA project (Openness PROFINET node) —" +
            " NOT by probing the CPU over S7 and NOT by exporting/parsing AML. Returns the primary IE IP plus all network nodes" +
            " (address, subnet, type). This is the correct, fast way to discover a PLC's IP before GoOnline/ReadPlcLiveValuesS7.")]
        public static ResponseJsonReport GetDeviceIpAddress(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
        {
            try
            {
                var data = Portal.GetDeviceIpAddress(devicePath);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                var ip = data["ipAddress"]?.ToString() ?? string.Empty;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"{devicePath} IP: {(string.IsNullOrEmpty(ip) ? "(no address configured on any node)" : ip)}"
                        : (data["message"]?.ToString() ?? "Device not found."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = found }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetDeviceIpAddress failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetProjectTopology"), Description(
            "[L1][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " One-shot, read-only project topology from Openness: every device with its network nodes (IP, subnet, node type)." +
            " Call this early to understand the project's devices and subnets at a glance, instead of probing S7 or parsing AML.")]
        public static ResponseJsonReport GetProjectTopology()
        {
            try
            {
                var data = Portal.GetProjectTopology();
                int count = data["deviceCount"]?.GetValue<int>() ?? 0;
                return new ResponseJsonReport
                {
                    Ok = count > 0,
                    Message = $"Project topology: {count} device(s).",
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = count > 0 }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetProjectTopology failed: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DumpDeviceAttributes"), Description(
            "[L2][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " Read-only inventory of EVERY Openness attribute exposed on a device's items (CPU, modules, interfaces, ports):" +
            " name, access mode (read-only vs read/write), current value, value type." +
            " Run this ONCE per CPU/firmware to learn what is actually exposed, then drive hardware reads/writes from that" +
            " ground truth instead of guessing attribute names. Optional nameFilter narrows to attributes whose name contains" +
            " a substring (e.g. 'protection', 'putget', 'ip'). NOTE: GetAttributeInfos() does not enumerate every gettable" +
            " attribute on all CPUs, so absence here means 'not enumerated', not a guaranteed 'no interface'.")]
        public static ResponseJsonReport DumpDeviceAttributes(
            [Description("devicePath: device name, CPU/program name, or full name (e.g. 'S7-1200 station_3', '安全PLC', 'S7-1500/ET200MP station_1').")] string devicePath,
            [Description("nameFilter: optional case-insensitive substring to narrow attribute names (e.g. 'protection'). Empty = all.")] string? nameFilter = null)
        {
            try
            {
                var data = Portal.DumpDeviceAttributes(devicePath, nameFilter);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                int items = data["itemCount"]?.GetValue<int>() ?? 0;
                int attrs = data["totalAttributes"]?.GetValue<int>() ?? 0;
                int writable = data["writableAttributes"]?.GetValue<int>() ?? 0;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"{devicePath}: {attrs} attribute(s) across {items} item(s) ({writable} writable)."
                        : (data["message"]?.ToString() ?? "Not found."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = found }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"DumpDeviceAttributes failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "GetPutGetAccess"), Description(
            "[L2][Category:Hardware][PreCondition:Connect+OpenProject]" +
            " Read whether a CPU permits remote PUT/GET access — the precondition for ReadPlcLiveValuesS7 on DB areas." +
            " If enabled=false, S7 absolute reads of DBs will fail; enable with SetPutGetAccess (then hardware DownloadToPlc).")]
        public static ResponseJsonReport GetPutGetAccess(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath)
        {
            try
            {
                var data = Portal.GetPutGetAccess(devicePath);
                bool found = data["found"]?.GetValue<bool>() ?? false;
                bool enabled = data["enabled"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = found,
                    Message = found
                        ? $"PUT/GET access on {devicePath}: {(enabled ? "ENABLED" : "DISABLED")} (attribute '{data["attributeName"]}')."
                        : (data["message"]?.ToString() ?? "Not found."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = found }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"GetPutGetAccess failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "SetPutGetAccess"), Description(
            "[L2][Category:Hardware][CONFIG-WRITE][PreCondition:Connect+OpenProject]" +
            " Enable or disable remote PUT/GET access on a CPU (the precondition for S7 DB reads)." +
            " This is a hardware-configuration change — you must run DownloadToPlc afterwards for it to take effect on the live CPU." +
            " Returns before/after readback evidence.")]
        public static ResponseJsonReport SetPutGetAccess(
            [Description("devicePath: device name from GetProjectTree, e.g. 'PLC_1'.")] string devicePath,
            [Description("enable: true to permit remote PUT/GET access, false to forbid it.")] bool enable = true)
        {
            try
            {
                var data = Portal.SetPutGetAccess(devicePath, enable);
                bool ok = data["ok"]?.GetValue<bool>() ?? false;
                return new ResponseJsonReport
                {
                    Ok = ok,
                    Message = ok
                        ? $"PUT/GET access on {devicePath} set to {enable}. Download hardware config to apply."
                        : (data["message"]?.ToString() ?? "SetPutGetAccess failed."),
                    Data = data,
                    Meta = new JsonObject { ["timestamp"] = DateTime.Now, ["success"] = ok }
                };
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"SetPutGetAccess failed for '{devicePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        // Autonomy helper: when a compile/export/import is blocked because TIA is in online mode,
        // take ALL PLCs offline via Openness and retry once — never hand the toggle back to the user.
        internal static bool IsOnlineModeError(Exception ex)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
            {
                var m = e.Message ?? string.Empty;
                if (m.IndexOf("online mode", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (m.IndexOf("not permitted", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    m.IndexOf("online", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        internal static T WithAutoOffline<T>(Func<T> op)
        {
            try { return op(); }
            catch (Exception ex) when (IsOnlineModeError(ex))
            {
                try { Portal.GoOfflineAll(); } catch { }
                return op(); // retry once, now fully offline
            }
        }

        [McpServerTool(Name = "CompareSoftwareToOnline"), Description(
            "[L2][Category:PLC-Online][PreCondition:Connect+OpenProject+GoOnline]" +
            " Compare the offline PLC software in the project against the program currently running on the physical CPU." +
            " Use after editing blocks to confirm what differs from the live CPU before downloading," +
            " or after a download to verify offline/online consistency." +
            " Returns a tree-walked list of differences (only entries where ComparisonResult is not 'Equal' are reported)." +
            " Requires GoOnline to be called first; will return IsOnline=false with guidance otherwise.")]
        public static ResponseCompare CompareSoftwareToOnline(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("maxDepth: maximum tree depth to walk (default 4). Lower = faster but less detail.")] int maxDepth = 4,
            [Description("maxEntries: cap on differences returned (default 200). Truncated=true in response if reached.")] int maxEntries = 200)
        {
            try
            {
                return Portal.CompareSoftwareToOnline(softwarePath, maxDepth, maxEntries);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error comparing '{softwarePath}' to online: {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "CheckDownloadReadiness"), Description(
            "[L1][Category:PLC-Online][PreCondition:Connect+OpenProject+CompileSoftware]" +
            " Check whether a PLC is ready to receive a program download WITHOUT actually downloading." +
            " Verifies: DownloadProvider service is available, a network/IP configuration exists in the hardware config." +
            " Returns Ready=true only when all checks pass." +
            " Use this before DownloadToPlc to surface problems early (missing IP, no hardware config, etc.)." +
            " Does NOT compile — run CompileSoftware first to ensure blocks are consistent.")]
        public static ResponseCheckDownload CheckDownloadReadiness(
            [Description("softwarePath: path to the PLC software in the project tree, e.g. 'PLC_1'")] string softwarePath)
        {
            try
            {
                return Portal.CheckDownloadReadiness(softwarePath);
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error checking download readiness for '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        [McpServerTool(Name = "DownloadToPlc"), Description(
            "[L1][Category:PLC-Online][ONLINE-WRITE][PreCondition:Connect+OpenProject+CompileSoftware+CheckDownloadReadiness]" +
            " Download the compiled PLC program to the physical CPU over the network." +
            " The CPU will stop briefly during download and restart automatically (controlled by startAfterDownload)." +
            " SAFETY: Verify no personnel are near the machine before downloading. This changes live PLC behavior." +
            " Workflow: Connect → OpenProject → CompileSoftware → CheckDownloadReadiness → DownloadToPlc → GetCpuOnlineState." +
            " On success State=Success or Warning. On Error check Errors[] for details." +
            " Default options (keepActualValues=true, consistentBlocksOnly=true) are safe for most scenarios." +
            " Set keepActualValues=false only when DB initial values must be reset — this is irreversible.")]
        public static ResponseDownload DownloadToPlc(
            [Description("softwarePath: path to the PLC software, e.g. 'PLC_1'")] string softwarePath,
            [Description("consistentBlocksOnly: true=download only consistent blocks (safe default), false=download all blocks even inconsistent ones")] bool consistentBlocksOnly = true,
            [Description("keepActualValues: true=preserve current DB actual values (safe default), false=reset all DB values to initial values (irreversible)")] bool keepActualValues = true,
            [Description("startAfterDownload: true=automatically set CPU to RUN after download (default), false=leave CPU in STOP")] bool startAfterDownload = true,
            [Description("stopBeforeDownload: true=automatically stop CPU before download (required for most downloads), false=attempt online download without stopping")] bool stopBeforeDownload = true,
            [Description("password: optional CPU access password. Required when the CPU has download protection configured. Leave empty for unprotected CPUs.")] string password = "")
        {
            try
            {
                var result = Portal.DownloadToPlc(
                    softwarePath,
                    consistentBlocksOnly,
                    keepActualValues,
                    startAfterDownload,
                    stopBeforeDownload,
                    string.IsNullOrWhiteSpace(password) ? null : password);

                if (result.Ok == false && result.Errors != null && result.Errors.Length > 0)
                    throw new McpException(
                        $"Download to '{softwarePath}' failed: {result.Message}",
                        McpErrorCode.InternalError);

                return result;
            }
            catch (McpException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new McpException($"Unexpected error downloading to '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        private sealed class CompilerMessageCollectResult
        {
            public List<string> Raw { get; } = new List<string>();
            public List<string> Errors { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();
            public List<string> Info { get; } = new List<string>();
        }

        private static CompilerMessageCollectResult CollectCompilerMessages(object? messagesRoot)
        {
            var collected = new CompilerMessageCollectResult();
            if (messagesRoot is System.Collections.IEnumerable enumerable && messagesRoot is not string)
            {
                foreach (var message in enumerable)
                    WalkCompilerMessageNode(message, collected);
            }
            return collected;
        }

        private static void WalkCompilerMessageNode(object? message, CompilerMessageCollectResult collected)
        {
            if (message == null) return;

            var formatted = FormatCompilerMessage(message);
            if (!string.IsNullOrWhiteSpace(formatted))
            {
                collected.Raw.Add(formatted!);
                ClassifyCompilerMessage(message, formatted!, collected);
            }

            if (!TryGetCompilerMessageChildren(message, out var children)) return;
            foreach (var child in children)
                WalkCompilerMessageNode(child, collected);
        }

        private static void ClassifyCompilerMessage(object message, string formatted, CompilerMessageCollectResult collected)
        {
            var state = ReadCompilerMessageState(message);
            var description = ReadCompilerMessageProperty(message, "Description") ?? string.Empty;
            var hasChildren = HasCompilerMessageChildren(message);

            if (IsCompilerSummaryDescription(description))
                return;

            if (IsCompilerErrorState(state))
            {
                if (!hasChildren || !string.IsNullOrWhiteSpace(description))
                    AddUniqueCompilerLine(collected.Errors, formatted);
                return;
            }

            if (IsCompilerWarningState(state))
            {
                if (!hasChildren || !string.IsNullOrWhiteSpace(description))
                    AddUniqueCompilerLine(collected.Warnings, formatted);
                return;
            }

            if (!string.IsNullOrWhiteSpace(description))
                AddUniqueCompilerLine(collected.Info, formatted);
        }

        private static bool HasCompilerMessageChildren(object message)
        {
            return TryGetCompilerMessageChildren(message, out var children) && children.Count > 0;
        }

        private static bool TryGetCompilerMessageChildren(object message, out List<object> children)
        {
            children = new List<object>();
            try
            {
                var messagesValue = message.GetType().GetProperty("Messages")?.GetValue(message);
                if (messagesValue is System.Collections.IEnumerable enumerable && messagesValue is not string)
                {
                    foreach (var child in enumerable)
                    {
                        if (child != null)
                            children.Add(child);
                    }
                }
            }
            catch
            {
                // best effort only
            }

            return children.Count > 0;
        }

        private static string? ReadCompilerMessageProperty(object message, string propertyName)
        {
            try
            {
                var value = message.GetType().GetProperty(propertyName)?.GetValue(message);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string ReadCompilerMessageState(object message)
        {
            return ReadCompilerMessageProperty(message, "State") ?? string.Empty;
        }

        private static bool IsCompilerErrorState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            return state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0
                || state.IndexOf("fehler", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompilerWarningState(string state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;
            return state.IndexOf("warning", StringComparison.OrdinalIgnoreCase) >= 0
                || state.IndexOf("warnung", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCompilerSummaryDescription(string? description)
        {
            if (string.IsNullOrWhiteSpace(description)) return false;
            var text = description!.Trim();
            return text.StartsWith("Compiling finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Compilation finished", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("Kompilierung beendet", StringComparison.OrdinalIgnoreCase);
        }

        private static void AddUniqueCompilerLine(List<string> target, string line)
        {
            if (!target.Contains(line))
                target.Add(line);
        }

        private static void AppendCompilerEngineeringAttributes(object message, List<string> parts)
        {
            var getAttribute = message.GetType().GetMethod(
                "GetAttribute",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            if (getAttribute == null) return;

            foreach (var attrName in new[]
            {
                "Line", "Column", "BlockName", "Severity", "ErrorCode", "Message", "Text", "ObjectPath"
            })
            {
                if (parts.Any(p => p.StartsWith(attrName + "=", StringComparison.OrdinalIgnoreCase)))
                    continue;

                try
                {
                    var value = getAttribute.Invoke(message, new object[] { attrName });
                    if (value == null) continue;
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add($"{attrName}={text}");
                }
                catch
                {
                    // attribute not supported on this message type
                }
            }
        }

        private static string? FormatCompilerMessage(object? message)
        {
            if (message == null) return null;

            try
            {
                var t = message.GetType();
                var parts = new List<string>();

                foreach (var name in new[]
                {
                    "State", "Severity", "ErrorCode", "Message", "Description", "Text",
                    "Path", "ObjectPath", "BlockName", "Line", "Column", "DateTime"
                })
                {
                    var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || p.GetIndexParameters().Length != 0) continue;

                    object? value = null;
                    try { value = p.GetValue(message); } catch { }
                    if (value == null) continue;

                    var s = value.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        parts.Add($"{name}={s}");
                }

                AppendCompilerEngineeringAttributes(message, parts);

                if (parts.Count == 0)
                {
                    foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length != 0) continue;
                        if (string.Equals(p.Name, "Messages", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(p.Name, "Parent", StringComparison.OrdinalIgnoreCase))
                            continue;

                        object? value = null;
                        try { value = p.GetValue(message); } catch { }
                        if (value == null) continue;
                        var s = value.ToString();
                        if (!string.IsNullOrWhiteSpace(s) && s != t.FullName)
                            parts.Add($"{p.Name}={s}");
                    }
                }

                return parts.Count > 0 ? string.Join("; ", parts) : message.ToString();
            }
            catch
            {
                return message.ToString();
            }
        }

        [McpServerTool(Name = "GetSoftwareTree"), Description("[L1][PLC-Software] Get the full PLC block/type/external-source hierarchy as ASCII tree. Requires: Connect + OpenProject. softwarePath from GetProjectTree (e.g. 'PLC_1'). ALWAYS call before ExportBlock/ImportBlock to get exact group paths (e.g. 'Program blocks/FBs/FB_Motor'). Returns OB/FB/FC/GlobalDB/UDT/ExternalSource blocks with group hierarchy.")]
        public static ResponseSoftwareTree GetSoftwareTree(
            [Description("softwarePath: defines the path in the project structure to the plc software")] string softwarePath)
        {
            try
            {
                var tree = Portal.GetSoftwareTree(softwarePath);

                if (!string.IsNullOrEmpty(tree))
                {
                    return new ResponseSoftwareTree
                    {
                        Message = $"Software tree retrieved from '{softwarePath}'",
                        Tree = "```\n" + tree + "\n```",
                        Meta = new JsonObject
                        {
                            ["timestamp"] = DateTime.Now,
                            ["success"] = true
                        }
                    };
                }
                else
                {
                    throw new McpException($"Failed retrieving software tree from '{softwarePath}'", McpErrorCode.InternalError);
                }
            }
            catch (Exception ex) when (ex is not McpException)
            {
                throw new McpException($"Unexpected error retrieving software tree from '{softwarePath}': {ex.Message}{McpHints.Recovery(ex)}", ex, McpErrorCode.InternalError);
            }
        }

        #endregion
    }
}
