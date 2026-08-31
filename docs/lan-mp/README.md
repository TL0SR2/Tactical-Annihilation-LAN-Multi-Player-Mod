# 遭遇战局域网联机插件 — 文档中心

> **状态：DRAFT r2（设计基线已收敛；模块待人工 APPROVED）**  
> 原则：**先打草案 → 人工审核 → 再写代码**。  
> 设计基线：[ADR-001](./adr/ADR-001-host-authority.md) · [ADR-002](./adr/ADR-002-injection-host.md) · [ADR-003](./adr/ADR-003-determinism-rng.md)

本目录存放联机插件规格，**不是**游戏本体文档。目标：*Tactical Annihilation*（Unity Mono / `Assembly-CSharp.dll`）。

## 阅读顺序

| 顺序 | 文件 | 用途 |
|:---:|:---|:---|
| 0 | [adr/](./adr/) | **先读三份 ACCEPTED ADR** |
| 1 | [00-PROJECT_OVERVIEW.md](./00-PROJECT_OVERVIEW.md) | 范围与成功标准 |
| 2 | [01-ENGINEERING_STANDARDS.md](./01-ENGINEERING_STANDARDS.md) | 工程规范与术语 |
| 3 | [03-REVIEW_PROCESS.md](./03-REVIEW_PROCESS.md) | 审核门禁 |
| 4 | [02-OUTLINE.md](./02-OUTLINE.md) | 阶段 / MVP（P2 不开战） |
| 5 | [STATUS.md](./STATUS.md) | 看板 |
| 6 | [SELF-REVIEW.md](./SELF-REVIEW.md) | 问题关闭对照表 |
| 7 | [modules/](./modules/) | 模块草案 |
| 7b | [modules/M01-lobby-seats.md](./modules/M01-lobby-seats.md) | **房间座位归属 / 入房容量（M01 增补，待审）** |

## 硬规则

1. 无模块 APPROVED + 门禁 → 禁止实现。  
2. **多端进战必须经 M03 开战闸**（P2 只做大厅）。  
3. 与 ADR 冲突的文档视为 bug。  
4. 改设计先改文档。

## 证据与工作区

| 路径 | 说明 |
|:---|:---|
| `AnnW_Data/Managed/Assembly-CSharp.dll` | 游戏逻辑程序集（只读分析） |
| `_decomp/` | 本地反编译草稿（工具输出，**非**规格来源的最终权威；规格以 DLL + 本目录 APPROVED 文档为准） |
| `docs/lan-mp/` | 本插件工程文档 |

## 变更约定

- 草案修订：更新文件头 `修订` 行，并在 `STATUS.md` 记一笔。  
- 过审：将文档状态改为 `APPROVED`，记录审核人与日期。  
- 过审后若发现与代码不符：先降为 `NEEDS-REVISION`，修文档后再动实现。
