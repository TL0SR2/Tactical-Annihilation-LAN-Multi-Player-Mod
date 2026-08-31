# M04 附录 — 改状态路径候选表

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT** |
| 修订 | 2026-08-29 r1 |
| 用途 | M03 门禁 / M04 挂钩审计；非完整证明 |

状态列：`候选` = 已知出口，实现前 dnSpy 确认；`必钩` = MVP 必须纳入；`延后` = v1 可吞掉或禁用。

| 出口 | 类型 | 建议 | 备注 |
|:---|:---|:---|:---|
| `GameController.ExecuteAction` | 行动 | 必钩 | 核心 Apply |
| `UnitData.DoAction` | 行动 | 候选 | 与上者关系需确认，避免双钩 |
| `GameAPI.DoActionInstant` | 行动 | 必钩 | 脚本/工具捷径 |
| `GameAPI.MannualEndTurn` | 回合 | 必钩 | |
| `GameAPI.TryEndHumanTurn` | 回合 | 必钩 | 门禁优先 |
| `GameAPI.MoveUnitInstantly` | 移动 | 必钩 | |
| `GameAPI.CreateUnit` / `RemoveUnit` | 单位 | 必钩 | 稳定 ID 分配点 |
| `GameAPI.ConvertUnitOwnerShip` | 单位 | 候选 | |
| `UX_Manager` 确认移动/行动 | UX | 必钩 | 改发 Intent |
| `UX_Manager.proc_SkillDoAction` / `DoSkillDirectly` | 指挥官技能 | 必钩 | Guest→Intent；Host 施放后 Command+附件；Guest 只套附件 |
| `UI_SkillBtn.OnClick` | 技能 UI | 必钩 | 非己回合门禁 |
| `GS_Battle.last_human_player` / `GetDisplay*` | FOW/视角 | 必钩 | 每回合强制 LocalHuman；见 FOW 审查 |
| `UndoMoveData` / Undo UI | 撤销 | 必钩 | **不禁用**；须 Host 同步 |
| `PlayerAI.OnStartTurn_DoTurn` | AI | 必钩 | 仅 Host 执行 |
| `SkirmishLogic` / `EndGame` | 胜负 | 必钩 | Host→MatchEnd |
| `BattleEventBus.OnActionExecuted` 等 | 观察 | 审计 | 不当唯一权威 |
| `GameAPI.SetPlayerResource` 等 | 调试 | 候选 | 联机局应拒绝或仅 Host |
| 生产/建造/卸载相关 `ActionCate` | 行动 | 候选 | 随 DoAction 覆盖；RNG 则附件 |
| `script_processing` / GameRule 队列 | 脚本 | 候选 | 遭遇战若触发：仅 Host 推进或双端同源脚本 |

**P4 开工前：** 负责人对「必钩」行签字；遗漏导致的分叉优先补表而非改 ADR。

**签字：** ________________ 日期：________
