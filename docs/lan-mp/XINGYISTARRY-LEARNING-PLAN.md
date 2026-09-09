# 竞品研究与吸收草案 — XingyiStarry.Mp → AnnW.LanMp

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT → 人工确认开工（2026-09-08）** |
| 修订 | 2026-09-08 r3 |
| 竞品 | XingyiStarry.Mp **0.3.8**（`XingyiStarry.Mp.dll` + `XingyiStarry.Mp.Protocol.dll`） |
| 我方 | AnnW.LanMp **0.19.5**（以 `LanMpVersion.Current` 为准） |
| 证据目录 | `E:\XingyiStarry-MP-0.3.8-decompiled\`（本地反编译；**不入库**） |
| 设计基线 | [ADR-001](./adr/ADR-001-host-authority.md) · [ADR-003](./adr/ADR-003-determinism-rng.md) · [ADR-004](./adr/ADR-004-turn-authority.md) |
| 硬约束 | `.cursor/rules/lanmp-architecture.mdc`（INV-T* / INV-VIEW / INV-ACCEPT / SafePump） |
| 人工决议 | 大厅维持现状（A06 Reject）；其余 Adopt/Adapt/Defer/Reject 表同意；战局手测中勿部署覆盖运行中 DLL |

---

## 0. 目标与非目标

### 目标

1. **系统性**读懂对方架构（消息流、权威环、表现、大厅、纠偏），而非只抄表面手感。
2. 把对方优点拆成 **语义契约（WHAT）** 与 **运行时机制（HOW）**，只吸收不破坏我方基线的部分。
3. 产出可排期的优化条目：每条有证据、冲突检查、验收标准、建议挂靠模块。

### 非目标

- 不推翻 ADR-001（Host 权威 Intent→Command）去换成「Guest 完整命令回放」。
- 不引入 Guest `MannualEndTurn` 追赶、不绕开 `CommandApplyQueue` / `AnnWCoroutine.SafePump`。
- 不把对方全量 `Save_General` 每操作同步当成主路径（ADR-003 选项 C 仅作纠偏）。
- 不把本草案当作实现许可；过审后按条目单独立项 / 改模块文档再编码。

### 成功标准（本研究计划本身）

| ID | 标准 |
|:---|:---|
| L1 | 竞品能力矩阵填完（§3），每格有反编译文件引用 |
| L2 | 吸收决策表填完（§4）：Adopt / Adapt / Defer / Reject + 理由 |
| L3 | 至少 1 轮「差距手测」：同场景双方插件各打一局，记录体感差异清单 |
| L4 | 产出 P-Learn 排期（§6）并写入 STATUS；无 APPROVED 条目不开工权威环改动 |

---

## 1. 方法（强制）

### 1.1 语义契约 vs 运行时机制

评估对方任一特性时，先分两列，再决定能否进我方：

| 层 | 问什么 | 例子（对方） | 进我方时 |
|:---|:---|:---|:---|
| **语义契约 WHAT** | 数据形状、策略、不变量、用户可见承诺 | Begin 后 Move 可立刻开播；随机记录带 CallSite+Ordinal | 可移植到 Presentation / 协议字段 / 纯规则 |
| **运行时机制 HOW** | 队列、协程泵、TCP、帧哈希链实现 | `CoroutineObject.StartCoroutine` 直挂；权威帧 SHA256 链 | 只学意图；实现必须服从 INV-T10 / 现有 NetSession |

**禁止句式：**「对方用了 X 机制，所以我们也要换成 X。」  
**正确句式：**「对方承诺了 Y 体验；我们在 ADR 内用什么机制兑现 Y。」

### 1.2 证据纪律（grep receipt）

每个主张必须有：

1. 竞品：反编译路径 + 类型/方法名（例：`InputGate.MaySubmit`）  
2. 我方：现行代码或 ADR/INV 条款（例：`GuestMutationGate` / INV-ACCEPT）  
3. 手测：若主张是「体验更好」，必须有可复现步骤，不能仅靠架构推断  

本地证据（不入库）：

```text
E:\XingyiStarry-MP-0.3.8-extract\          # 发布包
E:\XingyiStarry-MP-0.3.8-decompiled\Mp\    # 主插件反编译
E:\XingyiStarry-MP-0.3.8-decompiled\Protocol\
```

重解命令备忘：

```powershell
ilspycmd "...\XingyiStarry.Mp.dll" -p -o "E:\XingyiStarry-MP-0.3.8-decompiled\Mp"
ilspycmd "...\XingyiStarry.Mp.Protocol.dll" -p -o "E:\XingyiStarry-MP-0.3.8-decompiled\Protocol"
```

### 1.3 冲突检查清单（每条 Adopt/Adapt 必过）

改动提案在写入实现前，逐项勾选：

- [ ] INV-T1/T2：Guest 游标只经 EndTurn Command 字段，无 `MannualEndTurn` 追赶  
- [ ] INV-T3：战局 Apply 仍单队列  
- [ ] INV-T8 / INV-VIEW：`last_human_player` = 本机视角  
- [ ] INV-T9：Host Accept 只用 `SuppressNetworkEmit`  
- [ ] INV-T10：Apply 体经 `SafePump`；不直挂原版 enumerator 进 CoroutineObject  
- [ ] INV-T11：LAN 检查点仍是附件域 EndTurn hash，不把 Apply 后 recapture 当 Strict 停手条件  
- [ ] INV-T12：Guest 最多一个 in-flight Intent  
- [ ] INV-ACCEPT：几何/FOW 合法性仍仅 Host Accept  
- [ ] ADR-001：Intent ≠ 完整可执行结果；附件仍是 ADR-001 真理  
- [ ] ADR-003：不以「全端再掷骰」为主同步；RandomTape 若引入则是 **Host 已抽结果的窄带重放**，不是锁步 seed  

任一冲突 → **Reject** 或降级为 **Adapt（仅表现层）**。

---

## 2. 架构对照（基线共识）

### 2.1 一句话

| | XingyiStarry.Mp | AnnW.LanMp |
|:---|:---|:---|
| 模型 | Host 执行 + 广播 `GameCommand` + `RandomTape` → Guest 回放 | Host Accept Intent → 广播 Command（+ attachment）→ Guest Apply |
| 定序 | `OperationQueue` + AuthorityFrame 链 | `CommandApplyQueue` |
| 大厅 | 嵌入原版遭遇战 UI | 独立联机大厅 |
| 纠偏 | 帧哈希链 + `Save_General` 分片快照追帧 | EndTurn 附件 hash + StateSnapshot 修复 |

两边都是 Host 权威；**不是**「对方锁步、我方 Host」。差异在 **Guest 载荷形态、表现开播时机、大厅壳、RNG 覆盖策略**。

### 2.2 对方消息 / 命令枚举（精确）

**MessageType：** Hello, Welcome, Reject, Heartbeat, RoomState, ClaimSeat, SetReady, StartMatch, CommandRequest, CommandAccepted, CommandRejected, AuthorityFrame, HistoryRequest/Complete, SnapshotRequest/Manifest/Chunk/Complete, CatchUpComplete, SessionEnded, LobbyDraftChange, ParticipantNotice  

**CommandKind：** Move, Action, EquipmentAction, EquipmentMoveAction, BuildWithMove, Skill, UndoMove, EndTurn, AutoGuideStart, AutoGuideCancel  

**AuthorityFrameType：** OperationBegin, Resolution, OperationEnd, TurnPhase, SeatChanged, MatchEnded, OperationFailed  

### 2.3 对方核心类型地图（研究入口）

| 域 | 类型 | 读什么 |
|:---|:---|:---|
| 门禁 | `InputGate`, `ExecutionContext`, `ExecutionOrigin` | 捕获 vs 权威执行双态 |
| 会话 | `HostSession`, `ClientSession`, `OperationQueue`, `RoomState` | 授权、队列、席位、断线变 AI |
| 执行 | `GameCommandExecutor`, 各类 `*CapturePatch` | 捕获面、Validate 深度、协程挂载 |
| 随机 | `RandomTape`, `GameplayRandom*`, `ShareExperienceShufflePatch` | 覆盖面与失败语义 |
| 协议 | `ProtocolCodec`, `AuthorityHashChain`, `PacketFraming`, `Snapshot*` | 帧、哈希、快照分片 |
| UI | `NativeSkirmishLobby`, `NativeLobbyPanel`, `MainMenuEntry` | 原版嵌入策略 |
| 表现 | `XingyiStarryMpPlugin` 中 Begin 即时回放 / 装备两段 | 延迟与分段开播 |

我方对照入口：`TurnAuthority`, `CommandApplyQueue`, `AnnWCoroutine`, `GuestMutationGate`, `IntentAcceptLegalityRules`, `StateChecksumService`, `LanRoom*` / `LanLobby*`, `Presentation/*`。

---

## 3. 能力矩阵（研究填写表）

> 状态：`Done` = 证据已摘录；`Gap` = 已知差距待手测；`Unk` = 未核实。  
> r1 已据反编译填初值；**手测列在 L3 前必须补**。

| ID | 能力 | 对方 | 我方 | 差距性质 | 研究状态 |
|:---|:---|:---|:---|:---|:---|
| C01 | 权威模型 | Host 执行+回放 | Intent→Command | 机制不同，目标同类 | Done |
| C02 | 操作定序 | OperationQueue + Begin/Res/End | CommandApplyQueue | 可学生命周期语义 | Done |
| C03 | 输入门禁语义 | `MaySubmit` / `ShouldRunOriginal` | GuestMutationGate 分散 | **语义可收敛** | Done |
| C04 | Move 观战开播 | Begin 即可回放 | 等 Command/附件 | **表现延迟** | Gap |
| C05 | 装备/建造移动 | 两段 prelude+resolution | 待核对现行路径 | **表现分段** | Gap |
| C06 | RNG 同步 | 窄 RandomTape | 附件优先（ADR-003） | 可 **Adapt 窄带** | Done |
| C07 | AI 同步 | 无独立 AI Command；嵌权威上下文 | Host AI→Command | 我方更完整；对方断线 AI 叙事强 | Done |
| C08 | Undo / AutoGuide | 一等 CommandKind | Undo 有；AutoGuide 待核对 | 功能面 | Gap |
| C09 | 大厅壳 | 原版遭遇战嵌入 | 独立大厅 | **产品手感** | Gap |
| C10 | 本机 FOW/视角 | `ApplyLocalPerspectiveAndControl` | INV-VIEW + ApplyLocalViewBinding | 同方向 | Done |
| C11 | 断线 / 重连 | 断线变 AI + 认领 | v1 掉线结束（Out of Scope） | 产品后置 | Done |
| C12 | 校验 / 纠偏 | 帧哈希链 + 全量快照追帧 | 附件 EndTurn hash + 快照 | 互补，勿替换 | Done |
| C13 | 协程泵 | 直挂 CoroutineObject | SafePump（硬要求） | **禁止照搬对方** | Done |
| C14 | Accept 合法性 | Validate 偏浅 | INV-ACCEPT 单门 | **禁止变浅** | Done |
| C15 | Guest 飞行中请求 | 未见单飞行闸 | 单 Intent in-flight | 保持我方 | Done |
| C16 | 内容/插件互斥 | Fingerprint + 拒 annw.lanmp | 版本门 | 可学指纹思路 | Gap |
| C17 | 协议编码 | 二进制 CanonicalWriter | JSON Envelope | 机制；非体验瓶颈 | Done |

---

## 4. 吸收决策表（Adopt / Adapt / Defer / Reject）

| ID | 来源能力 | 决策 | 落地形态（草案） | 挂靠 | 冲突风险 |
|:---|:---|:---|:---|:---|:---|
| A01 | InputGate 双态语义 | **Adapt** | 文档化 + 收敛 GateUtil：`MaySubmit` / `ShouldApplyAuthoritative` 对照表；不改权威模型 | M03 | 低 |
| A02 | Begin 即时 Move 开播 | **Adapt** | Presentation-ahead：远端观战在几何已定且无 RNG 时先播移动动画；棋盘仍以 Command 为准；失败回滚表现 | M04 Presentation | 中（须防乐观写权威） |
| A03 | 装备移动两段 | **Adapt** | 表现层 prelude/resolution；Intent/Command 字段可增 stage，不改为整命令回放 | M04 | 中 |
| A04 | RandomTape（Hurt/Die/Wreck…） | **Adapt（窄）** 或 **Defer** | 仅当附件路径仍出现「动画数字不同步」时，对白名单 CallSite 附带 Host 已抽值；失败→现有附件/快照。**默认仍 ADR-003 附件** | ADR-003 增补 / M04 | 高（范围失控则 Reject） |
| A05 | AuthorityFrame 生命周期 | **Adapt（文档+追踪）** | 内部 sync trace / 日志对齐 Begin·Resolution·End；**不必**改线协议枚举 | M04/M05 | 低 |
| A06 | 原版遭遇战大厅壳 | **Reject（产品）** | **维持独立联机大厅**（用户 2026-09-08 确认）；仅可做独立大厅抛光，不改为嵌入 screen_skirmish | M01 | — |
| A07 | 断线变 AI / 重连 | **Defer** | 等 Host AI Command 全覆盖验收；触及 O7 Out of Scope，需改 00 概况 | 新 ADR | 高 |
| A08 | SHA256 帧哈希链 | **Defer / 可选审计** | 开发期 journal 可参考；不替代 EndTurn 附件 hash | M05 | 低 |
| A09 | `Save_General` 每操作快照 | **Reject（主路径）** | 仅保留/加强现有失败纠偏快照 | M05 | — |
| A10 | Guest 完整 GameCommand 回放 | **Reject** | 削弱 Intent/附件分离与 INV-ACCEPT | — | — |
| A11 | 直挂 CoroutineObject | **Reject** | 违反 INV-T10 | — | — |
| A12 | 浅 Validate 替代 Host 几何门 | **Reject** | 违反 INV-ACCEPT | — | — |
| A13 | 内容指纹握手 | **Adapt** | Hello 带内容/规则指纹，拒不匹配；与现有 PluginVersion 并列 | M02 | 低 |
| A14 | 插件互斥检测 | **Adapt** | 启动检测 XingyiStarry.Mp / 双联机插件，明确报错 | M06 | 低 |

**决策含义：**

- **Adopt**：语义与机制都可近似照搬（在 ADR 内）。  
- **Adapt**：学语义/体验，换用我方机制实现。  
- **Defer**：有价值，但依赖产品/范围/前置验收。  
- **Reject**：与基线冲突或已被证伪。

---

## 5. 研究阶段（先研究，后优化）

### Phase R0 — 证据固化（0.5–1 日）

- [ ] 确认反编译目录完整；关键类型列表与 §2.3 一致  
- [ ] 将本草案链入 [README](./README.md) / [STATUS](./STATUS.md)  
- [ ] 冻结「禁止照搬」列表（§4 Reject）为评审共识  

**出口：** 人工确认 Reject 列表；允许进入 R1。

### Phase R1 — 差距手测（L3，强制）

同机器或 LAN，固定一张图、固定双方席位，各插件打至少一局：

| 场景 | 记录 |
|:---|:---|
| 大厅选图/改席/Ready/开战 | 步骤数、误触、是否像原版 |
| Host 移动 → Guest 观战首帧延迟 | 目测或日志时间戳 |
| 攻击（含伤害飘字/残骸） | 两端数字与动画是否同拍 |
| 装备移动或建造+移动（若地图有） | 是否「先走后结算」 |
| 指挥官技能 | 门控与表现 |
| Undo / 结束回合 / AI 回合 | 卡顿、白屏、假观战 |
| 故意制造分叉或断线 | 恢复策略与体感 |

产出：`docs/lan-mp/XINGYISTARRY-HANDTEST-NOTES.md`（或本文件附录），每条差异标 Cxx / Axx。

**出口：** 手测清单非空且已映射到 §4 决策（可改决策，不可无证据改 Reject→Adopt）。

### Phase R2 — 深度读码（按域）

按优先级读对方 + 我方对照（每域一页笔记：流程、不变量、可移植语义）：

1. **表现开播**（C04/C05/A02/A03）  
2. **门禁双态**（C03/A01）  
3. **RNG 窄带**（C06/A04）— 对照 ADR-003，写「白名单候选 + 不进名单理由」  
4. **大厅壳**（C09/A06）— 对照 M01「禁止克隆 screen_skirmish」是否仍成立  
5. **断线 AI**（C11/A07）— 仅评估，不实现  

**出口：** 每域「可移植语义 ≤ 5 条」；超出则拆单。

### Phase R3 — 优化立项（仅 Adapt/Adopt）

每条立项包含：

1. 用户可见收益  
2. 语义契约（不变式）  
3. 拟改文件 / 模块  
4. §1.3 冲突检查结果  
5. 测试计划（含双端手测）  
6. 是否需要新 ADR 或模块修订  

**出口：** STATUS「实现许可」出现具体条目后才编码。

---

## 6. 建议排期（P-Learn，草案）

在现有 P0–P6 之外的 **学习优化轨**（不替代权威环维护）：

| 序 | 条目 | 决策 | 预估 | 依赖 |
|:---:|:---|:---|:---|:---|
| PL0 | 互斥检测 + 文档链接本草案 | A14 | S | R0 | **完成**（0.19.5） |
| PL1 | Gate 双态表收敛（文档→小重构） | A01 | S | R0 | **完成** |
| PL2 | Move 观战 Presentation-ahead | A02 | M | — | **完成**（Accept+Host-local 提前广播） |
| PL3 | 装备/建造移动两段表现 | A03 | M | — | **Adapt 完成**：无独立 kind；EQ=UnitMoved→DoAction；靠 PL2 移动腿提前 |
| PL4 | Hello 内容指纹 | A13 | S | M02 | **完成** |
| PL5 | ~~大厅手感方案选型~~ | A06 | — | — | **取消**（维持独立大厅） |
| PL6 | 窄 RandomTape 可行性 ADR-003 增补草案 | A04 | M | — | **Defer 文档** → `adr/ADR-003-appendix-random-tape.md` |
| PL7 | 断线变 AI 范围评估（改 Out of Scope？） | A07 | L | AI Command 验收 | **仍 Defer** |

S ≈ 小改；M ≈ 跨数文件；L ≈ 产品/ADR。

---

## 7. 明确不吸收（防漂移）

写成检查句，PR / 实现评审用：

1. 不得以「对方 Guest 也跑原版命令」为由，让 Guest 本地推进权威游标。  
2. 不得删除或绕过 `AnnWCoroutine.SafePump`。  
3. 不得在 Guest 恢复 `GetMoveZone`/`CanDoAction` fail-fast 作为第二扇门。  
4. 不得用操作级全量 `Save_General` 替换附件域 EndTurn 检查点。  
5. 不得为追手感引入第二个 EndTurn / 并行 `CoApply*`。  
6. 学习文档与代码注释不得把对方内部名（AuthorityFrame、RandomTape）当成我方协议承诺，除非正式进 Protocol 并升版本。

---

## 8. 审核关注点

- [x] Reject 列表是否同意（尤其 A10–A12）— **同意**
- [x] A02 Presentation-ahead 是否允许「表现可回滚、权威不可乐观」— **同意**
- [x] A04 RandomTape 默认 Defer 是否足够保守 — **同意**
- [x] A06 大厅 — **维持独立联机大厅，不改为原版壳**
- [x] A07 是否维持 v1 Out of Scope — **同意（Defer）**

**审稿结论：** 通过（2026-09-08 用户确认；大厅按当前走）  
**审稿人 / 日期：** 用户 / 2026-09-08

---

## 9. 附录 A — r1 反编译摘录（grep receipt）

| 主张 | 证据 |
|:---|:---|
| 双态门禁 | `Mp/.../InputGate.cs`：`ShouldRunOriginal` / `MaySubmit` |
| Begin 即时 Move/Undo | `XingyiStarryMpPlugin.CanReplayAtBegin`；Begin 分支 `StartClientReplay(Empty)` |
| 装备两段 | 同文件 `StartClientEquipmentMovePrelude` / `StartClientEquipmentMoveResolution` |
| RandomTape 覆盖 | `GameplayRandomRangePatches`：Hurt/Die/CreateWreck/AutoSetCmdPos |
| 浅 Validate | `GameCommandExecutor.Validate`：Move 无几何门；Action 用 `CanDoAction` |
| 协程挂载 | `GameCommandExecutor.Start` → `GameController.self.StartCoroutine` |
| 本机视角 | `ApplyLocalPerspectiveAndControl`：`last_human_player` + `TriggerFOWDirty` |
| 断线变 AI | `RoomState.MarkDisconnected` → `AiControlled=true` |
| 拒 AnnW | `XingyiStarryMpPlugin.HasOldLanPlugin`（annw.lanmp） |
| 我方 SafePump | `LanMp/.../CommandApplyQueue.cs` / `AnnWCoroutine.cs` |
| 我方 INV-ACCEPT | `IntentAcceptLegalityRules` + ADR-001 补充规则 |

## 附录 B — 术语对照（避免混用）

| 对方 | 我方近似 | 注意 |
|:---|:---|:---|
| CommandRequest | Intent | 对方载荷更接近可执行命令 |
| AuthorityFrame Resolution | resultAttachment / 随机结果 | 对方是随机带；我方是结构化附件 |
| ClientReplay | ApplyingRemoteCommand + Apply | 对方重跑原版 API；我方 Apply+附件 |
| OperationBegin 即时回放 | Presentation-ahead（拟） | 不得写成「Guest 权威执行」 |
| NativeSkirmishLobby | LanRoom / LanLobby | 壳不同，席位语义可对照 |

---

## 变更记录

| 修订 | 日期 | 说明 |
|:---|:---|:---|
| r3 | 2026-09-08 | PL2/PL3-Adapt/PL4/PL6 文档落地；版本 0.19.5；全量烟测 |
| r2 | 2026-09-08 | 人工通过；A06 Reject；开工 PL0/PL1；战局中禁止覆盖运行中插件 |
| r1 | 2026-09-08 | 初稿：方法、矩阵、决策、阶段、排期；基于 0.3.8 反编译 |
