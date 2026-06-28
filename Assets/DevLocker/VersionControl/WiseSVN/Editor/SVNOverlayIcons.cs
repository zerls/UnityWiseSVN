// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN
{
	/// <summary>
	/// 在 Project 窗口中渲染 SVN 覆盖图标；挂钩 Unity 文件变更 API 按需刷新。
	/// </summary>
	[InitializeOnLoad]
	internal static class SVNOverlayIcons
	{
		// ── 偏好快捷访问器 ────────────────────────────────────────────────────
		private static SVNPreferencesManager.PersonalPreferences m_PersonalPrefs =>
			SVNPreferencesManager.Instance.PersonalPrefs;

		private static bool IsActive =>
			SVNPreferencesManager.Instance.IsIntegrationEnabled &&
			(m_PersonalPrefs.PopulateStatusesDatabase ||
			 SVNPreferencesManager.Instance.ProjectPrefs.EnableLockPrompt);

		// ── 运行时状态缓存 ────────────────────────────────────────────────────
		private static bool     m_ShowNormalStatusIcons;
		private static bool     m_ShowExcludeStatusIcons;
		private static bool     m_ShowJunctionOverlayIcon = true;
		private static string[] m_ExcludedPaths           = Array.Empty<string>();

		private static GUIContent m_DataIsIncompleteWarning;
		private static int?       m_RefreshProgressId;

		// ── 启动期偏好重放 ────────────────────────────────────────────────────
		// 静态构造完成时 ProjectWindow 的事件派发器可能尚未就绪，导致首次
		// re-bind 对第一帧无效。在启动后 ~3 秒内重放 5 次，覆盖各版本时序差异。
		private static int    s_StartupResimulateFiresRemaining;
		private static double s_StartupResimulateNextFireAt;

		private static void StartupResimulatePreferencesTick()
		{
			if (EditorApplication.timeSinceStartup < s_StartupResimulateNextFireAt) return;

			SVNPreferencesManager.Instance.NotifyPreferencesChanged();
			s_StartupResimulateNextFireAt = EditorApplication.timeSinceStartup + 0.6;

			if (--s_StartupResimulateFiresRemaining <= 0)
				EditorApplication.update -= StartupResimulatePreferencesTick;
		}

		// ════════════════════════════════════════════════════════════════════
		//  静态构造：订阅事件 + 启动重放
		// ════════════════════════════════════════════════════════════════════
		static SVNOverlayIcons()
		{
			SVNPreferencesManager.Instance.PreferencesChanged             += PreferencesChanged;
			SVNPreferencesManager.Instance.StatusProviderChanged          += OnStatusProviderChanged;
			SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += OnDatabaseChanged;

			// 即使主 Provider 是 TSVNCache，也必须监听 CLI 数据库事件。
			// TSVNCache 不追踪未版本化条目，缺此订阅则 Unversioned 图标在重启后无法重绘。
			SVNStatusesDatabase.Instance.DatabaseChanged -= OnDatabaseChanged;
			SVNStatusesDatabase.Instance.DatabaseChanged += OnDatabaseChanged;

			PreferencesChanged();

			// 启动重放（见上方注释）
			s_StartupResimulateFiresRemaining = 5;
			s_StartupResimulateNextFireAt     = EditorApplication.timeSinceStartup + 0.1;
			EditorApplication.update         -= StartupResimulatePreferencesTick;
			EditorApplication.update         += StartupResimulatePreferencesTick;
		}

		// ── Provider 切换（CLI → TSVNCache）时重新订阅并刷新 ─────────────────
		private static void OnStatusProviderChanged()
		{
			SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += OnDatabaseChanged;
			OnDatabaseChanged();
		}

		// ── 偏好变更回调 ──────────────────────────────────────────────────────
		private static void PreferencesChanged()
		{
			if (IsActive) {
				// 先 -= 再 += 防止多次订阅堆叠
				EditorApplication.projectWindowItemOnGUI -= ItemOnGUI;
				EditorApplication.projectWindowItemOnGUI += ItemOnGUI;

				m_ShowNormalStatusIcons   = m_PersonalPrefs.ShowNormalStatusOverlayIcon;
				m_ShowExcludeStatusIcons  = m_PersonalPrefs.ShowExcludedStatusOverlayIcon;
				m_ShowJunctionOverlayIcon = m_PersonalPrefs.ShowJunctionOverlayIcon;
				m_ExcludedPaths = m_PersonalPrefs.Exclude
					.Concat(SVNPreferencesManager.Instance.ProjectPrefs.Exclude)
					.ToArray();
			} else {
				EditorApplication.projectWindowItemOnGUI -= ItemOnGUI;
			}

			OnDatabaseChanged();
		}

		// ════════════════════════════════════════════════════════════════════
		//  刷新菜单项
		// ════════════════════════════════════════════════════════════════════
		public const string InvalidateDatabaseMenuText = "Assets/SVN/\U0001F504  Refresh Icons && Locks";

		[MenuItem("Window/Version Control/SVN/\U0001F504  Refresh Icons && Locks %&r",
				   false, ContextMenus.SVNContextMenusManager.WindowMenuPriority + 60)]
		public static void InvalidateDatabaseMenu()
		{
			if (!m_PersonalPrefs.EnableCoreIntegration || !m_PersonalPrefs.PopulateStatusesDatabase) {
				EditorUtility.DisplayDialog(
					Tr("overlay.integration_disabled.title"),
					Tr("overlay.integration_disabled.msg"),
					Tr("common.ok"));
				return;
			}

			WiseSVNIntegration.ClearLastDisplayedError();
			SVNPreferencesManager.Instance.TemporarySilenceLockPrompts = false;
			SVNStatusesDatabase.Instance.m_GlobalIgnoresCollected       = false;
			SVNStatusesDatabase.Instance.InvalidateDatabase();
			LockPrompting.SVNLockPromptDatabase.Instance.ClearKnowledge();

			// 重置进度条（防止残留旧 ID）
			if (m_RefreshProgressId.HasValue) {
				EditorApplication.update -= UpdateDatabaseRefreshProgress;
				Progress.Remove(m_RefreshProgressId.Value);
				m_RefreshProgressId = null;
			}

			m_RefreshProgressId = Progress.Start(
				Tr("overlay.refresh.title"),
				Tr("overlay.refresh.msg"),
				Progress.Options.Indefinite);
			EditorApplication.update += UpdateDatabaseRefreshProgress;
		}

		private static void UpdateDatabaseRefreshProgress()
		{
			// 刷新完成前每帧上报 50%（Indefinite 模式仅做动画，数值无实际意义）
			if (m_RefreshProgressId.HasValue)
				Progress.Report(m_RefreshProgressId.Value, 0.5f);
		}

		private static void OnDatabaseChanged()
		{
			if (m_RefreshProgressId.HasValue) {
				EditorApplication.update -= UpdateDatabaseRefreshProgress;
				Progress.Remove(m_RefreshProgressId.Value);
				m_RefreshProgressId = null;
			}
			EditorApplication.RepaintProjectWindow();
		}

		internal static GUIContent GetDataIsIncompleteWarning()
		{
			if (m_DataIsIncompleteWarning == null) {
				m_DataIsIncompleteWarning = EditorGUIUtility.IconContent("console.warnicon.sml");
				m_DataIsIncompleteWarning.tooltip = Tr("overlay.data_incomplete.tooltip");
			}
			return m_DataIsIncompleteWarning;
		}

		// ════════════════════════════════════════════════════════════════════
		//  主绘制入口（纯调度，不含逻辑细节）
		// ════════════════════════════════════════════════════════════════════
		private static void ItemOnGUI(string guid, Rect selectionRect)
		{
			if (string.IsNullOrEmpty(guid) || guid.StartsWith("00000000", StringComparison.Ordinal)) {
				// 仅对 Assets 根节点显示"数据不完整"警告图标
				if (SVNPreferencesManager.Instance.StatusProvider.DataIsIncomplete &&
					guid.Equals(SVNStatusesDatabase.ASSETS_FOLDER_GUID, StringComparison.OrdinalIgnoreCase)) {
					DrawDataIncompleteWarning(selectionRect);
				}
				return;
			}

			string assetPath = AssetDatabase.GUIDToAssetPath(guid);

			// ① 从主 Provider 获取状态（TSVNCache 快速路径，或 CLI 兜底）
			var statusData = SVNPreferencesManager.Instance.StatusProvider.GetStatus(assetPath);

			// ② 以 CLI 数据库补全缺失字段（CLI 是 Lock / Remote / Unversioned 的权威来源）
			MergeCliStatus(ref statusData, guid);

			// ③ 按优先级分层绘制（各层占不同角落，互不遮挡）
			DrawRemoteStatusIcon(selectionRect, statusData);       // P2：远程状态 → 右上
			DrawLockStatusIcon(selectionRect, statusData, guid);   // P1：锁状态   → 右下（可点击）
			DrawFileStatusIcon(selectionRect, statusData, guid);   // P3：文件状态 → 左下

			// P4：NTFS Junction 徽章 → 左上（仅 Junction 根节点）
			if (m_ShowJunctionOverlayIcon
				&& Utils.JunctionResolver.HasJunctions
				&& Utils.JunctionResolver.IsJunctionRoot(assetPath)) {
				DrawJunctionBadge(selectionRect);
			}
		}

		// ════════════════════════════════════════════════════════════════════
		//  数据层：CLI 状态合并
		// ════════════════════════════════════════════════════════════════════
		// TSVNCache 结构上无法提供：RemoteStatus / LockDetails /
		// Unversioned（m_UnversionedFolders） / MovedTo / SwitchedExternal。
		// 每次 ItemOnGUI 仅一次字典查找，开销可忽略。
		private static void MergeCliStatus(ref SVNStatusData statusData, string guid)
		{
			var db = SVNStatusesDatabase.Instance.GetKnownStatusData(guid);
			if (!db.IsValid) return;

			// 文件状态：CLI 兼合成 Unversioned/Ignored，优先级高于 TSVNCache
			if (db.Status != VCFileStatus.None)
				statusData.Status = db.Status;

			if (db.PropertiesStatus != VCPropertiesStatus.None)
				statusData.PropertiesStatus = db.PropertiesStatus;

			if (db.TreeConflictStatus != VCTreeConflictStatus.Normal)
				statusData.TreeConflictStatus = db.TreeConflictStatus;

			if (db.SwitchedExternalStatus != VCSwitchedExternal.Normal)
				statusData.SwitchedExternalStatus = db.SwitchedExternalStatus;

			// 锁：CLI 区分 LockedHere/LockedOther/Broken/Stolen 并携带完整 LockDetails
			if (db.LockStatus != VCLockStatus.NoLock) {
				statusData.LockStatus  = db.LockStatus;
				statusData.LockDetails = db.LockDetails;
			}

			// 远端状态：TSVNCache 不查远端（check_out_of_date=FALSE），由 CLI 独占
			if (db.RemoteStatus != VCRemoteFileStatus.None)
				statusData.RemoteStatus = db.RemoteStatus;

			if (string.IsNullOrEmpty(statusData.Path))
				statusData.Path = db.Path;
		}

		// ════════════════════════════════════════════════════════════════════
		//  Emoji 绘制核心辅助
		//
		//  原理：Unity IMGUI 文字/Emoji 的渲染尺寸由 fontSize 决定，与 Rect 无关。
		//  每次绘制前将 fontSize 动态设为 rect.height × scaleFactor，
		//  使 Emoji 视觉上填满 BuildIconRect 计算出的目标矩形。
		//
		//  scaleFactor ≈ 0.82：Emoji 字形内部含留白（ascender/descender），
		//  直接用 1.0 会溢出边框；0.82 是在 Windows(Segoe UI Emoji) /
		//  macOS(Apple Color Emoji) 两端测试后的经验最优值。
		//  如目标平台字体差异较大，可在调用处传入自定义 scaleFactor 微调。
		// ════════════════════════════════════════════════════════════════════
		private static GUIStyle s_EmojiStyle;

		private static void DrawEmoji(Rect rect, GUIContent content, float scaleFactor = 0.82f)
		{
			if (s_EmojiStyle == null) {
				s_EmojiStyle = new GUIStyle(GUIStyle.none) {
					alignment = TextAnchor.MiddleCenter,
					padding   = new RectOffset(0, 0, 0, 0),
					// richText = false 避免 Emoji 中的 < > 被误解析为富文本标签
					richText  = false,
				};
			}
			// Mathf.Max(8,...) 保证极小矩形（缩略图最低缩放）下仍可辨认
			s_EmojiStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(rect.height * scaleFactor));
			GUI.Label(rect, content, s_EmojiStyle);
		}

		// ════════════════════════════════════════════════════════════════════
		//  视图层：各图标绘制方法
		// ════════════════════════════════════════════════════════════════════

		// ── 远程状态图标（右上角）────────────────────────────────────────────
		// 将原始纹理图标替换为 Emoji，无外部资源依赖，主题自适应。
		// 遇到未映射的枚举值时回退到原始纹理图标，保持前向兼容。
		private static GUIContent s_RemoteModified;

		private static GUIContent GetRemoteContent()
		{
			// ⬇ U+2B07 DOWNWARDS BLACK ARROW：直觉含义"远端有更新，需要拉取"
			// VCRemoteFileStatus 只有 None 和 Modified 两个值，None 在调用前已过滤。
			return s_RemoteModified ??= new GUIContent("⬇️", Tr("overlay.tooltip.remote_modified"));
		}

		private static void DrawRemoteStatusIcon(Rect sel, SVNStatusData statusData)
		{
			if (statusData.RemoteStatus == VCRemoteFileStatus.None) return;

			var content = GetRemoteContent();
			if (content == null) return;

			var iconRect = BuildIconRect(sel, IconSlot.TopRight);

			// 有 image（纹理回退路径）→ 标准 GUI.Label
			// 无 image（Emoji 路径）  → DrawEmoji 动态字体大小
			if (content.image != null)
				GUI.Label(iconRect, content);
			else
				DrawEmoji(iconRect, content);
		}

		// ── 锁状态图标（右下角，可点击弹出详情）─────────────────────────────
		private static void DrawLockStatusIcon(Rect sel, SVNStatusData statusData, string guid)
		{
			if (statusData.LockStatus == VCLockStatus.NoLock) return;

			var icon = SVNPreferencesManager.Instance.GetLockStatusIconContent(statusData.LockStatus);
			if (icon == null) return;

			if (GUI.Button(BuildIconRect(sel, IconSlot.BottomRight), icon, EditorStyles.label))
				ShowLockDetailsDialog(guid);
		}

		/// <summary>弹出锁详情对话框，展示所有已知的锁记录。</summary>
		private static void ShowLockDetailsDialog(string guid)
		{
			var sb = new StringBuilder();
			foreach (var data in SVNStatusesDatabase.Instance.GetAllKnownStatusData(guid, false, true, true)) {
				sb.AppendLine(Tr("overlay.lockdetails.msg",
					System.IO.Path.GetFileName(data.Path),
					ObjectNames.NicifyVariableName(data.LockStatus.ToString()),
					data.LockDetails.Owner,
					FormatLockDate(data.LockDetails.Date),
					data.LockDetails.Message));
			}
			EditorUtility.DisplayDialog(
				Tr("overlay.lockdetails.title"),
				sb.ToString().TrimEnd(),
				Tr("common.ok"));
		}

		/// <summary>
		/// 将锁日期字符串格式化为 yyyy-MM-dd HH:mm:ss。
		/// 容忍形如 "2020-09-08 23:32:13 +0300 (??, 08 ??? 2020)" 的奇特格式。
		/// 修复了原始代码中 IndexOf 返回 -1 时 Substring 崩溃的潜在 Bug。
		/// </summary>
		private static string FormatLockDate(string dateStr)
		{
			if (string.IsNullOrEmpty(dateStr)) return dateStr;

			if (DateTime.TryParse(dateStr, out var date))
				return date.ToString("yyyy-MM-dd HH:mm:ss");

			// 尝试截取括号前的标准部分再解析（parenIdx > 0 防止 IndexOf 返回 -1 导致崩溃）
			int parenIdx = dateStr.IndexOf('(');
			if (parenIdx > 0 && DateTime.TryParse(dateStr.Substring(0, parenIdx), out date))
				return date.ToString("yyyy-MM-dd HH:mm:ss");

			return dateStr; // 解析失败时原样返回，不抛异常
		}

		// ── 文件状态图标（左下角）─────────────────────────────────────────────
		private static void DrawFileStatusIcon(Rect sel, SVNStatusData statusData, string guid)
		{
			VCFileStatus fileStatus = ResolveFileStatus(statusData, guid);

			// Normal / Excluded / Ignored 受偏好开关独立控制
			if (!m_ShowNormalStatusIcons && fileStatus == VCFileStatus.Normal) return;
			if (!m_ShowExcludeStatusIcons &&
				(fileStatus == VCFileStatus.Excluded || fileStatus == VCFileStatus.Ignored)) return;

			var icon = SVNPreferencesManager.Instance.GetFileStatusIconContent(fileStatus);
			if (icon == null || icon.image == null) return;

			GUI.Label(BuildIconRect(sel, IconSlot.BottomLeft), icon);
		}

		/// <summary>
		/// 计算最终显示的文件状态，优先级：冲突提升 > 属性修改 > 原始状态 > Normal 回填。
		/// </summary>
		private static VCFileStatus ResolveFileStatus(SVNStatusData statusData, string guid)
		{
			// 冲突优先级最高（匹配 TortoiseSVN Shell 行为）
			if (statusData.PropertiesStatus == VCPropertiesStatus.Conflicted
				|| statusData.TreeConflictStatus == VCTreeConflictStatus.TreeConflict)
				return VCFileStatus.Conflicted;

			VCFileStatus fileStatus = statusData.Status;

			// 属性修改 + 文本 Normal → 展示为 Modified
			if (statusData.PropertiesStatus == VCPropertiesStatus.Modified
				&& fileStatus == VCFileStatus.Normal)
				fileStatus = VCFileStatus.Modified;

			// 不在数据库中 → svn status 仅输出非 Normal 条目，缺席即代表 Normal
			if (m_ShowNormalStatusIcons && !statusData.IsValid) {
				fileStatus = VCFileStatus.Normal;
				if (m_ExcludedPaths.Length > 0) {
					string path = AssetDatabase.GUIDToAssetPath(guid);
					if (SVNPreferencesManager.ShouldExclude(m_ExcludedPaths, path))
						fileStatus = m_ShowExcludeStatusIcons ? VCFileStatus.Excluded : VCFileStatus.None;
				}
			}

			return fileStatus;
		}

		// ── Junction 徽章（左上角）────────────────────────────────────────────
		// 使用共用 DrawEmoji，fontSize 自动跟随 BuildIconRect 矩形高度缩放，
		// 替换原来固定 fontSize=10 的独立 GUIStyle（在大矩形时显示偏小的根因）。
		// 注：彩色 Emoji 在 Windows/macOS 均以全彩字形渲染，GUIStyle.textColor 染色无效，
		//     故移除了原来的"柔和蓝"着色逻辑。
		private static GUIContent s_JunctionBadgeContent;

		private static void DrawJunctionBadge(Rect sel)
		{
			s_JunctionBadgeContent ??= new GUIContent("🔗", Tr("overlay.tooltip.junction"));
			DrawEmoji(BuildIconRect(sel, IconSlot.TopLeft), s_JunctionBadgeContent);
		}

		// ── 数据不完整警告（Assets 根节点专用）────────────────────────────────
		private static void DrawDataIncompleteWarning(Rect sel)
		{
			const float h = 20f;
			// 右上角，距边缘 8px
			var iconRect = new Rect(sel.x + sel.width - h - 8f, sel.y - 2f, h, h);
			GUI.Label(iconRect, GetDataIsIncompleteWarning());
		}

		// ════════════════════════════════════════════════════════════════════
		//  图标矩形构建器 — 统一四角布局，消除重复的 Rect 运算
		//
		//  角落分配（不重叠）：
		//       TopLeft(Junction) ┌───────┐ TopRight(Remote)
		//                         │       │
		//    BottomLeft(FileStatus)└───────┘ BottomRight(Lock)
		//
		//  尺寸规则：
		//    列表视图 — Remote/Lock 用行高(sel.height)；File/Junction 固定 14px
		//    网格视图 — Remote/Lock/File 为 max(18, width×36%)；Junction max(14, width×22%)
		// ════════════════════════════════════════════════════════════════════
		private enum IconSlot { TopRight, BottomRight, BottomLeft, TopLeft }

		private static Rect BuildIconRect(Rect sel, IconSlot slot)
		{
			bool isList = sel.width > sel.height;

			if (isList) {
				// 列表视图：从右往左依次排列 Remote / Lock，File / Junction 固定在左侧
				switch (slot) {
					case IconSlot.TopRight:    // 远程：右数第 1 格
						return new Rect(sel.x + sel.width - sel.height * 2f, sel.y, sel.height, sel.height);
					case IconSlot.BottomRight: // 锁：右数第 2 格
						return new Rect(sel.x + sel.width - sel.height * 3f, sel.y, sel.height, sel.height);
					case IconSlot.BottomLeft:  // 文件状态：行左侧，垂直居中
						return new Rect(sel.x - 3f, sel.y + 7f, 14f, 14f);
					case IconSlot.TopLeft:     // Junction：行左侧，略高
						return new Rect(sel.x,      sel.y + 1f, 14f, 14f);
					default: return Rect.zero;
				}
			} else {
				// 网格视图：36% 缩放 + 18px 下限，保证低缩放级别下图标仍可辨识
				float w      = Mathf.Max(18f, sel.width * 0.36f);
				float offset = sel.width - w; // 用于右对齐和底部对齐

				switch (slot) {
					case IconSlot.TopRight:    // 远程：右上（稍向上 4px 避免与文件名标签重叠）
						return new Rect(sel.x + offset, sel.y - 4f,          w, w);
					case IconSlot.BottomRight: // 锁：右下
						return new Rect(sel.x + offset, sel.y + offset + 2f, w, w);
					case IconSlot.BottomLeft:  // 文件状态：左下
						return new Rect(sel.x,          sel.y + offset + 1f, w, w);
					case IconSlot.TopLeft: {   // Junction：左上，22% 比例略小以示区分
						float jw = Mathf.Max(14f, sel.width * 0.22f);
						return new Rect(sel.x, sel.y, jw, jw);
					}
					default: return Rect.zero;
				}
			}
		}
	}
}