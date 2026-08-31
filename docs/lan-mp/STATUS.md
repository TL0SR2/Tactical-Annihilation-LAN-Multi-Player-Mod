# 审核看板 STATUS

| 字段 | 值 |
|:---|:---|
| 更新 | 2026-08-31 r24 |
| 说明 | 0.15.2：Host EndTurn 白屏（INV-VIEW / FOW 重入 + Accept 标志语义） |

---

## 文档日志

```text
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
| M02 会话 TCP | **可用** | Host+1 Guest；主线程 Pump |
| M03 开战闸 | **基本可用** | LobbyStart→Bootstrap→进战 |
| M03/M04 回合权威 | **0.15.2 待复测** | TurnAuthority + INV-VIEW；ADR-004 |
| M05 StateHash | **有代码、待在正确环上验收** | |
| 主机迁移 / 重连 | **不做（v1）** | ADR-001 |

## 实现许可

| 模块 | 许可 |
|:---|:---|
| M01-lobby-seats 期 A | ALLOWED |
| ADR-004 回合权威（0.15） | **ALLOWED**（用户确认系统性重构计划） |

## 插件

目标版本 **0.15.2**（EndTurn 白屏修复 + nextPlayer ApplyQueue）

非 P0 功能补丁冻结（见 `LanMp/BACKLOG.md`）。
