# 审核看板 STATUS

| 字段 | 值 |
|:---|:---|
| 更新 | 2026-09-07 r26 |
| 说明 | M07/ADR-005 指挥官技能全阶段实现（0.19.0）；待手测 |

---

## 文档日志

```text
2026-09-07 | M07 | 审计修复 r3 | agent | B1–B10 CastSkill 死亡/资源/Suppress/看门狗/FOW/UI
2026-09-07 | M07 | 用户开工→实现 | agent | Phase0–3：loadout+energy+EffectHost；版本 0.19.0
2026-09-07 | ADR-005 + M07 | DRAFT | agent | 指挥官 skill/PS 装载与能量附件；先审后实现
2026-08-31 | 实现 | v0.15.2 | agent | Host EndTurn 白屏：INV-VIEW + Suppress-only Accept
2026-08-31 | ADR-004 | ACCEPTED | 用户 | 回合游标+ApplyQueue；开工 0.15
2026-08-30 | 审计 | r22 | agent | 写入 DRIFT-AUDIT；STATUS 改为诚实分列；启动 0.13 权威环整改
2026-08-30 | 实现 | v0.11.0 | agent | 期A：座位状态机、入房Reject、SeatEdit、Bake
2026-08-30 | M01-lobby-seats | DRAFT→APPROVED | 负责人 | 「那就动手吧」授权期 A 实现
```

---

## 实现状态（诚实）

| 模块 | 状态 | 说明 |
|:---|:---|:---|
| M01 大厅座位期 A | **可用** | Standby/Reject/SeatEdit/Bake |
| M02 会话 TCP | **可用** | Host+多 Guest；主线程 Pump |
| M03 开战闸 | **基本可用** | LobbyStart→Bootstrap→进战 |
| M03/M04 回合权威 | **已发布迭代** | TurnAuthority + INV-VIEW；ADR-004 |
| M05 StateHash | **有代码；Hash 已含 coEnergy** | 待手测 |
| M07 指挥官技能 | **已实现 0.19.0；待手测** | Bootstrap loadout + 能量/EffectHost 附件 |
| 主机迁移 / 重连 | **不做（v1）** | ADR-001 |

## 实现许可

| 模块 | 许可 |
|:---|:---|
| M01-lobby-seats 期 A | ALLOWED |
| ADR-004 回合权威（0.15） | **ALLOWED** |
| M07 / ADR-005 指挥官技能 | **ALLOWED**（用户「开工，全阶段实现」） |

## 插件

目标版本 **0.19.0**（`LanMpVersion.Current`；协议含 skillId/psIds + coEnergy）

非门禁功能补丁仍先走草案（见 `03-REVIEW_PROCESS.md`）。
