# LanMp backlog

## P0 — 回合权威收口（0.15.7，待手测验收）

规格：`docs/lan-mp/adr/ADR-004-turn-authority.md`  
诊断：`BattleSyncTrace` + `Compare-SyncTrace.ps1`

**0.15.7：** Guest Intent 飞行锁 + Host Accept 全程 Suppress（修连射/双广播/双建筑）。  
**0.15.6：** ApplyDrift 不 Strict 锁输入。  
**0.15.4：** CoroutineObject `yield null` 白屏。

验收（2 人 + ≥2 AI）通过前，**非 P0 功能补丁仍冻结**。

- [ ] 全程两边操作者横幅一致  
- [ ] Host 结束回合不白屏；Guest EndTurn(nextPlayer)  
- [ ] Guest 不被校验误锁；不连射；BUILD 不双份  
- [ ] Guest 移动/开火须等 Host Command  
- [ ] 无双结 / 跳过人类  

### 已知债务

- `ResultAttachmentBridge.Apply` 对 CaptureBoard 非恒等（ApplyDrift 日志）

---

## P1 — Guest AutoCmd（P0 验收后）

Guest 回合 AutoCmd → Intent；PlayerAI 仍仅 Host。

---

## BUILD 同步（0.14.4）

已修一轮；复现再跟，优先级低于 P0 验收。
