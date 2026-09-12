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
8. **INV-SOLO 红线：** 安装插件不得改变单机/战役/原版遭遇战语义。Prefix 可常驻，但行为变更必须 `InLanBattle`（或明确 LAN 房间上下文）；嵌套 `TriggerFOWDirty` **整段拦截**、技能 FOW 改写到 owner/cur_player、`energy_max` 覆写、全局遭遇战 CO 选人解锁均属历史泄漏。
9. **INV-VIEW：** LAN 下 `last_human_player` = 本机 FOW/UI 视角，**不是**当前行动的远端人类席；禁止与 FOWDirty 重绑定互殴（会主线程死锁）。**禁止拦截嵌套 `TriggerFOWDirty` 本体**（CO 技能施放会嵌套 FOW）；LAN 仅在最外层 FOWDirty 调用 `ApplyLocalViewBinding`。无 `owner` 的 CO `CanDoAction`/`GetEffectZone` FOW 必须用施法方 `action.player`，不得用本机观战 FOW。
10. Host 权威 EndTurn Accept **只**用 `SuppressNetworkEmit`，不用 `ApplyingRemoteCommand`。  
11. AnnW `CoroutineObject`：**禁止** `yield return null` 等待（同帧忙等）；帧等待用 `0f` / `AnnWCoroutine.NextTick`。**ApplyQueue / Host Accept 边界必须经 `AnnWCoroutine.SafePump`**（展平嵌套 `IEnumerator`、`null`/`0`/`0f`→NextTick），不得把原版 `DoMoveWithAni` 等直接挂进 CoroutineObject（否则 Apply 永久卡住 → Guest 假观战）。`yield return 0`（boxed int，原版移动/攻击 lerp）必须按「让出一帧」处理，不可当成等待 0 秒而同帧跑完（否则 Guest 瞬移）。
12. **双超时策略：** Apply/技能/Intent 等待用**挂起预算**（检测卡死协程/丢 Nack）；Guest RemoteWatch、Host 回合/AI SafePump、Host EndTurn Accept 等 `EndTurnReady` 用**回合跨度**（无墙钟上限，仅战局结束退出）。人类长时间不操作与多 AI 长考是合法静默，不得用 Apply 的 45s/RemoteWatch 600s 误杀；对端死亡靠 Net heartbeat。
13. **`SuppressNetworkEmit` 寿命：** 只覆盖「挡 Bus 双发」的短临界区（Accept 入口 `MannualEndTurn`、Accept 动画 Apply）。**禁止**把 Suppress 拉长到 turn-span 等待；EndTurn Accept 用 `HostEndTurnAcceptWaiting` 声明「下一条 EndTurn 由 Accept 广播」，等待期间 Bus 仍可 emit。无 Guest 时 Bus skip ≠ `broadcast-failed` Abort。
14. **MatchEnd 结算附件：** Host 广播前捕获各席 `PlayerBattleStatics` + `turn_snaps`（`settlementJson`）；双方在 `AllowVanillaEndGameUi`→`EndGame` **之前** stamp。Guest 不跑 Die/NextTurn 记账，本地统计为空则结算全 0 / 回放坏。结算后**退出房间**（双方 Disconnect，禁止 KeepHosting 立刻 `LanRoomPanel.Open`）；Battle 场景只留原版 MissionEnd/LevelSummary。
15. **INV-UX-FALLTHROUGH：** Intent 捕获 Prefix 在 Suppress/Applying 下 `return true` 仅当 Accept/Apply 正在驱动**本方法**；Surrender/RestartLevel 等热座破坏 API **禁止**放行；异命令 Suppress 下玩家 UX 应 toast 拦截；`ShouldBlockUx` 不得在 `SkillCastSuppressEmit` 下观战拦截 Host Accept 施法；离开战局通知不受 Suppress 跳过。

## 后果

- M03/M04 修订；实现模块 `TurnAuthority`、`CommandApplyQueue`  
- 协议 CommandDto 扩展字段；双端须同版本 **0.15.x**  
- 推翻须新 ADR SUPERSEDE  

## 审核

**审稿结论：** ACCEPTED（用户确认计划即授权开工）  
**日期：** 2026-08-31
