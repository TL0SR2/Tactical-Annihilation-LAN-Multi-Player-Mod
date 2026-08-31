# 03 — 草案审核流程

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（设计已收敛；待人工副署）** |
| 修订 | 2026-08-29 r2 |

---

## 1. 目的

先对齐设计再实现。设计基线以 ACCEPTED 的 ADR 为准；模块 APPROVED 后才开工。

## 2. 状态机

```
DRAFT → IN-REVIEW → APPROVED → 允许实现（范围内）
              ↘ NEEDS-REVISION →（修改）→ DRAFT/IN-REVIEW
              ↘ REJECTED
```

实现中发现与规格不符：文档降为 `NEEDS-REVISION`，**停写功能代码**。

ADR：`ACCEPTED` → 可被 `SUPERSEDED`（须新 ADR）。

## 3. 审核清单

1. 是否越权进入 Out of Scope  
2. 是否与 ADR-001/002/003 冲突  
3. 可测性  
4. 证据 / `UNVERIFIED`  
5. 硬依赖是否已 APPROVED  
6. 回滚：关掉插件是否回到单机  

## 4. 权限

- 仅负责人：`APPROVED` / ADR `SUPERSEDED`  
- Agent：可改 DRAFT、提议、**不得**自批 APPROVED  

## 5. STATUS 日志格式

```text
YYYY-MM-DD | 文档ID | 旧状态→新状态 | 人 | 备注
```

## 6. 实现开工门禁

同时满足：

1. **D00、D01、D02、D03** 均为 APPROVED（或负责人书面豁免并写 SCOPE）；  
2. **ADR-001、ADR-002、ADR-003** 为 ACCEPTED（或副署确认）；  
3. 目标模块 `Mxx` 为 APPROVED；  
4. 其硬依赖模块均为 APPROVED；  
5. `STATUS.md` 中该模块实现许可为 `ALLOWED`；  
6. 若实现含「多端 LoadScene / 战场输入」： **M03 必须已 APPROVED**（落实「无门禁不开战」）。  

## 7. 审核关注点

- [ ] 门禁含 D02 + 三份 ADR 是否同意  
- [ ] M03 对开战的硬约束是否同意  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
