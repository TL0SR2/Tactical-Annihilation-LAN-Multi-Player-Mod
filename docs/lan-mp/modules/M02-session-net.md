# M02 — 会话与网络传输

| 字段 | 值 |
|:---|:---|
| 状态 | **DRAFT（对齐 ADR r2）** |
| 修订 | 2026-08-29 r2 |
| 阶段 | P2（及后续承载） |
| 硬依赖 | M06 |
| 设计基线 | ADR-001（角色名）；传输不实现权威逻辑 |

---

## 1. 目的

LAN 上建立 Host/Guest 会话，提供**可靠、有序**的消息通道。不解析战斗语义。

## 2. 职责

| 做 | 不做 |
|:---|:---|
| Host 听端口；Guest **手动 IP:Port** 加入 | v1 LAN 广播发现（P6 可选） |
| Envelope 收发、心跳、断线通知 | 调用 `GS_Battle` |
| protocolVersion 协商 | 加密（v1 明文 LAN） |

## 3. 封套

```text
Envelope {
  protocolVersion: uint16
  msgType: enum
  battleId: string    // 大厅可空；开局后必填
  seq: uint32         // 每发送端单调
  payload: bytes
}
```

未知 `msgType`：**严格模式断开**（v1 默认，避免半同步假活）。  
版本不符：拒绝加入。

## 4. 会话状态机

```
Idle → Hosting | Connecting → InLobby → ReadyWait → Starting → InBattle → Ended
                              ↘ Failed / Disconnected → Ended（v1 不重连）
```

`Starting/InBattle` 仅在 M03 开战流程之后进入。

## 5. 非功能（v1）

| 项 | 值 |
|:---|:---|
| 人数 | 2–4 Peer（可再升，但每端 1 LocalHuman） |
| 心跳 | 2s |
| 超时 | 10s → Ended |
| payload 上限 | 实现时定数；超限拒收并提示 |

## 6. 验收

- [ ] 手动 IP 可加入 InLobby  
- [ ] 断线双方 Ended 并提示  
- [ ] 版本不符无法加入  

## 7. 审核关注点

- [ ] v1 仅手动 IP  
- [ ] 断线不重连  

**审稿结论：** □ 通过　□ 修改后再审　□ 否决  
**审稿人 / 日期：** ________________
