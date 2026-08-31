# 00 — 项目概况

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（设计已收敛；待人工副署）** |
| 修订 | 2026-08-29 r2 |
| 受众 | 插件开发者 / 审核者 |
| 设计基线 | [ADR-001](./adr/ADR-001-host-authority.md) · [ADR-002](./adr/ADR-002-injection-host.md) · [ADR-003](./adr/ADR-003-determinism-rng.md) |

---

## 1. 一句话目标

为 *Tactical Annihilation* 的**遭遇战（Skirmish）**提供**局域网多人联机**：Host 权威模拟，各端各控己方槽位，按回合同步。

## 2. 背景事实（已核实）

- Unity Mono；逻辑在 `Assembly-CSharp.dll`。  
- 权威状态 `GS_Battle`；回合 `GameController`；开局 DTO `StartGameSetting` / `SGS_Player`。  
- `PlayerControl` 仅 Human + AI_*，无远程人类语义 → 插件侧车 `SlotBinding`。  
- 无现成对战网络；Steamworks 不作 v1 传输前提。  
- `DynOb` / `Save_General` 可用于 Hash 与纠偏快照。  

## 3. 范围内（In Scope）— v1 / MVP

| ID | 内容 |
|:---|:---|
| S1 | 遭遇战大厅配置同步（图、槽位、FOW、胜负、QuickStart 等） |
| S2 | 局域网 **连接**（v1：**手动 IP:Port**；发现为可选增强） |
| S3 | 每端 1×LocalHuman；非本机回合输入门禁 |
| S4 | Intent→Host Apply→Command；含 Host AI 与 MatchEnd |
| S5 | 回合 StateHash；失败则暂停或快照（开发期强制检出） |
| S6 | BepInEx + Harmony 宿主（ADR-002） |

## 4. 范围外（Out of Scope）— v1 明确不做

| ID | 内容 |
|:---|:---|
| O1 | 战役 / BattleSpace 联机 |
| O2 | 公网匹配 / NAT 穿透优先 |
| O3 | 反作弊；**默认信任 LAN；Host 可篡改状态** |
| O4 | 改官方服务器；默认改发游戏 DLL |
| O5 | RTS 式实时帧同步 |
| O6 | 未过审模块的顺手实现 |
| O7 | **断线重连 / 中途加入**（掉线即结束会话） |
| O8 | **联机局存档再加载为联机**（单机存档策略另案） |
| O9 | 同机多 LocalHuman 热座 |
| O10 | 观战位、旁路透视超本阵营 FOW |

## 5. 成功标准

1. 两台 LAN 机器：大厅配置一致 → Ready → **门禁武装后**同时进战，各控 1 槽位。  
2. 非行动方客户端无法经 UX/`MannualEndTurn` 改逻辑状态。  
3. AI 仅 Host 结算并同步。  
4. 连续 ≥10 回合 StateHash 一致；人为分叉可检出。  
5. 插件禁用时单机遭遇战与原版一致。  

## 6. 风险与缓解（设计层已关闭项）

| 风险 | 缓解 |
|:---|:---|
| 无门禁进战双端抢操作 | **P2 禁止多端 LoadScene**；开战并入 P3+M03 |
| RNG/协程分叉 | ADR-003：Host 结算；Hash+快照 |
| 输入旁路 | M04 附录突变点白名单 + 持续审计 |
| 协程难钩 | ADR-002：优先同步入口 |
| Steam 目录被清 | 源码/文档独立仓库（D01） |
| 游戏更新 | 启动时 DLL 哈希；挂钩最小化 |

## 7. 术语

见 [01-ENGINEERING_STANDARDS.md](./01-ENGINEERING_STANDARDS.md)。

## 8. 审核关注点

- [ ] In/Out Scope（含 O7–O10）是否同意  
- [ ] 信任 Host 的 LAN 假设是否接受  
- [ ] 成功标准是否可测  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
