// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

#if UNITY_2021_2_OR_NEWER

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEditor.Toolbars;
using UnityEngine;
using UnityEngine.UIElements;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN
{
	// ─────────────────────────────────────────────────────────────────────────
	// Shared state & drawing helpers used by all three display surfaces.
	// ─────────────────────────────────────────────────────────────────────────
	internal static class SVNStatusBadge
	{
		internal static string BranchName = string.Empty;
		internal static SVNAsyncOperation<string> BranchOp;

		// ── Adaptive badge colors ────────────────────────────────────────────
		private static readonly Color k_ColorClean    = new Color(0.18f, 0.46f, 0.28f, 1f); // green
		private static readonly Color k_ColorModified = new Color(0.70f, 0.45f, 0.05f, 1f); // amber
		private static readonly Color k_ColorRemote   = new Color(0.55f, 0.30f, 0.05f, 1f); // orange
		private static readonly Color k_ColorConflict = new Color(0.65f, 0.12f, 0.10f, 1f); // red
		private static readonly Color k_ColorOffline  = new Color(0.30f, 0.30f, 0.32f, 1f); // gray

		private static GUIStyle s_BadgeStyle;
		private static GUIStyle s_MenuBtnStyle;

		internal static GUIStyle BadgeStyle {
			get {
				if (s_BadgeStyle == null) {
					s_BadgeStyle = new GUIStyle(EditorStyles.miniLabel) {
						normal    = { textColor = Color.white, background = null },
						fontStyle = FontStyle.Bold,
						alignment = TextAnchor.MiddleCenter,
						padding   = new RectOffset(6, 6, 2, 2),
					};
				}
				return s_BadgeStyle;
			}
		}

		internal static GUIStyle MenuBtnStyle {
			get {
				if (s_MenuBtnStyle == null) {
					s_MenuBtnStyle = new GUIStyle(EditorStyles.miniButton) {
						normal    = { textColor = Color.white, background = null },
						hover     = { textColor = new Color(1f, 1f, 1f, 0.75f), background = null },
						fontStyle = FontStyle.Bold,
						padding   = new RectOffset(3, 3, 2, 2),
						fixedWidth = 18f,
					};
				}
				return s_MenuBtnStyle;
			}
		}

		internal static Color GetBadgeColor(int modified, int remote, bool conflict, bool offline)
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs != null && !prefs.AdaptiveSVNStatusColor) return prefs.SVNStatusBadgeColor;
			if (offline)        return k_ColorOffline;
			if (conflict)       return k_ColorConflict;
			if (remote > 0)     return k_ColorRemote;
			if (modified > 0)   return k_ColorModified;
			return k_ColorClean;
		}

		// Returns (modified, remote, hasConflict).
		internal static (int modified, int remote, bool conflict) CountStatuses()
		{
			if (!SVNStatusesDatabase.Instance.IsReady) return (0, 0, false);

			int modified = 0, remote = 0;
			bool conflict = false;
			foreach (var s in SVNStatusesDatabase.Instance.GetAllKnownStatusData(true, false, false)) {
				if (s.Status == VCFileStatus.Conflicted)  { conflict = true; modified++; continue; }
				if (s.Status != VCFileStatus.Normal
				 && s.Status != VCFileStatus.Excluded
				 && s.Status != VCFileStatus.Ignored
				 && s.Status != VCFileStatus.None)
					modified++;
				if (s.RemoteStatus != VCRemoteFileStatus.None) remote++;
			}
			return (modified, remote, conflict);
		}

		internal static bool DownloadChangesEnabled()
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs == null) return false;
			return prefs.DownloadRepositoryChanges == SVNPreferencesManager.BoolPreference.Enabled ||
				(prefs.DownloadRepositoryChanges == SVNPreferencesManager.BoolPreference.SameAsProjectPreference
				 && SVNPreferencesManager.Instance.ProjectPrefs.DownloadRepositoryChanges);
		}

		internal static void RefreshBranch()
		{
			if (BranchOp != null) return;
			BranchOp = SVNAsyncOperation<string>.Start(_ => WiseSVNIntegration.GetWorkingCopyRootURL());
			BranchOp.Completed += op => {
				BranchOp = null;
				BranchName = ParseBranchFromURL(op.Result ?? string.Empty);
				SVNTitleBarUpdater.RequestUpdate();
			};
		}

		internal static string ParseBranchFromURL(string url)
		{
			if (string.IsNullOrEmpty(url)) return string.Empty;
			int idx = url.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
			if (idx >= 0) {
				string after = url.Substring(idx + "/branches/".Length);
				int slash = after.IndexOf('/');
				return slash >= 0 ? after.Substring(0, slash) : after;
			}
			if (url.IndexOf("/trunk", StringComparison.OrdinalIgnoreCase) >= 0) return "trunk";
			idx = url.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
			if (idx >= 0) {
				string after = url.Substring(idx + "/tags/".Length);
				int slash = after.IndexOf('/');
				return "tags/" + (slash >= 0 ? after.Substring(0, slash) : after);
			}
			string trimmed = url.TrimEnd('/');
			int last = trimmed.LastIndexOfAny(new[] { '/', '\\' });
			return last >= 0 ? trimmed.Substring(last + 1) : trimmed;
		}

		// ── Shared IMGUI badge drawing ───────────────────────────────────────
		internal static void DrawBadgeGUI()
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs == null || !prefs.EnableCoreIntegration || !prefs.PopulateStatusesDatabase) {
				GUILayout.Label(Tr("overlay.svnstatus.disabled"), BadgeStyle);
				return;
			}

			bool offline = !SVNStatusesDatabase.Instance.IsReady;
			var (modified, remote, conflict) = CountStatuses();
			bool download = DownloadChangesEnabled();
			string branch = string.IsNullOrEmpty(BranchName) ? "?" : BranchName;
			string label  = download
				? $"[{branch}]  M:{modified}  R:{remote}"
				: $"[{branch}]  M:{modified}";
			Color bgColor = GetBadgeColor(modified, remote, conflict, offline);

			GUILayout.BeginHorizontal(GUILayout.ExpandWidth(false));

			// Badge label
			var lblContent = new GUIContent(label);
			var lblSize    = BadgeStyle.CalcSize(lblContent);
			var lblRect    = GUILayoutUtility.GetRect(lblSize.x, lblSize.y + 4f, BadgeStyle, GUILayout.ExpandWidth(false));
			if (Event.current.type == EventType.Repaint)
				EditorGUI.DrawRect(lblRect, bgColor);
			GUI.Label(lblRect, lblContent, BadgeStyle);

			// "…" menu button (slightly darker shade of same color)
			var btnContent = new GUIContent("…");
			var btnSize    = MenuBtnStyle.CalcSize(btnContent);
			var btnRect    = GUILayoutUtility.GetRect(btnSize.x, lblRect.height, MenuBtnStyle, GUILayout.ExpandWidth(false));
			Color btnColor = bgColor * new Color(0.85f, 0.85f, 0.85f, 1f);
			btnColor.a = 1f;
			if (Event.current.type == EventType.Repaint)
				EditorGUI.DrawRect(btnRect, btnColor);
			if (GUI.Button(btnRect, btnContent, MenuBtnStyle))
				ShowContextMenu();

			GUILayout.EndHorizontal();
		}

		internal static void ShowContextMenu()
		{
			var menu = new GenericMenu();
			menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.update_all")),    false, SVNContextMenusManager.UpdateAll);
			menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.commit_all")),    false, SVNContextMenusManager.CommitAll);
			menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.refresh")),       false, () => {
				if (SVNStatusesDatabase.Instance.IsReady) SVNStatusesDatabase.Instance.InvalidateDatabase();
			});
			menu.AddSeparator(string.Empty);
			menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.refresh_branch")), false, () => {
				BranchName = string.Empty;
				RefreshBranch();
			});
			menu.ShowAsContext();
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// 1. Floating SceneView Overlay panel
	// ─────────────────────────────────────────────────────────────────────────
	[Overlay(typeof(SceneView), "svn-status-bar", "SVN Status")]
	public class SVNStatusBarOverlay : Overlay
	{
		public override VisualElement CreatePanelContent()
		{
			if (string.IsNullOrEmpty(SVNStatusBadge.BranchName))
				SVNStatusBadge.RefreshBranch();

			SVNStatusesDatabase.Instance.DatabaseChanged     += Repaint;
			SVNPreferencesManager.Instance.PreferencesChanged += OnPrefsChanged;

			OnPrefsChanged();
			return new IMGUIContainer(SVNStatusBadge.DrawBadgeGUI);
		}

		private void OnPrefsChanged() =>
			displayed = SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusOverlay ?? true;
	}

	// ─────────────────────────────────────────────────────────────────────────
	// 2. SceneView Toolbar item
	// ─────────────────────────────────────────────────────────────────────────
	[Overlay(typeof(SceneView), "svn-status-toolbar", "SVN Status Bar")]
	public class SVNStatusToolbarOverlay : ToolbarOverlay
	{
		SVNStatusToolbarOverlay() : base(SVNStatusToolbarElement.id) { }
	}

	[EditorToolbarElement(SVNStatusToolbarElement.id, typeof(SceneView))]
	class SVNStatusToolbarElement : VisualElement
	{
		public const string id = "WiseSVN/StatusToolbar";

		public SVNStatusToolbarElement()
		{
			if (string.IsNullOrEmpty(SVNStatusBadge.BranchName))
				SVNStatusBadge.RefreshBranch();

			SVNStatusesDatabase.Instance.DatabaseChanged     += MarkDirtyRepaint;
			SVNPreferencesManager.Instance.PreferencesChanged += OnPrefsChanged;

			Add(new IMGUIContainer(SVNStatusBadge.DrawBadgeGUI) { style = { flexGrow = 0 } });
			style.flexDirection = FlexDirection.Row;
			style.alignItems    = Align.Center;

			OnPrefsChanged();
		}

		private void OnPrefsChanged() =>
			style.display = (SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusToolbar ?? true)
				? DisplayStyle.Flex : DisplayStyle.None;
	}

	// ─────────────────────────────────────────────────────────────────────────
	// 3. Windows title bar updater  (Win32 only, experimental)
	// ─────────────────────────────────────────────────────────────────────────
	[InitializeOnLoad]
	internal static class SVNTitleBarUpdater
	{
		private const string k_Prefix = "[SVN: ";
		private const string k_Suffix = " ] ";
		private static bool s_Pending = false;

		static SVNTitleBarUpdater()
		{
			SVNStatusesDatabase.Instance.DatabaseChanged     += () => s_Pending = true;
			SVNPreferencesManager.Instance.PreferencesChanged += OnPrefsChanged;
			EditorApplication.update += Tick;
		}

		internal static void RequestUpdate() => s_Pending = true;

		private static void OnPrefsChanged()
		{
			if (!(SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusTitleBar ?? false))
				WriteTitle(string.Empty);
			else
				s_Pending = true;
		}

		private static void Tick()
		{
			if (!s_Pending) return;
			s_Pending = false;
			if (!(SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusTitleBar ?? false)) return;

			var (mod, rem, _) = SVNStatusBadge.CountStatuses();
			bool dl = SVNStatusBadge.DownloadChangesEnabled();
			string badge = dl
				? $"{SVNStatusBadge.BranchName}  M:{mod}  R:{rem}"
				: $"{SVNStatusBadge.BranchName}  M:{mod}";
			WriteTitle(badge);
		}

#if UNITY_EDITOR_WIN
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern bool SetWindowText(IntPtr hWnd, string lpString);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int GetWindowTextLength(IntPtr hWnd);

		private static void WriteTitle(string svnBadge)
		{
			try {
				IntPtr hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
				if (hwnd == IntPtr.Zero) return;

				int len = GetWindowTextLength(hwnd) + 1;
				var sb = new System.Text.StringBuilder(len);
				GetWindowText(hwnd, sb, len);
				string cur = sb.ToString();

				// Strip previous SVN badge if present.
				int end = cur.IndexOf(k_Suffix, StringComparison.Ordinal);
				if (end >= 0 && cur.StartsWith(k_Prefix, StringComparison.Ordinal))
					cur = cur.Substring(end + k_Suffix.Length);

				SetWindowText(hwnd, string.IsNullOrEmpty(svnBadge) ? cur : $"{k_Prefix}{svnBadge}{k_Suffix}{cur}");
			} catch { /* non-critical display feature; ignore all exceptions */ }
		}
#else
		private static void WriteTitle(string _) { }
#endif
	}
}

#endif
