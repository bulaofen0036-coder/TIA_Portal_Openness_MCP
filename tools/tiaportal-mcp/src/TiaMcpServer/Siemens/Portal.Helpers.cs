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
    // Partial: private helper. Extracted from Portal.cs (god-file split); behavior unchanged.
    public partial class Portal
    {
        #region private helper

        private bool IsPortalNull()
        {
            if (_portal == null)
            {
                _logger?.LogWarning("No TIA portal available.");

                return true;
            }

            return false;
        }

        private bool IsProjectNull()
        {
            if (_project == null)
            {
                // Self-heal for less-capable AI drivers that call a tool before Connect/Open.
                // Deliberately conservative (this is a 99-call-site predicate):
                //   - connected but unbound  -> rebind a project already open in the TIA UI;
                //   - not connected, but a TIA process is already running -> attach & bind it;
                //   - not connected and no TIA running -> do NOT launch (would be a slow failure);
                //     fall through to the actionable "no project" message so the AI calls Connect.
                try
                {
                    if (_portal != null)
                    {
                        GetState();
                    }
                    else
                    {
                        bool tiaRunning = false;
                        try { tiaRunning = TiaPortal.GetProcesses().Any(); } catch { }
                        if (tiaRunning) ConnectPortal();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "IsProjectNull self-heal (auto connect/bind) failed");
                }
            }

            if (_project == null)
            {
                _logger?.LogWarning("No TIA project available.");

                return true;
            }

            return false;
        }

        private bool IsSessionNull()
        {
            if (_session == null)
            {
                _logger?.LogWarning("No TIA session available.");

                return true;
            }

            return false;
        }

        #region  GetTree ...

        private string GetTreePrefix(List<bool> ancestorStates, bool isLast)
        {
            var prefix = new StringBuilder();
            
            // Build prefix based on ancestor states
            for (int i = 0; i < ancestorStates.Count; i++)
            {
                prefix.Append(ancestorStates[i] ? "    " : "│   ");
            }
            
            // Add current level connector
            prefix.Append(isLast ? "└── " : "├── ");
            return prefix.ToString();
        }

        private void GetProjectTreeDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates)
        {
            if (devices.Count == 0) return;
            
            // Check if this is the last main section
            var hasOtherSections = (_project?.DeviceGroups != null && _project.DeviceGroups.Count > 0) ||
                                  (_project?.UngroupedDevicesGroup != null);
            var isLastMainSection = !hasOtherSections;
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastMainSection)}Devices [Collection]");

            var deviceList = devices.ToList();
            var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection };
            
            for (int i = 0; i < deviceList.Count; i++)
            {
                var device = deviceList[i];
                var isLastDevice = i == deviceList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [Device: {device.TypeIdentifier}]");

                if (device.DeviceItems != null && device.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, device.DeviceItems, new List<bool>(newAncestorStates) { isLastDevice });
                }
            }
        }

        private void GetProjectTreeGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
        {
            if (groups.Count == 0) return;
            
            var isLastMainSection = _project?.UngroupedDevicesGroup == null;
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastMainSection)}Groups [Collection]");

            var groupList = groups.ToList();
            var newAncestorStates = new List<bool>(ancestorStates) { isLastMainSection };
            
            for (int i = 0; i < groupList.Count; i++)
            {
                var group = groupList[i];
                var isLastGroup = i == groupList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{group.Name} [Group]");

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                
                if (group.Devices != null && group.Devices.Count > 0)
                {
                    GetProjectTreeGroupDevices(sb, group.Devices, groupAncestorStates, group.Groups != null && group.Groups.Count > 0);
                }
                
                if (group.Groups != null && group.Groups.Count > 0)
                {
                    GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
                }
            }
        }
        
        private void GetProjectTreeGroupDevices(StringBuilder sb, DeviceComposition devices, List<bool> ancestorStates, bool hasSubGroups)
        {
            var deviceList = devices.ToList();
            
            for (int i = 0; i < deviceList.Count; i++)
            {
                var device = deviceList[i];
                var isLastDevice = i == deviceList.Count - 1 && !hasSubGroups;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastDevice)}{device.Name} [Device]");
                
                if (device.DeviceItems != null && device.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, device.DeviceItems, new List<bool>(ancestorStates) { isLastDevice });
                }
            }
        }
        
        private void GetProjectTreeSubGroups(StringBuilder sb, DeviceUserGroupComposition groups, List<bool> ancestorStates)
        {
            var groupList = groups.ToList();
            
            for (int i = 0; i < groupList.Count; i++)
            {
                var group = groupList[i];
                var isLastGroup = i == groupList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{group.Name} [Subgroup]");
                
                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                
                if (group.Devices != null && group.Devices.Count > 0)
                {
                    GetProjectTreeGroupDevices(sb, group.Devices, groupAncestorStates, group.Groups != null && group.Groups.Count > 0);
                }
                
                if (group.Groups != null && group.Groups.Count > 0)
                {
                    GetProjectTreeSubGroups(sb, group.Groups, groupAncestorStates);
                }
            }
        }

        private void GetProjectTreeDeviceItemsRecursive(StringBuilder sb, DeviceItemComposition deviceItems, List<bool> ancestorStates)
        {
            var deviceItemsList = deviceItems.ToList();
            
            for (int i = 0; i < deviceItemsList.Count; i++)
            {
                var deviceItem = deviceItemsList[i];
                var isLastDeviceItem = i == deviceItemsList.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastDeviceItem)}{deviceItem.Name} [DeviceItem]");
                
                var itemAncestorStates = new List<bool>(ancestorStates) { isLastDeviceItem };
                
                // Get software first
                GetProjectTreeDeviceItemSoftware(sb, deviceItem, itemAncestorStates);
                
                // Then get items
                if (deviceItem.Items != null && deviceItem.Items.Count > 0)
                {
                    GetProjectTreeItems(sb, deviceItem.Items, itemAncestorStates, deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                }
                
                // Finally get sub-device items
                if (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0)
                {
                    GetProjectTreeDeviceItemsRecursive(sb, deviceItem.DeviceItems, itemAncestorStates);
                }
            }
        }
        
        private void GetProjectTreeItems(StringBuilder sb, DeviceItemAssociation items, List<bool> ancestorStates, bool hasSubDeviceItems)
        {
            var itemsList = items.ToList();
            
            for (int i = 0; i < itemsList.Count; i++)
            {
                var subItem = itemsList[i];
                var isLastItem = i == itemsList.Count - 1 && !hasSubDeviceItems;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastItem)}{subItem.Name} [Hardware Component]");
            }
        }


        private void GetProjectTreeDeviceItemSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates)
        {
            var softwareContainer = deviceItem.GetService<SoftwareContainer>();
            var hasSoftware = false;
            
            //PLC software
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                   (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems)}PlcSoftware: {plcSoftware.Name} [PLC Program]");
                hasSoftware = true;
            }

            //WinCC HMI software
            if (softwareContainer?.Software is HmiTarget hmiTarget)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                   (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiTarget: {hmiTarget.Name} [HMI Program]");
            }

            //Unified HMI software: dlls will only exist on TIA Portal V19 and newer.
            if (Engineering.TiaMajorVersion >= 19)
                TryGetUnifiedSoftware(sb, deviceItem, ancestorStates, softwareContainer, hasSoftware);
        }

        private bool TryGetUnifiedSoftware(StringBuilder sb, DeviceItem deviceItem, List<bool> ancestorStates, SoftwareContainer? softwareContainer, bool hasSoftware)
        {
            if (softwareContainer?.Software is HmiSoftware hmiSoftware)
            {
                var hasOtherItems = (deviceItem.Items != null && deviceItem.Items.Count > 0) ||
                                    (deviceItem.DeviceItems != null && deviceItem.DeviceItems.Count > 0);
                sb.AppendLine($"{GetTreePrefix(ancestorStates, !hasOtherItems && !hasSoftware)}HmiSoftware: {hmiSoftware.Name} [HMI Program]");
                hasSoftware = true;
            }

            return hasSoftware;
        }

        private void GetProjectTreeUngroupedDeviceGroup(StringBuilder sb, DeviceSystemGroup ungroupedDevicesGroup, List<bool> ancestorStates)
        {
            sb.AppendLine($"{GetTreePrefix(ancestorStates, true)}UngroupedDevicesGroup: {ungroupedDevicesGroup.Name} [System Group]");

            if (ungroupedDevicesGroup.Devices != null && ungroupedDevicesGroup.Devices.Count > 0)
            {
                var deviceList = ungroupedDevicesGroup.Devices.ToList();
                var newAncestorStates = new List<bool>(ancestorStates) { true };
                
                for (int i = 0; i < deviceList.Count; i++)
                {
                    var device = deviceList[i];
                    var isLastDevice = i == deviceList.Count - 1;
                    
                    sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastDevice)}{device.Name} [{device.TypeIdentifier}]");
                }
            }
        }

        #endregion

        #region GetSoftwareTree ...

        public string GetSoftwareTree(string softwarePath)
        {
            _logger?.LogInformation("Getting software tree for path: {SoftwarePath}", softwarePath);

            if (IsProjectNull())
            {
                return string.Empty;
            }

            try
            {
                var softwareContainer = GetSoftwareContainer(softwarePath);
                if (softwareContainer?.Software is PlcSoftware plcSoftware)
                {
                    StringBuilder sb = new();
                    sb.AppendLine($"{plcSoftware.Name} [PLC Software]");
                    
                    var ancestorStates = new List<bool>();
                    var sections = new List<Action>();
                    
                    var hasBlocks = plcSoftware.BlockGroup != null;
                    var hasTypes = plcSoftware.TypeGroup != null;
                    
                    // Add blocks section
                    if (hasBlocks)
                    {
                        var blockGroup = plcSoftware.BlockGroup;
                        if (blockGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeBlockGroup(sb, blockGroup, ancestorStates, "Program blocks", !hasTypes));
                        }
                    }
                    
                    // Add types section
                    if (hasTypes)
                    {
                        var typeGroup = plcSoftware.TypeGroup;
                        if (typeGroup != null)
                        {
                            sections.Add(() => GetSoftwareTreeTypeGroup(sb, typeGroup, ancestorStates, "PLC data types", true));
                        }
                    }
                    
                    
                    // Execute sections
                    for (int i = 0; i < sections.Count; i++)
                    {
                        sections[i]();
                    }

                    return sb.ToString();
                }
                else
                {
                    return $"No PLC software found at path: {softwarePath}";
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting software tree for {SoftwarePath}", softwarePath);
                return $"Error retrieving software tree: {ex.Message}";
            }
        }
        
        private void GetSoftwareTreeBlockGroup(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeBlockGroupRecursive(StringBuilder sb, PlcBlockGroup blockGroup, List<bool> ancestorStates)
        {
            // Get blocks in this group
            var blocks = blockGroup.Blocks.ToList();
            var subGroups = blockGroup.Groups.ToList();
            
            // First, add all blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                // Block is last only if it's the last block AND there are no subgroups following
                var isLastBlock = (i == blocks.Count - 1) && (subGroups.Count == 0);

                var blockTypeName = new[] { "ArrayDB", "GlobalDB", "InstanceDB" }.Contains(block.GetType().Name)
                    ? "DB"
                    : block.GetType().Name;

                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastBlock)}{block.Name} [{blockTypeName}{block.Number}, {block.ProgrammingLanguage}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Block Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeBlockGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeTypeGroup(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates, string groupLabel, bool isLastSection)
        {
            
            sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastSection)}{groupLabel}"); // [Collection]
            var newAncestorStates = new List<bool>(ancestorStates) { isLastSection };
            
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName=="PlcStruct" ? "UDT": typeTypeName;

                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(newAncestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(newAncestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }
        
        private void GetSoftwareTreeTypeGroupRecursive(StringBuilder sb, PlcTypeGroup typeGroup, List<bool> ancestorStates)
        {
            // Get types in this group
            var types = typeGroup.Types.ToList();
            var subGroups = typeGroup.Groups.ToList();
            
            // First, add all types
            for (int i = 0; i < types.Count; i++)
            {
                var type = types[i];
                // Type is last only if it's the last type AND there are no subgroups following
                var isLastType = (i == types.Count - 1) && (subGroups.Count == 0);

                var typeTypeName = type.GetType().Name;
                typeTypeName = typeTypeName == "PlcStruct" ? "UDT" : typeTypeName;

                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastType)}{type.Name} [{typeTypeName}]");
            }
            
            // Then, add all subgroups recursively
            for (int i = 0; i < subGroups.Count; i++)
            {
                var subGroup = subGroups[i];
                var isLastGroup = i == subGroups.Count - 1;
                
                sb.AppendLine($"{GetTreePrefix(ancestorStates, isLastGroup)}{subGroup.Name}"); // [Type Group]

                var groupAncestorStates = new List<bool>(ancestorStates) { isLastGroup };
                GetSoftwareTreeTypeGroupRecursive(sb, subGroup, groupAncestorStates);
            }
        }

        #endregion

        #region GetSoftwareContainer ...

        /// <summary>
        /// Resolve a TIA Openness service (e.g. OnlineProvider, DownloadProvider) for a given
        /// PLC software path. Tries the PlcSoftware first, then walks up the SoftwareContainer's
        /// DeviceItem chain. Required because some hardware variants (notably 1200-series CPUs in
        /// nested device groups) only expose Online/Download providers on the CPU DeviceItem,
        /// not on the PlcSoftware itself.
        /// </summary>
        private T? ResolvePlcService<T>(string softwarePath, PlcSoftware plcSoftware)
            where T : class, IEngineeringService
        {
            var direct = plcSoftware.GetService<T>();
            if (direct != null) return direct;

            var sc = GetSoftwareContainer(softwarePath);
            var di = sc?.Parent as DeviceItem;
            while (di != null)
            {
                var s = di.GetService<T>();
                if (s != null) return s;
                di = di.Parent as DeviceItem;
            }
            return null;
        }

        /// <summary>
        /// Subscribes a password handler to the OnlineLegitimation event so a protected CPU
        /// can be authenticated during GoOnline / Download. Returns an IDisposable that
        /// unsubscribes when disposed; callers MUST dispose to avoid handler leaks.
        /// Returns null when no password is provided or the configuration object is not a
        /// ConnectionConfiguration (no event to hook).
        /// </summary>
        private static IDisposable? AttachPasswordHandler(object? configuration, string? password)
        {
            if (string.IsNullOrEmpty(password) || configuration is not ConnectionConfiguration conn)
                return null;

            // Build the SecureString once. The same instance can be reused across multiple
            // legitimation prompts within the same call (e.g., read-then-write access).
            var secure = new SecureString();
            foreach (var c in password!) secure.AppendChar(c);
            secure.MakeReadOnly();

            OnlineConfigurationDelegate handler = (cfg) =>
            {
                if (cfg is OnlinePasswordConfiguration pwdCfg)
                {
                    pwdCfg.SetPassword(secure);
                }
            };
            conn.OnlineLegitimation += handler;
            return new HandlerScope(() => conn.OnlineLegitimation -= handler);
        }

        private sealed class HandlerScope : IDisposable
        {
            private Action? _detach;
            public HandlerScope(Action detach) { _detach = detach; }
            public void Dispose() { _detach?.Invoke(); _detach = null; }
        }

        private SoftwareContainer? GetSoftwareContainer(string softwarePath)
        {
            if (_project == null)
            {
                if (_softwareCacheProject != null) { _softwareContainerCache.Clear(); _softwareCacheProject = null; }
                return null;
            }

            // Invalidate cached resolutions whenever the open project instance changes
            // (open/close/create/attach all reassign _project). Free check, no COM call.
            if (!ReferenceEquals(_project, _softwareCacheProject))
            {
                _softwareContainerCache.Clear();
                _softwareCacheProject = _project;
            }

            if (_softwareContainerCache.TryGetValue(softwarePath, out var cached))
            {
                return cached;
            }

            var resolved = ResolveSoftwareContainerUncached(softwarePath);
            if (resolved != null)
            {
                _softwareContainerCache[softwarePath] = resolved;
            }
            return resolved;
        }

        private SoftwareContainer? ResolveSoftwareContainerUncached(string softwarePath)
        {
            if (_project == null)
            {
                return null;
            }

            string[] pathSegments = softwarePath.Split('/');
            int index = 0;

            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            // in Devices
            if (_project.Devices != null)
            {
                softwareContainer = GetSoftwareContainerInDevices(_project.Devices, pathSegments, index);
                if (softwareContainer != null)
                {
                    return softwareContainer;
                }
            }

            // in Groups
            if (_project.DeviceGroups != null)
            {
                softwareContainer = GetSoftwareContainerInGroups(_project.DeviceGroups, pathSegments, index);
                if (softwareContainer != null)
                {
                    return softwareContainer;
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDevices(DeviceComposition devices, string[] pathSegments, int index)
        {

            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            string nextSegment = index + 1 < pathSegments.Length ? pathSegments[index + 1] : string.Empty;

            if (devices != null)
            {
                SoftwareContainer? softwareContainer = null;
                Device? device = null;
                DeviceItem? deviceItem = null;

                // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
                // use segment to find device
                device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    // If path is only the device name (no next segment), or next segment doesn't match,
                    // search within the device's device items for one that actually hosts a SoftwareContainer.
                    var scFromDeviceItems = FindFirstSoftwareContainer(device.DeviceItems, string.IsNullOrWhiteSpace(nextSegment) ? null : nextSegment);
                    if (scFromDeviceItems != null) return scFromDeviceItems;

                    // Otherwise fall back to the old behavior (exact next-segment match, non-recursive)
                    if (!string.IsNullOrWhiteSpace(nextSegment))
                    {
                        deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(nextSegment, StringComparison.OrdinalIgnoreCase));
                        softwareContainer = GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index + 1);
                        if (softwareContainer != null)
                        {
                            return softwareContainer;
                        }
                    }
                }

                // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
                // ignored segment for Device.Name and use it for DeviceItem.Name
                // IMPORTANT: multiple DeviceItems can share the same name (Unified HMI often does).
                // Prefer the one that actually has a SoftwareContainer service.
                var flatItems = devices.SelectMany(d => d.DeviceItems).ToList();
                var scFromFlat = FindFirstSoftwareContainer(flatItems, segment);
                if (scFromFlat != null) return scFromFlat;

                deviceItem = flatItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
                if (deviceItem != null)
                {
                    return GetSoftwareContainerInDeviceItem(deviceItem, pathSegments, index);
                }

            }

            return null;
        }

        private static SoftwareContainer? FindFirstSoftwareContainer(IEnumerable<DeviceItem> roots, string? preferName)
        {
            try
            {
                var stack = new Stack<DeviceItem>(roots?.Where(x => x != null) ?? Enumerable.Empty<DeviceItem>());
                while (stack.Count > 0)
                {
                    var it = stack.Pop();
                    if (it == null) continue;

                    var nameOk = string.IsNullOrWhiteSpace(preferName) || it.Name.Equals(preferName, StringComparison.OrdinalIgnoreCase);
                    if (nameOk)
                    {
                        try
                        {
                            var sc = it.GetService<SoftwareContainer>();
                            if (sc != null) return sc;
                        }
                        catch { }
                    }

                    try
                    {
                        if (it.DeviceItems != null)
                        {
                            foreach (var ch in it.DeviceItems)
                                if (ch != null) stack.Push(ch);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInGroups(DeviceUserGroupComposition groups, string[] pathSegments, int index)
        {
            if (index >= pathSegments.Length)
                return null;

            string segment = pathSegments[index];
            SoftwareContainer? softwareContainer = null;

            if (groups != null)
            {
                var group = groups.FirstOrDefault(g => g.Name.Equals(segment));
                if (group != null)
                {
                    // when segment matched
                    softwareContainer = GetSoftwareContainerInDevices(group.Devices, pathSegments, index + 1);
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }

                    return GetSoftwareContainerInGroups(group.Groups, pathSegments, index + 1);
                }
            }

            return null;
        }

        private SoftwareContainer? GetSoftwareContainerInDeviceItem(DeviceItem deviceItem, string[] pathSegments, int index)
        {
            if (deviceItem != null)
            {
                // when segment matched
                if (index == pathSegments.Length - 1)
                {
                    // get from DeviceItem
                    var softwareContainer = deviceItem.GetService<SoftwareContainer>();
                    if (softwareContainer != null)
                    {
                        return softwareContainer;
                    }
                }
            }

            return null;
        }

        #endregion

        #region Get...ByPath

        private Device? GetDeviceByPath(string devicePath)
        {
            if (_project?.Devices == null || string.IsNullOrWhiteSpace(devicePath))
                return null;

            devicePath = devicePath.Trim();

            // Siemens hardware device names legitimately contain '/', e.g. "S7-1500/ET200MP station_1".
            // An exact full-name match must therefore win BEFORE we treat '/' as a path separator,
            // otherwise such devices are impossible to address (split -> bogus group lookup -> null).
            // Also accept the IDE-visible CPU name (a DeviceItem name, e.g. "安全PLC"/"5T车"), which differs
            // from the parent Device name (e.g. "S7-1200 station_3"); resolve it back to its owning Device.
            var allDevices = EnumerateAllDevices().ToList();
            var exact = allDevices.FirstOrDefault(d => d.Name.Equals(devicePath, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            var byItem = allDevices.FirstOrDefault(d => DeviceHasItemNamed(d.DeviceItems, devicePath));
            if (byItem != null) return byItem;

            var pathSegments = devicePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments.Length == 0)
            {
                return null;
            }

            // Try top-level device first
            if (pathSegments.Length == 1)
            {
                return _project.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));
            }

            // Traverse device groups
            DeviceUserGroupComposition? groups = _project.DeviceGroups;
            DeviceUserGroup? group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[0], StringComparison.OrdinalIgnoreCase));

            if (group == null)
            {
                return null;
            }

            for (int i = 1; i < pathSegments.Length; i++)
            {
                // Try to find device in current group
                var device = group.Devices.FirstOrDefault(d => d.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (device != null)
                {
                    return device;
                }

                // Try to find subgroup
                group = group.Groups.FirstOrDefault(g => g.Name.Equals(pathSegments[i], StringComparison.OrdinalIgnoreCase));
                if (group == null)
                {
                    break;
                }
            }

            return null;
        }

        // Enumerate every Device in the project, including those nested inside device groups.
        private IEnumerable<Device> EnumerateAllDevices()
        {
            if (_project?.Devices != null)
                foreach (Device d in _project.Devices) yield return d;
            foreach (var d in EnumerateGroupDevices(_project?.DeviceGroups)) yield return d;
        }

        private static IEnumerable<Device> EnumerateGroupDevices(DeviceUserGroupComposition? groups)
        {
            if (groups == null) yield break;
            foreach (var g in groups)
            {
                foreach (Device d in g.Devices) yield return d;
                foreach (var d in EnumerateGroupDevices(g.Groups)) yield return d;
            }
        }

        // True if the device contains a DeviceItem (CPU/module, at any nesting depth) with the given name.
        private static bool DeviceHasItemNamed(DeviceItemComposition? items, string name)
        {
            if (items == null) return false;
            foreach (DeviceItem it in items)
            {
                if (it.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                if (DeviceHasItemNamed(it.DeviceItems, name)) return true;
            }
            return false;
        }

        private DeviceItem? GetDeviceItemByPath(string deviceItemPath)
        {
            if (_project == null || _project.Devices == null)
            {
                return null;
            }

            // Split the device path by '/' to get each device name  
            var pathSegments = deviceItemPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            DeviceItem? deviceItem = null;

            // initial devices and groups
            var devices = _project.Devices;
            var groups = _project.DeviceGroups;

            for (int index = 0; index < pathSegments.Length; index++)
            {
                deviceItem = GetDeviceItemFromDevice(pathSegments, devices, index);

                if (deviceItem == null)
                {
                    // search in groups
                    var group = groups?.FirstOrDefault(g => g.Name.Equals(pathSegments[index], StringComparison.OrdinalIgnoreCase));
                    if (group != null)
                    {
                        devices = group.Devices;
                        if (devices != null)
                        {
                            deviceItem = GetDeviceItemFromDevice(pathSegments, devices, index + 1);
                        }

                        if (deviceItem != null)
                        {
                            return deviceItem;
                        }

                        // not found, but on the path
                        groups = group.Groups;
                        devices = group.Devices;
                    }
                }
                else
                {
                    return deviceItem;
                }
            }

            return deviceItem;
        }

        private static DeviceItem? GetDeviceItemFromDevice(string[] pathSegments, DeviceComposition? devices, int index)
        {
            string segment = pathSegments[index];
            string nextSegment = index + 1 < pathSegments.Length ? pathSegments[index + 1] : string.Empty;

            DeviceItem? deviceItem = null;

            // a pc based plc has a Device.Name = 'PC-System_1' or something like that, which is visible in the TIA-Portal IDE
            // use segment to find device
            var device = devices.FirstOrDefault(d => d.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            if (device != null)
            {
                if (string.IsNullOrWhiteSpace(nextSegment))
                {
                    deviceItem = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase))
                        ?? device.DeviceItems.FirstOrDefault();
                }
                else
                {
                    var first = device.DeviceItems.FirstOrDefault(di => di.Name.Equals(nextSegment, StringComparison.OrdinalIgnoreCase));
                    deviceItem = first;
                    var nextIndex = index + 2;
                    while (deviceItem != null && nextIndex < pathSegments.Length)
                    {
                        var wanted = pathSegments[nextIndex];
                        deviceItem = deviceItem.DeviceItems.FirstOrDefault(di => di.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                        nextIndex++;
                    }
                }

            }

            // a hardware plc has a Device.Name = 'S7-1500/ET200MP-Station_1' or something like that, which is not visible in the TIA-Portal IDE
            if (device == null)
            {
                deviceItem = devices
                .SelectMany(d => d.DeviceItems)
                .FirstOrDefault(di => di.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            }

            return deviceItem;
        }

        private PlcBlockGroup? GetPlcBlockGroupByPath(string softwarePath, string groupPath)
        {
            if (_project == null)
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                if (plcSoftware?.BlockGroup == null)
                {
                    return null;
                }


                // Split the path by '/' to get each group name
                var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

                PlcBlockGroup? currentGroup = plcSoftware.BlockGroup;

                // GetSoftwareTree labels the root user block group "Program blocks" (localized "程序块").
                // Callers naturally copy that full path, so skip a leading segment that denotes the root itself.
                var startIndex = 0;
                if (groupNames.Length > 0 &&
                    (groupNames[0].Equals("Program blocks", StringComparison.OrdinalIgnoreCase) ||
                     groupNames[0].Equals("程序块", StringComparison.OrdinalIgnoreCase)))
                {
                    startIndex = 1;
                }

                for (var i = startIndex; i < groupNames.Length; i++)
                {
                    currentGroup = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupNames[i], StringComparison.OrdinalIgnoreCase));

                    if (currentGroup == null)
                    {
                        return null;
                    }
                }

                return currentGroup;
            }

            return null;
        }

        // ======================================================================
        // Block group operations: create (sub)groups + move a block into a group.
        // NOTE: TIA Openness has NO API to reparent an existing PlcBlock
        // (PlcBlock.Parent is read-only). So:
        //   - EnsurePlcBlockGroup: native, via PlcBlockUserGroupComposition.Create.
        //   - MoveBlockToGroup: export -> Delete -> import-into-group round-trip,
        //     so generated blocks can be organized into layer folders afterwards.
        // ======================================================================

        // Navigate the block-group tree by path, CREATING any missing user groups.
        // Returns the leaf group (or null if software not found). `created` lists the
        // group names that were newly created this call (for reporting / idempotency).
        public PlcBlockGroup? EnsurePlcBlockGroup(string softwarePath, string groupPath, out List<string> created)
        {
            created = new List<string>();
            if (IsProjectNull())
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware || plcSoftware.BlockGroup == null)
            {
                return null;
            }

            var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            PlcBlockGroup currentGroup = plcSoftware.BlockGroup;
            foreach (var groupName in groupNames)
            {
                var next = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    next = currentGroup.Groups.Create(groupName);
                    created.Add(groupName);
                    _logger?.LogInformation($"Created PLC block group '{groupName}'");
                }
                currentGroup = next;
            }
            return currentGroup;
        }

        // Move an existing block (found anywhere by exact name) into targetGroupPath.
        // Implemented as export -> Delete -> import-into-group because Openness cannot
        // reparent a block. Prefers SIMATIC SD documents (.s7dcl, keeps comments);
        // falls back to SimaticML XML for mixed-language/STL blocks. Returns a summary.
        public string MoveBlockToGroup(string softwarePath, string blockName, string targetGroupPath, bool autoCreateGroup = true)
        {
            if (IsProjectNull())
            {
                throw new PortalException(PortalErrorCode.InvalidState, "No project is open in TIA Portal");
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is not PlcSoftware plcSoftware || plcSoftware.BlockGroup == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"PlcSoftware not found at '{softwarePath}'");
            }

            // 1) find the block anywhere by exact name
            var all = new List<PlcBlock>();
            GetBlocksRecursive(plcSoftware.BlockGroup, all);
            var block = all.FirstOrDefault(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
            if (block == null)
            {
                throw new PortalException(PortalErrorCode.NotFound, $"Block '{blockName}' not found in '{softwarePath}'");
            }

            // 2) ensure the target group exists
            var targetGroup = autoCreateGroup
                ? EnsurePlcBlockGroup(softwarePath, targetGroupPath, out _)
                : GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
            if (targetGroup == null)
            {
                throw new PortalException(PortalErrorCode.NotFound,
                    $"Target block group '{targetGroupPath}' not found (set autoCreateGroup=true to create it)");
            }

            // already in the target group?
            if (ReferenceEquals(block.Parent, targetGroup))
            {
                return $"Block '{blockName}' already in group '{targetGroupPath}' (no move needed)";
            }

            // 3) export -> delete -> import into target group (no native reparent)
            var tempDir = Path.Combine(Path.GetTempPath(), "tia_mcp_move", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string method;
            try
            {
                bool usedDocs;
                try
                {
                    var exp = block.ExportAsDocuments(new DirectoryInfo(tempDir), blockName);
                    usedDocs = exp != null && exp.State == DocumentResultState.Success;
                }
                catch (EngineeringNotSupportedException)
                {
                    usedDocs = false; // mixed-language / STL -> fall back to XML
                }

                if (usedDocs)
                {
                    block.Delete();
                    var res = targetGroup.Blocks.ImportFromDocuments(new DirectoryInfo(tempDir), blockName, ImportDocumentOptions.Override);
                    if (res == null || res.State != DocumentResultState.Success)
                    {
                        throw new PortalException(PortalErrorCode.ImportFailed,
                            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (documents)");
                    }
                    method = "documents(.s7dcl)";
                }
                else
                {
                    var xml = Path.Combine(tempDir, blockName + ".xml");
                    block.Export(new FileInfo(xml), ExportOptions.None);
                    block.Delete();
                    var imp = targetGroup.Blocks.Import(new FileInfo(xml), ImportOptions.Override);
                    if (imp == null || imp.Count == 0)
                    {
                        throw new PortalException(PortalErrorCode.ImportFailed,
                            $"Re-import of '{blockName}' into '{targetGroupPath}' failed (xml)");
                    }
                    method = "xml(SimaticML)";
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best-effort cleanup */ }
            }

            // 4) verify the block is now under the target group
            var verifyGroup = GetPlcBlockGroupByPath(softwarePath, targetGroupPath);
            var present = verifyGroup?.Blocks.FirstOrDefault(b => b.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase)) != null;
            if (!present)
            {
                throw new PortalException(PortalErrorCode.ImportFailed,
                    $"Move of '{blockName}' to '{targetGroupPath}' could not be verified");
            }

            return $"Moved '{blockName}' to '{targetGroupPath}' via {method}";
        }

        private PlcTypeGroup? GetPlcTypeGroupByPath(string softwarePath, string groupPath)
        {
            if (_project == null)
            {
                return null;
            }

            var softwareContainer = GetSoftwareContainer(softwarePath);
            if (softwareContainer?.Software is PlcSoftware plcSoftware)
            {
                if (plcSoftware?.TypeGroup == null)
                {
                    return null;
                }

                var groupNames = groupPath.Split(['/'], StringSplitOptions.RemoveEmptyEntries);

                PlcTypeGroup? currentGroup = plcSoftware.TypeGroup;

                foreach (var groupName in groupNames)
                {
                    currentGroup = currentGroup.Groups.FirstOrDefault(g => g.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));

                    if (currentGroup == null)
                    {
                        return null;
                    }
                }

                return currentGroup;
            }

            return null;
        }

        private string GetPlcBlockGroupPath(PlcBlockGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            PlcBlockGroup? nullableGroup = group;
            var path = group.Name;

            while (nullableGroup != null && nullableGroup.Parent != null)
            {
                try
                {
                    //group = (PlcBlockGroup) group.Parent;
                    if (group is PlcBlockSystemGroup systemGroup)
                    {
                        // do not get parent for system group
                        break;
                    }

                    nullableGroup = nullableGroup.Parent as PlcBlockGroup;
                }
                catch (Exception)
                {
                    // Handle any exceptions that may occur while accessing the parent
                    break;
                }

                if (nullableGroup != null)
                {
                    path = $"{nullableGroup.Name}/{path}";
                }
            }

            return path;
        }

        private string GetPlcTypeGroupPath(PlcTypeGroup group)
        {
            if (group == null)
            {
                return string.Empty;
            }

            PlcTypeGroup? nullableGroup = group;
            var path = group.Name;

            while (nullableGroup != null && nullableGroup.Parent != null)
            {
                try
                {
                    //group = (PlcTypeGroup) group.Parent;
                    if (group is PlcTypeSystemGroup systemGroup)
                    {
                        // do not get parent for system group
                        break;
                    }

                    nullableGroup = nullableGroup.Parent as PlcTypeGroup;
                }
                catch (Exception)
                {
                    // Handle any exceptions that may occur while accessing the parent
                    break;
                }

                if (nullableGroup != null)
                {
                    path = $"{nullableGroup.Name}/{path}";
                }
            }

            return path;
        }

        #endregion

        #region GetRecursive ...

        private bool GetDevicesRecursive(DeviceUserGroup group, List<Device> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Devices)
            {
                if (composition is Device device)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(device.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this device if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this device
                        continue;
                    }

                    list.Add(device);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetDevicesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        private bool GetBlocksRecursive(PlcBlockGroup group, List<PlcBlock> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Blocks)
            {
                if (composition is PlcBlock block)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(block.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this block if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this block
                        continue;
                    }

                    list.Add(block);

                    anySuccess = true;
                }
            }

            foreach (var subgroup in group.Groups)
            {
                anySuccess = GetBlocksRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        private bool GetTypesRecursive(PlcTypeGroup group, List<PlcType> list, string regexName = "")
        {
            var anySuccess = false;

            foreach (var composition in group.Types)
            {
                if (composition is PlcType type)
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(regexName) && !Regex.IsMatch(type.Name, regexName, RegexOptions.IgnoreCase))
                        {
                            continue; // Skip this block if it doesn't match the pattern
                        }
                    }
                    catch (Exception)
                    {
                        // Invalid regex pattern, skip this block
                        continue;
                    }

                    list.Add(type);

                    anySuccess = true;
                }

            }

            foreach (PlcTypeGroup subgroup in group.Groups)
            {
                anySuccess = GetTypesRecursive(subgroup, list, regexName);
            }

            return anySuccess;
        }

        #region meta (reflection helpers)

        private object? ResolveObject(string objectKind, string objectPath, string softwarePath)
        {
            if (IsProjectNull()) return null;

            switch ((objectKind ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "project":
                    return _project;

                case "portal":
                    return _portal;

                case "device":
                    return GetDevice(objectPath);

                case "deviceitem":
                case "device_item":
                case "device-item":
                    return GetDeviceItem(objectPath);

                case "software":
                case "plcsoftware":
                case "plc_software":
                case "plc-software":
                {
                    // PLC 优先
                    var plcsw = GetPlcSoftware(objectPath);
                    if (plcsw != null) return plcsw;
                    // HMI 兜底（Unified / Classic），让 DescribeObject/InvokeObject 也能操作 HMI
                    try
                    {
                        var sc = GetSoftwareContainer(objectPath);
                        if (sc?.Software != null) return sc.Software;
                    }
                    catch { }
                    return null;
                }

                case "hmi":
                case "hmisoftware":
                case "hmi_software":
                case "hmi-software":
                {
                    try
                    {
                        var sc = GetSoftwareContainer(objectPath);
                        if (sc?.Software != null) return sc.Software;
                    }
                    catch { }
                    return null;
                }

                case "block":
                    if (string.IsNullOrWhiteSpace(softwarePath)) return null;
                    return GetBlock(softwarePath, objectPath);

                case "type":
                    if (string.IsNullOrWhiteSpace(softwarePath)) return null;
                    return GetType(softwarePath, objectPath);

                case "hmiscreen":
                case "hmi_screen":
                case "hmi-screen":
                {
                    // objectPath: "HMI_RT_1:Main"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var screenName = parts[1];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    return TryFindByNameInCollection(sc.Software, new[] { "Screens", "ScreenFolder" }, screenName);
                }

                case "hmitagtable":
                case "hmi_tagtable":
                case "hmi-tagtable":
                {
                    // objectPath: "HMI_RT_1:默认变量表"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var tableName = parts[1];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    return TryFindByNameInCollection(sc.Software, new[] { "TagTables" }, tableName);
                }

                case "hmitag":
                case "hmi_tag":
                case "hmi-tag":
                {
                    // objectPath: "HMI_RT_1:默认变量表:StartPB"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 3);
                    if (parts.Length != 3) return null;
                    var swPath = parts[0];
                    var tableName = parts[1];
                    var tagName = parts[2];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var table = TryFindByNameInCollection(sc.Software, new[] { "TagTables" }, tableName);
                    if (table == null) return null;
                    var tagsComp = table.GetType().GetProperty("Tags")?.GetValue(table);
                    if (tagsComp == null) return null;
                    try
                    {
                        if (tagsComp is System.Collections.IEnumerable en)
                        {
                            foreach (var it in en)
                            {
                                var n = TryGetName(it);
                                if (!string.IsNullOrWhiteSpace(n) &&
                                    string.Equals(n!.Trim(), tagName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return it;
                                }
                            }
                        }
                    }
                    catch { }
                    return TryFindByNameInCollection(tagsComp, Array.Empty<string>(), tagName);
                }

                case "hmiconnection":
                case "hmi_connection":
                case "hmi-connection":
                {
                    // objectPath: "HMI_RT_1:HMI_Connection_1"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 2);
                    if (parts.Length != 2) return null;
                    var swPath = parts[0];
                    var connectionName = parts[1];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var conns = TryGetPropertyValue(sc.Software, "Connections");
                    if (conns == null) return null;
                    return FindExistingByName(conns, connectionName) ?? TryFindByNameInCollection(conns, Array.Empty<string>(), connectionName);
                }

                case "hmiscreenitem":
                case "hmi_screenitem":
                case "hmi-screenitem":
                case "hmiscreen_item":
                case "hmi_screen_item":
                case "hmi-screen-item":
                {
                    // objectPath: "HMI_RT_1:Main:BTN_Start"
                    var parts = (objectPath ?? "").Split(new[] { ':' }, 3);
                    if (parts.Length != 3) return null;
                    var swPath = parts[0];
                    var screenName = parts[1];
                    var itemName = parts[2];
                    var sc = GetSoftwareContainer(swPath);
                    if (sc?.Software == null) return null;
                    var screen = TryFindByNameInCollection(sc.Software, new[] { "Screens", "ScreenFolder" }, screenName);
                    if (screen == null) return null;
                    var itemsComp = screen.GetType().GetProperty("ScreenItems")?.GetValue(screen);
                    if (itemsComp == null) return null;
                    try
                    {
                        if (itemsComp is System.Collections.IEnumerable en)
                        {
                            foreach (var it in en)
                            {
                                var n = TryGetName(it);
                                if (!string.IsNullOrWhiteSpace(n) &&
                                    string.Equals(n!.Trim(), itemName, StringComparison.OrdinalIgnoreCase))
                                {
                                    return it;
                                }
                            }
                        }
                    }
                    catch { }
                    return null;
                }

                default:
                    return null;
            }
        }

        private static string? TryGetName(object? o)
        {
            if (o == null) return null;
            try
            {
                var t = o.GetType();
                var p = t.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                var v = p?.GetValue(o);
                return v?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ModelContextProtocol.ObjectMember> DescribeMembers(object o, int maxMembers)
        {
            var t = o.GetType();
            var list = new List<ModelContextProtocol.ObjectMember>();

            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                list.Add(new ModelContextProtocol.ObjectMember
                {
                    Kind = "Property",
                    Name = p.Name,
                    Type = p.PropertyType.FullName ?? p.PropertyType.Name
                });
                if (list.Count >= maxMembers) return list;
            }

            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.IsSpecialName) continue;
                var ps = m.GetParameters();
                var sig = $"{m.Name}({string.Join(", ", ps.Select(x => $"{x.ParameterType.Name} {x.Name}"))}) -> {m.ReturnType.Name}";
                list.Add(new ModelContextProtocol.ObjectMember
                {
                    Kind = "Method",
                    Name = m.Name,
                    Type = m.ReturnType.FullName ?? m.ReturnType.Name,
                    Signature = sig
                });
                if (list.Count >= maxMembers) return list;
            }

            return list;
        }

        private static object? GetPropertyPathValue(object root, string propertyPath)
        {
            object? current = root;
            foreach (var part in (propertyPath ?? string.Empty).Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (current == null) return null;
                var t = current.GetType();
                var p = t.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
                if (p == null) return null;
                if (p.GetIndexParameters().Length != 0) return null;
                current = p.GetValue(current);
            }
            return current;
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeObject(string objectKind, string objectPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                TypeName = o.GetType().FullName ?? o.GetType().Name,
                Members = DescribeMembers(o, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "", int maxMembers = 200)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = $"{objectPath}.{propertyPath}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var v = GetPropertyPathValue(o, propertyPath);
            if (v == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Property not found",
                    ObjectKind = objectKind,
                    ObjectPath = $"{objectPath}.{propertyPath}",
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = $"{objectPath}.{propertyPath}",
                TypeName = v.GetType().FullName ?? v.GetType().Name,
                Members = DescribeMembers(v, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectValue GetObjectProperty(string objectKind, string objectPath, string propertyPath, string softwarePath = "")
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var v = GetPropertyPathValue(o, propertyPath);
            var vt = v?.GetType();

            object? outValue = v;
            if (v is IEnumerable enumerable && v is not string)
            {
                var items = new List<string>();
                foreach (var it in enumerable)
                {
                    if (it == null) continue;
                    items.Add(TryGetName(it) ?? it.ToString() ?? "");
                    if (items.Count >= 200) break;
                }
                outValue = items;
            }

            return new ModelContextProtocol.ResponseObjectValue
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                ValueType = vt?.FullName ?? (v == null ? null : v.GetType().Name),
                Value = outValue
            };
        }

        public ModelContextProtocol.ResponseObjectChildren ListObjectChildren(string objectKind, string objectPath, string collectionProperty, string softwarePath = "", int limit = 200)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectChildren
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Collection = collectionProperty,
                    Items = Array.Empty<string>()
                };
            }

            var v = GetPropertyPathValue(o, collectionProperty);
            if (v is not IEnumerable enumerable || v is string)
            {
                return new ModelContextProtocol.ResponseObjectChildren
                {
                    Message = "Collection not found or not enumerable",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Collection = collectionProperty,
                    Items = Array.Empty<string>()
                };
            }

            var items = new List<string>();
            foreach (var it in enumerable)
            {
                if (it == null) continue;
                items.Add(TryGetName(it) ?? it.ToString() ?? "");
                if (items.Count >= Math.Max(1, Math.Min(2000, limit))) break;
            }

            return new ModelContextProtocol.ResponseObjectChildren
            {
                Message = "OK",
                ObjectKind = objectKind,
                ObjectPath = objectPath,
                Collection = collectionProperty,
                Items = items
            };
        }

        private static ModelContextProtocol.ResponseObjectValue InvokeOnInstance(object instance, string resultKind, string resultPath, string methodName, JsonArray? args, bool allowWrite)
        {
            var hardDenyReason = GetHardDeniedReflectionReason(instance, resultKind, resultPath, methodName);
            if (!string.IsNullOrWhiteSpace(hardDenyReason))
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = hardDenyReason,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }

            var safe = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ToString",
                "GetAttribute",
                "GetAttributeInfos"
            };

            if (!allowWrite && !safe.Contains(methodName))
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Method not allowed (read-only mode)",
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }

            var argValues = new List<object?>();
            if (args != null)
            {
                foreach (var a in args)
                {
                    if (a == null) { argValues.Add(null); continue; }
                    if (a is JsonValue jv)
                    {
                        if (jv.TryGetValue<string>(out var s)) { argValues.Add(s); continue; }
                        if (jv.TryGetValue<int>(out var i)) { argValues.Add(i); continue; }
                        if (jv.TryGetValue<long>(out var l)) { argValues.Add(l); continue; }
                        if (jv.TryGetValue<double>(out var d)) { argValues.Add(d); continue; }
                        if (jv.TryGetValue<bool>(out var b)) { argValues.Add(b); continue; }
                        argValues.Add(jv.ToString());
                        continue;
                    }
                    argValues.Add(a.ToString());
                }
            }

            try
            {
                var t = instance.GetType();
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(m => !m.IsSpecialName && m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                MethodInfo? mi = methods.FirstOrDefault(m => m.GetParameters().Length == argValues.Count);
                if (mi == null)
                {
                    return new ModelContextProtocol.ResponseObjectValue
                    {
                        Message = "Method not found (signature mismatch)",
                        ObjectKind = resultKind,
                        ObjectPath = resultPath
                    };
                }

                var ps = mi.GetParameters();
                var converted = new object?[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    var av = argValues[i];
                    if (av == null) { converted[i] = null; continue; }
                    var pt = ps[i].ParameterType;
                    if (pt == typeof(string)) { converted[i] = av.ToString(); continue; }
                    if (pt == typeof(int)) { converted[i] = Convert.ToInt32(av); continue; }
                    if (pt == typeof(long)) { converted[i] = Convert.ToInt64(av); continue; }
                    if (pt == typeof(double)) { converted[i] = Convert.ToDouble(av); continue; }
                    if (pt == typeof(bool)) { converted[i] = Convert.ToBoolean(av); continue; }
                    if (pt == typeof(object)
                        && methodName.Equals("SetAttribute", StringComparison.OrdinalIgnoreCase)
                        && i == 1
                        && argValues.Count >= 2
                        && argValues[0] is string attrName)
                    {
                        var oldValue = instance.GetType()
                            .GetMethod("GetAttribute", new[] { typeof(string) })
                            ?.Invoke(instance, new object[] { attrName });
                        converted[i] = oldValue == null ? av : CoerceReflectionValue(av, oldValue.GetType());
                        continue;
                    }
                    converted[i] = av;
                }

                var result = mi.Invoke(instance, converted);

                object? outValue = result;
                if (result is IEnumerable enumerable && result is not string)
                {
                    var items = new List<string>();
                    foreach (var it in enumerable)
                    {
                        if (it == null) continue;
                        items.Add(TryGetName(it) ?? it.ToString() ?? "");
                        if (items.Count >= 200) break;
                    }
                    outValue = items;
                }

                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "OK",
                    ObjectKind = resultKind,
                    ObjectPath = resultPath,
                    ValueType = result?.GetType().FullName ?? (result == null ? null : result.GetType().Name),
                    Value = outValue
                };
            }
            catch (TargetInvocationException tie)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = tie.InnerException?.Message ?? tie.Message,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
            catch (Exception ex)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = ex.Message,
                    ObjectKind = resultKind,
                    ObjectPath = resultPath
                };
            }
        }

        private static string? GetHardDeniedReflectionReason(object instance, string resultKind, string resultPath, string methodName)
        {
            var instanceType = instance.GetType().FullName ?? instance.GetType().Name;
            var haystack = string.Join(" ", instanceType, resultKind ?? "", resultPath ?? "", methodName ?? "");

            if (haystack.IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Denied by safety policy: force-table and force-related operations are not exposed through this MCP server.";
            }

            var isOnlineMonitorSurface =
                haystack.IndexOf("Online", StringComparison.OrdinalIgnoreCase) >= 0 ||
                haystack.IndexOf("Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                haystack.IndexOf("Watch", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!isOnlineMonitorSurface)
            {
                return null;
            }

            var mutatingPrefixes = new[]
            {
                "Set",
                "Write",
                "Create",
                "Delete",
                "Remove",
                "Import",
                "Add",
                "Insert",
                "Update",
                "Modify",
                "GoOnline",
                "GoOffline",
                "Download",
                "Activate",
                "Start",
                "Stop"
            };

            if (mutatingPrefixes.Any(p => (methodName ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                return "Denied by safety policy: online/watch/monitor surfaces are read-only. The MCP server may read current status only and must not modify watch-table objects or PLC values.";
            }

            return null;
        }

        public ModelContextProtocol.ResponseObjectValue InvokeObject(string objectKind, string objectPath, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        {
            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }
            return InvokeOnInstance(o, objectKind, objectPath, methodName, args, allowWrite);
        }

        private static Type? FindTypeBySuffix(string typeSuffix)
        {
            if (string.IsNullOrWhiteSpace(typeSuffix)) return null;
            var suf = typeSuffix.Trim();

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    var n = t.FullName ?? t.Name;
                    if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase) || t.Name.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
                        return t;
                }
            }

            return null;
        }

        private static object? TryGetService(object target, Type serviceType)
        {
            try
            {
                var t = target.GetType();
                var mi = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetService" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (mi == null) return null;

                var g = mi.MakeGenericMethod(serviceType);
                return g.Invoke(target, null);
            }
            catch
            {
                return null;
            }
        }

        public ModelContextProtocol.ResponseObjectDescribe DescribeService(string objectKind, string objectPath, string serviceTypeSuffix, string softwarePath = "", int maxMembers = 200)
        {
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var st = FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "Service type not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = null,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            var svc = TryGetService(o, st);
            if (svc == null)
            {
                return new ModelContextProtocol.ResponseObjectDescribe
                {
                    Message = "GetService failed (service not available for this object)",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath,
                    TypeName = st.FullName ?? st.Name,
                    Members = Array.Empty<ModelContextProtocol.ObjectMember>()
                };
            }

            return new ModelContextProtocol.ResponseObjectDescribe
            {
                Message = "OK",
                ObjectKind = "Service",
                ObjectPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}",
                TypeName = svc.GetType().FullName ?? svc.GetType().Name,
                Members = DescribeMembers(svc, Math.Max(10, Math.Min(2000, maxMembers))).ToList()
            };
        }

        public ModelContextProtocol.ResponseObjectValue InvokeService(string objectKind, string objectPath, string serviceTypeSuffix, string methodName, JsonArray? args = null, string softwarePath = "", bool allowWrite = false)
        {
            if ((serviceTypeSuffix ?? string.Empty).IndexOf("Force", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Denied by safety policy: force-related services are not exposed through this MCP server.",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var o = ResolveObject(objectKind, objectPath, softwarePath);
            if (o == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Object not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var st = FindTypeBySuffix(serviceTypeSuffix!);
            if (st == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "Service type not found",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var svc = TryGetService(o, st);
            if (svc == null)
            {
                return new ModelContextProtocol.ResponseObjectValue
                {
                    Message = "GetService failed (service not available for this object)",
                    ObjectKind = objectKind,
                    ObjectPath = objectPath
                };
            }

            var svcPath = $"{objectKind}:{objectPath}::{serviceTypeSuffix}";
            return InvokeOnInstance(svc, "Service", svcPath, methodName, args, allowWrite);
        }

        #endregion

        #endregion

        #endregion
    }
}
