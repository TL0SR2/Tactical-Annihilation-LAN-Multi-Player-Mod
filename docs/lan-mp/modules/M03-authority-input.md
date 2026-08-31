# M03 — 权威映射、输入门禁与授权开战

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（对齐 ADR r2）** |
| 修订 | 2026-08-29 r2 |
| 阶段 | **P3（唯一授权多端进战）** |
| 硬依赖 | M01, M06；ADR-001 |
| 设计基线 | ADR-001/003 |

---

## 1. 目的

1. 定义 Host / SlotBinding / LocalHuman。  
2. **武装输入门禁**。  
3. **唯有本模块**在全员 Ready 且门禁就绪后，触发多端 `LobbyStart`→`LoadScene("ANNW_Battle")`。  

## 2. 游戏锚点

- `Player.is_ai`、`GS_Battle.is_player_in_control`、`cur_player`  
- `PlayerControl` 无 Remote → 侧车映射  
- UX：`UX_Manager`；结束回合：`GameAPI.TryEndHumanTurn` / `MannualEndTurn`  
- 判胜：`SkirmishLogic` → 仅 Host 有效并广播 `MatchEnd`  

## 3. SlotBinding

```text
SlotBinding {
  pos_ind: int
  ownerPeerId: string | null
  kind: LocalHuman | RemoteHuman | AI | Empty
}
```

| 规则 | 说明 |
|:---|:---|
| AI | 仅 Host 跑 `PlayerAI`；向全端发 Command |
| RemoteHuman | 本机 UX 永不放行；等待网络 Command |
| LocalHuman | 仅当权威回合属于该槽 **且** Host 确认轮到该玩家时可发 Intent（Host 本地可直接 Apply） |
| v1 | **每 Peer 恰好 1×LocalHuman** |

`SetupForSkirmish` 之后：Guest 上 RemoteHuman 不得以 `is_ai=false` 且可控的方式暴露给 UX（门禁层强制）。

## 4. 输入门禁（最低集）

挡住并吞掉（无 Intent）：

- `UX_Manager` 选/移/行动确认  
- `TryEndHumanTurn` / `MannualEndTurn`  
- 附录候选表中其它本机捷径（见 M04 附录）  

非本机回合提示：「非你的回合」。

## 5. 授权开战（P3 核心）

前置：`Lobby.OnCanStartChanged(true)` + 本机门禁模块已注册钩子。

```text
Host: ArmGates() → 广播 LobbyStart{battleId, battleSeed, dto}
All:  Apply dto → start_game_setting → LoadScene("ANNW_Battle")
All:  PrepareBattle 后安装 SlotBinding 与门禁
```

任一端门禁未武装 → **禁止** Host 发 `LobbyStart`。

## 6. FOW / 视角

本机迷雾与信息 UI 绑定 **LocalHuman.fraction**。  
实现前核对是否硬编码 `cur_player`（原 UNVERIFIED → **P3 开工检查项**，列入 STATUS）。

## 7. MatchEnd

Host 上 `SkirmishLogic` / `EndGame` 路径触发 → 广播 `MatchEnd`（**多席结果**：`results[]` / `winnerFraction` / `reason`）→ 各端统一进结算；Guest 抑制独立判胜抢跑。

**回合游标（ADR-004）：** 权威 `turns`/`cur_player` 仅 Host 推进；Guest 只经带 `nextPlayerIndex` 的 `EndTurn` Command 写入。详见 ADR-004。

## 8. 验收

- [ ] 无 M03 武装时无法多端进战  
- [ ] Guest 在 Host 回合无法改逻辑状态  
- [ ] AI 仅 Host 日志出现决策  
- [ ] ADR-004：双端操作者横幅一致（多 AI）  
- [ ] Host 席败北不单独停环（仍有多阵营时）  
- [ ] MatchEnd 双端同时结算  

## 9. 审核关注点

- [ ] 「唯一开战闸门」  
- [ ] 每端 1 LocalHuman  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
