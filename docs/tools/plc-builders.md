# PLC Builder MCP Tools

Document id: `plc-builders`

This document is the stable contract for the PLC XML builder tools exposed by the TIA MCP server.
All tools in this page are designed for TIA Portal V21 XML shapes and keep the existing safety gates:

- Build-only tools are offline and return XML strings only.
- `PlcBuildAndImport` defaults to `dryRun=true`; it builds XML, writes a temp file, classifies it, and returns an import plan.
- Real TIA writes happen only when `dryRun=false`, after the caller has resolved `softwarePath` and group paths from `GetProjectTree` / `ValidateAutomationContext`.
- Import is not a compile proof. For `dryRun=false`, keep `compileAfter=true` unless you have a specific reason not to.
- Do not use guessed addresses or guessed DB members for HMI/PLC integration. Use exported symbols or an explicit mapping.

## Tool Selection

Use build-only tools when you need XML for review, reports, or later import:

| Tool | Scope | Writes Project |
|---|---|---|
| `BuildPlcUdtXml` | UDT / `SW.Types.PlcStruct` | No |
| `BuildPlcTagTableXml` | PLC tag table / `SW.Tags.PlcTagTable` | No |
| `BuildPlcGlobalDbXml` | Global DB / `SW.Blocks.GlobalDB` | No |
| `BuildStructuredTextXml` | SCL `StructuredText/v4` fragment | No |
| `BuildFlgNetCallXml` | LAD `FlgNet/v5` FC call network | No |
| `ComposePlcFcBlockXml` | SCL FC block XML | No |
| `ComposePlcFbBlockXml` | SCL FB block XML, no instance DB | No |

Use `PlcBuildAndImport` when you want one call to build and prepare the import path:

| Mode | Behavior |
|---|---|
| `dryRun=true` | Build XML, write a temp XML file, classify it, return discovered objects. No TIA connection and no project write. |
| `dryRun=false` | Build XML, write a temp XML file, import by classified kind, optionally compile. Requires a connected project and verified paths. |

## BuildPlcUdtXml

Input:

```json
{
  "members": [
    {
      "name": "FaultActive",
      "datatype": "Bool",
      "externalWritable": true,
      "commentZhCn": "故障激活"
    },
    {
      "name": "FaultCode",
      "datatype": "Int",
      "commentZhCn": "故障代码"
    }
  ]
}
```

Required fields:

| Path | Meaning |
|---|---|
| `$.members[]` | At least one UDT member |
| `$.members[].name` | Member name |
| `$.members[].datatype` | TIA datatype, for example `Bool`, `Int`, `Real`, `"MyUDT"` |

Optional fields:

| Path | Meaning |
|---|---|
| `$.members[].externalWritable` | Emits `ExternalWritable` boolean attribute |
| `$.members[].commentZhCn` / `comment` | Chinese comment text |

## BuildPlcTagTableXml

Input:

```json
{
  "tableName": "StartStop",
  "tags": [
    { "name": "StartPB", "dataTypeName": "Bool", "logicalAddress": "%I0.0" },
    { "name": "RunOut", "dataTypeName": "Bool", "logicalAddress": "%Q0.0" }
  ]
}
```

Aliases:

- `tableName` may be `name`.
- `dataTypeName` may be `datatype` or `dataType`.
- `logicalAddress` may be `address`.

Validation:

- Each tag must have a name, datatype, and absolute TIA logical address.
- Logical address must start with `%`.

## BuildPlcGlobalDbXml

Input:

```json
{
  "dbName": "DB_HMI_Template_Data",
  "dbNumber": 101,
  "staticMembers": [
    {
      "name": "MotorRun",
      "datatype": "Bool",
      "externalWritable": true,
      "commentZhCn": "电机运行",
      "startValue": "false"
    },
    {
      "name": "SpeedSet",
      "datatype": "Int",
      "commentZhCn": "速度设定",
      "startValue": "0"
    }
  ]
}
```

Aliases:

- `dbName` may be `name`.
- `dbNumber` may be `number`.
- `staticMembers` may be `members`.

Memory layout (issue #31):

- Default is **Standard** (non-optimized access). This is what the builder has always emitted, and what
  PUT/GET partners, absolute S7 reads (`ReadPlcLiveValuesS7`) and classic-HMI absolute tags require.
- `"optimized": true` emits an **Optimized** DB. `"memoryLayout": "Standard" | "Optimized"` is accepted as an
  alias; giving both with different meanings is an error.
- Unknown top-level keys are rejected with a did-you-mean hint (a misspelt `optimised` no longer produces a
  DB with the wrong layout silently).
- To confirm the layout of a DB in the project, read `MemoryLayout` from `GetBlockInfo`. `PlcBuildAndImport`
  does this automatically after import (see below).

## BuildStructuredTextXml

Input:

```json
{
  "operations": [
    { "op": "if", "condition": "Start" },
    { "op": "assignment", "target": "Run", "value": "TRUE", "indent": 2 },
    { "op": "else" },
    { "op": "assignment", "target": "Run", "value": "FALSE", "indent": 2 },
    { "op": "endif" }
  ]
}
```

Supported operations:

| `op` | Required fields | Notes |
|---|---|---|
| `if` / `ifheader` | `condition` | Emits `IF <condition> THEN` |
| `else` | none | Emits `ELSE` |
| `endif` / `end_if` | none | Emits `END_IF;` |
| `assignment` / `assign` | `target`, `literalValue` or `value` | Emits `<target> := <value>;` |
| `token` | `text` | Low-level token escape hatch |
| `blank` | optional `count` | Low-level spacing |
| `newline` | none | Low-level line break |

Set `innerOnly=true` when embedding the result into `ComposePlcFcBlockXml`.

## BuildFlgNetCallXml

Input:

```json
{
  "callName": "Limit_Protect",
  "parameters": [
    {
      "name": "Current_Location",
      "section": "Input",
      "dataType": "Real",
      "symbol": "DB_Axis.Actual.Position"
    },
    {
      "name": "Enable",
      "section": "Input",
      "dataType": "Bool",
      "sourceKind": "constant",
      "value": "1"
    },
    {
      "name": "Fault",
      "section": "Output",
      "dataType": "Bool",
      "symbolPath": ["DB_Axis", "Fault"]
    }
  ]
}
```

Rules:

- `callName` may be `name`.
- Global variables use `symbolPath[]` or dotted `symbol` / `path` / `plcTag`.
- Constants use `sourceKind=constant` and `value`.
- `section` is usually `Input` or `Output`.

## ComposePlcFcBlockXml

Input:

```json
{
  "blockName": "FC_StartStop",
  "blockNumber": 1,
  "inputs": [
    { "name": "Start", "datatype": "Bool" },
    { "name": "Stop", "datatype": "Bool" }
  ],
  "outputs": [
    { "name": "Run", "datatype": "Bool" }
  ],
  "structuredText": {
    "operations": [
      { "op": "if", "condition": "Stop" },
      { "op": "assignment", "target": "Run", "value": "FALSE", "indent": 2 },
      { "op": "else" },
      { "op": "assignment", "target": "Run", "value": "TRUE", "indent": 2 },
      { "op": "endif" }
    ]
  }
}
```

Aliases:

- `blockName` may be `name`.
- `blockNumber` may be `number`.

You may provide either:

- `structuredTextInnerXml`, or
- `structuredText.operations[]`.

## ComposePlcFbBlockXml

Input:

```json
{
  "blockName": "FB_Motor",
  "blockNumber": 20,
  "inputs": [
    { "name": "Start", "datatype": "Bool" },
    { "name": "Stop", "datatype": "Bool" }
  ],
  "outputs": [
    { "name": "Run", "datatype": "Bool" }
  ],
  "statics": [
    { "name": "Latch", "datatype": "Bool" }
  ],
  "structuredText": {
    "operations": [
      { "op": "if", "condition": "Stop" },
      { "op": "assignment", "target": "Latch", "value": "FALSE", "indent": 2 },
      { "op": "else" },
      { "op": "assignment", "target": "Latch", "value": "TRUE", "indent": 2 },
      { "op": "endif" },
      { "op": "assignment", "target": "Run", "value": "Latch" }
    ]
  }
}
```

Supported interface arrays:

| Path | TIA section |
|---|---|
| `inputs[]` | Input |
| `outputs[]` | Output |
| `inouts[]` / `inOuts[]` | InOut |
| `statics[]` / `staticMembers[]` | Static |
| `temps[]` / `tempMembers[]` | Temp |

This tool does not create an instance DB. Import the FB first, compile in TIA, then create or regenerate instance DBs through a separately verified workflow.

## PlcBuildAndImport

Minimal dry run:

```json
{
  "softwarePath": "",
  "kind": "fc",
  "json": "{ \"blockName\":\"FC_DryRun\", \"blockNumber\":12, \"inputs\":[{\"name\":\"Start\",\"datatype\":\"Bool\"}], \"outputs\":[{\"name\":\"Run\",\"datatype\":\"Bool\"}], \"structuredText\":{\"operations\":[{\"op\":\"if\",\"condition\":\"Start\"},{\"op\":\"assignment\",\"target\":\"Run\",\"value\":\"TRUE\",\"indent\":2},{\"op\":\"endif\"}]}}",
  "dryRun": true
}
```

Real import checklist:

1. Run `Connect`.
2. Run `GetProjectTree`.
3. Run `ValidateAutomationContext`.
4. Resolve `softwarePath` and the target group path from the tree.
5. Run `PlcBuildAndImport(..., dryRun=true)` first and inspect `WrittenFiles` / `Discovered*`.
6. Run `PlcBuildAndImport(..., dryRun=false, compileAfter=true)`.
7. Check `Compile.ErrorCount`. An import failure is returned as an error (not as a normal response
   with `Failed[]`), and for blocks the tool reads the block back: `Meta.verified` plus
   `Meta.readback {name, number, blockKind, secondaryType, memoryLayout}`. A read-back that does not match
   the generated XML (for example a DB built with `optimized:false` that reads back `Optimized`) is an error.
8. Save only after successful compile/readback.

Supported `kind` values:

| Kind | Classified XML | Import path |
|---|---|---|
| `udt` | `SW.Types.PlcStruct` | `ImportType` |
| `tagtable` | `SW.Tags.PlcTagTable` | `ImportPlcTagTable` |
| `globaldb` | `SW.Blocks.GlobalDB` | `ImportBlock` |
| `fc` | `SW.Blocks.FC` | `ImportBlock` |
| `fb` | `SW.Blocks.FB` | `ImportBlock` |
| `ob` | `SW.Blocks.OB` | `ImportBlock` |

Not yet supported by this one-step builder:

- `instanceDb`
- partial network editing

## `kind=ob` / BuildPlcObXml (issue #30)

Organization blocks must be built as XML. The `.s7dcl` header (`ORGANIZATION_BLOCK "Name"`) carries no OB
number or event class, and TIA V21 imports every such OB as a **Program cycle** OB without an error: a
Startup (OB100) or cyclic-interrupt OB silently becomes a program-cycle OB.

```json
{
  "blockName": "Startup",
  "blockNumber": 100,
  "eventClass": "Startup",
  "temps": [ { "name": "i", "datatype": "Int" } ],
  "structuredText": { "operations": [ { "op": "assignment", "target": "i", "value": "0" } ] }
}
```

| `eventClass` | Allowed numbers | Extra |
|---|---|---|
| `ProgramCycle` | 1, 123..32767 | — |
| `Startup` | 100, 123..32767 | — |
| `CyclicInterrupt` | 30..38, 123..32767 | `cyclicTimeUs` required (µs; `cyclicTimeMs` also accepted), optional `phaseOffsetUs` |

- `eventClass` may be omitted for OB1 / OB100 / OB30..38; it is inferred from the number.
- The number/class pairing is checked before any XML is written. TIA itself rejects a wrong pairing at import
  ("'101' is not a valid OB number").
- The body is optional (an empty cyclic-interrupt OB is a valid placeholder).
- The OB's start-information inputs (`Initial_Call`, `Remanence`, `LostRetentive`, `Event_Count`, …) are
  filled in by TIA for each class; the builder leaves the Input section empty.
- Hardware / time-of-day / time-delay interrupts and error OBs are refused: they need hardware-event or
  schedule configuration that block XML does not carry. Export an existing OB of that type with `ExportBlock`
  and import it with `ImportBlock`.
- After import `PlcBuildAndImport` reads the OB back: name, number, `SecondaryType` (event class) and, for
  cyclic interrupts, `CyclicTime`.
- Unknown JSON keys are rejected.

Use explicit existing import tools for artifacts you already have as verified TIA exports.
