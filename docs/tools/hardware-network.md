# Hardware / Network Tools

Document id: `hardware-network`

这些工具用于把硬件网络配置拆成可组合、可验证的原语。核心原则是：路径必须来自 TIA 读回，写入后必须返回 readback 证据，不根据名字猜 CPU、HMI、接口或属性。

## Safe Workflow

1. `Connect`
2. `GetState`
3. `GetProjectTree`
4. `GetDeviceItemTree(deviceItemPath)`
5. `GetDeviceItemNetworkInfo(deviceItemPath)`
6. `PlanHardwareNetworkConfiguration(planJson)`
7. `EnsureSubnet(...)`
8. `AttachDeviceNodeToSubnet(...)`
9. `SetCpuCommonSettings(...)`
10. `GetDeviceItemNetworkInfo(...)` or returned `readback`
11. Compile/save only after the readback and diagnostics are acceptable.

## PlanHardwareNetworkConfiguration

`PlanHardwareNetworkConfiguration(planJson)` is offline-only. It does not connect to TIA Portal and does not modify a project.

Supported operation types:

- `EnsureSubnet`
- `AttachDeviceNodeToSubnet`
- `SetCpuCommonSettings`

Example:

```json
{
  "operations": [
    {
      "type": "EnsureSubnet",
      "anchorDeviceItemPath": "PLC_1/PLC_1.CPU_1",
      "subnetType": "PROFINET",
      "subnetName": "PN_IE_1",
      "ip": "192.168.0.1",
      "mask": "255.255.255.0"
    },
    {
      "type": "AttachDeviceNodeToSubnet",
      "deviceItemPath": "HMI_1/HMI_1.IE_CP_1",
      "interfaceIndex": 0,
      "subnetName": "PN_IE_1"
    },
    {
      "type": "SetCpuCommonSettings",
      "cpuPath": "PLC_1/PLC_1.CPU_1",
      "settings": {
        "exactAttributes": {
          "Name": "PLC_1"
        }
      }
    }
  ]
}
```

The planner rejects guessed paths such as `PLC`, `CPU`, `HMI`, wildcard paths, unsupported subnet types, invalid IPv4/mask values, and CPU settings that use aliases instead of exact TIA attribute names.

## EnsureSubnet

`EnsureSubnet(anchorDeviceItemPath, subnetType, subnetName)` creates or reuses an Industrial Ethernet / PROFINET subnet by anchoring on a real device item path.

Rules:

- `anchorDeviceItemPath` must come from `GetProjectTree` / `GetDeviceItemTree`.
- `subnetType` is limited to `PROFINET`, `PN`, `PN/IE`, `IndustrialEthernet`, or `Industrial Ethernet`.
- The tool returns `readback` lines containing node path, item, node type, and `connectedSubnet`.

## AttachDeviceNodeToSubnet

`AttachDeviceNodeToSubnet(deviceItemPath, interfaceIndex, subnetName, anchorDeviceItemPath?)` attaches one discovered Industrial Ethernet / PROFINET node to a subnet.

Rules:

- Resolve `deviceItemPath` from project readback.
- Use `interfaceIndex` from the candidate node list in the returned metadata.
- Pass `anchorDeviceItemPath` only when the subnet may need to be ensured first.
- The tool returns `readback`; success is true only when the requested subnet and target node are visible after the operation.

## SetCpuCommonSettings

`SetCpuCommonSettings(cpuPath, settingsJson)` writes exact CPU device-item attributes.

`settingsJson` must use this shape:

```json
{
  "exactAttributes": {
    "ExactAttributeNameFromGetDeviceItemNetworkInfo": "value"
  }
}
```

Do not pass aliases such as `ip`, `gateway`, or `profinetName` unless those are the exact TIA attribute names returned by `GetDeviceItemInfo` or `GetDeviceItemNetworkInfo`. The tool rejects missing and non-writable attributes and returns applied/rejected lists plus readback evidence.

## CreateS7Connection / GetPlcConnections (issue #29, TIA V21+)

S7 connections live in the PLC's connection table and are what PUT/GET instructions reference by their
connection ID. TIA V21 exposes them through `Siemens.Engineering.HW.CommunicationConnections`
(`GetService<CommunicationManagement>()` on the CPU device item). V20 does not, so both tools report
"requires V21" there.

Partner PLC in the same project (both PLCs already on one subnet, e.g. via `ConnectDeviceNodesToProfinetSubnet`):

```json
{ "plc": "PLC_1", "partnerPlc": "PLC_2", "connectionName": "S7_to_PLC2", "localConnectionId": "101" }
```

TIA creates the partner side automatically (it appears in `GetPlcConnections` for `PLC_2`).

Partner PLC in another project (unspecified partner):

```json
{ "plc": "PLC_1", "partnerIp": "192.168.0.50", "connectionName": "S7_to_Remote", "localConnectionId": "102" }
```

`partnerRack` / `partnerSlot` default to 0 / 1 (S7-1200/1500 CPU).

- Connection IDs are **hexadecimal**, as in TIA's connection table: `"101"` means 16#101. `16#101` and
  `W#16#101` are accepted. Leave `localConnectionId` empty to let TIA assign one (its default is 16#100).
  On S7-1200 (FW V4.7) IDs below 16#100 were rejected by the hardware compile.
- Connection names must be unique on the PLC; a clash is refused before anything is created.
- After creating, the tool reads the values back and (by default) compiles the CPU hardware. An error reported
  under this connection deletes it again and returns TIA's message. Compile errors elsewhere on the device
  (for example the S7-1200 "password for confidential PLC configuration data" setting) are only counted
  (`compile.otherErrorCount`).
- PUT/GET also needs "Permit access with PUT/GET communication" on the partner CPU (`SetPutGetAccess`) and
  non-optimized DBs (`PlcBuildAndImport kind=globaldb` defaults to Standard).

## Safety Notes

- These tools are offline project-edit tools; they do not go online and do not perform Force operations.
- Online monitoring remains read-only and separate from hardware network edits.
- Never save the project until the returned readback and later compile diagnostics are acceptable.
