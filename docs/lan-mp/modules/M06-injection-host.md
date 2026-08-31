# M06 — 注入与宿主

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（对齐 ADR-002 r2）** |
| 修订 | 2026-08-29 r2 |
| 阶段 | P1 |
| 硬依赖 | ADR-002 |
| 设计基线 | [ADR-002](../adr/ADR-002-injection-host.md) |

---

## 1. 目的

BepInEx + Harmony 加载插件；生命周期、配置、日志；**不**含战斗同步业务。

## 2. 约束（来自 ADR-002）

- 目标：Unity Mono。  
- 禁止默认改发 `Assembly-CSharp.dll`。  
- **优先钩同步方法**；避免直接改写 `IEnumerator` 方法体；必要时钩调度点或同步包装器。  
- 启动日志：插件版本、`Assembly-CSharp` SHA256。  
- 源码/文档在独立仓库。  

## 3. 职责

| 做 | 不做 |
|:---|:---|
| 入口、配置（启用联机、端口）、模块生命周期 | 大厅/指令业务 |
| 场景探测（菜单 / `ANNW_Battle`） | 未 APPROVED 的业务补丁 |
| 挂钩点注册表（白名单强制） | |

## 4. 挂钩白名单（实施须分模块批准）

| 点 | 模块 |
|:---|:---|
| 遭遇战确认开局 / Ready UI | M01 |
| `LobbyStart` 响应与 `start_game_setting` | M03 |
| `UX_Manager` 输入、`TryEndHumanTurn` | M03 |
| `ExecuteAction` / `MannualEndTurn` / CreateUnit 等 | M04 |
| `EndGame` / 判胜 | M03/M04 |
| `PlayerAI` 入口 | M04（仅 Host） |

未列补丁点禁止合并。

## 5. 验收（P1）

- [ ] 注入成功 + 哈希日志  
- [ ] 场景探测日志  
- [ ] 禁用插件 = 单机冒烟通过  

## 6. 审核关注点

- [ ] 协程挂钩约束  
- [ ] 白名单制  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
