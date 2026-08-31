# M04 — 指令同步（Intent / Command）

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（对齐 ADR r2）** |
| 修订 | 2026-08-29 r2 |
| 阶段 | P4 |
| 硬依赖 | M02, M03；ADR-001/003 |
| 附录 | [M04-appendix-mutation-candidates.md](./M04-appendix-mutation-candidates.md) |

---

## 1. 目的

在 Host 权威下同步一切改变 `GS_Battle` 的玩家/AI 行为：Guest 发 **Intent**，Host **Validate+Apply**，再广播 **Command**（可含结果附件）。

## 2. 游戏锚点

- `GameController.ExecuteAction` / `UnitData.DoAction`  
- `GameAPI.MannualEndTurn` / `TryEndHumanTurn`  
- `BattleEventBus` 审计用  
- `ActionCate` 枚举  

## 3. 主路径（最优默认）

```
本机 UX → M03 门禁 → Intent(peer)
  → Host: Validate → Apply(权威 GS_Battle，含 RNG)
  → Host: Broadcast Command(± resultAttachment)
  → Guests: Apply(Command)  // 不再掷逻辑骰
  → EndTurn 后：M05 StateHash
```

Host 本地玩家：可 Intent 短路为直接 Apply，仍须广播 Command 使 Guest 对齐。

AI：仅 Host 产生 Command 并广播。

## 4. 数据模型

```text
Intent {
  intentId, battleId, turn, playerIndex,
  kind: DoAction | EndTurn | …,
  unitRef, actionCate?, targetPos?, extras?
}

Command {
  cmdId, sourceIntentId?, battleId, turn, playerIndex,
  kind, unitRef, actionCate?, targetPos?, extras?,
  resultAttachment?,
  // ADR-004 EndTurn
  endedPlayerIndex?, turnBefore?, nextPlayerIndex?, turnsAfter?, endTurnReason?
}
```

v1 **最小 kind 集**：`DoAction`、`EndTurn`、（Host）`AiDoAction`、`MatchEnd`（可由 M03 发）。

**EndTurn（ADR-004）：** Host 在下一手 `OnPlayerTurnStarted` 后广播；载荷必含 `nextPlayerIndex`/`turnsAfter`。Guest **禁止**用 `MannualEndTurn` 猜下一手；经 **ApplyQueue** 串行 Apply 附件并写游标。

**Undo：** 联机**不禁用**。撤销须走 Host 权威同步（Intent/Command 或等价 Apply，含必要结果附件）；禁止仅本机 Undo。

## 5. 单位稳定引用

开工前必须用 dnSpy/反编译确认 `UnitData` 是否已有稳定 ID。  

**设计选择（有则用，无则建）：**

1. 优先游戏已有持久 ID 字段；  
2. 否则 Host 在 `CreateUnit` 时分配 `netUnitId` 并写入侧车表，Command 只带 `netUnitId`。  

禁止用易变 list 下标作为跨回合唯一依据。

## 6. Host 校验（最低）

拒绝：错回合、错归属、单位死亡、非法行动、turn 不匹配、门禁失败。  
Guest 以 Host 回执为准；超时重发 Intent（幂等 `intentId`）。

## 7. 与 ADR-003

- 默认先尝试「Guest Apply 同一 Command 入口」；  
- StateHash 系统性失败 → 对该 `ActionCate` 强制 `resultAttachment` 或回合快照，**不改** Host 权威。  

动画：`skipAnimation` 策略允许 Guest 加速表现；不得影响 Hash 集合。

## 8. 验收

- [ ] 跨端移动/攻击后关键状态一致  
- [ ] 非法 Intent 被拒  
- [ ] AI 行动 Guest 可见且一致  
- [ ] 突变点附录已核对并签字  

## 9. 审核关注点

- [ ] 最小 kind 集  
- [ ] Undo 经 Host 同步（不禁用 UI）  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
