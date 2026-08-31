# M01 增补 — 房间座位归属与入房容量

| 字段 | 值 |
|:---|:---|
| 状态 | **APPROVED** |
| 修订 | 2026-08-30 r4 |
| 隶属 | [M01-lobby.md](./M01-lobby.md) |
| 设计基线 | ADR-001（Host 权威；分字段归属 ≠ 客机写 Draft） |
| 实现许可 | **ALLOWED（期 A）** |

---

## 1. 目的

规定联机房间内：

1. 哪些设置由 **房主** 决定，哪些由 **入座人类** 自己决定；  
2. 槽位状态机（纯 AI / 人类位·暂由 AI / 已入座）；  
3. 颜色与起始位置的互斥与 Host 仲裁；  
4. 入房容量与 **可解析的拒绝结果**。

**不做：** 观战模式；客机整包覆盖 `LobbyDraft`。

---

## 2. 权威模型（与 ADR-001 对齐）

| 规则 | 说明 |
|:---|:---|
| Draft 唯一写者 | **仅 Host** 持有权威 Draft 并广播 |
| 客机改偏好 | 发 `SeatEditRequest` → Host 校验 → 改 Draft → 广播；失败回 `SeatEditNack` |
| 房主改自己偏好 | 可走同一 Request（本地短路）或直写后 Publish；结果仍是 Host Draft |
| 伪写防护 | Host **忽略** 客机发来的整包 `LobbyDraft` |

---

## 3. 槽位状态机

地图指挥官位数 = 槽位总数 `N`。每个槽处于下列之一：

| 状态 | 含义 | 计入「可加入空位」 | 房主可调 AI 参数 | 色/位/CO 归属 |
|:---|:---|:---|:---|:---|
| **Disabled** | 关闭，不参战 | 否 | — | — |
| **Ai** | 纯 AI，不给人坐 | 否 | 是 | 仅 Host |
| **HumanStandby** | 人类位，**当前由 AI 占位** | **是** | 是（占位参数） | 占位阶段仅 Host；入座后见 §5 |
| **HumanSeated** | 人类位，已有 `peerId` | 否（已占用） | 否 | **该玩家**（阵营 / 色 / 位 / CO） |

### 3.1 创建房间后的默认

1. 房主自动占据 **一个人类槽** → `HumanSeated`（房主位）。  
2. **其余全部槽位默认 `Ai`（纯 AI）**。  
3. 此时「可加入空位」= 0 → **不允许新玩家加入**（见 §7）。

### 3.2 房主把槽切成人类位

`Ai`（或允许的其它来源）→ **`HumanStandby`**：

- **立刻**用 AI 占位（难度 / 颜色 / 位置模式与位置 / CO 等均为房主可调的默认值）；  
- **不是**开战时再填 AI；  
- UI 必须区分 **「AI」** 与 **「人类位（暂 AI）」**，避免房主不知道哪几个坑还能加人。

### 3.3 玩家加入

- Host 将新人分到 **编号最小的 `HumanStandby`**（规则固定、可预期）。  
- 该槽 → `HumanSeated`：写入 `peerId` / 显示名；AI 占位消失。  
- 颜色 / 位置 / CO 改归该玩家；入座时可继承占位时的色位 CO 作为初值（减少必点次数），玩家仍可改。

### 3.4 玩家离开

- 该槽回到 **`HumanStandby`**；  
- **建议**恢复该槽在「上次作为占位 AI」时的 Host 模板（难度/色/位/CO），而不是留空行。

### 3.5 人类位改回纯 AI

- 仅当槽为 `HumanStandby`（无真人）时，允许 Host：`HumanStandby` → `Ai`（可加入空位 −1）。  
- 若为 `HumanSeated`：v1 **禁止**直接改掉（或须先具备踢人；踢人 Outside 本增补亦可写「暂不支持，禁止减到挤人」）。

### 3.6 减少人类容量

- 禁止把「人类相关槽」减到少于 **当前已入座真人数**（房主 + 客机）。  
- 已有真人在线时，不得关掉其座位。

---

## 4. AI 占位即默认态（相对旧设想的变更）

| 旧设想 | 本方案（已拍板） |
|:---|:---|
| 空人类槽显示「等待」；开战烘烤再变 AI | **切成人类位当下**即为 `HumanStandby`（AI 占位） |
| 开战逻辑参与「填空」 | 开战 **不再** 做「空人类→AI」；Draft 里已是完整阵容 |

开战 Host **Bake** 仍负责：

- `posMode=Random` → 按 `battleSeed` 分配空闲出生点并写入最终 Draft；  
- 空 `coId` 随机 → Host 抽定具体 CO 再广播；  
- 然后 `LobbyStart`。

---

## 5. 字段归属矩阵

| 字段 | Host（结构 / 纯 AI / Standby 占位） | 入座人类（自己的槽） | 他人槽 |
|:---|:---|:---|:---|
| 地图 / 迷雾 / 胜负 / 开局单位 | ✅ | ❌ | ❌ |
| 槽状态：关 / 纯 AI / 人类位 | ✅ | ❌ | ❌ |
| AI 难度（纯 AI 与 Standby） | ✅ | ❌ | ❌ |
| `team`（阵营） | 纯 AI / Standby：✅；已入座：❌ | ✅（玩家自选） | ❌ |
| `color` | 纯 AI / Standby：✅；已入座：❌ | ✅ | ❌ |
| `posMode` / `pos` | 纯 AI / Standby：✅；已入座：❌ | ✅ | ❌ |
| `coId`（及开战必需附属） | 纯 AI / Standby：✅；已入座：❌ | ✅ | ❌ |
| Ready | 自己 | 自己 | — |
| 开战按钮 | ✅ | ❌ | ❌ |

**颜色唯一（强制）：** 全场已启用槽（纯 AI / Standby / Seated）占用的颜色不可再选；冲突 → `SeatEditNack(ColorTaken)`，以到达 Host 顺序先到先得。不可关闭。

---

## 6. 起始位置

### 6.1 Fixed

1. 请求到达 Host 后校验：目标出生点是否已被其它 **启用且 Fixed** 的槽占用（含纯 AI、`HumanStandby`、`HumanSeated`）。  
2. 空闲 → 写入 Draft 并广播。  
3. 占用 → `SeatEditNack(PosTaken)`；以 **到达 Host 的顺序** 为准（先到先得）。

### 6.2 Random

- 房间阶段：`posMode=Random`，不占用具体出生点表。  
- UI 对他端显示「随机」。  
- **开战 Bake**：Host 对所有 Random 槽洗牌，从剩余空闲点分配，写入最终 Draft 再 `LobbyStart`；禁止各端本地随机。

---

## 7. 入房容量与拒绝

### 7.1 容量

```text
joinableSlots = count(seats where state == HumanStandby)
onlineHumans  = count(HumanSeated)   // 含房主
可接纳新人   ⟺ joinableSlots >= 1
```

创建后默认无 `HumanStandby` → 拒绝加入，与「人类位人数不足」一致。

房主每多开一个人类位（→ `HumanStandby`），可多进一名真人。

### 7.2 握手（必须可解析）

```text
Guest TCP connect
 → Hello { peerId, displayName, protocolVersion }
 → Host:
      协议不匹配     → LobbyReject { code = ProtocolMismatch }
      已开战         → LobbyReject { code = BattleStarted }
      joinableSlots=0 → LobbyReject { code = RoomFull 或 NoHumanSlot,
                                      maxHumans, onlineHumans, joinableSlots }
      通过           → Welcome + 立即 LobbyDraft（含分到的 seatIndex）
 → Guest:
      LobbyReject → 断连 + 大厅明确文案（禁止连接成功却无反馈）
      Welcome     → 进房间页并 Apply Draft
```

**禁止**仅静默 `Close()` TCP 且不发 Reject（相对现状必须改掉）。

建议 `LobbyReject.code` 枚举至少含：`ProtocolMismatch` | `BattleStarted` | `RoomFull` / `NoHumanSlot` | `Generic`。

### 7.3 网络分期（期 A / 期 B）

当前传输层是 **Host ↔ 单一 Guest 的一条 TCP**（第二台机器连上来会被踢掉或占坑，且没有正规 Reject）。  
座位方案在语义上支持「多个 HumanStandby → 多个真人」，但 **多真人同时在线** 需要 Host 同时维护多条连接并按 peer 广播。这两件事拆开做：

| 期 | 做什么 | 故意先不做 |
|:---|:---|:---|
| **A** | 座位状态机、字段归属、Request/Nack、色/位唯一仲裁、Bake、`LobbyReject` 可解析；**传输仍最多 1 名 Guest** | 3+ 真人同时连入 |
| **B** | 多路 Accept、按 peer 广播 Draft/Ready、`joinableSlots` 可同时坐满多名真人 | — |

**期 A 的具体玩法含义：**

1. 房主创建 → 仅自己是人类，其余纯 AI → 外人加入会被 `LobbyReject`（无人形空位）。  
2. 房主开 **1 个** `HumanStandby` → 恰好允许 **1 名** 客机加入并入座；色/阵营/位/CO 按本文自选。  
3. 房主若再开第 2、第 3 个 `HumanStandby`：这些坑在房间里仍是「暂 AI」，**可以开打**（Standby 不挡 Ready），但 **期 A 不会再接入第 2 名真人**——传输层已有 Guest 时，新人 Hello → `LobbyReject(RoomFull)`（或 `GuestSlotTaken`：当前版本仅支持 1 名客机）。  
4. 因此期 A 必须把 Reject 做对：客机看到明确文案，而不是「连上又断 / 无提示」。

**期 A 对 Host UI 的约束（已拍板采用）：**  
允许 Host 开多个 `HumanStandby`（方便先摆好「多人位 + 暂 AI」的阵容再开打），但顶部固定提示：

> 当前联机最多再进入 1 名真人；其余人类位将以 AI 占位参战，直至多人连接版本。

（不强制「最多 1 个 Standby」——避免房主无法预摆 3v3 里「两个人类位暂 AI」的配置。）

**为何先期 A：** 座位归属与入房拒绝是体验正确性的主干；多连接是传输工程量，绑在同一里程碑会拖垮审查与验收。期 A 用 2 人实机即可验完状态机与 Reject；期 B 再验 3+ 人。

---

## 8. Ready / 开战

- 需要 Ready 的：**仅 `HumanSeated`**（真人类）。  
- `HumanStandby` / 纯 AI **不**挡 Ready、**不**挡开战。  
- 结构变更（换图、改槽状态）→ **全员** Ready 清零。  
- 某人改自己的色/位/CO → **仅该人** Ready 取消。  
- `CanStart`：所有 `HumanSeated` 已 Ready + 地图合法 + Fixed 无冲突 +（若开启）颜色唯一。  
- `LobbyStart` 前执行 §4 Bake，再广播最终 Draft。

---

## 9. UI 要点（自建房间页）

- 槽行徽章：`关闭` / `AI` / `人类位·暂 AI` / `人类·显示名`。  
- Host：可切换槽状态、调纯 AI 与 Standby 参数、调规则与地图；**不可**改已入座他人的阵营/色/位/CO。  
- 客机：规则与地图只读；**仅自己的行**可改阵营/色/位/CO。  
- 顶部一句状态：例如「可加入空位 1 · 人类位暂由 AI 占位，加入后替换」；期 A 另附「最多再进 1 名真人」提示。  
- 加入失败：大厅展示 Reject 文案，不进房间。

---

## 10. 协议概念（逻辑）

- 扩展座位 DTO：`state`（或等价）、`peerId`、`posMode`；弱化写死的 `hostSlotIndex` / `guestSlotIndex`，改为「本机槽 = `peerId == me`」。  
- `LobbyReject`、`SeatEditRequest`、`SeatEditNack`。  
- 成功编辑以新 `LobbyDraft` 为准；失败必须 Nack，避免 UI 假成功。

---

## 11. 验收（文档级）

- [ ] 创建房间后第二台机器加入 → 收到可解析 Reject，且 UI 正确提示  
- [ ] Host 将一槽改为人类位 → 该槽显示暂 AI且参数可调 → 客机可加入并占据该槽  
- [ ] 客机可改自己阵营/颜色/位置；不可改地图/他人槽；色与位冲突先到先得  
- [ ] 客机离开 → 槽回 HumanStandby（暂 AI）  
- [ ] 开战 Draft 无需再「空人类填 AI」；Random 位两端一致  

---

## 12. 审核关注点

- [x] §3 状态机与「切人类位即 AI 占位」  
- [x] 入座人类自选 `team`；颜色强制唯一；CO 归入座人类  
- [x] 期 A：单 Guest 传输；可开多个 Standby；第 2 真人 `LobbyReject`；Host 顶部提示  
- [ ] Reject 文案与 code 细表（实现时定枚举值即可）  

**审稿结论：** ☑ 通过  
**审稿人 / 日期：** 负责人 / 2026-08-30（「那就动手吧」）
