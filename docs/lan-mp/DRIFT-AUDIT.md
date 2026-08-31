# 文档 ↔ 代码漂移审计

| 字段 | 值 |
|:---|:---|
| 更新 | 2026-08-30 |
| 插件 | 随 0.13.x 整改；合入前须复核本表 |

判定：`OK` = 文档与代码一致；`DRIFT` = 文档声称有、代码未满足；`WIP` = 本轮整改中。

| ID | 文档断言 | 代码事实（整改前） | 判定 | 处置 |
|:---|:---|:---|:---|:---|
| D01 | M04：UX→Intent→Host Validate+Apply→Command | 无生产路径调用 `SubmitIntent`；`HostAcceptIntent` 只 Broadcast | DRIFT→**WIP关闭中** | 0.13：EndTurn/Undo/Guest DoAction+Move 经 Intent；HostAccept Validate+Apply+Broadcast |
| D02 | Host 校验错回合/错归属/非法行动 | Validate 空 | **WIP** | `IntentValidateRules` + IntentNack |
| D03 | Guest 非本机回合强门禁 | 仅部分 UX Prefix；无单位归属 | **WIP** | SelectUnit/点击/EndTurn + 单位归属 |
| D04 | Guest 本机操作进入权威局 | Guest 本地改局且不发网 | **WIP** | Guest Intent + 乐观预测 + Host Apply |
| D05 | EventBus 仅审计非权威源 | Bus 作 Host 唯一发射源 | **部分** | Host 本机 UX 仍可 Bus 发射；Intent Apply 路径 Suppress 后手发 |
| D06 | 附录必钩 | 缺多项 | **WIP** | ExecuteAction/DoActionInstant/Move/EndTurn/Undo/AI/EndGame 已钩 |
| D07 | M05 MVP | 有代码待正确环验收 | **待验** | 环修好后手测 Hash |
| D08 | 战中断线 MatchAbort | 无 | **WIP** | MatchAbort + AbortMatch |
| D09 | 回房座位刷新 | 残影 | **WIP** | Release 清 guest 名 + Draft 刷新 |
| D10 | STATUS 诚实 | 曾夸大 | **OK** | STATUS r22 分列 |
| D11 | SlotBinding index | 需实测 | **待验** | |
| D12 | Undo 经 Host | 未挂钩 | **WIP** | UndoLastMove → Intent |

## 合入门禁

- 声称「战中可玩」前：D01–D05、D08 不得仍为 DRIFT。
- 声称「MVP」前：D06、D07、D12 关闭或书面降级并改文档。
