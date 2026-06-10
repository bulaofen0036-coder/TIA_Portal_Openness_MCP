# SIMATIC SD (.s7dcl) LAD — 生产级参考手册

> 基于西门子官方规范 Entry ID: **109994073** (V1.0, 09/2025)，每行语法均来自官方 Listing 案例。
> 配套 skill: `s7dcl-lad-reference`

---

## 零、官方文档索引

| 文档 | ID | 获取方式 |
|------|-----|---------|
| SIMATIC Source Documents LADDER Format Description | `109994073` | support.industry.siemens.com 浏览器下载 |
| Creating and Managing Blocks §1.5 | — | docs.tia.siemens.cloud |
| TIA Portal 内置帮助 | — | TIA 中按 F1 搜索"结构化文档" |

---

## 一、文件对（UTF-8 with BOM）

| 文件 | 内容 |
|------|------|
| `Name.s7dcl` | 块声明 + 网络代码 |
| `Name.s7res` | `MLC_*` → 多语言文本 |

导入: `ImportBlocksFromDocuments(softwarePath, groupPath="", importPath=目录)`

---

## 二、块模板

### 2.1 FC (函数)
```
{
    S7_BlockComment := "MLC_xxx";
    S7_BlockNumber := "901";
    S7_BlockTitle := "MLC_xxx";
    S7_Optimized := "TRUE";
    S7_PreferredLanguage := "LAD";
    S7_Version := "0.1"
}
FUNCTION "FC_Name" : Void
    VAR_INPUT   "A" : Bool;  "Val" : Int;  END_VAR
    VAR_OUTPUT  "Out" : Bool;  "Result" : Int;  END_VAR
    VAR_TEMP    "tmp" : Bool;  END_VAR

    { S7_Language := "LAD"; S7_NetworkComment := "MLC_xxx"; S7_NetworkTitle := "MLC_xxx" }
    NETWORK
        RUNG wire#powerrail
            Contact( #"A" )
            Coil( #Out )
        END_RUNG
    END_NETWORK
END_FUNCTION
```

### 2.2 FB (函数块)
比 FC 多 `VAR` (Static) 区——定时器/计数器实例必须放这里。
```
FUNCTION_BLOCK "FB_Name"
    VAR_INPUT   "Trig" : Bool;  END_VAR
    VAR_OUTPUT  "Q" : Bool;  END_VAR
    VAR         "tonInst" : TON_TIME;  END_VAR     ← Static
    VAR_TEMP    "edge" : Bool;  END_VAR

    { S7_Language := "LAD" }
    NETWORK
        RUNG wire#powerrail
            Contact( #Trig )
            { S7_Templates := "timeType := Time" }
            #tonInst.TON( pt := T#2s, et => )
            Coil( #Q )
        END_RUNG
    END_NETWORK
END_FUNCTION_BLOCK
```

### 2.3 OB (组织块)
```
{
    S7_Optimized := "TRUE";
    S7_PreferredLanguage := "LAD";
    S7_Version := "0.1"
}
ORGANIZATION_BLOCK "Main"
    { S7_Language := "LAD" }
    NETWORK
        RUNG wire#powerrail
            ...instructions...
        END_RUNG
    END_NETWORK
END_ORGANIZATION_BLOCK
```

### 2.4 SCL 网络
```
    { S7_Language := "SCL" }
    NETWORK
          #Out := #In1 AND #In2;
          IF #Enable THEN #Result := #Value; END_IF;
    END_NETWORK
```

---

## 三、指令全集（按官方案例逐条验证）

> 格式说明: `Contact( x )` — 单操作数触点，操作数内联。多操作数指令分行列出所有参数。

### 3.1 触点 Contacts

| 指令 | 操作数 | 语义 | 语法 |
|------|--------|------|------|
| `Contact` | 1 | `out := in AND a` | `Contact( a )` |
| `I_Contact` | 1 | `out := in AND NOT a` | `I_Contact( a )` |
| `P_Contact` | 2 | 上升沿检测 | `P_Contact( operand:=sig, bit:=store )` |
| `N_Contact` | 2 | 下降沿检测 | `N_Contact( operand:=sig, bit:=store )` |
| `GT_Contact` | 2 | `in1 > in2` | `{SrcType:=Int} GT_Contact(in1:=#A, in2:=100)` |
| `LT_Contact` | 2 | `in1 < in2` | `{SrcType:=Int} LT_Contact(in1:=#A, in2:=0)` |
| `EQ_Contact` | 2 | `in1 == in2` | `{SrcType:=Int} EQ_Contact(in1:=#X, in2:=#Y)` |
| `NE_Contact` | 2 | `in1 <> in2` | `{SrcType:=Int} NE_Contact(in1:=#X, in2:=0)` |
| `GE_Contact` | 2 | `in1 >= in2` | `{SrcType:=Int} GE_Contact(in1:=#V, in2:=#Limit)` |
| `LE_Contact` | 2 | `in1 <= in2` | `{SrcType:=Int} LE_Contact(in1:=#V, in2:=#Max)` |
| `IsValidContact` | 1 | 检查浮点有效性 | `IsValidContact( x )` |
| `IsNotValidContact` | 1 | 检查浮点无效 | `IsNotValidContact( x )` |
| `EQ_TypeContact` | 2 | 比较数据类型 | `EQ_TypeContact(in1:=, in2:=)` |
| `NE_TypeContact` | 2 | 比较数据类型 | `NE_TypeContact(in1:=, in2:=)` |
| 标志触点 | 0 | AS300/400 | `BR_FlagContact()`, `OS_FlagContact()`, etc. |

### 3.2 线圈 Coils

| 指令 | 操作数 | 语义 | 语法 |
|------|--------|------|------|
| `Coil` | 1 | `x := in` | `Coil( x )` |
| `I_Coil` | 1 | `x := NOT in` | `I_Coil( x )` |
| `S_Coil` | 1 | `IF in THEN x:=TRUE` | `S_Coil( x )` |
| `R_Coil` | 1 | `IF in THEN x:=FALSE` | `R_Coil( x )` |
| `P_Coil` | 2 | 上升沿触发 | `P_Coil( operand:=out, bit:=store )` |
| `N_Coil` | 2 | 下降沿触发 | `N_Coil( operand:=out, bit:=store )` |
| `TP_Coil` | 2 | 启动脉冲定时器 | `TP_Coil( timer:=#inst, pt:=T#1s )` |
| `TOn_Coil` | 2 | 启动接通延时 | `TOn_Coil( timer:=#inst, pt:=T#500ms )` |
| `TOf_Coil` | 2 | 启动断开延时 | `TOf_Coil( timer:=#inst, pt:=T#2s )` |
| `TOnr_Coil` | 2 | 启动累积延时 | `TOnr_Coil( timer:=#inst, pt:=T#3s )` |
| `SP_Coil` | 2 | Simatic 脉冲 | `SP_Coil( timer:=#inst, pt:=S5T#1s )` |
| `SE_Coil` | 2 | Simatic 扩展脉冲 | `SE_Coil( timer:=#inst, pt:=S5T#1s )` |
| `SD_Coil` | 2 | Simatic 接通延时 | `SD_Coil( timer:=#inst, pt:=S5T#1s )` |
| `SS_Coil` | 2 | Simatic 保持延时 | `SS_Coil( timer:=#inst, pt:=S5T#1s )` |
| `SF_Coil` | 2 | Simatic 断开延时 | `SF_Coil( timer:=#inst, pt:=S5T#1s )` |
| `PT_Coil` | 2 | 运行时改延时 | `PT_Coil( timer:=#inst, pt:=T#5s )` |
| `RT_Coil` | 1 | 复位定时器 | `RT_Coil( #inst )` |
| `CU_Coil` | 1 | 加计数 | `CU_Coil( counter )` |
| `CD_Coil` | 1 | 减计数 | `CD_Coil( counter )` |
| `SC_Coil` | 2 | 预置计数值 | `SC_Coil( counter:=#ctr, pv:=C#100 )` |
| `JumpCoil` | 1 | 条件跳转(真) | `JumpCoil( label )` |
| `I_JumpCoil` | 1 | 条件跳转(假) | `I_JumpCoil( label )` |
| `ReturnCoil` | 1 | 返回+ENO | `ReturnCoil( x )` |
| `ReturnFalse` | 0 | 返回 ENO=false | `ReturnFalse()` |
| `ReturnTrue` | 0 | 返回 ENO=true | `ReturnTrue()` |
| `Return` | 0 | 返回 ENO=rung-in | `Return()` |

### 3.3 否定
```
Not()           out := NOT in
```

### 3.4 ENO-Boxes (数学/转换/调用)

#### 数学运算
```
{ S7_Templates := "SrcType := Int" }     ← 必选(除非 Auto)
Add( in1 := #A, in2 := #B, out => #SUM )
Sub( in1 := #A, in2 := #B, out => #DIFF )
Mul( in1 := #A, in2 := #B, out => #PROD )
Div( in1 := #A, in2 := #B, out => #QUOT )
Mod( in1 := #A, in2 := #B, out => #REM )
```

多输入: `Add(in1:=#A, in2:=#B, in3:=#C, in4:=#D, out=>#SUM)`

#### 传送 & 转换
```
Move( in := 42, out1 => #DST )
{ S7_Templates := "[ inType := Int, outType := Real ]" }
Convert( in := #X, out => #Y )
```

#### EN/ENO 控制
```
{ S7_GenerateENO := TRUE }    ← 开启 ENO 计算
```
默认关闭时 ENO:=EN，不导出 pragma。

#### FC 调用 (官方案例 Listing 25)
```
RUNG wire#powerrail
    Contact( x )
    FC_Name(
        in1 := a,
        in2 := b,
        io1 := c,
        out1 => d
    )
END_RUNG
```

#### FB 调用 (官方案例 Listing 26)
```
RUNG wire#powerrail
    Contact( x )
    instName(
        in1 := a,
        in2 := b,
        io1 := c,
        out1 => d
    )
END_RUNG
```

规则: FC 用函数名，FB 用实例名，均不带引号。Contact 直连调用=EN 输入。**禁止 Contact 和调用之间插入 wire#** — 会切断 EN 连接。

#### Calculate Box (官方案例 Listing 38)
```
{
    S7_Templates := "SrcType := Real";
    S7_Expression := "sqr( sin( in1 )) + sqr( cos( in2 ))"
}
Calculate(
    in1 := a,
    in2 := b,
    out => c
)
```

### 3.5 Q-Boxes (双稳态/定时器/计数器)

#### 双稳态 Flip-Flop (官方案例 Listing 27)
```
RUNG wire#powerrail
    Contact( set )
    c.S_RS( wire#w1 )           ← 第二个输入通过 wire# 汇入
    Coil( q )
END_RUNG
RUNG wire#powerrail
    Contact( reset )
END_RUNG wire#w1
```
`c.S_SR( wire#w1 )` 同理。第二个输入是主导的。

#### IEC 定时器 (官方案例 Listing 29)
```
{ S7_Templates := "timeType := Time" }
#inst.TP(  pt := T#10s,  et => #elapsed )
#inst.TON( pt := T#2s,   et => #elapsed )
#inst.TOF( pt := T#1s,   et => #elapsed )
#inst.TONR(pt := T#3s,   et => #elapsed )
```
实例 MUST 在 FB VAR (Static) 中声明: `"inst" : TON_TIME;`

#### IEC 计数器 (官方案例 Listing 30)
```
{ S7_Templates := "countType := DInt" }

#inst.Ctu(  r := #Reset,  pv := #Preset,  cv => #Value )
#inst.Ctd(  ld := #Load,  pv := #Preset,  cv => #Value )
#inst.Ctud( cu := , cd := , r := #Reset, ld := #Load,
            pv := #Preset, qd => , qu => , cv => #Value )
```

### 3.6 跳转 & 标签

#### Label (官方案例 Listing 31)
```
{ S7_Language := "LAD" }
NETWORK
    Label( here )              ← 必须紧跟 NETWORK
    RUNG wire#powerrail ...
END_NETWORK
```

#### Jump (官方案例 Listing 32)
```
JumpCoil( here )        if rung-in true, jump
I_JumpCoil( here )      if rung-in false, jump
```
必须为 RUNG 最后指令。每网络最多 1 个跳转。

#### JumpList (官方案例 Listing 35)
```
RUNG wire#powerrail
    Contact( a )
    JumpList(
        k := b,
        dest0 => label0,
        dest1 => label1,
        dest2 => label2
    )
END_RUNG
```

#### Switch (官方案例 Listing 36)
```
RUNG wire#powerrail
    Contact( a )
    Switch(
        k := b,
        EQ c => label0,
        LT d => label1,
        NE e => label2,
        else => label_else
    )
END_RUNG
```
关系: `EQ NE LT GT LE GE`. 第一个匹配获胜。

---

## 四、并联分支 (官方案例 Listing 14)

```
NETWORK
    RUNG wire#powerrail
        Contact( a )
        wire#w1
        Coil( x )
    END_RUNG
    RUNG wire#powerrail
        Contact( b )
    END_RUNG wire#w1
    RUNG wire#powerrail
        Contact( c )
    END_RUNG wire#w1
END_NETWORK
```
等价 SCL: `x := a OR b OR c;`

---

## 五、变量引用

| 目标 | 语法 | 说明 |
|------|------|------|
| 块接口变量 | `#VarName` 或 `#"VarName"` | Input/Output/InOut |
| 静态变量 | `#VarName` | FB VAR 区 |
| 临时变量 | `#VarName` | VAR_TEMP 区 |
| 全局 DB 成员 | `"DB_Name".Member` | 双引号包裹 DB 名 |
| 字位访问 | `"WordVar".%X0` | 第 0 位 |
| Wire 标识 | `wire#name` | 网络级作用域 |
| 左母线 | `wire#powerrail` | 预定义 |

---

## 六、.s7res 格式

```
MultiLingualTexts:
  - id: MLC_548
    zh-CN: 块注释
    en-US: Block comment
  - id: MLC_3fA
    zh-CN: N1-串联
    en-US: N1-Series
```

每个 `MLC_*` 必须在 .s7res 中有对应条目。务必同时提供 `en-US`。

---

## 七、陷阱速查

| # | 问题 | 症状 | 正确做法 |
|---|------|------|----------|
| 1 | **Contact 和 Box 间插入 wire#** | `Pin 'en' connection is missing` | Contact 直连 Box |
| 2 | **定时器放 FC Temp** | 编译报错 | FB Static (`TON_TIME`) |
| 3 | **缺少 UTF-8 BOM** | 导入乱码/失败 | 两文件都带 BOM |
| 4 | **MLC_ID 不匹配** | 导入失败 | 逐条核对 .s7res |
| 5 | **超过 1 个跳转/网络** | 语义歧义 | 拆分网络 |
| 6 | **FC 调用用了实例名** | 未找到块 | FC 用函数名 |
| 7 | **.s7res 缺 en-US** | LAD 导入失败 | 加 `en-US:` 行 |
| 8 | **忘写 `S7_Language`** | 网络无效 | 每个 NETWORK 前加 pragma |
| 9 | **猜测未知指令语法** | 导入报错 | `ExportBlocksAsDocuments` 导出真实语法 |
| 10 | **wire# 放错位置** | EN 断连 | wire# 只用于并联分支 |

---

## 八、本地样本路径

| 样本 | 内容 |
|------|------|
| `lad-cookbook/MCPVerify_FC_LAD.s7dcl` | 串联/并联/SR/比较/Move/Add |
| `lad-cookbook/MCPVerify_FB_LAD_v3.s7dcl` | TON/P_Trig/Not/LT |
| `PLC_1/程序块/Main.s7dcl` | OB1: FB调用/DB访问/位寻址/多RUNG |
| `PLC_1/程序块/FB_BasicLatch.s7dcl` | SCL 启停自锁 |
| `PLC_1/程序块/FB_StepSequenceDemo.s7dcl` | SCL 状态机 |
| `PLC_1/程序块/FB_TimerCounterDemo.s7dcl` | SCL 计数 |
| `DS文件收集/SIMATIC_Source_Documents_LADDER_Format_Description.pdf` | 官方规范 Entry ID 109994073 |
