# ADR-003 附录 — 窄 RandomTape 可行性（Defer）

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT / DEFERRED（P-Learn PL6；默认不做）** |
| 日期 | 2026-09-08 |
| 父 ADR | [ADR-003](./ADR-003-determinism-rng.md) |
| 竞品参考 | XingyiStarry.Mp `RandomTape` + Hurt/Die/Wreck/AutoSetCmdPos transpiler |

## 结论（先写）

**不替代 ADR-003 主策略（Host 结算 + 结果附件）。**  
仅当手测证明「附件已对齐但飘字/残骸动画仍不同拍」时，再对**白名单 CallSite** 评估 Host 已抽随机值的窄带重放。

## 可移植语义（WHAT）

- Host 在权威结算中记录 `(CallSite, Ordinal, Kind, Value)`  
- Guest Apply 同 CallSite 序列消费；多一条/错位 → Abort → 现有快照纠偏  

## 不可照搬（HOW）

- 不以全引擎 Random 劫持为主同步  
- 不直挂 CoroutineObject 跑带 tape 的原版 enumerator（INV-T10）  
- 失败不得静默；必须走附件/M05  

## 白名单候选（若开工）

| CallSite | 理由 |
|:---|:---|
| `UnitData.Hurt` / `Die` | 伤害飘字与死亡表现 |
| `GameTileData.CreateWreck` | 残骸 |
| `Player.AutoSetCmdPos` | 自动落点 |

经验 Shuffle 等另议。

## 开工门槛

1. 双端手测记录「附件 hash 一致但仍不同拍」用例  
2. 本附录升 APPROVED + 模块修订  
3. 覆盖面冻结；超出白名单 → 新审  

**审稿结论：** □ 搁置（默认）　□ 批准白名单实现  
