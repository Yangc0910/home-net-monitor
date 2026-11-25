# 家庭网络监测

Windows 11 x64 本地监测。仪表盘地址为 [127.0.0.1:8765](http://127.0.0.1:8765/)。它记录这台电脑经 Wi‑Fi 访问 Google Mesh 网关、上游本地设备、RCN 参考地址和公共服务的表现，并提供中文时间线、HVAC 标注、CSV 和英文 ISP 报告。

## 发布与启动

依赖 .NET 10 SDK 构建。自包含发布包运行时无需另外安装 SDK。以下命令从项目目录执行。

在另一台 Windows x64 电脑部署时，先克隆仓库。这两条命令可在 PowerShell 中执行。

```powershell
git clone https://github.com/Yangc0910/home-net-monitor.git
Set-Location home-net-monitor
```

```powershell
.\scripts\Publish.ps1
.\artifacts\publish\HomeNetMonitor.exe --root "$env:LOCALAPPDATA\HomeNetMonitor"
```

如果本机使用项目开发时安装的 SDK，发布命令可写为：

```powershell
.\scripts\Publish.ps1 -Dotnet "$env:LOCALAPPDATA\HomeNetSdk\dotnet.exe"
```

正式部署请先关掉前台进程，然后在**管理员 PowerShell** 执行：

```powershell
.\scripts\Service.ps1 -Action Install
```

服务由 Windows 开机启动，以 LocalService 运行。启动后检查：

```powershell
Get-Service HomeNetMonitor
Invoke-RestMethod http://127.0.0.1:8765/api/status
```

安装建立 `%ProgramData%\HomeNetMonitor\config.json` 和 `data\monitor.db`。配置更改后用管理员 PowerShell 执行 `Restart-Service HomeNetMonitor`。前台模式可用 `--write-default-config <文件>` 生成配置模板，用 `--config <文件>` 指定另一份配置。监听地址固定为 IPv4 回环，端口可改。已有前台数据独立保留于其指定的根目录。

## 使用与默认值

- 默认网关取自配置的 Wi‑Fi 网卡。ICMP 间隔 5 秒，TCP、DNS、HTTPS 和 Wi‑Fi 状态间隔 15 秒。配置项包括 `adapter`、`pingSeconds`、`checkSeconds`、`endpoints`、`rawDays` 和 `historyDays`。原始样本默认保留 30 天，分钟汇总和事件默认保留 365 天。接口的时间范围使用 Unix 毫秒，截止时间不计入范围。
- `192.168.0.1` 默认标作上游本地设备。确认 modem 身份和地址后，可以配置私有 IPv4 的 `modemStatusUrl`，或者增加 `layer: "modem"` 的 ICMP 目标。状态 URL 记录 HTTP 响应及耗时。DOCSIS 信号、事件日志和设备归属仍需从设备或 ISP 核对。
- HVAC 事件按纽约时间记录。可补记实际发生时间。在夏令时回拨导致同一时刻出现两次时，选择对应 UTC 偏移。相关性表按所选事件列出历史窗口和覆盖率。仪表盘中的纽约日期及 CSV 的 UTC 字段可用于对照 ISP 日志。
- WLAN 接口状态、RSSI 和 Windows WLAN 事件的可用性以实际服务账户为准。快照中的 traceroute 可用于检查路径变化。单点 Wi‑Fi 观测无法总是区分 PC、Mesh、modem 与 ISP，报告保留未经确认的原因。
- 数据库可能保留已回收的页面供后续写入复用。历史保留锁定的是记录年龄。跨原始保留期查看分钟汇总或导出时，显示结果以已完成的分钟为准。

### 按需启用测速

选择一个允许自动测速的 HTTPS LibreSpeed 后端。单独下载并校验官方 `librespeed-cli` Windows x64 发布包，将可信可执行文件放入管理员管理的目录。根据 [发行包](https://github.com/librespeed/speedtest-cli/releases/tag/v1.0.15) 和项目中的 `speed-server.example.json` 创建只包含一个服务端的 JSON 文件。

在 `config.json` 的 `speed` 中设置 `enabled: true`、`executable` 和 `serverFile`。推荐使用绝对路径，并确保 LocalService 有读取权限。默认纽约时间 03:00–04:00 最多一次自动测试。手动测试也要求 WAN 可用且电脑流量已空闲约 30 秒。服务端会接收到测试流量及连接所需的来源 IP。客户端遥测关闭，监测数据库保留测速数值和服务端名称。

## Wi‑Fi 归属与 Nest 供暖

仪表盘按实际连接的 `Gongxifacai`、`Astound` 等划分趋势。两条网络的数据来自同一台电脑的分时观测。切换期间单独记录，早期无 SSID 样本保留为旧记录。时间范围、Wi‑Fi 筛选会应用于探测和故障 CSV。原始样本 CSV 包含每轮使用的网关及 DNS。

Nest 自动记录需要家用 Google 账号、Device Access（一次性 US$5）、启用了 SDM 和 Pub/Sub 的 Google Cloud 项目。使用自己的 Pub/Sub 主题，并按 Google 的说明授予 SDM 发布权限。再创建 Pull 订阅和一个专用服务账号，仅在该订阅上授予 Pub/Sub Subscriber 权限。把 Web OAuth 客户端（授权重定向地址 `https://www.google.com`）和订阅服务账号的 JSON 保存在项目目录之外。Google 提供 [Device Access 注册](https://developers.google.com/nest/device-access/get-started)、[事件订阅](https://developers.google.com/nest/device-access/subscribe-to-events) 和 [授权](https://developers.google.com/nest/device-access/authorize) 步骤。

用管理员 PowerShell 7 执行以下命令，替换项目、订阅和本地凭据文件路径。授权码只在脚本的遮罩提示中输入。

```powershell
.\scripts\Configure-Nest.ps1 -DeviceAccessProject '<Device Access 项目 UUID>' -Subscription 'projects/<Cloud 项目>/subscriptions/<拉取订阅>' -OAuthClientFile 'C:\Secrets\oauth.json' -SubscriberKeyFile 'C:\Secrets\pubsub.json'
Restart-Service HomeNetMonitor
```

脚本写入受 Windows 目录权限保护的服务文件，并启用采集。仪表盘的「Nest 供暖」显示订阅状态、恒温器报告的供暖周期和覆盖缺口。网络采集独立运行。订阅收到事件后，服务先写入 SQLite，再确认消息。Google 已收到的事件可在电脑重连后补收，前提是订阅仍保留这些事件。

既有 Nest 能源历史可在 Google Home 或 Nest App 核对。若用户下载的 Takeout 文件提供明确的启停时间，可用「家庭事件」补记。每天的总供暖时长仅供背景参考。Nest 供暖事件反映恒温器状态，压缩机的实际通电时间需另行观察。

## 维护

```powershell
.\scripts\Service.ps1 -Action Backup -Destination 'C:\Backups\HomeNetMonitor.db'
.\scripts\Service.ps1 -Action Upgrade
.\scripts\Service.ps1 -Action Disable
.\scripts\Service.ps1 -Action Enable
.\scripts\Service.ps1 -Action Uninstall
```

备份目标目录应事先存在。备份包括 SQLite 一致性快照和同名 `.config.json`。恢复时先停服务，把现有数据目录移到独立备份位置，再创建原位置的 `data` 目录，将备份放入并命名为 `monitor.db`，恢复配置，然后按安装脚本的目录 ACL 授予 LocalService 数据目录写权限。启动后验证历史和采集。卸载服务会保留数据及已发布的程序目录。升级前脚本创建备份并保留上个版本。

## 验收

```powershell
dotnet run --project tests/Replay.csproj
```

重启时停在登录屏幕，再登录并核对服务启动及 Wi‑Fi 首条成功样本。若开机未登录时 Wi‑Fi 未接通，记录该时段并用 Windows 的 WLAN 日志核对。首个 24 小时记录 CPU、常驻内存和数据库增长，七天内按实际时间记录 HVAC 启停。之后从仪表盘选取最近七天生成英文报告。
