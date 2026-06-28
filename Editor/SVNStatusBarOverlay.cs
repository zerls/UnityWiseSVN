// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

#if UNITY_2021_2_OR_NEWER

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN
{
	// ─────────────────────────────────────────────────────────────────────────
	// Shared state & drawing helpers used by all display surfaces.
	// ─────────────────────────────────────────────────────────────────────────
	internal static class SVNStatusBadge
	{
		internal static string BranchName = string.Empty;
		internal static SVNAsyncOperation<string> BranchOp;

		// Raised whenever the data backing the tooltip changes (branch name resolves
		// or the statuses database refreshes). Toolbar injector subscribes to update
		// the UIToolkit tooltip string. SceneView label can ignore it.
		internal static event Action TooltipChanged;

		// Conflict-override and offline colors are still hard-coded —
		// branch-pattern colors are configurable via PersonalPreferences.BranchColorRules.
		private static readonly Color k_ColorConflict = new Color(0.65f, 0.12f, 0.10f, 1f); // red
		private static readonly Color k_ColorOffline  = new Color(0.30f, 0.30f, 0.32f, 1f); // gray

		private static GUIStyle s_BadgeStyle;

		// Visual chrome to match Unity's native main-toolbar dropdown buttons (Layers / Layout).
		internal const string DropdownIndicator = " ▾";    // U+25BE — appended to label as the arrow glyph
		internal const float ToolbarButtonHeight = 22f;     // matches Unity's main-toolbar button height

		internal static GUIStyle BadgeStyle {
			get {
				if (s_BadgeStyle == null) {
					// Inherit EditorStyles.toolbarButton's font, padding, and overall feel so the badge
					// blends into the main toolbar instead of looking like a smaller foreign element.
					// We strip the native background images so our flat colored fill can show through,
					// and force white text since the background can be any branch color.
					// The ▾ dropdown indicator is appended to the label manually (the native arrow
					// in toolbarDropDown is part of the background image we're removing).
					s_BadgeStyle = new GUIStyle(EditorStyles.toolbarButton) {
						normal      = { textColor = Color.white, background = null },
						hover       = { textColor = Color.white, background = null },
						active      = { textColor = Color.white, background = null },
						focused     = { textColor = Color.white, background = null },
						onNormal    = { textColor = Color.white, background = null },
						onHover     = { textColor = Color.white, background = null },
						onActive    = { textColor = Color.white, background = null },
						onFocused   = { textColor = Color.white, background = null },
						fontStyle   = FontStyle.Bold,
						alignment   = TextAnchor.MiddleCenter,
						fixedHeight = 0,                              // allow rect-driven sizing
						padding     = new RectOffset(8, 8, 0, 0),
					};
				}
				return s_BadgeStyle;
			}
		}

		// Larger variant of BadgeStyle for the SceneView label. Returns a fresh style each call
		// (font size varies with prefs; cheap to allocate per OnSceneGUI tick).
		internal static GUIStyle MakeSceneViewBadgeStyle(int fontSize)
		{
			return new GUIStyle(EditorStyles.boldLabel) {
				normal    = { textColor = Color.white, background = null },
				fontSize  = fontSize,
				alignment = TextAnchor.MiddleCenter,
				padding   = new RectOffset(10, 10, 4, 4),
			};
		}

		// Resolves a branch name to its configured color using regex rules.
		// Falls back to PersonalPreferences.DefaultBranchColor when nothing matches,
		// or to SVNStatusBadgeColor when AdaptiveSVNStatusColor is off.
		internal static Color ResolveBranchColor(string branch)
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs == null) return k_ColorOffline;
			if (!prefs.AdaptiveSVNStatusColor) return prefs.SVNStatusBadgeColor;

			if (!string.IsNullOrEmpty(branch) && prefs.BranchColorRules != null) {
				foreach (var rule in prefs.BranchColorRules) {
					if (rule == null || string.IsNullOrEmpty(rule.Pattern)) continue;
					try {
						if (Regex.IsMatch(branch, rule.Pattern, RegexOptions.IgnoreCase))
							return rule.Color;
					} catch (ArgumentException) {
						// Invalid regex — skip silently; user sees fallback color which signals "no match".
					}
				}
			}
			return prefs.DefaultBranchColor;
		}

		// Toolbar badge color: conflict and offline are safety overrides;
		// otherwise the branch-pattern color drives the background.
		internal static Color GetBadgeColor(int modified, int remote, bool conflict, bool offline)
		{
			if (offline)  return k_ColorOffline;
			if (conflict) return k_ColorConflict;
			return ResolveBranchColor(BranchName);
		}

		// Tooltip text used by the toolbar badge (UIToolkit tooltip on the parent VisualElement).
		internal static string BuildTooltip()
		{
			var (modified, remote, conflict) = CountStatuses();
			string branch    = string.IsNullOrEmpty(BranchName) ? "?" : BranchName;
			string yes       = Tr("common.yes");
			string no        = Tr("common.no");
			return string.Format(Tr("overlay.svnstatus.tooltip"),
				branch, modified, remote, conflict ? yes : no);
		}

		// Returns (modified, remote, hasConflict). Counts come from the active status provider.
		internal static (int modified, int remote, bool conflict) CountStatuses()
		{
			var provider = SVNPreferencesManager.Instance.StatusProvider;
			if (!provider.IsReady) return (0, 0, false);

			int modified = 0, remote = 0;
			bool conflict = false;
			foreach (var s in provider.EnumerateInteresting()) {
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
				TooltipChanged?.Invoke();
			};
		}

		// Raises TooltipChanged. Called externally when the statuses database refreshes.
		internal static void NotifyTooltipChanged() => TooltipChanged?.Invoke();

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
		// Compact display: branch glyph + branch name only. Counts and conflict
		// state are surfaced via the UIToolkit tooltip on the parent VisualElement.
		// The whole badge is clickable — opens the context menu on click.
		internal static void DrawBadgeGUI()
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs == null || !SVNPreferencesManager.Instance.IsIntegrationEnabled) {
				return;
			}
			if (!prefs.EnableCoreIntegration || !prefs.PopulateStatusesDatabase) {
				GUILayout.Label(Tr("overlay.svnstatus.disabled"), BadgeStyle);
				return;
			}

			bool offline = !SVNPreferencesManager.Instance.StatusProvider.IsReady;
			var (modified, remote, conflict) = CountStatuses();
			string branch = string.IsNullOrEmpty(BranchName) ? "?" : BranchName;
			// U+2387 ALTERNATIVE KEY SYMBOL (branch glyph) + name + manual ▾ to match native toolbar dropdowns.
			string label  = $"⎇ {branch}{DropdownIndicator}";
			Color bgColor = GetBadgeColor(modified, remote, conflict, offline);

			var content = new GUIContent(label);
			var size    = BadgeStyle.CalcSize(content);
			// Use toolbar-button height so the badge visually aligns with native toolbar buttons.
			var rect    = GUILayoutUtility.GetRect(size.x, ToolbarButtonHeight, BadgeStyle, GUILayout.ExpandWidth(false));

			if (Event.current.type == EventType.Repaint)
				EditorGUI.DrawRect(rect, bgColor);

			if (GUI.Button(rect, content, BadgeStyle))
				ShowContextMenu(rect);

			EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
		}

		// Opens a native-style popup window anchored below the badge — matches Unity's main-toolbar
		// dropdowns (Layers / Layout) rather than the generic right-click context menu.
		internal static void ShowContextMenu(Rect badgeRect)
		{
			var items = new List<SVNStatusBadgePopup.Item>();
			bool hasSelection = SVNContextMenusManager.HasSelectedAssets();

			// ── Conflict banner (P1-3) ────────────────────────────────────
			// When the working copy has conflicts, surface a one-click jump to "Check Changes" at the top.
			var (modified, _, conflict) = CountStatuses();
			if (conflict) {
				items.Add(new SVNStatusBadgePopup.Item {
					Label   = string.Format(Tr("overlay.svnstatus.menu.conflict_banner"), modified),
					OnClick = SVNContextMenusManager.CheckChangesAll,
					Enabled = true,
				});
				items.Add(SVNStatusBadgePopup.Item.Separator);
			}

			int selCount = hasSelection ? Selection.assetGUIDs.Length : 0;
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = hasSelection
					? string.Format(Tr("overlay.svnstatus.menu.update_selected_n"), selCount)
					: Tr("overlay.svnstatus.menu.update_selected_none"),
				OnClick = SVNContextMenusManager.UpdateSelected,
				Enabled = hasSelection,
			});
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = hasSelection
					? string.Format(Tr("overlay.svnstatus.menu.commit_selected_n"), selCount)
					: Tr("overlay.svnstatus.menu.commit_selected_none"),
				OnClick = SVNContextMenusManager.CommitSelected,
				Enabled = hasSelection,
			});
			items.Add(SVNStatusBadgePopup.Item.Separator);
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = Tr("overlay.svnstatus.menu.update_all"),
				OnClick = SVNContextMenusManager.UpdateAll,
				Enabled = true,
			});
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = Tr("overlay.svnstatus.menu.commit_all"),
				OnClick = SVNContextMenusManager.CommitAll,
				Enabled = true,
			});
			items.Add(SVNStatusBadgePopup.Item.Separator);
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = Tr("overlay.svnstatus.menu.refresh"),
				OnClick = () => SVNPreferencesManager.Instance.StatusProvider.InvalidateAll(),
				Enabled = true,
			});
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = Tr("overlay.svnstatus.menu.refresh_branch"),
				OnClick = () => { BranchName = string.Empty; RefreshBranch(); },
				Enabled = true,
			});
			// ── P2-3: extra quick-access entries ──────────────────────────
			items.Add(SVNStatusBadgePopup.Item.Separator);
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = "⚙ " + Tr("overlay.svnstatus.menu.preferences"),
				OnClick = SVNPreferencesWindow.ShowProjectPreferences,
				Enabled = true,
			});
			items.Add(new SVNStatusBadgePopup.Item {
				Label   = "☱ " + Tr("overlay.svnstatus.menu.show_log_all"),
				OnClick = SVNContextMenusManager.ShowLogAll,
				Enabled = true,
			});

			UnityEditor.PopupWindow.Show(badgeRect, new SVNStatusBadgePopup(items));
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Native-style dropdown popup that mimics Unity's main-toolbar menus
	// (Layers / Layout) — taller rows with hover highlight, thin separators,
	// disabled rows greyed out. Not a GenericMenu (those render with smaller
	// rows and a different aesthetic that's wrong for the main toolbar).
	// ─────────────────────────────────────────────────────────────────────────
	internal class SVNStatusBadgePopup : PopupWindowContent
	{
		internal struct Item
		{
			public string Label;
			public Action OnClick;
			public bool   Enabled;
			public bool   IsSeparator;

			public static Item Separator => new Item { IsSeparator = true };
		}

		private readonly List<Item> m_Items;
		private int m_HoverIndex = -1;

		// Sizing tuned to match Unity's native toolbar dropdowns.
		private const float k_RowHeight        = 22f;
		private const float k_SeparatorHeight  = 7f;
		private const float k_HorizontalPadding = 14f;
		private const float k_VerticalPadding   = 4f;
		private const float k_MinWidth          = 200f;
		private const float k_ExtraWidthSlack   = 28f;     // breathing room past the longest label

		private static readonly Color k_HoverColor    = new Color(0.24f, 0.49f, 0.91f, 1f);   // Unity selection blue
		private static readonly Color k_SeparatorColor = new Color(1f, 1f, 1f, 0.15f);

		private GUIStyle m_ItemStyle;
		private float m_Width;

		public SVNStatusBadgePopup(List<Item> items)
		{
			m_Items = items;
		}

		private GUIStyle ItemStyle {
			get {
				if (m_ItemStyle == null) {
					m_ItemStyle = new GUIStyle(EditorStyles.label) {
						alignment = TextAnchor.MiddleLeft,
						padding   = new RectOffset(0, 0, 0, 0),
						margin    = new RectOffset(0, 0, 0, 0),
					};
				}
				return m_ItemStyle;
			}
		}

		public override Vector2 GetWindowSize()
		{
			if (m_Width <= 0) {
				float maxLabel = 0f;
				foreach (var item in m_Items) {
					if (item.IsSeparator) continue;
					var size = ItemStyle.CalcSize(new GUIContent(item.Label));
					if (size.x > maxLabel) maxLabel = size.x;
				}
				m_Width = Mathf.Max(k_MinWidth, maxLabel + k_HorizontalPadding * 2 + k_ExtraWidthSlack);
			}

			float h = k_VerticalPadding * 2;
			foreach (var item in m_Items) {
				h += item.IsSeparator ? k_SeparatorHeight : k_RowHeight;
			}
			return new Vector2(m_Width, h);
		}

		public override void OnGUI(Rect rect)
		{
			var evt = Event.current;
			float y = k_VerticalPadding;
			int prevHover = m_HoverIndex;
			int hoverCandidate = -1;

			for (int i = 0; i < m_Items.Count; i++) {
				var item = m_Items[i];

				if (item.IsSeparator) {
					if (evt.type == EventType.Repaint) {
						var sepRect = new Rect(k_HorizontalPadding * 0.5f, y + 3f,
							rect.width - k_HorizontalPadding, 1f);
						EditorGUI.DrawRect(sepRect, k_SeparatorColor);
					}
					y += k_SeparatorHeight;
					continue;
				}

				var rowRect = new Rect(0, y, rect.width, k_RowHeight);
				bool hover = rowRect.Contains(evt.mousePosition) && item.Enabled;
				if (hover) hoverCandidate = i;

				if (evt.type == EventType.Repaint) {
					if (hover) EditorGUI.DrawRect(rowRect, k_HoverColor);

					var labelRect = new Rect(rowRect.x + k_HorizontalPadding, rowRect.y,
						rowRect.width - k_HorizontalPadding * 2, rowRect.height);
					var prevColor = GUI.color;
					if (!item.Enabled) GUI.color = new Color(1f, 1f, 1f, 0.4f);
					else if (hover)    GUI.color = Color.white;
					ItemStyle.Draw(labelRect, item.Label, false, false, false, false);
					GUI.color = prevColor;
				}

				if (evt.type == EventType.MouseDown && rowRect.Contains(evt.mousePosition) && item.Enabled) {
					var action = item.OnClick;
					editorWindow.Close();
					action?.Invoke();
					evt.Use();
					return;
				}

				y += k_RowHeight;
			}

			m_HoverIndex = hoverCandidate;
			if (m_HoverIndex != prevHover) editorWindow.Repaint();

			// Repaint on mouse move so hover highlight tracks.
			if (evt.type == EventType.MouseMove) editorWindow.Repaint();
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Main Toolbar badge — injected via reflection into Unity's top toolbar.
	// Works by locating ToolbarZoneRightAlign in the internal Toolbar VE tree.
	// ─────────────────────────────────────────────────────────────────────────
	[InitializeOnLoad]
	static class SVNMainToolbarInjector
	{
		static readonly Type k_ToolbarType =
			typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");

		static VisualElement s_BadgeRoot;
		static IMGUIContainer s_ImguiContainer;
		static int s_RetryCount;
		const int k_MaxRetries = 60;   // up to ~60 frames of delayCall retries

		static SVNMainToolbarInjector()
		{
			SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += OnDatabaseChanged;
			SVNPreferencesManager.Instance.StatusProviderChanged          += OnProviderUpgraded;
			SVNPreferencesManager.Instance.PreferencesChanged             += OnPrefsChanged;
			SVNStatusBadge.TooltipChanged                                 += RefreshTooltip;
			// Even in TSVNCache mode, the CLI database still scans in the background and is
			// the only source for some fields (out-of-date, lock owner). Listen to it directly
			// so the toolbar badge / tooltip refresh when CLI lands new data.
			SVNStatusesDatabase.Instance.DatabaseChanged -= OnDatabaseChanged;
			SVNStatusesDatabase.Instance.DatabaseChanged += OnDatabaseChanged;
			EditorApplication.delayCall += TryInject;
		}

		// Re-subscribe after CLI → TSVNCache upgrade.
		static void OnProviderUpgraded()
		{
			SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += OnDatabaseChanged;
			OnDatabaseChanged();
		}

		static void OnDatabaseChanged()
		{
			s_ImguiContainer?.MarkDirtyRepaint();
			SVNStatusBadge.NotifyTooltipChanged();
		}

		static void RefreshTooltip()
		{
			if (s_BadgeRoot != null) s_BadgeRoot.tooltip = SVNStatusBadge.BuildTooltip();
		}

		static void TryInject()
		{
			// If already injected and still attached to a panel, nothing to do.
			if (s_BadgeRoot?.panel != null) { ApplyVisibility(); return; }

			if (s_RetryCount++ > k_MaxRetries) {
				Debug.LogWarning("[WiseSVN] Failed to inject SVN status badge into main toolbar after many retries. The internal Unity Toolbar layout may have changed.");
				return;
			}

			var toolbar = Resources.FindObjectsOfTypeAll(k_ToolbarType)
				.OfType<ScriptableObject>().FirstOrDefault();
			if (toolbar == null) { EditorApplication.delayCall += TryInject; return; }

			var root = k_ToolbarType
				.GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance)
				?.GetValue(toolbar) as VisualElement;
			if (root == null) { EditorApplication.delayCall += TryInject; return; }

			var zone = root.Q("ToolbarZoneRightAlign");
			if (zone == null) { EditorApplication.delayCall += TryInject; return; }

			if (string.IsNullOrEmpty(SVNStatusBadge.BranchName))
				SVNStatusBadge.RefreshBranch();

			s_ImguiContainer = new IMGUIContainer(SVNStatusBadge.DrawBadgeGUI) {
				style = {
					flexGrow   = 0,
					flexShrink = 0,
					height     = 22,
					minWidth   = 60,
					alignSelf  = Align.Center,
				}
			};

			s_BadgeRoot = new VisualElement {
				style = {
					flexDirection = FlexDirection.Row,
					alignItems    = Align.Center,
					flexGrow      = 0,
					flexShrink    = 0,
					marginLeft    = 4,
					marginRight   = 4,
					height        = 22,
				}
			};
			s_BadgeRoot.Add(s_ImguiContainer);

			zone.Insert(0, s_BadgeRoot);
			RefreshTooltip();
			ApplyVisibility();
		}

		static void OnPrefsChanged() => ApplyVisibility();

		static void ApplyVisibility()
		{
			if (s_BadgeRoot == null) return;
			bool enabled = SVNPreferencesManager.Instance?.IsIntegrationEnabled ?? false;
			bool show    = enabled && (SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusToolbar ?? true);
			s_BadgeRoot.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
		}
	}

	// ─────────────────────────────────────────────────────────────────────────
	// Windows title bar — appends " [SVN: branch]" at the end of the title.
	// ─────────────────────────────────────────────────────────────────────────
	[InitializeOnLoad]
	internal static class SVNTitleBarUpdater
	{
		private const string k_Suffix    = " [";
		private const string k_SuffixEnd = "]";
		private static bool s_Pending = false;

		static SVNTitleBarUpdater()
		{
			SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += () => s_Pending = true;
			SVNPreferencesManager.Instance.StatusProviderChanged          += () => {
				SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += () => s_Pending = true;
			};
			SVNPreferencesManager.Instance.PreferencesChanged += OnPrefsChanged;
			EditorApplication.update += Tick;

			// Trigger an initial update so the title-bar appears on startup without
			// relying on a later database/prefs event to fire.
			EditorApplication.delayCall += () => {
				if (SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusTitleBar ?? false) {
					if (string.IsNullOrEmpty(SVNStatusBadge.BranchName))
						SVNStatusBadge.RefreshBranch();
					s_Pending = true;
				}
			};
		}

		internal static void RequestUpdate() => s_Pending = true;

		private static void OnPrefsChanged()
		{
			bool enabled = SVNPreferencesManager.Instance?.IsIntegrationEnabled ?? false
				&& (SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusTitleBar ?? false);
			if (!enabled) {
				WriteTitle(string.Empty);
				return;
			}
			// Title bar can be enabled even if the main-toolbar badge is hidden, so
			// kick off a branch fetch here too — otherwise the title may stay empty.
			if (string.IsNullOrEmpty(SVNStatusBadge.BranchName))
				SVNStatusBadge.RefreshBranch();
			s_Pending = true;
		}

		private static void Tick()
		{
			if (!s_Pending) return;
			s_Pending = false;
			if (!(SVNPreferencesManager.Instance?.IsIntegrationEnabled ?? false)) return;
			if (!(SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusTitleBar ?? false)) return;
			WriteTitle(SVNStatusBadge.BranchName);
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

				// Strip previous SVN suffix if present.
				int start = cur.LastIndexOf(k_Suffix, StringComparison.Ordinal);
				if (start >= 0 && cur.EndsWith(k_SuffixEnd, StringComparison.Ordinal))
					cur = cur.Substring(0, start);

				SetWindowText(hwnd, string.IsNullOrEmpty(svnBadge) ? cur : $"{cur}{k_Suffix}{svnBadge}{k_SuffixEnd}");
			} catch { /* non-critical display feature; ignore all exceptions */ }
		}
#else
		private static void WriteTitle(string _) { }
#endif
	}

	// ─────────────────────────────────────────────────────────────────────────
	// SceneView branch label — large semi-transparent branch name in the
	// bottom-left of every SceneView. Color follows the same branch-pattern
	// rules as the toolbar badge. Conflict state does NOT recolor this label
	// (it's a passive marker; the toolbar badge handles the safety alert).
	// ─────────────────────────────────────────────────────────────────────────
	[InitializeOnLoad]
	internal static class SVNSceneViewBranchLabel
	{
		static SVNSceneViewBranchLabel()
		{
			SVNPreferencesManager.Instance.PreferencesChanged += OnPrefsChanged;
			EditorApplication.delayCall += OnPrefsChanged;
		}

		private static void OnPrefsChanged()
		{
			SceneView.duringSceneGui -= OnSceneGUI;
			bool enabled = SVNPreferencesManager.Instance?.IsIntegrationEnabled ?? false
				&& (SVNPreferencesManager.Instance?.PersonalPrefs.ShowSVNStatusSceneView ?? false);
			if (enabled)
				SceneView.duringSceneGui += OnSceneGUI;

			foreach (var sv in SceneView.sceneViews)
				(sv as SceneView)?.Repaint();
		}

		private static void OnSceneGUI(SceneView sv)
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs == null) return;

			string name = SVNStatusBadge.BranchName;
			if (string.IsNullOrEmpty(name)) {
				SVNStatusBadge.RefreshBranch();
				return;
			}

			Handles.BeginGUI();

			// Same visual language as the toolbar badge: colored background + ⎇ prefix + white bold text.
			// Alpha applies to the background only — text stays opaque white for readability.
			Color bg = SVNStatusBadge.ResolveBranchColor(name);
			bg.a = Mathf.Clamp01(prefs.SceneViewBranchAlpha);

			int fontSize = Mathf.Clamp(prefs.SceneViewBranchFontSize, 8, 64);
			var style = SVNStatusBadge.MakeSceneViewBadgeStyle(fontSize);

			string label   = $"⎇ {name}";
			var   content  = new GUIContent(label);
			Vector2 size   = style.CalcSize(content);

			float bottomMargin = 28f; // clear of the SceneView's own bottom info bar
			var rect = new Rect(14f, sv.position.height - size.y - bottomMargin, size.x, size.y);

			EditorGUI.DrawRect(rect, bg);
			GUI.Label(rect, content, style);

			Handles.EndGUI();
		}
	}
}

#endif
