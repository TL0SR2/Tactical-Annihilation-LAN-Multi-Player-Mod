# 01 — 工程规范

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（设计已收敛；待人工副署）** |
| 修订 | 2026-08-29 r2 |
| 适用范围 | 本插件全部文档与后续代码 |

---

## 1. 工作流（强制）

见 [03-REVIEW_PROCESS.md](./03-REVIEW_PROCESS.md)。摘要：无 APPROVED/门禁满足 → 禁止实现；设计冲突先改文档。

**设计基线 ADR**（已 ACCEPTED，人工可副署或 SUPERSEDED）：

- [ADR-001](./adr/ADR-001-host-authority.md) Host 权威  
- [ADR-002](./adr/ADR-002-injection-host.md) BepInEx + Harmony  
- [ADR-003](./adr/ADR-003-determinism-rng.md) Host RNG/结算  

与 ADR 冲突的模块叙述视为文档 bug。

## 2. 文档状态枚举

| 状态 | 可否写实现代码 |
|:---|:---|
| `DRAFT` / `IN-REVIEW` / `NEEDS-REVISION` / `REJECTED` / `SUPERSEDED` | 否 |
| `APPROVED` | 是（仅覆盖范围） |
| ADR：`ACCEPTED` | 设计可依赖；仍不代替模块 APPROVED |

唯有**负责人**可将文档标为 `APPROVED` 或将 ADR 标为 `SUPERSEDED`。Agent 不得自批 APPROVED。

## 3. 仓库与目录约定

| 路径 | 约定 |
|:---|:---|
| **独立 Git 仓库**（推荐）或并列 `TacticalAnnihilation.LanMp/` | 插件源码 + **本 `docs/lan-mp` 权威副本** |
| 游戏安装目录 | 只读分析；可放调试用 `_decomp/`；**勿作为文档唯一存放处** |
| Steam 安装内的 `docs/lan-mp` | 若仍存在，视为工作副本，合并回仓库 |

游戏 DLL 禁止当二进制依赖提交；启动时记录哈希到日志。

`_decomp/`：本地工具输出，默认 gitignore。

## 4. 术语表

| 术语 | 含义 |
|:---|:---|
| Host | 权威端：模拟、AI、RNG、判胜 |
| Guest | 非 Host 的联机端（避免「Client」兼指程序） |
| Peer | Host 或 Guest |
| Slot | `SGS_Player` / `Player` 槽位 |
| LocalHuman / RemoteHuman / AI / Empty | 插件 `SlotBinding.kind` |
| Intent | Guest/Host 本地产生的操作请求（未权威化） |
| Command | Host 校验后、全端 Apply 的指令（可含结果附件） |
| `GS_Battle` / `StartGameSetting` / `DynOb` | 游戏原类型 |
| MatchEnd | Host 广播的胜负结束消息 |
| `UNVERIFIED` | 未用 DLL 核对的断言 |

游戏符号名保持英文原名。

## 5. 与原版交互原则

1. 联机未启用 ≈ 原版。  
2. 挂钩最小化 + 白名单（M06）。  
3. 不宣称官方多人。  
4. 存档：v1 不做联机存档加载（概况 O8）。  
5. 遵循 ADR-001/003：Guest 不跑权威 AI/RNG。  

## 6. 编码规范（实现期）

- C#；目标框架随 BepInEx 模板锁定。  
- 消息必含：`protocolVersion`、`battleId`（开局后）、`seq`；逻辑消息另含 `turn`、`playerIndex`。  
- Intent/Command schema 版本化；破坏性变更走 ADR。  
- 测试优先：门禁拒绝、序列化往返、Hash 稳定（键序规范化）。  
- 日志分级；默认关闭热路径 Debug。  

## 7. 证据规则

声称游戏行为须有类型/方法名或 `UNVERIFIED`。用户向说明另走代码现实核对。

## 8. 安全与信任

- 自有副本模组增强；不传播完整商业反编译树。  
- **信任 LAN + 信任 Host**；不做反作弊。  
- FOW 不超出本机阵营合法视野。  

## 9. 审核关注点

- [ ] 独立仓库约定  
- [ ] 术语 Intent/Command/Guest  
- [ ] ADR 基线确认  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
