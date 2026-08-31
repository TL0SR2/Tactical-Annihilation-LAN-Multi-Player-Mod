# M05 — 状态哈希与纠偏

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（对齐 ADR r2）** |
| 修订 | 2026-08-29 r2 |
| 阶段 | P5；**MVP 必含** |
| 硬依赖 | M04；ADR-003 |

---

## 1. 目的

在回合边界证明逻辑收敛；失败则按策略暂停或快照覆盖。

## 2. 锚点

`GS_Battle.Save_General` → 规范化子集 → Hash。`TurnSnap` 不作主校验源。

## 3. Hash 集合

**纳入：** 单位逻辑态、玩家资源与归属、turn、current_co_index、defeat_rules、关键 `functions`、地形逻辑块  

**排除：** 相机、UX 选中、音效、`play_time`、纯表现  

键序/列表序必须规范化后再哈希。附录白名单在首次跑通后冻结并过审。

## 4. 时机

每位玩家 `EndPlayerTurn` 权威完成后：Host 算 Hash → 广播 `StateHash` → Guest 比对。

## 5. 失败策略

| 模式 | 行为 | 何时 |
|:---|:---|:---|
| Strict | 暂停 + 错误 UI | **开发期 / MVP 默认** |
| Repair | Host `StateSnapshot` 覆盖 Guest | 试玩/发行可配 |
| LogOnly | 仅日志 | **禁止**用于验收 |

## 6. 验收

- [ ] ≥10 回合一致  
- [ ] 人为改 Guest 资源后 Strict 检出  
- [ ] Repair 后恢复一致  

## 7. 审核关注点

- [ ] MVP 默认 Strict  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
