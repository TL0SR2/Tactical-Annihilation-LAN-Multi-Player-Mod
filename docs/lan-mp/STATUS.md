# 审核看板 STATUS

| 字段 | 值 |
|:---|:---|
| 更新 | 2026-09-08 r29 |
| 说明 | P-Learn PL0–PL4 落地（0.19.5）；PL6 附录 Defer；待用户手测 |

---

## 文档日志

```text
2026-09-08 | P-Learn | PL2–PL4+PL6文档 | agent | Move ahead / EQ-Adapt / content FP / RandomTape appendix；0.19.5
2026-09-08 | P-Learn | 开工 PL0/PL1 | agent+用户 | A06 大厅维持；制品输出 LanMp/artifacts；禁覆盖战局 DLL
2026-09-08 | 竞品学习 | DRAFT r1 | agent | XINGYISTARRY-LEARNING-PLAN：矩阵/决策/R0–R3/P-Learn；禁改权威环
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
| 竞品吸收（XingyiStarry） | **PL0–PL4 已编码 0.19.5** | PL5 取消；PL6 附录；PL7 Defer；待手测 |
| 主机迁移 / 重连 | **不做（v1）** | ADR-001 |

## 实现许可

| 模块 | 许可 |
|:---|:---|
| M01-lobby-seats 期 A | ALLOWED |
| ADR-004 回合权威（0.15） | **ALLOWED** |
| M07 / ADR-005 指挥官技能 | **ALLOWED**（用户「开工，全阶段实现」） |
| P-Learn PL0–PL4 | **ALLOWED / 已实现**（2026-09-08） |
| P-Learn PL5 大厅壳 | **不做**（维持独立大厅） |
| P-Learn PL6–PL7 | Defer（PL6 仅附录草案） |

## 插件

目标版本 **0.19.5**（`LanMpVersion.Current`；Hello 含 contentFingerprint）

非门禁功能补丁仍先走草案（见 `03-REVIEW_PROCESS.md`）。
