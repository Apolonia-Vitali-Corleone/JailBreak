# JailBreak

JailBreak 是一个基于 WinForms 的托盘小工具，用于在 Windows 上一键切换全局系统代理。程序通过修改当前用户的 WinINet 注册表键、刷新系统代理状态并更新托盘图标提示来完成切换。

## 功能特点

- **托盘快捷操作**: 右键菜单提供启用/关闭全局代理、更新规则、打开配置目录以及退出等动作，双击托盘图标即可快速切换全局代理开关。

- **自定义绕行规则**: 工具自动读取 `proxy-bypass.txt`(全局代理绕行名单)，忽略空行及以 `#` 或 `//` 开头的注释，生成系统所需的绕行字符串。

- **托盘状态指示**: 根据当前注册表状态显示红/黑圆点图标及提示文字，区分"Proxy: ON"与"Proxy: OFF"。

- **可靠应用设置**: 每次写入注册表后都会调用 `InternetSetOption` 触发系统刷新，确保依赖 WinINet 的程序即时生效。

## 目录结构

```
JailBreak/
├─ Program.cs          // 主程序与托盘逻辑
├─ JailBreak.csproj    // 项目文件(.NET 6 WinForms)
├─ proxy-bypass.txt    // 全局代理绕行名单
├─ proxy.ico           // 托盘图标资源
└─ publish.bat         // 发布脚本
```

## 快速上手

### 1. 准备环境

- Windows 10/11(需要能够访问系统托盘和注册表)。
- .NET 6 SDK 或运行库，项目目标框架为 `net6.0-windows` 且启用了 Windows Forms。

### 2. 配置上游代理

在 `Program.cs` 中修改 `ProxyServer` 常量为实际的代理地址(格式 `host:port` 或 `IP:port`)。默认值仅用于示例，正式使用前请替换。

```csharp
private const string ProxyServer = "10.0.0.1:38964";  // 修改为你的代理地址
```

### 3. 维护绕行规则文件

`proxy-bypass.txt`: 列出需要直连的域名/IP。支持通配 `*`，按行维护，程序会自动去重并拼接为分号分隔字符串。

示例内容:
```
localhost
127.0.0.1
<local>
*.local
192.168.*
10.*
```

运行过程中可使用托盘菜单中的"仅应用最新绕行规则"在不切换开关的情况下热更新列表。

### 4. 启动与操作

- 使用 Visual Studio、`dotnet run` 或运行已发布的可执行文件启动程序；托盘区会出现图标。
- 通过托盘菜单启用或关闭全局代理，双击托盘图标快速切换全局代理开关。
- 菜单中的"打开配置目录"可快速进入程序所在目录以编辑规则文件。

状态指示:
- 红点 + `Proxy: ON (...)`: 全局代理开启。
- 黑点 + `Proxy: OFF`: 代理关闭。

### 5. 工作原理

程序操作当前用户的以下注册表键: `ProxyEnable`、`ProxyServer`、`ProxyOverride`；根据所选模式开启或关闭对应配置，并通过 WinINet API 通知系统刷新。

启用全局代理时:
- `ProxyEnable` 设为 1
- `ProxyServer` 设为你配置的代理地址
- `ProxyOverride` 设为从 `proxy-bypass.txt` 读取的绕行列表

关闭代理时:
- `ProxyEnable` 设为 0(其他值保留但不生效)

## 构建与发布

- 调试构建: 在 Windows 上执行 `dotnet build` 或使用 Visual Studio。
- 生成自包含的 64 位发行包: 运行根目录中的 `publish.bat`(内部执行 `dotnet publish -c Release -r win-x64 --self-contained true`)。
- 发布输出位于 `bin/Release/net6.0-windows/win-x64/publish/`，包含可直接运行的 EXE 及依赖。

## 注意事项

- 修改注册表需要当前用户权限；如被组策略覆盖，设置可能会被还原。

- 部分应用使用自带网络栈或独立的 WinHTTP 配置，可能不会跟随系统代理，需要单独设置。

- 退出程序时会自动释放托盘图标与 GDI 资源，建议通过托盘菜单选择"退出"以确保清理逻辑执行。

## 托盘菜单功能说明

- **开启全局代理**: 启用全局代理，所有流量通过代理服务器，绕行列表中的地址除外。
- **关闭全局代理**: 关闭代理，所有流量直连。
- **切换开/关**: 在开启和关闭之间切换(双击托盘图标也有同样效果)。
- **仅应用最新绕行规则**: 重新读取 `proxy-bypass.txt` 并应用，不改变代理开关状态。
- **打开配置目录**: 打开程序所在目录，方便编辑配置文件。
- **退出**: 退出程序并清理资源。
