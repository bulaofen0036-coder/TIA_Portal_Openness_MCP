using System.Collections.Generic;

namespace TiaMcpServer.ModelContextProtocol
{
    /// <summary>
    /// Model-facing guidance, shipped inside the server so EVERY MCP client benefits —
    /// including hosts that never load SKILL.md (VS Code, Cursor, LobeChat, third-party
    /// agents). Two delivery channels:
    ///   1. <see cref="ServerInstructions"/> — returned in the MCP initialize handshake;
    ///      most hosts inject it into the model's system context automatically.
    ///   2. <see cref="Topic(string)"/> — on-demand cheat sheets via the GetAuthoringGuide
    ///      tool, for syntax details too large for the handshake.
    /// All facts here are verified against live TIA V20/V21 machines; do not add
    /// speculative syntax.
    /// </summary>
    public static class McpGuides
    {
        public const string ServerInstructions =
@"TIA Portal MCP server (Siemens PLC/HMI engineering via Openness). How to work well:

FIRST CALL: Bootstrap — returns environment status, connection state, the recommended next tool, and operating rules. Do this before anything else. If the environment itself seems broken (TIA missing, group membership, nothing connects), call Doctor for a plain-language diagnosis with exact fixes.

GOLDEN PATHS (pick one, do not improvise):
- Whole new project → ScaffoldProject with ONE JSON spec (PLC + blocks + HMI + compile + save in a single call). The DEFAULT call is a dry run (offline spec validation, nothing created); when it reports clean, call again with dryRun=false to actually create.
- Add/modify code in an existing project → write SCL or S7DCL text files, then ImportFromDocuments (PREFERRED, .s7dcl) or GenerateBlocksFromExternalSource (.scl). NEVER hand-write SimaticML FlgNet XML for ladder logic — it is fragile (UId bookkeeping, XML entities) and the #1 cause of failed imports. Use S7DCL ladder text instead (GetAuthoringGuide topic 'lad').
- Read/understand a project → GetProjectTree, GetBlocksWithHierarchy. To READ ONE BLOCK'S LOGIC use DescribeBlockLogic — it returns readable LADDER rungs (series ' · ', parallel ' + ') and inline SCL, and flags contacts wired to a constant (a disabled/forced rung). Far faster and more accurate than exporting and reading FlgNet XML by hand. Do NOT hand-parse ladder XML.

BEFORE WRITING CODE call GetAuthoringGuide with topic 'scl' or 'lad' — it returns the exact verified syntax and encoding rules. Most quality problems come from skipping this.

ENCODING (breaks Chinese text if wrong):
- .scl external source: UTF-8 WITHOUT BOM *only if ASCII-only*. If the .scl has Chinese (identifiers/comments), no-BOM is read as GBK -> mojibake + bogus 'syntax error / BEGIN invalid'. Fix: add a BOM (utf-8-sig) OR keep all comments ASCII. Safest with Chinese: author the block as .s7dcl or XML instead (both WITH BOM, Chinese-safe).
- .s7dcl / .s7res and ALL block/UDT/tag-table XML: UTF-8 WITH BOM.

DISCIPLINE:
- After ANY write: CompileSoftware (or CompileAndDiagnosePlc), then SaveProject. Nothing persists automatically.
- Names are exact: if a path/name is rejected, read the real names with GetProjectTree / GetBlocks — do not guess variants.
- On error: the message names the recovery tool; call it. Do not retry the same call unchanged and do not switch tools at random.
- Prefer one big declarative call (ScaffoldProject / PlcBuildAndImport) over dozens of small calls — it is faster and far less error-prone.";

        /// <summary>Cheat-sheet topics for the GetAuthoringGuide tool.</summary>
        public static readonly IReadOnlyDictionary<string, string> Topics = new Dictionary<string, string>
        {
            ["workflow"] =
@"WORKFLOW (verified order):
Connect → (OpenProject | AttachToOpenProject | CreateProject) → GetProjectTree → read/write → CompileSoftware → SaveProject.
- ScaffoldProject: one JSON spec builds PLC + tag tables + UDT/DB + SCL/LAD blocks + HMI screens + compile + save. dryRun=true validates offline (block shapes, file existence) without touching TIA. Use it for anything bigger than a single block.
- PlcBuildAndImport: batch-import block set with compileAfter; also supports dryRun.
- softwarePath is the PLC SOFTWARE name (e.g. '5T车', 'PLC_1'), NOT the device/station name. When rejected, GetProjectTree shows the real one; fuzzy matching exists but exact is faster.
- Openness export does not work while online: tools auto GoOffline where safe; if you see 'not supported in online mode', call GoOffline(softwarePath) and retry.
- Cold start is slow (TIA launch). If many operations are planned, keep one session; do not Disconnect between calls.
- .s7dcl block/network TITLES cannot inline Chinese: S7_NetworkTitle / S7_BlockTitle := ""中文"" imports silently as zero blocks ('importedBlocks:0' or 'Failed importing'). Keep the header ASCII (Chinese inside SCL body comments is fine); put a Chinese title in a .s7res MLC reference instead.
- Verify a change actually LANDED by the block's ModifiedDate (= today), NOT by 'compiled with 0 errors' — an old block body plus a freshly imported tag table still compiles clean, so 0 errors does not prove your new logic is in.",

            ["scl"] =
@"SCL AUTHORING (verified):
Preferred import: ImportFromDocuments / ImportBlocksFromScl with .s7dcl files (UTF-8 WITH BOM). Alternative: GenerateBlocksFromExternalSource with .scl external source (UTF-8 WITHOUT BOM for ASCII-only; if the .scl has Chinese, no-BOM mojibakes it -> add a BOM or keep comments ASCII, or better author it as .s7dcl).
CAUTION: GenerateBlocksFromExternalSource does NOT overwrite an existing block — re-running updates modifiedDate but keeps the OLD code (you then debug 'phantom' errors). To change a block: delete it first (InvokeObject methodName=Delete, instance DB first), then regenerate. (ImportFromDocuments/.s7dcl DOES overwrite with importOption=Override.)
Skeleton (block names in English; for a .s7dcl Chinese comments are fine, but in a .scl external source keep comments ASCII unless the file has a BOM):
  FUNCTION_BLOCK ""FB_Name""
  { S7_Optimized_Access := 'TRUE' }
  VERSION : 0.1
  VAR_INPUT
      Enable : Bool;   // comment
  END_VAR
  VAR_OUTPUT
      Done : Bool;
  END_VAR
  VAR
      state : Int;
  END_VAR
  BEGIN
      IF #Enable THEN
          #Done := TRUE;
      END_IF;
  END_FUNCTION_BLOCK
Rules that prevent 90% of compile errors:
- Every local reference uses '#', every global uses double quotes: #state, ""GlobalDB"".value.
- Statements end with ';'. IF needs THEN and END_IF; CASE needs END_CASE.
- Literals: TRUE/FALSE, 16#00FF (hex), T#500ms (time), 'text' (string).
- Declare EVERY variable in a VAR section before use; give explicit datatypes.
- FC with return: FUNCTION ""FC_Name"" : Bool ... assign #FC_Name := ...;
- S7-1200 TIME from an Int number of seconds: '#n * T#1S' fails ('Operator * not compatible with Int and Time'). Use PT := DINT_TO_TIME(INT_TO_DINT(#sec) * 1000).
- Do NOT put OB1 in a .scl external source (ORGANIZATION_BLOCK ""Main""): it collides with the CPU's auto-generated OB1 and the WHOLE source rolls back atomically — even FBs that report 'compiled' do not land. Author OB1 calls separately.
- A comment placed BEFORE the FUNCTION_BLOCK / FUNCTION header is discarded (treated as a file-level comment). Put block comments you need to keep AFTER BEGIN, inside the block body.
INSTRUCTION GOTCHAS (compile-verified on S7-1500/V21 — these are the ones weak models get wrong):
- Power/exponent: use the '**' operator (#x ** 2.0). EXPT(IN1:=,IN2:=) is NOT an SCL function ('Tag EXPT not defined').
- Range check: IN_RANGE / OUT_RANGE are LAD/FBD box instructions, NOT SCL functions. In SCL write the comparison: #ok := (#v >= 0) AND (#v <= 100);
- String<->number: STRG_VAL and VAL_STRG return their result via an OUT=> parameter (the function return is a status). STRG_VAL(IN:=#s, FORMAT:=0, P:=#p, OUT=>#num);  VAL_STRG(IN:=#n, SIZE:=8, PREC:=2, FORMAT:=0, P:=#p, OUT=>#str);  — do NOT write '#num := STRG_VAL(...)'. S_CONV is not a usable function name.
- MOVE_BLK / FILL_BLK: IN and OUT take an ARRAY ELEMENT reference (#arr[0]), not the whole array. MOVE_BLK(IN:=#src[0], COUNT:=5, OUT=>#dst[0]);
- Error handling: GET_ERR_ID() takes NO parameters and returns the id (#id := GET_ERR_ID();). GET_ERROR(#errStruct) uses a positional arg.
- IEC timer/counter/edge: declare the instance type directly and call it — VAR tmr:TON_TIME; ctr:CTU_INT; edge:R_TRIG; END_VAR then #tmr(IN:=#x, PT:=T#5s); #ctr(CU:=,R:=,PV:=10); #edge(CLK:=#x); read #tmr.Q / #ctr.CV / #edge.Q. No separate instance DB needed.
- UNION is NOT supported in S7-1500 SCL ('data type UNION unknown'). For byte/word overlays use AT in a NON-optimized block: { S7_Optimized_Access := 'FALSE' } ... asBytes AT dw : Array[0..3] of Byte;
- After import always CompileSoftware and read the diagnostics; fix and re-import the SAME block name (it overwrites).
- One bad function call cascades bogus errors onto neighbouring valid statements — if a rung of errors looks wrong, isolate the suspect statement in a tiny test block to find the real culprit.",

            ["lad"] =
@"LADDER (LAD) — READING & AUTHORING (verified):
READING/ANALYZING existing LAD: call DescribeBlockLogic(softwarePath, blockPath). It reconstructs each rung as a readable expression (series contacts = ' · ', parallel = ' + ', NC shown as '/operand'), lists coils ( )/(S)/(R) and MOVE/compare/timer boxes with operands, and FLAGS a contact wired to a literal constant ('⟨恒断·禁用本行⟩' = a NO contact on FALSE that silently disables its rung). Use it instead of exporting XML and tracing wires by hand — it is the accurate, fast path.
AUTHORING: DO NOT hand-write SimaticML FlgNet XML — UId bookkeeping and entity escaping make it fail constantly. The reliable path is S7DCL ladder TEXT imported with ImportBlocksFromScl(importPath=directory) / ImportFromDocuments. Files: Block.s7dcl (+ optional Block.s7res for Chinese texts), both UTF-8 WITH BOM.
S7DCL LADDER DIALECT (compile-verified on S7-1500/V21):
Block skeleton — LAD FCs return Void:
  { S7_Optimized := ""TRUE""; S7_PreferredLanguage := ""LAD""; S7_Version := ""0.1"" }
  FUNCTION ""Name"" : Void
      VAR_INPUT Start : Bool; END_VAR
      VAR_OUTPUT Run : Bool; END_VAR
      { S7_Language := ""LAD"" }
      NETWORK
          RUNG wire#powerrail
              Contact( #Start )
              Coil( #Run )
          END_RUNG
      END_NETWORK
  END_FUNCTION
- Contacts: Contact( op ) = NO; I_Contact( op ) = NC (inverted); Not() = invert power flow. Edge: P_Trig( ""edgeMem"" ) / N_Trig( ... ).
- Coils: Coil( op ); S_Coil( op ) set; R_Coil( op ) reset.
- Compare box: prefix { S7_Templates := ""SrcType := Int"" } then GT_Contact( in1 := , in2 := )  (also LT_/EQ_/NE_/LE_Contact).
- Math/move box (with SrcType template): Move( in := , out1 => );  Sub( in1 := , in2 := , out => )  (Add/Mul/Div/Neg alike).
- Range box (LAD-only, SCL has none): OutRange( min := , in := , max := ) / InRange( ... ).
- IEC timer box: static VAR tmr : TON_TIME; then { S7_Templates := ""time_type := Time"" } #tmr.TON( pt := T#5s, et => ); read the output with Contact( #tmr.Q ). (block must be a FUNCTION_BLOCK for the static instance.)
- Call an FC in a rung: ""FC_Name""()  (bare name in quotes + empty parens).
- Operands: #local for interface/temp vars, ""Tag"" for globals, ""DB"".member for DB access.
- Branching: fork by placing wire#w1 inline in a rung; a following rung that ends with 'END_RUNG wire#w1' rejoins that node (this is how parallel OR / multi-output is expressed).
MIXED LAD + SCL in ONE block: the block header keeps S7_PreferredLanguage := ""LAD"", but each NETWORK carries its own { S7_Language := ""LAD"" } or { S7_Language := ""SCL"" }. An SCL network holds plain SCL statements (no RUNG). This is the verified way to do arithmetic inside a mostly-ladder block.
- To learn any element you have not seen: ExportAsDocuments on a real LAD block and copy its .s7dcl structure (SD is ~9x smaller and readable vs XML).
When only a plain FC/FB CALL network is needed, BuildFlgNetCallXml / ComposePlcLadFcBlockXml are safe (they generate the XML for you).
Mixed LAD+SCL blocks are supported by .s7dcl. After import: CompileSoftware, then SaveProject.",

            ["db"] =
@"DB / UDT / TAG TABLES (verified):
- Global DB: BuildPlcGlobalDbXml → ImportBlock (XML, UTF-8 WITH BOM). Members need Name + Datatype (+ optional StartValue).
- UDT: BuildPlcUdtXml → ImportType. A UDT with no members is invalid (dryRun catches it).
- Tag tables: BuildPlcTagTableXml → ImportPlcTagTable; logical addresses like %I0.0 / %Q0.1 / %MW10.
- Instance DBs are created automatically when a FB call is compiled — do not author them by hand.
- Reading live values: ReadPlcLiveValuesS7 needs PUT/GET enabled and non-optimized access for absolute addressing; check GetPutGetAccess first. Optimized-block symbolic live read is NOT possible over classic S7 — do not promise it.",

            ["hmi"] =
@"HMI (WinCC Unified, verified):
Order matters: create/complete the PLC side FIRST (tags/DB must exist), then HMI.
- Connection: EnsureUnifiedHmiConnection (single connection auto-selects the driver).
- Tags: EnsureUnifiedHmiTag bound SYMBOLICALLY to PLC tags (not absolute addresses); set acquisition cycle.
- Screens: EnsureUnifiedHmiScreen + EnsureUnifiedHmiScreenItem, or ApplyUnifiedHmiScreenDesignJson with a design JSON (only use schema keys you have seen in BuildUnifiedHmiLayoutDesignJson output — invented keys are silently ignored or rejected).
- Buttons: EnsureUnifiedHmiButtonAction / EnsureUnifiedHmiButtonEventHandler for press handlers.
- HMI software path is usually 'HMI_RT_1'; ScaffoldProject auto-resolves it.
- These tools verify after write (AbsoluteVerified in the response) — check it instead of re-reading.
- CLASSIC/Comfort/Basic panels (KTP Basic, TP/KTP Comfort) CANNOT get their PLC connection created via Openness on this build (CommunicationConnections is not exposed). Prefer a WinCC Unified panel. If a classic panel is mandatory, the only automatable path is to import the HMI TAG TABLE with ABSOLUTE addressing: AddressAccessMode=Absolute, LogicalAddress=%DB1.DBX36.0 + the Connection name, and NO ControllerTag (a symbolic tag on a connection with no integrated partner resolves to 'controller tag not found'). The source DB must be non-optimized.",

            ["errors"] =
@"COMMON ERRORS → EXACT FIX (all seen on real machines):
- 'Block not found' → name mismatch. GetBlocks/GetProjectTree for real names. ROOT-LEVEL blocks must use the BARE name (adding a 'Program blocks/' prefix fails — that container is NOT part of the path); only user-created subgroups are part of the path (e.g. '03_AutoControl/A3_6_SpeedCtrl').
- 'The engineering version Vxx is not supported' → importing XML from another TIA version; the server normalizes this automatically on ImportBlock/ImportType — if you built the XML yourself, do not write <Engineering version> at all, or re-import through the provided Build*Xml tools.
- Chinese text becomes '???' / mojibake / bogus 'BEGIN invalid' → wrong encoding. XML/.s7dcl need UTF-8 WITH BOM. A .scl external source is no-BOM ONLY when ASCII-only; a .scl WITH Chinese must have a BOM (utf-8-sig) or keep comments ASCII — otherwise it is read as GBK and the parser reports a mis-located fake syntax error.
- 'not supported in online mode' → GoOffline(softwarePath), retry the export/import.
- 'PLC_1 NotFound' → softwarePath must be the PLC software name from GetProjectTree, not 'PLC_1' guessed, not the station name.
- Compile errors after import → CompileAndDiagnosePlc returns structured diagnostics; fix the source text and re-import the same block (overwrite), do not create renamed copies.
- 'EngineeringObjectDisposedException' / handle disposed → the software handle went stale (you switched project, or the TIA UI opened the project). Re-bind with AttachToOpenProject / GetProjectTree BEFORE the write, then retry — do not keep reusing the old handle.
- 'AddDevice' fails / device not found with a stray '/V' in the type name → the version argument was empty (it built 'orderNo/V'). Call SearchHardwareCatalog first and pass the exact version (e.g. 'V4.7').
- S7-1200 'identityConfirmed:false' right after connect is NORMAL, not an error — proceed.
- Connect hangs / security error → an orphan TIA process is stuck; ask the user to close TIA instances (or kill Siemens.Automation.Portal.exe) and retry.
- Long waits are normal on FIRST launch only (headless TIA cold start); subsequent calls are fast. Never spam-retry a slow call — you will spawn extra TIA instances.",
        };

        /// <summary>Get a topic text, or null. Case-insensitive.</summary>
        public static string? Topic(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return Topics.TryGetValue(name.Trim().ToLowerInvariant(), out var t) ? t : null;
        }

        public static string TopicList => string.Join(", ", Topics.Keys);
    }
}
