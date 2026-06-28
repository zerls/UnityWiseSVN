// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using DevLocker.VersionControl.WiseSVN.Providers;
using DevLocker.VersionControl.WiseSVN.Utils;
using System;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN
{
	/// <summary>
	/// Layer 3 -- pure rendering. SVN overlay icons in the Project window.
	/// All data logic (multi-source merge, conflict escalation, junction detection)
	/// has been moved to SVNStatusResolver; this file only does O(1) cache reads
	/// and GUI drawing.
	/// </summary>
	[InitializeOnLoad]
	internal static class SVNOverlayIcons
	{
		private static SVNPreferencesManager.PersonalPreferences m_PersonalPrefs =>
			SVNPreferencesManager.Instance.PersonalPrefs;

		private static bool IsActive =>
			SVNPreferencesManager.Instance.IsIntegrationEnabled &&
			(m_PersonalPrefs.PopulateStatusesDatabase ||
			 SVNPreferencesManager.Instance.ProjectPrefs.EnableLockPrompt);

		private static bool     m_ShowNormalStatusIcons;
		private static bool     m_ShowExcludeStatusIcons;
		private static bool     m_ShowJunctionOverlayIcon = true;
		private static string[] m_ExcludedPaths           = Array.Empty<string>();

		private static GUIContent m_DataIsIncompleteWarning;
		private static int?       m_RefreshProgressId;

		// startup preference replay
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

		static SVNOverlayIcons()
		{
			SVNPreferencesManager.Instance.PreferencesChanged += PreferencesChanged;
			SVNPreferencesManager.Instance.StatusProviderChanged += OnStatusProviderChanged;

			// Listen to the Resolver (not raw data sources) to avoid the empty-m_Data
			// flicker window during CLI DB rebuilds. The Resolver receives
			// DatabaseChangeStarting / DatabaseChanged and holds previous merge
			// results until the rebuild completes.
			SVNStatusResolver.Instance.ResolvedChanged += OnResolvedChanged;

			// CLI DB events still needed for progress bar management and
			// DataIsIncomplete warning state.
			SVNStatusesDatabase.Instance.DatabaseChanged += OnDatabaseChanged;

			PreferencesChanged();

			s_StartupResimulateFiresRemaining = 2;
			s_StartupResimulateNextFireAt     = EditorApplication.timeSinceStartup + 0.1;
			EditorApplication.update         -= StartupResimulatePreferencesTick;
			EditorApplication.update         += StartupResimulatePreferencesTick;
		}

		private static void OnStatusProviderChanged()
		{
			SVNStatusResolver.Instance.RebuildAll();
		}

		private static void PreferencesChanged()
		{
			if (IsActive) {
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

			OnResolvedChanged();
		}

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
		}

		private static void OnResolvedChanged()
		{
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

		// ==================================================================
		//  Main entry -- pure rendering, zero data logic
		// ==================================================================
		private static void ItemOnGUI(string guid, Rect selectionRect)
		{
			if (string.IsNullOrEmpty(guid) || guid.StartsWith("00000000", StringComparison.Ordinal)) {
				if (SVNPreferencesManager.Instance.StatusProvider.DataIsIncomplete &&
					guid.Equals(SVNStatusesDatabase.ASSETS_FOLDER_GUID, StringComparison.OrdinalIgnoreCase)) {
					DrawDataIncompleteWarning(selectionRect);
				}
				return;
			}

			// O(1) from the Resolver -- all merge/escalation already applied
			var r = SVNStatusResolver.Instance.GetResolved(guid);

			// P3 file status -> bottom-left
			DrawFileStatusIcon(selectionRect, r);

			// P2 remote status -> top-right
			DrawRemoteStatusIcon(selectionRect, r);

			// P1 lock status -> bottom-right (clickable)
			DrawLockStatusIcon(selectionRect, r, guid);

			// P4 junction badge -> top-left
			if (r.IsJunctionRoot && m_ShowJunctionOverlayIcon)
				DrawJunctionBadge(selectionRect);
		}

		// ==================================================================
		//  View layer -- each method takes ResolvedStatusData
		// ==================================================================

		private static GUIContent s_RemoteModified;

		private static void DrawRemoteStatusIcon(Rect sel, ResolvedStatusData r)
		{
			if (r.RemoteStatus == VCRemoteFileStatus.None) return;

			s_RemoteModified ??= new GUIContent("⬇️",
				Tr("overlay.tooltip.remote_modified"));
			WiseSVNGUIUtils.DrawEmoji(BuildIconRect(sel, IconSlot.TopRight), s_RemoteModified);
		}

		private static void DrawLockStatusIcon(Rect sel, ResolvedStatusData r, string guid)
		{
			if (r.LockStatus == VCLockStatus.NoLock) return;

			var icon = SVNPreferencesManager.Instance.GetLockStatusIconContent(r.LockStatus);
			if (icon == null) return;

			// Lock icon is always drawn as emoji via DrawEmoji (same rendering path as file-status and remote).
			// It remains clickable: MouseDown then ShowLockDetailsDialog.
			var rect = BuildIconRect(sel, IconSlot.BottomRight);
			if (Event.current.type == EventType.MouseDown && rect.Contains(Event.current.mousePosition))
				ShowLockDetailsDialog(guid);
			WiseSVNGUIUtils.DrawEmoji(rect, icon);
		}

		private static void ShowLockDetailsDialog(string guid)
		{
			var sb = new StringBuilder();
			foreach (var data in SVNStatusesDatabase.Instance.GetAllKnownStatusData(guid, false, true, true)) {
				sb.AppendLine(Tr("overlay.lockdetails.msg",
					System.IO.Path.GetFileName(data.Path),
					ObjectNames.NicifyVariableName(data.LockStatus.ToString()),
					data.LockDetails.Owner,
					WiseSVNGUIUtils.FormatDate(data.LockDetails.Date),
					data.LockDetails.Message));
			}
			EditorUtility.DisplayDialog(Tr("overlay.lockdetails.title"),
				sb.ToString().TrimEnd(), Tr("common.ok"));
		}

		private static void DrawFileStatusIcon(Rect sel, ResolvedStatusData r)
		{
			VCFileStatus fs = r.FileStatus;

			if (!m_ShowNormalStatusIcons && fs == VCFileStatus.Normal) return;
			if (!m_ShowExcludeStatusIcons &&
				(fs == VCFileStatus.Excluded || fs == VCFileStatus.Ignored)) return;

			var icon = SVNPreferencesManager.Instance.GetFileStatusIconContent(fs);
			if (icon == null) return;

			if (icon.image != null) {
				GUI.Label(BuildIconRect(sel, IconSlot.BottomLeft), icon);
			} else {
				WiseSVNGUIUtils.DrawEmoji(BuildIconRect(sel, IconSlot.BottomLeft), icon);
			}
		}

		private static GUIContent s_JunctionBadgeContent;

		private static void DrawJunctionBadge(Rect sel)
		{
			s_JunctionBadgeContent ??= new GUIContent("\U0001f517",
				Tr("overlay.tooltip.junction"));
			WiseSVNGUIUtils.DrawEmoji(BuildIconRect(sel, IconSlot.TopLeft), s_JunctionBadgeContent);
		}

		private static void DrawDataIncompleteWarning(Rect sel)
		{
			const float h = 20f;
			var iconRect = new Rect(sel.x + sel.width - h - 8f, sel.y - 2f, h, h);
			GUI.Label(iconRect, GetDataIsIncompleteWarning());
		}

		// ==================================================================
		//  Icon rect builder -- unified corner layout
		// ==================================================================
		private enum IconSlot { TopRight, BottomRight, BottomLeft, TopLeft }

		private static Rect BuildIconRect(Rect sel, IconSlot slot)
		{
			bool isList = sel.width > sel.height;

			if (isList) {
				// 列表视图：Emoji 在固定 14px 尺寸下显得偏大，缩小到 60%（≈ 8.4px）
				const float emojiSize = 14f * 0.6f;
				switch (slot) {
					case IconSlot.TopRight:
						return new Rect(sel.x + sel.width - sel.height * 2f, sel.y, emojiSize, emojiSize);
					case IconSlot.BottomRight:
						return new Rect(sel.x + sel.width - sel.height * 3f, sel.y, emojiSize, emojiSize);
					case IconSlot.BottomLeft:
						return new Rect(sel.x, sel.y + 7f, emojiSize, emojiSize);
					case IconSlot.TopLeft:
						return new Rect(sel.x, sel.y + 1f, emojiSize, emojiSize);
					default: return Rect.zero;
				}
			} else {
				float w      = Mathf.Max(18f, sel.width * 0.36f);
				float offset = sel.width - w;
				float rw     = Mathf.Max(14f, sel.width * 0.25f);
				float roff   = sel.width - rw;

				switch (slot) {
					case IconSlot.TopRight:
						return new Rect(sel.x + roff, sel.y - 2f, rw, rw);
					case IconSlot.BottomRight:
						return new Rect(sel.x + offset, sel.y + offset + 2f, rw, rw);
					case IconSlot.BottomLeft:
						return new Rect(sel.x, sel.y + offset + 1f, rw, rw);
					case IconSlot.TopLeft: {
						float jw = Mathf.Max(14f, sel.width * 0.22f);
						return new Rect(sel.x, sel.y, jw, jw);
					}
					default: return Rect.zero;
				}
			}
		}
	}
}
