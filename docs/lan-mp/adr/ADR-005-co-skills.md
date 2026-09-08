# ADR-005 — 指挥官主动/被动：开局装载与结果附件

| 字段 | 值 |
|:---|:---|
| ADR 编号 | ADR-005 |
| 标题 | LAN 下 CO skill/PS 装载权威与能量/效果同步边界 |
| 状态 | **ACCEPTED（用户开工授权 2026-09-07；可后补书面副署）** |
| 日期 | 2026-09-07 |
| 决策人 | （待负责人） |
| 相关 | ADR-001 Host 权威；ADR-003 Host RNG/结算；ADR-004 回合游标；M01/M03/M04；[FOW-SKILL-AUDIT.md](../FOW-SKILL-AUDIT.md) |

---

## 背景

联机遭遇战中指挥官目前表现为「仅有肖像、无主动技/被动增益」。

**代码现实（已核对）：**

1. `BattleBootstrap` 构造 `SGS_Player` 时硬编码 `skill = null`、`ps_list = new List<SD_ANNW_PS>()`（`LanMp/.../BattleBootstrap.cs`）。
2. Vanilla `GS_Battle.SetupForSkirmish` **仅当** `SGS_Player.skill` / `ps_list` 非空时才调用 `CO_Data.SetSKill` / `AddPS`（反编译 `GS_Battle`；DLL：`Assembly-CSharp`）。
3. `Player.AddCO` → `CO_Data.Init` **只**绑定 `sd_commander` 并清零能量，**不**装默认 skill/PS。默认装载 API 为 `CO_Data.SetAsDefaultSkillAndPS` / `SetRandomSkillAndPS`（DLL 已确认）。
4. `LobbySeatDto` **仅有** `coId`，无 skill/PS 字段；`BakeForStart` 只解析随机 `coId`。
5. CastSkill：**已有** Guest Intent → Host 施放 → `OnSkillCastDone` → Command + `CaptureBoard`；Guest Apply **attach-only**（`FowAndSkillPatches` / `CommandSyncService`）。`FOW-SKILL-AUDIT.md` 描述的施法环已接线，但被开局空 loadout **架空**。
6. 能量：`UnitData.ReduceHP` → `CO_Data.AddEnergy`。Guest DoAction attach-only **不**走 `ReduceHP`。`PlayerSnapDto` **无** `energy` / EffectHost 字段。
7. **Zero / 自由栏：** 表默认 `skill+pss` 不含 Zero 与第三被动；须从 `UI_CO_SelectResult` 写入 `skillId/psIds`，或 Host `GS_CO.GetActual*` / 解锁池随机戳 DTO。Stamp **不得**覆盖已著作者选技。遭遇战 `PartPS.IsAvailable` 的 `Max(1,level)` 会锁死自由栏（index==2）——LAN 下补丁为 skirmish 解锁三槽。

用户可见症状：Host/Guest 均无被动加成、技能按钮实质不可用（`skill_action == null`）。

## 决策驱动因素

- 不得违反 ADR-001/003：Guest 不重放施法 RNG、不为充能重放 `ReduceHP`。
- 开局两端 `SetupForSkirmish` 必须得到**同一** skill/PS 集合（档案 `GS_CO.GetActualSkill` / `GetActualPS` 可能因本地进度不一致）。
- 已有 CastSkill 管线应复用，禁止另开平行 EndTurn Prefix「补技能」。
- 成功标准（分阶段可测）：见模块 [M07-co-skills.md](../modules/M07-co-skills.md)。

## 选项

### 选项 A — 仅本机 `SetAsDefaultSkillAndPS`（两端各自从表装默认）

开局 `AddCO` 后若 skill 空则调用 `SetAsDefaultSkillAndPS()`；Lobby 仍只传 `coId`。

- 优点：改动最小；默认表数据通常一致。  
- 缺点：不覆盖解锁/自定义/随机 skill·PS；双端表或 MOD 不一致即分叉；与「Host 权威 loadout」弱对齐。

### 选项 B — Host 权威 loadout 进 Lobby / Bake（采纳为主路径）

`LobbySeatDto`（或 Bake 结果）增加 Host 解析后的 `skillId` + `psIds[]`；`BattleBootstrap` 写入 `SGS_Player`；随机 CO/技能由 Host `BakeForStart`（或等价）一次定稿。

- 优点：与 ADR-001 一致；可扩展随机/解锁；Guest 不读本地档案。  
- 缺点：协议与大厅 UI 工作量更大。

### 选项 C — Guest 重放 `proc_CastSkill` / `Hurt` 以驱动能量与效果

- 优点：少扩附件。  
- 缺点：**直接违反 ADR-003**；否决。

### 选项 D — 每步全量 `CO_Data.SaveOb` 快照

- 优点：效果完备。  
- 缺点：包体大、耦合存档格式；可作为 Phase 2 的 **EffectHost 子集** 手段，不宜作开局唯一方案。

## 决定（草案提议，待 ACCEPTED）

**主路径：选项 B（Host 权威 loadout）+ 能量/效果进 ResultAttachment。**

补充规则：

1. **开局（Phase 0）**  
   - Host 在 `BakeForStart`（或 LobbyStart 前单点）为每个存在座位写入权威 `skillId` / `psIds`（可先用默认表：`SD_ANNW_CO.skill` + `pss`/`ps1`/`ps2`，或 `SetAsDefaultSkillAndPS` 的等价解析结果戳回 DTO）。  
   - **禁止** Guest 用 `GS_CO.GetActual*` 自行决定 loadout。  
   - `BattleBootstrap` **删除**空 `skill`/`ps_list` 硬编码，改为消费座位字段。  
   - MVP 可落地「默认技」捷径：Host Bake 时若未选手动 skill，则解析默认并写入 DTO（仍走 B，而非两端静默 A）。  
   - **房间选将：** `UI_CO_SelectResult.skill` / `list_ps` 经 `SeatEditRequest.setLoadout` 写入座位；`CoLoadoutResolver.StampSeat` 若已有著作者 loadout 则跳过，避免盖掉 Zero/自由栏。空座位再走表 → `GetActual*` → 解锁池随机。  
   - **遭遇战自由栏 UI：** Harmony 修正 `PartPS.IsAvailable`（skirmish 解锁 index&lt;3）并抬升技/被动选择弹层。

2. **能量（Phase 1）**  
   - 扩展 `PlayerSnapDto`：`coEnergy`（及必要的 `skillUsedTimes` / 展示用 percent）；`CaptureBoard`/`Apply` 读写 `CO_Data`。  
   - Guest **不得**为充能调用 `ReduceHP`/`AddEnergy` 模拟链。

3. **主动技（Phase 2）**  
   - 保持现有 CastSkill Intent→Command + attach-only。  
   - 附件补齐施放后能量清零与 **EffectHost（或等价效果列表）**；召唤物继续依赖现有单位附件 / `TrySpawnMissing`。  
   - Host Accept：仅 `SuppressNetworkEmit`（INV-T9）。

4. **被动中途变更（Phase 3）**  
   - 开局 PS：两端本地 `AddPS`→`ApplyForExisting`（表驱动，与 loadout 一致即可）。  
   - 战斗中脚本加/撤 PS：Host 模拟 + 附件（或专用 Command）；禁止 Guest 本地脚本权威化。

5. **明确否决**  
   - 选项 C；用 `MannualEndTurn` / 额外 Prefix「补技能」修分叉；Guest 乐观 `proc_CastSkill`。

## 后果

- 须新增/修订模块草案 **M07**；回写 M01（座位字段）、M03（Bootstrap）、M04（CastSkill 附件完备）、M05（Hash 是否纳入 energy——建议纳入，Apply 必须可还原）。  
- `FOW-SKILL-AUDIT.md` 验收前提「有可放技能」在 Phase 0 落地前**不成立**；文档须标注缺口。  
- 随机技能 API `SetRandomSkillAndPS`：仅 Host Bake 调用，结果写入 DTO 后再开战。  
- 若推翻本 ADR：须新 ADR；回滚为「无技能联机」或纯选项 A 须显式 SUPERSEDED。

## 审核

**审稿结论：** □ ACCEPTED　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
