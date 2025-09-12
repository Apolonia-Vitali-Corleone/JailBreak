# Jailbreak v0.1.0

---

## 简介 (Chinese)

这是项目的第一代版本，功能非常基础。  

### 功能

- 程序运行后仅在 **系统托盘** 中显示图标。  
- **默认状态**：黑色图标，提示文本为 `jailbreak — double-click to toggle`。  
- **切换逻辑**：  
  - 双击图标后切换为红色，提示文本为 `jailbreak: RED`。  
  - 再次双击切回黑色，提示文本为 `jailbreak: BLACK`。  

除此之外，本版本没有其他功能。

### 运行

```bash
dotnet build
dotnet run
```

### 发布

```
dotnet publish -c Release
```

## Introduction (English)

This is the **first generation** of the project, with only very basic functionality.

### Features

- The application runs as a **system tray icon** only.
- **Default state**: black icon with tooltip text `jailbreak — double-click to toggle`.
- **Toggle behavior**:
  - Double-click changes the icon to red with tooltip `jailbreak: RED`.
  - Double-click again switches back to black with tooltip `jailbreak: BLACK`.

No other features are included in this version.

### Run

```
dotnet build
dotnet run
```

### Publish

```
dotnet publish -c Release
```