# WiseSVN for Unity — 用户指南

> **本分支 (Fork)** 在原版 [NibbleByte/UnityWiseSVN](https://github.com/NibbleByte/UnityWiseSVN) 基础上增加了中英文国际化、SVN 忽略管理器等功能。

---

## 1. 简介

WiseSVN 是 Unity Editor 的 Subversion (SVN) 版本控制集成插件。它挂接 Unity 的资源管线，让 SVN 操作与文件操作保持同步，提供 Project 窗口覆盖图标、SVN 命令右键菜单、锁定提示工作流和分支选择器。

**支持平台：** Windows (TortoiseSVN)、macOS (SnailSVN)、Linux (RabbitVCS 或 CLI)

---

## 2. 安装

### 方式一：Unity Asset Store
搜索 "Wise SVN"，或访问 [Asset Store](https://assetstore.unity.com/packages/tools/version-control/wise-svn-162636)。

### 方式二：OpenUPM
```bash
npm install -g openupm-cli
openupm add devlocker.versioncontrol.wisesvn
```

### 方式三：GitHub UPM（不推荐）
在 `Packages/manifest.json` 中添加：
```json
{
  "dependencies": {
    "devlocker.versioncontrol.wisesvn": "https://github.com/NibbleByte/UnityWiseSVN.git#upm"
  }
}
```

---

## 3. 前置条件

- **SVN CLI（命令行接口）** 必须安装：
  - Windows：安装 TortoiseSVN 时勾选 **"command line client tools"**
  - macOS/Linux：参考 [Subversion 官方安装指南](https://subversion.apache.org/packages.html)
- **图形客户端：** TortoiseSVN (Windows)、SnailSVN (macOS)、RabbitVCS (Linux)

---

## 4. 基础用法

### 菜单
所有命令位于顶部菜单 `Assets/SVN/` 或右键当前资源 → `SVN/`：
- **Diff / Resolve** — 查看差异 / 解决冲突
- **Update All** — 全部更新
- **Commit All / Commit** — 全部提交 / 提交所选
- **Add** — 添加未版本化文件
- **Revert All / Revert** — 全部回滚 / 回滚所选
- **Get Locks / Release Locks** — 获取 / 释放锁
- **Show Log All / Show Log** — 查看日志
- **Repo Browser** — 仓库浏览器
- **Switch Branch** — 切换分支
- **Blame** — 修改追溯
- **Cleanup** — 清理
- **Ignore Toggle** — 切换 svn:ignore（1.6.0 新增）
- **Ignore Manager** — 忽略管理器（1.6.0 新增）

### 覆盖图标
Project 窗口中每个资源左下角会显示 SVN 状态图标：
| 图标 | 含义 |
|------|------|
| 绿色 ✓ | 正常（无变更）|
| 红色 ✏ | 已修改 |
| 蓝色 + | 已添加 |
| 红色 ✕ | 已删除 |
| 黄色 ⚠ | 冲突 |
| 灰色 ○ | 已忽略 / 排除 |
| 蓝色 ? | 未版本化 |
| 锁图标 | 被锁定（绿色=自己，红色=他人）|
| 云图标 | 服务器有更新 |

### 文件操作自动同步
在 Unity 中移动/删除/创建资源时，WiseSVN 会自动执行对应的 `svn move`/`svn delete`/`svn add`。

---

## 5. 偏好设置详解

打开 `Assets/SVN/SVN Preferences`。

### 个人偏好（UserSettings/WiseSVN.prefs）
| 设置 | 说明 |
|------|------|
| 语言 | 可选 自动/English/简体中文。自动跟随系统语言 |
| 启用 SVN 集成 | 主开关，关闭后所有功能停用 |
| 启用覆盖图标 | 在 Project 窗口显示 SVN 状态图标 |
| 扫描 svn-ignore | 识别忽略列表中的文件 |
| 覆盖图标刷新间隔 | 秒数，-1 表示仅文件变更时刷新 |
| 检查仓库远端变更 | 联机查询服务器更新 |
| 自动锁定 | 修改资源时自动锁定 |
| Scene 视图冲突提示 | Scene/Prefab 过期或锁定时显示警告 |
| SVN CLI 路径 | 自定义 svn 可执行文件路径 |
| 右键菜单客户端 | TortoiseSVN / SnailSVN / RabbitVCS / CLI |

### 项目偏好（ProjectSettings/WiseSVN.prefs，可纳入版本控制）
| 设置 | 说明 |
|------|------|
| 检查仓库变更 | 团队级别的联机更新检查 |
| SVN CLI 路径 | Windows / macOS 分别配置 |
| 资源移动行为 | Normal / Add+Delete |
| 启用锁定提示 | Perforce 风格的锁提示（需要覆盖图标开启）|
| 锁定提示参数 | 监控目录、资源类型、排除规则 |
| 启用分支数据库 | 扫描 SVN 仓库中的分支 |
| 分支扫描参数 | 入口 URL、签名条目、排除目录 |

---

## 6. 锁定提示 (Lock Prompt)

当受监控的资源或其 `.meta` 被修改时，弹出窗口询问是否锁定：
- 显示资源是否被他人锁定或服务器有更新
- 支持强制夺取锁（Steal lock）
- 支持"修改时自动锁定"
- 跳过后同一资源不再弹窗（重启 Unity 后重新评估）

---

## 7. 分支选择器 (Branch Selector)

`Assets/SVN/Branch Selector`：
- 扫描 SVN 仓库中所有包含 Unity 项目的分支
- 按资源搜索各分支冲突
- 支持切换分支、打开 Repo Browser、查看日志

---

## 8. SVN 忽略管理（新增 1.6.0）

### Ignore Toggle
选中未版本化的资源 → `Assets/SVN/Ignore Toggle`，资源及其 `.meta` 将被加入父目录的 `svn:ignore`。

如果资源已版本化（已提交），会弹出三选项对话框：
1. 加入 `ignore-on-commit` 修改列表（TortoiseSVN 约定）
2. 取消
3. 标记删除 + 忽略

### Ignore Manager
`Assets/SVN/Ignore Manager` 打开管理窗口：
- 左侧列出所有设置了 `svn:ignore` 的目录
- 右侧编辑当前目录的模式列表
- `+` 按钮将 Project 窗口选中的文件夹加入管理
- `-` 按钮移除模式
- 点击 **Apply Changes** 将更改写入 SVN
- `svn:global-ignores` 在窗口中只读

---

## 9. 语言切换（新增 1.6.0）

偏好设置 → 个人 → 语言下拉框：
- **Auto（自动）**：跟随 Unity 系统语言
- **English**：强制英文
- **简体中文**：强制中文

切换后所有 WiseSVN UI 立即生效。

---

## 10. 常见问题

**Q: 覆盖图标不显示？**
- 检查个人偏好中"启用 SVN 集成"和"启用覆盖图标"是否勾选
- 检查项目是否在 SVN 版本控制下
- 尝试 `Assets/SVN/Refresh Icons & Locks`

**Q: 提示 "SVN CLI missing"？**
- Windows：重新安装 TortoiseSVN 并勾选 command line tools
- macOS：`brew install svn`
- 或在个人偏好中手动指定 svn 路径

**Q: 移动文件失败？**
- 检查目标目录是否已纳入版本控制
- 检查文件是否有冲突

---

## 11. 开发者 API

WiseSVN 提供公共 API 供其他工具集成。

### 静默模式
```csharp
WiseSVNIntegration.RequestSilence();
// ... do work ...
WiseSVNIntegration.ClearSilence();
```

### 临时禁用
```csharp
WiseSVNIntegration.RequestTemporaryDisable();
// ... do work ...
WiseSVNIntegration.ClearTemporaryDisable();
```

### 调用右键菜单命令
```csharp
SVNContextMenusManager.Commit();
SVNContextMenusManager.Update();
SVNContextMenusManager.Revert();
SVNContextMenusManager.ShowLog(assetPath);
```

### 直接执行 SVN 命令
```csharp
var statuses = new List<SVNStatusData>();
var result = WiseSVNIntegration.GetStatuses(statuses, timeout: 30000);

// 异步
var op = WiseSVNIntegration.UpdateAsync(timeout: 60000);
op.Completed += (o) => Debug.Log($"Update result: {o.Result}");
```

详见 `Developer_Guide.md`。
