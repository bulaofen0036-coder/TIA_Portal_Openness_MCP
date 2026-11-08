# V19 块接口读取：为什么必须走 SimaticML XML

> 结论：在 TIA Portal V19 的 Openness API 里，**只有 `DataBlock` 有 typed `Interface` 属性**。
> FB / FC / OB / UDT（`PlcType`）没有对应的 CLR 属性，读它们的声明只能用西门子官方的
> `Export` 导出 SimaticML XML，再解析 `<Interface><Sections>`。

## 1. 官方 V19 PublicAPI 的实际接口面

以下成员来自 TIA V19 安装目录随 PublicAPI 发布的官方 `Siemens.Engineering.xml`：

| 类型 | 官方成员 | 说明 |
|---|---|---|
| `Siemens.Engineering.SW.Blocks.DataBlock` | `P:...DataBlock.Interface` | 返回 `PlcBlockInterface`，**仅 DB 有** |
| `Siemens.Engineering.SW.Blocks.PlcBlock` | `P:...PlcBlock.InterfaceModifiedDate` | 只有修改时间，**没有 `Interface`** |
| `Siemens.Engineering.SW.Blocks.FunctionBlock` | 继承 `PlcBlock` | 没有自己的 `Interface` |
| `Siemens.Engineering.SW.Blocks.Function` | 继承 `PlcBlock` | 没有自己的 `Interface` |
| `Siemens.Engineering.SW.Blocks.OrganizationBlock` | 继承 `PlcBlock` | 没有自己的 `Interface` |
| `Siemens.Engineering.SW.Types.PlcType` | `P:...PlcType.InterfaceModifiedDate` | 只有修改时间，**没有 `Interface`** |
| `Siemens.Engineering.SW.Blocks.Interface.PlcBlockInterface` | `P:...Members` | 只有扁平成员表，没有 Section 分类 |

核对命令（PowerShell，路径按实际安装位置调整）：

```powershell
$xml = "C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19\Siemens.Engineering.xml"
Select-String -Path $xml -Pattern 'DataBlock\.Interface|PlcBlock\.InterfaceModifiedDate|PlcType\.InterfaceModifiedDate'
# 不应出现 P:...PlcBlock.Interface 或 P:...PlcType.Interface
```

`DataBlock.Interface` 返回的 `PlcBlockInterface` 也只有 `Members`，没有
Input/Output/InOut/Static/Temp/Constant 的段信息；DB 的“结构”就是它的 Static 段。

## 2. XML 导出本身是官方 API

- `PlcBlock.Export(FileInfo, ExportOptions)`：官方文档原文 “Simatic ML export of a Plc block”。
- `PlcType.Export(FileInfo, ExportOptions, DocumentInfoOptions)`：官方文档原文 “Simatic ML export of a Plc type”。

导出的 `<Interface>` 结构由西门子官方文档定义（“XML structure of the block interface section”）：

```xml
<Interface>
  <Sections>
    <Section Name="Input">
      <Member Name="Start" Datatype="Bool">
        <StartValue>false</StartValue>
        <Comment><MultiLanguageText Lang="zh-CN">启动</MultiLanguageText></Comment>
      </Member>
    </Section>
    <Section Name="Static">
      <Member Name="Config" Datatype="Struct">
        <Member Name="Speed" Datatype="Real">
          <StartValue>0.0</StartValue>
        </Member>
      </Member>
    </Section>
  </Sections>
</Interface>
```

## 3. 工具如何用它

`GetBlockInterface` / `GetTypeStructure`：

1. 在引擎侧把块/类型 `Export` 到 `%TEMP%\TiaMcpInterface_<guid>\`。
2. 用 `InterfaceXmlReader` 解析 `<Interface><Sections>`，转成 JSON（段、成员、类型、初值、
   注释、数组边界、嵌套结构；`maxMembers` 截断时 `Truncated=true`）。
3. 删除临时目录，宿主机只收 JSON，不依赖共享目录。

前置条件（工具会明确报错，不会静默返回空）：

- `Connect` + `OpenProject`；
- 块/类型 `IsConsistent=true`（否则先编译）；
- PLC 离线（先 `GoOffline`；在线模式 XML 导出不工作）。

## 4. 为什么不做 DB 的 typed 快路径

可以做，但只覆盖 DB，而且 `PlcBlockInterface.Members` 是扁平的，没有段分类。
为了 FB/FC/OB/DB/UDT 返回同一种结构，当前统一走 XML。若以后需要降低 DB 读取的
前置要求，可以单独加一条 typed 快路径，但不能替代 FB/FC/OB/UDT 的 XML 路径。

## 5. 参考

- Siemens TIA Portal Openness 文档：Accessing DB value parameter without export
  （`DataBlock.Interface` / `PlcBlockInterface.Members` 的官方示例）。
- Siemens TIA Portal Openness 文档：XML structure of the block interface section
  （`<Interface><Sections><Section><Member>` 的官方结构）。
- 本机官方 PublicAPI 文档：`...\Portal V19\PublicAPI\V19\Siemens.Engineering.xml`。
- 上游 2.7.10 的 `GetTypeInfo(includeMembers=true)` 同样先 `ExportType` 再解析 XML
  （`TypeMemberReader`），可作为独立佐证。
