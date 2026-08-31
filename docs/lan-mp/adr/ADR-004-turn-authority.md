# ADR-004 — 回合游标权威与 Command 串行 Apply

| 字段 | 值 |
|:---|:---|
| ADR 编号 | ADR-004 |
| 标题 | 回合游标仅 Host；EndTurn 携带 nextPlayer；全端串行 ApplyQueue |
| 状态 | **ACCEPTED（实现基线；随 0.15 落地）** |
| 日期 | 2026-08-31 |
| 决策人 | 用户确认系统性重构计划 |

---

## 背景

ADR-001 要求 Host 权威，但实现曾让 Host/Guest **各自**跑 `MannualEndTurn`→`TurnLoop`，用空洞 `EndTurn` 追赶，导致多 AI 下操作者分叉、双结、跳过人类、Host 败停环。属架构背叛，非边角 bug。

## 决策驱动因素

- 体验：全程双方「当前操作者」一致；可完整打完多人+AI 对局
- 成功标准：INV-T1…T7（见工程规则 / BACKLOG P0）
- 禁止「再加 Prefix 顶一下」式补丁

## 选项

### 选项 A — 继续双端 TurnLoop + 空 EndTurn 追赶

- 优点：工程省事  
- 缺点：已证伪；多 AI 必炸  

### 选项 B — TurnAuthority + ApplyQueue + EndTurn(nextPlayer)（采纳）

- 优点：与 ADR-001 一致；Guest 不猜下一手；可测  
- 缺点：改动面大；须统一 RemoteWatch  

## 决定

**选择 B。**

规则：

1. 权威游标（`turns` / `current_co_index` / `cur_player`）**仅 Host** 经 vanilla 结转推进；Guest **只**由 `EndTurn` Command 写入。  
2. `EndTurn` 必含：`endedPlayerIndex`、`turnBefore`、`nextPlayerIndex`、`turnsAfter`、`endTurnReason`、附件。  
3. Host 在 `OnPlayerTurnStarted`（下一手收入/StartTurn 之后）准备 EndTurn 游标字段；板面附件在广播前捕获。  
4. 所有战局 Command 经 **单一 ApplyQueue** 串行。  
5. 非本机席统一 RemoteWatch；Guest **禁止** `MannualEndTurn`/`EndPlayerTurn`/`StartNextPlayerTurn`/`NextTurn` 自行结转。  
6. LAN 下 `last_human_player.defeated` **不得**单独停止 `NextTurn`。  
7. MatchEnd 多席结果；仅 Host 判胜。  
8. **INV-VIEW：** LAN 下 `last_human_player` = 本机 FOW/UI 视角，**不是**当前行动的远端人类席；禁止与 FOWDirty 重绑定互殴（会主线程死锁）。  
9. Host 权威 EndTurn Accept **只**用 `SuppressNetworkEmit`，不用 `ApplyingRemoteCommand`。  
10. AnnW `CoroutineObject`：**禁止** `yield return null` 等待（同帧忙等）；帧等待用 `0f` / `AnnWCoroutine.NextTick`。

## 后果

- M03/M04 修订；实现模块 `TurnAuthority`、`CommandApplyQueue`  
- 协议 CommandDto 扩展字段；双端须同版本 **0.15.x**  
- 推翻须新 ADR SUPERSEDE  

## 审核

**审稿结论：** ACCEPTED（用户确认计划即授权开工）  
**日期：** 2026-08-31
