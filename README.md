# JailBreak

JailBreak 是一个基于 WinForms 的托盘小工具，用于在 Windows 上一键切换**全局系统代理**。程序通过修改当前用户的 WinINet 注册表键、刷新系统代理状态并更新托盘图标提示来完成切换。

## 工作原理

**全局代理模式**：默认所有流量都通过代理服务器，只有绕行列表中的地址才会直连。

- ✅ **走代理**：默认所有网站（Google、GitHub、YouTube 等国外网站）
- ⛔ **直连绕过**：仅 `proxy-bypass.txt` 中列出的地址（国内网站、局域网等）

这种模式适合需要访问国外网站的场景，国内网站通过绕行列表直连以提高速度。

## 功能特点

- **托盘快捷操作**: 右键菜单提供启用/关闭全局代理、更新规则、打开配置目录以及退出等动作，双击托盘图标即可快速切换全局代理开关。

- **自定义绕行规则**: 工具自动读取 `proxy-bypass.txt`（直连绕行名单），忽略空行及以 `#` 或 `//` 开头的注释，生成系统所需的绕行字符串。

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

`proxy-bypass.txt`：列出需要**直连（不走代理）**的域名/IP。

**应该添加到绕行列表的地址**：
- 🏠 局域网地址（`localhost`、`127.0.0.1`、`192.168.*`、`10.*`）
- 🇨🇳 国内网站（淘宝、京东、B站、微信等，直连速度更快）
- 🚀 CDN/静态资源（国内 CDN 直连避免代理流量浪费）

**不需要添加的地址**：
- 🌍 国外网站（GitHub、Google、YouTube 等）- 全局模式下默认就走代理

文件格式：
- 支持通配符 `*`（如 `*.baidu.com`、`192.168.*.*`）
- 支持注释行（以 `#` 或 `//` 开头）
- 每行一条规则，程序会自动去重并拼接

示例内容：
```
# 局域网
localhost
127.0.0.1
<local>
*.local
192.168.*
10.*

# 国内网站
*.taobao.com
*.jd.com
*.bilibili.com
*.qq.com
```

运行过程中可使用托盘菜单中的"仅应用最新绕行规则"在不切换开关的情况下热更新列表。

### 4. 启动与操作

- 使用 Visual Studio、`dotnet run` 或运行已发布的可执行文件启动程序；托盘区会出现图标。
- 通过托盘菜单启用或关闭全局代理，双击托盘图标快速切换全局代理开关。
- 菜单中的"打开配置目录"可快速进入程序所在目录以编辑规则文件。

状态指示:
- 红点 + `Proxy: ON (...)`: 全局代理开启。
- 黑点 + `Proxy: OFF`: 代理关闭。

### 5. 技术细节

程序操作当前用户的注册表键 `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings`：

**启用全局代理时**:
- `ProxyEnable` = 1（开启代理）
- `ProxyServer` = 你配置的代理地址（如 `10.0.0.1:38964`）
- `ProxyOverride` = 从 `proxy-bypass.txt` 读取的直连列表（分号分隔）

**关闭代理时**:
- `ProxyEnable` = 0（关闭代理，其他配置保留但不生效）

**流量走向示例**（全局代理开启时）：
```
访问 google.com      → 走代理 ✅（不在 bypass 列表）
访问 github.com      → 走代理 ✅（不在 bypass 列表）
访问 taobao.com      → 直连 ⛔（在 bypass 列表）
访问 192.168.1.1     → 直连 ⛔（在 bypass 列表）
```

## 构建与发布

- 调试构建: 在 Windows 上执行 `dotnet build` 或使用 Visual Studio。
- 生成自包含的 64 位发行包: 运行根目录中的 `publish.bat`(内部执行 `dotnet publish -c Release -r win-x64 --self-contained true`)。
- 发布输出位于 `bin/Release/net6.0-windows/win-x64/publish/`，包含可直接运行的 EXE 及依赖。

## 注意事项

- 修改注册表需要当前用户权限；如被组策略覆盖，设置可能会被还原。

- 部分应用使用自带网络栈或独立的 WinHTTP 配置，可能不会跟随系统代理，需要单独设置。

- 退出程序时会自动释放托盘图标与 GDI 资源，建议通过托盘菜单选择"退出"以确保清理逻辑执行。

## 托盘菜单功能说明

- **开启全局代理**: 启用全局代理，默认所有流量走代理，仅 `proxy-bypass.txt` 中的地址直连。
- **关闭全局代理**: 关闭代理，所有流量直连。
- **切换开/关**: 在开启和关闭之间切换（双击托盘图标也有同样效果）。
- **仅应用最新绕行规则**: 重新读取 `proxy-bypass.txt` 并应用最新的直连列表，不改变代理开关状态（适合修改绕行规则后立即生效）。
- **打开配置目录**: 打开程序所在目录，方便编辑 `proxy-bypass.txt` 等配置文件。
- **退出**: 退出程序并清理资源。
