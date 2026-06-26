# Changelog

All notable changes to WiseSVN for Unity will be documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [1.6.0] - 2026-06-27

### Added
- 中英文国际化支持：基于 `LocalizationManager` 的简易 i18n 系统，支持语言偏好持久保存
  - 英文 locale 作为 fallback，中文简体作为可选语言
  - `Auto` 模式根据 Unity 系统语言自动检测 (`SystemLanguage.ChineseSimplified`)
- SVN Ignore Manager 窗口 (`Assets/SVN/Ignore Manager`)：可视化浏览、添加、删除 `svn:ignore` 模式
- `WiseSVNIntegration.Propdel()` — 移除 SVN 属性（用于删除空的 `svn:ignore`）
- `WiseSVNIntegration.PropsetAsync()` — `Propset` 的异步封装
- 启用之前隐藏的 Ignore Toggle 上下文菜单 (`Assets/SVN/Ignore Toggle`)
- 自动 UPM 打包脚本 `Scripts/pack.sh`，支持 `--dry-run` 模式验证

### Changed
- 偏好设置窗口新增语言选择下拉框（个人偏好最顶部）
- 覆盖图标 tooltip 随语言设置动态切换
- SceneView 覆盖文本、覆盖图标对话框、数据不完整提示均支持中英文

## [1.5.12] - (prior upstream release)

- Updated package version to 1.5.12
- Renamed some methods.
- Added "Switch Branch" context menu.
- Added warning message for partial branching.
- Simplified the PromptForAuth() -> ShellUtils.ExecutePrompt() flow for Linux.
