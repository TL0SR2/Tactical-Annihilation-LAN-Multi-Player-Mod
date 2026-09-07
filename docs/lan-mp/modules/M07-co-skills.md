# M07 — 指挥官技能与被动（CO Skills）

| 字段 | 值 |
|:---|:---|
| 状态 | **IMPLEMENTED（用户开工授权；文档可后补 ACCEPTED/APPROVED 副署）** |
| 修订 | 2026-09-07 r3 |
| 阶段 | 功能补强；依赖已可用的 M01/M03/M04 |
| 硬依赖 | ADR-001、ADR-003、ADR-005；M01 座位；M03 Bootstrap；M04 CastSkill/附件 |
| 实现版本 | **0.19.0** |

---

## 实现纪要（r2）

| Phase | 落地 |
|:---|:---|
| 0 | `LobbySeatDto.skillId/psIds`；`AfterBakeCoLoadout`→`CoLoadoutResolver`；`BattleBootstrap` 填 `SGS_Player` |
| 1 | `PlayerSnapDto.coEnergy` / `skillUsedTimes`；Capture/Apply；Hash 含 energy |
| 2 | `effectObJson`（player + unit EffectHost）；CastSkill 后 `AfterAttachApply` |
| 3 | GuestMutationGate CastSkill 注释澄清；验收改手测 |

## 审计修复（r3 · 0.19.0）

| ID | 修复 |
|:---|:---|
| B4 | Host CastSkill Suppress 看门狗 45s + IntentNack `skill-timeout` |
| B3 | `OnSkillCastStarted` 起 Suppress，召唤折进 CastSkill Capture |
| B2 | `CastSkill` 资源 Apply=`AllPlayers` |
| B1/B8 | `CoApplyCastSkillAttachOnly`：死亡演出 + `idsBefore` 召唤表现 |
| B7 | `KickSkillCastCue` → `TriggerSkillCastStarted` |
| B6 | CastSkill 后 `RefreshLocalVision` |
| B5 | EffectHost `RemoveAll` 再 `LoadOb` |
| B10 | `UI_SkillBtn` 空 skill 防护 |
| B9 | SeatEdit 换 CO 清 loadout + Host 重戳 |

---

## 1. 目的

使联机遭遇战中指挥官具备与单机遭遇战同等的：

1. 开局主动技 + 被动 PS（单位/玩家增益）；  
2. 战斗中技能能量增长与 UI；  
3. 己方回合施放主动技，且 Guest 经 Intent→Host→Command+附件对齐。

**非目标（本模块 v1）：** 联机改自定义 skill 树 UI 大改；跨 MOD 热更表；反作弊。

## 2. 问题陈述（Intent ≠ Reality）

| 文档/意图 | 代码现实 |
|:---|:---|
| FOW-SKILL-AUDIT：CastSkill 附件对齐两端 | CastSkill 管线存在，但 `skill_action==null`，验收「可放技能」不成立 |
| 大厅可选指挥官 | 只同步 `coId`；Bootstrap 清空 `skill`/`ps_list` |
| ADR-003 Guest attach-only | 正确跳过 `ReduceHP` → Guest 无 `AddEnergy`；附件无 energy |

**主因：** 开局未装载。**次因：** 能量/效果未进附件。

## 3. 游戏锚点（DLL 已核对）

| 符号 | 用途 |
|:---|:---|
| `SGS_Player.sd_co` / `skill` / `ps_list` | 开战设置 |
| `GS_Battle.SetupForSkirmish` | `AddCO`；条件 `SetSKill`/`AddPS` |
| `CO_Data.Init` / `SetSKill` / `AddPS` / `SetAsDefaultSkillAndPS` / `SetRandomSkillAndPS` | 装载 |
| `CO_Data.AddEnergy` / `IsEnergyMax` / `proc_CastSkill` / `AfterSkillCast` | 能量与施放 |
| `UnitData.ReduceHP` | Host 充能触发点之一 |
| `GS_CO.GetActualSkill` / `GetActualPS` | 档案相关；**LAN 禁止 Guest 自决** |
| `UI_SkillBtn` / `UX_Manager.SetUXState_Skill` / `DoSkillDirectly` / `proc_SkillDoAction` | UX（已有门禁补丁） |

## 4. 插件锚点（现状）

| 位置 | 现状 |
|:---|:---|
| `BattleBootstrap` | `skill=null`，`ps_list=[]` — **必须改（Phase 0）** |
| `LobbySeatDto.coId` | 无 skill/PS — **须扩展或 Bake 侧车** |
| `LobbySeatLogic.BakeForStart` | 仅随机空 `coId` |
| `FowAndSkillPatches` | 非己回合挡技能；Guest CastSkill Intent |
| `CommandSyncService` | Host `OnSkillCastDone`；Guest attach-only |
| `PlayerSnapDto` | 无 CO 能量/效果 |
| `GuestMutationGate.Kind.CastSkill` | 枚举存在、施法入口未走此 gate（死路径；可清理） |

## 5. 分阶段范围（实现许可按阶段切开）

### Phase 0 — 开局装载（修主因）

**做：**

1. 协议：`LobbySeatDto` 增加 Host 权威字段（命名待定，建议）：  
   - `skillId: string`（空 = 无主动技）  
   - `psIds: string[]`（空数组 = 无被动；null 仅作 legacy omit 若需兼容旧 Guest——新版本可强制非 null）  
2. Host 在 `BakeForStart`（或 Start 前单次 Resolve）根据 `coId` 解析默认 loadout：  
   - 优先：`SD_ANNW_CO.skill` + `pss`（及 `ps1`/`ps2` 若表仍用）  
   - 随机 CO 已有：先定 `coId` 再解析 skill/PS  
   - 若产品需要「随机技能」：仅 Host 调 `SetRandomSkillAndPS` **等价逻辑**，把结果 ID 写入 DTO（开战前完成）  
3. `BattleBootstrap`：用座位字段填充 `SGS_Player.skill` / `ps_list`（`SDBase.Get`）；删空硬编码。  
4. 两端同一 `LobbyStart` draft → 同一 Setup → 同 `SetSKill`/`AddPS`。

**不做：** 能量附件、EffectHost、大厅技能自选 UI（可后置）。

**验收：**

- [ ] Host/Guest 开战后 `cur_player.co_data.skill` / `skill_action` 非 null（有表技能的 CO）  
- [ ] 单位上可见被动相关效果（与单机同 CO 对照）  
- [ ] 日志无双侧因缺 skill 的 `skill-unavailable` 刷屏（未充能满时按钮仍灰，属正常）

### Phase 1 — 能量进附件

**做：**

- `PlayerSnapDto` 增加 `coEnergy`（float 或与游戏字段同型）；可选 `skillUsedTimes`。  
- `ResultAttachmentBridge.CaptureBoard` / `Apply` 同步 `CO_Data`。  
- DoAction / CastSkill / EndTurn 等已 Capture 的路径自动带上。  
- M05：建议 Hash 纳入 energy（Apply 可还原后再开 Strict）。

**验收：**

- [ ] Host 造成伤害后 Guest 能量环进度一致（允许表现层帧差）  
- [ ] Guest 不调用 `AddEnergy` 作为权威写入（仅 Apply 戳字段）

### Phase 2 — 主动技结果完备

**做：**

- 保持 CastSkill 路径；施放后附件含能量清零 + 效果层。  
- EffectHost：优先评估 vanilla `SaveOb`/`LoadOb` 子集或效果 id 列表（实现前用 Cecil 再确认，标 `UNVERIFIED` 直至核对）。  
- 召唤类技能：单位出现在 `units[]`；沿用 `TrySpawnMissing`。  
- Host 协程失败路径：清理 `SuppressNetworkEmit`，Nack Guest。

**验收：**

- [ ] Host 放技能 → Guest 单位/资源/能量一致  
- [ ] Guest 己回合 Intent 放技能 → Host 执行 → 回执后 Guest 一致  
- [ ] AI（Host）放技能 → Guest 一致  
- [ ] 非己回合/观战：技能 UX 仍被挡

### Phase 3 — 被动中途变更与清理

- 脚本/事件中途 `AddPS`：附件或专用 Command。  
- 删除或真正接入 `GuestMutationGate.CastSkill`。  
- 更新 `FOW-SKILL-AUDIT.md` 验收为「已装载 + 可充能 + 可施放」。

## 6. 数据模型（草案）

```text
LobbySeatDto {
  … existing …
  skillId: string      // Host-authored; "" = none
  psIds: string[]      // Host-authored; empty = none
}

PlayerSnapDto {
  … existing …
  coEnergy: float      // -1 = legacy omit（若需）；新协议建议始终写
  skillUsedTimes: int  // optional
}
```

破坏性：旧 Guest 忽略未知 JSON 字段通常可前进；Bootstrap 依赖新字段时须 **插件版本握手**（已有 `PluginVersionMismatch`）——升版发版时抬 `LanMpVersion`。

## 7. 与既有 ADR

| ADR | 本模块约束 |
|:---|:---|
| ADR-001 | loadout / 施放 / AI 技能仅 Host 权威 |
| ADR-003 | Guest attach-only；不重掷技能 RNG |
| ADR-004 | 不借 EndTurn 修技能；ApplyQueue 串行 |
| ADR-005 | 决策正文 |

## 8. 风险与开放问题（审稿须勾选）

| # | 问题 | 建议默认 |
|:---:|:---|:---|
| Q1 | Phase 0 MVP 是否允许「仅默认表、大厅不选手动 skill」？ | **是**（先可玩） |
| Q2 | 是否同步玩家档案解锁技（`GetActual*`）？ | v1 **否**；只用表默认 / Host 随机结果 |
| Q3 | energy 是否进 EndTurn Hash？ | **是**（Phase 1 末） |
| Q4 | EffectHost 完整 SaveOb 还是白名单效果 id？ | Phase 2 开工前 Cecil 定案 |
| Q5 | `UI_SkillBtn` 在 `skill_action==null` 是否 NRE？ | Phase 0 前后加防护（实现项） |

## 9. 实现门禁

同时满足才可写功能代码：

1. **ADR-005 = ACCEPTED**  
2. **本模块 M07 = APPROVED**（可只批 Phase 0 范围）  
3. `STATUS.md` 对应实现许可 = `ALLOWED`  
4. 负责人确认 Q1–Q3 默认或改写  

Agent **不得**自批 APPROVED / ACCEPTED。

## 10. 审核

**审稿结论：** □ APPROVED（范围：________）　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
