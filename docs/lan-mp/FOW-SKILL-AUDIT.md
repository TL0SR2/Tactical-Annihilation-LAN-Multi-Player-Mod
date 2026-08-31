# FOW / 指挥官技能审查纪要（0.13.1）

## 战争迷雾

**游戏机制：**
- 显示迷雾：`GetDisplayFOWMap()` → `GetDisplayFraction()` → `GetDisplayPlayer()` → 优先 `last_human_player`
- `CanSeeMovement` / `CanObserve` **直接读** `last_human_player.fraction`（不经 GetDisplayPlayer）
- `StartPlayerTurn`：若当前玩家非 AI，会执行 `last_human_player = player`（联机下 Guest 机会在 Host 回合被写成 Host）
- 阵营染色 `Player.unity_color` 等也依赖 `last_human_player`

**插件对策（已落地）：**
- `ApplyLocalViewBinding` 强制 `last_human_player = LocalHuman`，并校正 `is_player_in_control`
- Harmony：`GetDisplayPlayer`、`GetDisplayFraction`
- 在 `TriggerPlayerTurnStarted` / `TriggerFOWDirty` **Prefix** 重绑（赶在 UI/FOW 监听前）
- PrepareBattle / 回合事件仍调用绑定

**验收：** Guest 在 Host/AI 回合应始终只见本阵营迷雾，不应「开全图」或看到 Host 视野。

## 指挥官技能

**游戏机制：**
- 入口：`UI_SkillBtn` → `SetUXState_Skill` → 点地 → `proc_SkillDoAction` → `CO_Data.proc_CastSkill`
- 捷径：`DoSkillDirectly`
- AI：`PlayerAI` 内 `proc_CastSkill`（仅 Host 应跑）
- 事件：`OnSkillCastStarted` / `OnSkillCastDone`（无载荷）

**插件对策（已落地）：**
- 非己回合：挡 `SetUXState_Skill` / `UI_SkillBtn` / `proc_SkillDoAction` / `DoSkillDirectly`
- Guest 施放：Intent `CastSkill`（**不做乐观预测**）→ Host 校验后本机施放协程 → `OnSkillCastDone` 广播 `CastSkill` + **ResultAttachment**
- Guest 收包：**禁止重放施法**，只 Apply 附件（ADR-003）
- Host 本机施放：同样在 `OnSkillCastDone` 广播附件

**验收：** 仅己方回合可开技能；施放后两端单位/资源一致；Guest 不能本地偷放技能。

## 仍须手测留意

- 技能若生成**全新 unit_id**，附件目前主要校正已有单位 HP/位/资源；极端技能若大量创生单位，可能需加强 CreateUnit 同步（已挡 Guest 本地 CreateUnit）。
- Host 本机复杂 UX 仍可能经 EventBus 发 DoAction（与 Intent 路径并存）；若出现双发日志再收紧。
