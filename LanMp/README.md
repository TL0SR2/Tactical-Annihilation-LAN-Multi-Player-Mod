# AnnW LAN Multiplayer Plugin

遭遇战局域网联机插件源码（设计见 `../docs/lan-mp/`）。

当前版本：**0.18.2**（`Plugin.cs` → `PluginVersion`）。

## 版本管理（Git）

本游戏安装目录已初始化 Git（白名单：仅 `LanMp/`、`docs/lan-mp/`、架构 rule）。  
`dist/`、`logs/`、反编译缓存与游戏本体**不入库**。

远程仓库：https://github.com/TL0SR2/Tactical-Annihilation-LAN-Multi-Player-Mod

```powershell
cd "E:\SteamLibrary\steamapps\common\Tactical Annihilation"
git push -u origin main
git push origin v0.16.12
```

## 分发包

Release zip（含 BepInEx 5.4 + 本插件）解压到游戏根目录即可：

```text
winhttp.dll / doorstop_config.ini / BepInEx/...
```

本地打包：

```powershell
powershell -File "LanMp\tools\Pack-Release.ps1"
```

产出：`LanMp\dist\AnnW.LanMp-<version>-with-BepInEx.zip`
## 依赖

- 游戏目录已安装 **BepInEx 5.4.x**（`win_x64`）
- .NET SDK（构建 `net472`）

## 构建

```powershell
dotnet build "LanMp\src\AnnW.LanMp\AnnW.LanMp.csproj" -c Release
```

输出目录：`BepInEx\plugins\AnnW.LanMp\AnnW.LanMp.dll`

## 使用（MVP 调试）

1. 启动 `AnnW.exe`
2. 主菜单 → **遭遇战** → **多人联机大厅**（插件注入的独立按钮）
3. 主机：**创建房间** → 选图/发布 Draft → 双方 Ready → **开始战斗**
4. 客机：填写 `主机IP:端口` → **加入房间** → Ready

普通「遭遇战 → 新游戏」仍是单机，不会自动打开联机大厅。

Join 连到面板填写的地址（默认 `127.0.0.1:24555`），无公共匹配服。

配置：`BepInEx\config\annw.lanmp.cfg`

## 自动化烟测（不需要双开游戏）

协议层已拆到 `AnnW.LanMp.Protocol`，可用 xUnit 在本机跑：

```powershell
dotnet test "LanMp\AnnW.LanMp.sln" -c Release
```

当前覆盖：

| 测试 | 覆盖 |
|:---|:---|
| `InputGateRulesTests` | M03 门禁纯逻辑 |
| `HashAndWireTests` | 地图哈希、地址解析、帧编解码 |
| `CommandProtocolTests` | Command/StateHash/ResultAttachment/StateSnapshot 往返 + loopback Command/快照投递 |
| `LoopbackLobbySmokeTests` | **同进程 Host+Guest TCP**：Draft → Ready → CanStart → LobbyStart |

```powershell
dotnet test "LanMp\AnnW.LanMp.sln" -c Release
```

建议：每次改协议/大厅/门禁后先跑 `dotnet test`；改游戏挂钩后再做人工冒烟。


Steam 版 **常常无法同机双开**（单实例 / SteamAPI / 存档锁）。这不代表联机代码坏了。

可选绕过（自行承担风险）：

- 两台电脑或一台物理机 + 虚拟机  
- 复制整个游戏目录到另一路径，用非 Steam 方式启动副本（可能仍与 Steam 冲突）  
- 后续可做「Listen 自测」：单进程内 Host+模拟 Guest（未实现）

当前请优先继续功能开发；有第二台机器再做联通冒烟。


| 模块 | 状态 |
|:---|:---|
| M06 宿主/日志/场景探测 | 已实现 |
| M02 TCP 会话 | 已实现（手动 IP） |
| M01 Draft/Ready | 已实现；原版遭遇战 Start→Draft 拦截 |
| M03 开战闸 + EndTurn 门禁补丁 | 已实现 |
| M04 Sync | Command±结果附件；Guest Apply |
| M05 Checksum | 真 Hash + Strict；Hash 失败快照纠偏 |
| UI Overlay | F8 分栏 + Validate/Publish + 活动日志 |
