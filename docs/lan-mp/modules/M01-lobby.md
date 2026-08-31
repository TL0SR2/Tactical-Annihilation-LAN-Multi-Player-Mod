# M01 — 大厅与开局同步

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（对齐 ADR；座位归属见增补）** |
| 修订 | 2026-08-30 r3 |
| 阶段 | **P2（不开战）** |
| 硬依赖 | M02, M06 |
| 设计基线 | ADR-001；开战条件见 M03 |
| 座位/入房 | **[M01-lobby-seats.md](./M01-lobby-seats.md)**（权威；本文件流程服从增补） |

---

## 1. 目的

Host/Guest 持有同一份遭遇战开局配置并完成 **Ready**。本模块 **不** 调用多端 `LoadScene("ANNW_Battle")`。

## 2. 游戏锚点

- 单机组装：`UI_MENU_LevelSelect_InfoSkm.Proc_StartLevel` → 联机改为写入 LobbyDraft  
- DTO：`StartGameSetting` / `SGS_Player` / `SaveOb`·`LoadOb`  
- 静态开战字段：`SS_ANNW_Game.start_game_setting`（仅 P3 开战流程写入）  

## 3. 职责

| 做 | 不做 |
|:---|:---|
| Draft 同步、槽位与地图身份校验、Ready 状态 | 传输层（M02） |
| 生成 `StartGameSetting` **对象**供开战流程使用 | 输入门禁（M03） |
| 为每 Peer 标注 LocalHuman 槽位意向 | 真正 LoadScene |

## 4. 流程

```
Connect（建房/加入；满员或无人形空位 → LobbyReject，见座位增补）
 → 进入专用联机房间页 LanRoomView（仿遭遇战结构，自建 UI，禁止克隆 screen_skirmish）
 → 创建后：房主 HumanSeated，其余默认纯 AI（此时不可加人）
 → Host 将槽切为人类位 → 立刻 HumanStandby（AI 占位，参数 Host 可调）→ 才可加人
 → 入座人类自改阵营/色/位/CO（Request；颜色强制唯一）；结构/规则/纯 AI 仅 Host
 → Host 广播权威 LobbyDraft；Guest 镜像 + 仅编辑本座
 → 已入座人类 Ready → Host CanStart → M03 开战闸 → LobbyStart（Bake：Random 位/随机 CO；不再「空人类填 AI」）
```

**硬约束：** 复用选图/槽位/开局**语义与数据源**；禁止机械 `Instantiate` 原版遭遇战房间树。  
**座位状态机 / 字段归属 / 入房拒绝：** 以 [M01-lobby-seats.md](./M01-lobby-seats.md) 为准（已拍板：切人类位即 AI 占位，而非开战再填）。

## 5. 接口（逻辑）

```text
Lobby.PublishDraft(dto)                 // 仅 Host
Lobby.RequestSeatEdit(...)              // 入座者偏好；Host 仲裁
Lobby.SetReady(peerId, ready)           // 仅 HumanSeated
Lobby.OnCanStartChanged(bool)
Lobby.BuildStartGameSetting() -> ...    // 含 Bake 后最终座位
LobbyReject / SeatEditNack              // 见座位增补
MapIdentity { id, contentHash }
```

`LobbyStart` 载荷（由 M03 开战触发，经 M02 发送）须含：`battleId`、`battleSeed`（ADR-003）、最终 Draft（Bake 后）。

## 6. 行为要求

1. **禁止**本模块单独 `LoadScene("ANNW_Battle")`。  
2. 地图 `contentHash` 不一致 → 拒绝 Ready。  
3. 默认尊重 demo/解锁限制。  
4. 开局后改槽：**禁止**（须回大厅新 Draft）。  
5. 自定义地图：v1 传全文或 Host 可访问的共享路径；payload 上限由 M02 限额，超限拒绝。  

## 7. 验收

- [ ] 双端 Draft 字段一致且可 Ready  
- [ ] 换图后 Guest 无法 Ready  
- [ ] 全员 Ready 时仍停留在菜单/大厅 UI（无战斗场景）  
- [ ] 座位增补 §11 验收项（Standby / Reject / 自选色位）  

## 8. 审核关注点

- [ ] 「P2 不开战」是否落实  
- [ ] 是否已对照并通过 [M01-lobby-seats.md](./M01-lobby-seats.md)  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
