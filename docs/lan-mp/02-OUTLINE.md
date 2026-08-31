# 02 — 总大纲

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（设计已按 ADR 收敛；待人工副署）** |
| 修订 | 2026-08-29 r2 |
| 前置 | 先读 ADR-001/002/003；建议先审 00 / 01 / 03 |
| 设计基线 | ADR-001 Host 权威 · ADR-002 BepInEx · ADR-003 Host RNG/结算 |

---

## 1. 阶段总览（已修：禁止「无门禁进战」）

| 阶段 | 名称 | 产出 | 进战？ |
|:---:|:---|:---|:---|
| P0 | 规格基线 | 概况/规范/流程/大纲/模块草案 + ADR | 否 |
| P1 | 宿主 | M06：注入、日志、场景探测 | 否 |
| P2 | 大厅会话 | M02+M01：连接 + 配置同步 + **就绪**；**禁止**多端 `LoadScene` | 否 |
| P3 | 门禁 + 开战 | M03：**输入门禁就绪后**才允许多端进入 `ANNW_Battle` | **是（首次）** |
| P4 | 指令同步 | M04：Intent→Host Apply→Command | 是 |
| P5 | 校验 | M05：回合 Hash + 快照纠偏（开发期强制） | 是 |
| P6 | 打磨 | UX、手动发现可选、文档冻结；**不含**重连（见 Out of Scope） | 是 |

**硬规则：** 不存在「已多端进战但尚未具备 M03 门禁」的合法阶段。P2 的「可玩」指大厅可玩，不是战场可玩。

## 2. MVP（最小可联机切片）

**MVP = ADR-001/002/003 + M06 + M02 + M01 + M03 + M04 + M05（严格 Hash）**

| 纳入 MVP | 不做（v1） |
|:---|:---|
| 手动 IP:Port 加入 | LAN 广播发现（P6 可选） |
| 每端恰好 1 个 LocalHuman | 同机热座多 LocalHuman |
| DoAction + EndTurn + Host AI Command | 完整 ActionCate 结果附件（按分叉再升级） |
| 开发期 StateHash 失败即暂停 | 重连、中途加入、联机存档加载 |
| Host 广播 MatchEnd | 观战位、反作弊 |

M05 不可从 MVP 剔除：无 Hash 无法证明 ADR-003 策略有效。发行版可将失败策略改为快照覆盖（仍属 M05）。

## 3. 模块地图与依赖

```
ADR-001/002/003（设计基线）
        │
        ▼
      M06 注入宿主
        │
        ▼
      M02 会话网络 ──► M01 大厅同步（P2：不开战）
        │                    │
        │                    ▼
        └──────────────► M03 门禁 + 授权开战（P3）
                              │
                              ▼
                         M04 指令同步（P4）
                              │
                              ▼
                         M05 状态校验（P5，MVP 必含）
```

| 模块 | 硬依赖 | 阶段 |
|:---|:---|:---|
| M06 | ADR-002 | P1 |
| M02 | M06；语义遵循 ADR-001 | P2 |
| M01 | M02, M06 | P2 |
| M03 | M01, M06；ADR-001 | P3 |
| M04 | M02, M03；ADR-001/003 | P4 |
| M05 | M04；ADR-003 | P5 |

M02 **不**硬依赖 ADR-001 才能编码传输层，但消息角色命名遵循 Host/Client。

## 4. 与游戏链路对齐（挂钩参考）

1. `UI_MENU_POP_SkirmishSelect` 选图  
2. `UI_MENU_LevelSelect_InfoSkm` 组装配置 → 联机改为写入 Lobby，而非直接开战  
3. **全体 Ready 且 M03 门禁已武装** → Host 发 `LobbyStart`（含 `battleId`、`battleSeed`）→ 各端设 `SS_ANNW_Game.start_game_setting` → `LoadScene("ANNW_Battle")`  
4. `GS_Battle.PrepareBattle` / `SetupForSkirmish`（插件映射 RemoteHuman）  
5. Intent / Command 经 `GameAPI` / `ExecuteAction` / `MannualEndTurn` 等白名单入口  
6. Host：`SkirmishLogic` 判胜 → 广播 `MatchEnd` → 各端 `EndGame`  

## 5. 架构基线（已非 UNVERIFIED）

| 议题 | 决定 | ADR |
|:---|:---|:---|
| 权威 | Host 权威 | ADR-001 |
| 注入 | BepInEx + Harmony；慎钩协程主体 | ADR-002 |
| 确定性 | Host 结算 RNG；Hash+快照兜底 | ADR-003 |
| 同步粒度 | Intent / Command，非鼠标轨迹 | M04 |
| 传输 | 可靠消息；v1 手动 IP | M02 |

## 6. 里程碑验收（L0–L5，避免与模块 ID 混淆）

| 里程碑 | 验收 |
|:---|:---|
| L0 | D00–D03 人工副署；ADR-001/002/003 副署或默认基线确认 |
| L1 | 插件注入 + DLL 哈希日志 + 场景探测 |
| L2 | 双端大厅配置一致并 Ready；**未**进战 |
| L3 | 双端同时进战；非本机回合无法改 `GS_Battle` |
| L4 | Intent/Command 下单位与资源一致；Host AI 可跑 |
| L5 | ≥10 回合 StateHash 一致；人为分叉可检出 |

## 7. 审核关注点

- [ ] 是否同意「P2 不开战」硬规则  
- [ ] MVP 是否同意必须含 M05  
- [ ] ADR 三人基线是否副署  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
