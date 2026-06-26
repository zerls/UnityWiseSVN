// MIT License Copyright(c) 2026 WiseSVN Ignore Manager Contributors

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN.Ignore
{
	/// <summary>
	/// Editor window for browsing and editing svn:ignore patterns across the working copy.
	///
	/// Reads patterns via WiseSVNIntegration.Propget; writes via Propset/Propdel.
	/// Uses Unity's built-in editor icons for a modern, theme-aware look.
	/// </summary>
	public class SVNIgnoreManagerWindow : EditorWindow
	{
		private const string LocalProp = "svn:ignore";
		private const string GlobalProp = "svn:global-ignores";

		// In-memory model of patterns per directory.
		private class DirEntry
		{
			public string RelativePath;     // e.g. "Assets/Scenes"
			public string AbsolutePath;     // native OS path used for SVN commands
			public List<string> LocalPatterns = new List<string>();
			public List<string> GlobalPatterns = new List<string>(); // read-only here
			public bool Dirty;
		}

		private readonly List<DirEntry> m_Dirs = new List<DirEntry>();
		private int m_SelectedDir = -1;
		private Vector2 m_LeftScroll;
		private Vector2 m_RightScroll;
		private string m_NewPatternBuffer = string.Empty;
		private bool m_IsScanning = false;
		private string m_StatusMessage = string.Empty;

		// MenuItem attribute is registered on SVNContextMenusManager.ShowIgnoreManager (keeps menu-priority constants centralized).
		public static void Open()
		{
			var w = GetWindow<SVNIgnoreManagerWindow>(true, Tr("ignoremgr.window.title"));
			w.minSize = new Vector2(640f, 400f);
			w.ShowUtility();
		}

		private void OnEnable()
		{
			LocalizationManager.OnLanguageChanged += OnLangChanged;
			titleContent = new GUIContent(Tr("ignoremgr.window.title"));
			RefreshDirectoryList();
		}

		private void OnDisable()
		{
			LocalizationManager.OnLanguageChanged -= OnLangChanged;
		}

		private void OnLangChanged()
		{
			titleContent = new GUIContent(Tr("ignoremgr.window.title"));
			Repaint();
		}

		// ---------- Toolbar ----------

		private void OnGUI()
		{
			DrawToolbar();
			EditorGUILayout.BeginHorizontal();
			DrawLeftPanel();
			DrawRightPanel();
			EditorGUILayout.EndHorizontal();
			DrawStatusBar();
		}

		private void DrawToolbar()
		{
			EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

			var refreshIcon = EditorGUIUtility.IconContent("d_Refresh");
			var refreshContent = new GUIContent(Tr("ignoremgr.toolbar.refresh"), refreshIcon.image, Tr("ignoremgr.toolbar.refresh.tooltip"));
			if (GUILayout.Button(refreshContent, EditorStyles.toolbarButton, GUILayout.MaxWidth(100f))) {
				RefreshDirectoryList();
			}

			bool anyDirty = m_Dirs.Any(d => d.Dirty);
			using (new EditorGUI.DisabledScope(!anyDirty)) {
				var applyContent = new GUIContent(Tr("ignoremgr.toolbar.apply"),
					EditorGUIUtility.IconContent("SaveAs").image,
					Tr("ignoremgr.toolbar.apply.tooltip"));
				if (GUILayout.Button(applyContent, EditorStyles.toolbarButton, GUILayout.MaxWidth(140f))) {
					ApplyAllChanges();
				}
			}

			GUILayout.FlexibleSpace();

			if (m_SelectedDir >= 0 && m_SelectedDir < m_Dirs.Count) {
				GUILayout.Label(Tr("ignoremgr.toolbar.path") + " " + m_Dirs[m_SelectedDir].RelativePath, EditorStyles.toolbarTextField);
			}

			EditorGUILayout.EndHorizontal();
		}

		// ---------- Left panel: directory list ----------

		private void DrawLeftPanel()
		{
			EditorGUILayout.BeginVertical(GUILayout.Width(240f));

			EditorGUILayout.LabelField(Tr("ignoremgr.left_header"), EditorStyles.boldLabel);

			m_LeftScroll = EditorGUILayout.BeginScrollView(m_LeftScroll);

			if (m_Dirs.Count == 0 && !m_IsScanning) {
				EditorGUILayout.HelpBox(Tr("ignoremgr.empty_left"), MessageType.Info);
			}

			for (int i = 0; i < m_Dirs.Count; i++) {
				var d = m_Dirs[i];
				string label = d.RelativePath + (d.Dirty ? " *" : string.Empty);
				bool selected = (i == m_SelectedDir);
				var style = selected ? EditorStyles.boldLabel : EditorStyles.label;
				if (GUILayout.Button(new GUIContent(label, d.AbsolutePath), style)) {
					m_SelectedDir = i;
				}
			}

			EditorGUILayout.EndScrollView();

			// Add-from-selection button.
			using (new EditorGUILayout.HorizontalScope()) {
				string selectedAsset = Selection.assetGUIDs != null && Selection.assetGUIDs.Length > 0
					? AssetDatabase.GUIDToAssetPath(Selection.assetGUIDs[0])
					: null;
				string targetDir = ResolveTargetDirectory(selectedAsset);
				bool canAdd = !string.IsNullOrEmpty(targetDir) && !m_Dirs.Any(d => d.RelativePath == targetDir);

				using (new EditorGUI.DisabledScope(!canAdd)) {
					var plusContent = new GUIContent("+ " + (targetDir ?? Tr("common.add")), Tr("ignoremgr.add_directory"));
					if (GUILayout.Button(plusContent)) {
						AddDirectoryToManage(targetDir);
					}
				}
			}

			EditorGUILayout.EndVertical();
		}

		private static string ResolveTargetDirectory(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return null;
			if (AssetDatabase.IsValidFolder(assetPath)) return assetPath;
			return Path.GetDirectoryName(assetPath)?.Replace('\\', '/');
		}

		// ---------- Right panel: pattern editor ----------

		private void DrawRightPanel()
		{
			EditorGUILayout.BeginVertical();
			m_RightScroll = EditorGUILayout.BeginScrollView(m_RightScroll);

			if (m_SelectedDir < 0 || m_SelectedDir >= m_Dirs.Count) {
				EditorGUILayout.HelpBox(Tr("ignoremgr.right_no_dir"), MessageType.None);
				EditorGUILayout.EndScrollView();
				EditorGUILayout.EndVertical();
				return;
			}

			var d = m_Dirs[m_SelectedDir];

			// svn:ignore section (editable)
			EditorGUILayout.LabelField(Tr("ignoremgr.section.local"), EditorStyles.boldLabel);

			if (d.LocalPatterns.Count == 0) {
				EditorGUILayout.LabelField("(empty)", EditorStyles.miniLabel);
			} else {
				for (int i = 0; i < d.LocalPatterns.Count; i++) {
					EditorGUILayout.BeginHorizontal();
					string newVal = EditorGUILayout.TextField(d.LocalPatterns[i]);
					if (newVal != d.LocalPatterns[i]) {
						d.LocalPatterns[i] = newVal;
						d.Dirty = true;
					}
					var minus = EditorGUIUtility.IconContent("d_Toolbar Minus");
					minus.tooltip = Tr("ignoremgr.remove_pattern_tooltip");
					if (GUILayout.Button(minus, GUILayout.Width(28f), GUILayout.Height(18f))) {
						d.LocalPatterns.RemoveAt(i);
						d.Dirty = true;
						GUIUtility.ExitGUI();
					}
					EditorGUILayout.EndHorizontal();
				}
			}

			EditorGUILayout.BeginHorizontal();
			m_NewPatternBuffer = EditorGUILayout.TextField(m_NewPatternBuffer);
			using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(m_NewPatternBuffer))) {
				if (GUILayout.Button(new GUIContent(Tr("ignoremgr.add_pattern_btn"), EditorGUIUtility.IconContent("d_Toolbar Plus").image), GUILayout.MaxWidth(80f))) {
					string p = m_NewPatternBuffer.Trim();
					if (!string.IsNullOrEmpty(p) && !d.LocalPatterns.Contains(p)) {
						d.LocalPatterns.Add(p);
						d.Dirty = true;
					}
					m_NewPatternBuffer = string.Empty;
					GUI.FocusControl(null);
				}
			}
			EditorGUILayout.EndHorizontal();
			EditorGUILayout.LabelField(Tr("ignoremgr.add_pattern_placeholder"), EditorStyles.miniLabel);

			EditorGUILayout.Space();

			// svn:global-ignores section (read-only)
			EditorGUILayout.LabelField(Tr("ignoremgr.section.global"), EditorStyles.boldLabel);
			if (d.GlobalPatterns.Count == 0) {
				EditorGUILayout.LabelField("(none)", EditorStyles.miniLabel);
			} else {
				using (new EditorGUI.DisabledScope(true)) {
					foreach (string p in d.GlobalPatterns) {
						EditorGUILayout.TextField(p);
					}
				}
			}
			EditorGUILayout.HelpBox(Tr("ignoremgr.global_readonly_note"), MessageType.Info);

			EditorGUILayout.EndScrollView();
			EditorGUILayout.EndVertical();
		}

		private void DrawStatusBar()
		{
			int totalPatterns = m_Dirs.Sum(d => d.LocalPatterns.Count);
			GUILayout.FlexibleSpace();
			EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
			GUILayout.Label(string.Format(Tr("ignoremgr.status.summary"), totalPatterns, m_Dirs.Count));
			GUILayout.FlexibleSpace();
			if (!string.IsNullOrEmpty(m_StatusMessage)) {
				GUILayout.Label(m_StatusMessage);
			}
			EditorGUILayout.EndHorizontal();
		}

		// ---------- Data operations ----------

		private void RefreshDirectoryList()
		{
			m_IsScanning = true;
			m_StatusMessage = Tr("common.scanning");
			m_Dirs.Clear();

			try {
				// Use SVNStatusesDatabase already-collected paths to seed; if empty, fall back to scanning
				// just "Assets" + "Packages" via Propget on the project root recursively.
				var collected = new HashSet<string>();

				// Recursive propget on project root for svn:ignore — fast and authoritative.
				var localEntries = new List<PropgetEntry>();
				WiseSVNIntegration.Propget(WiseSVNIntegration.ProjectRootNative, LocalProp, true, localEntries, WiseSVNIntegration.COMMAND_TIMEOUT, null);

				foreach (var e in localEntries) {
					if (string.IsNullOrEmpty(e.Value)) continue;
					AddOrUpdateDir(e.Path, e.Value, isGlobal: false, collected);
				}

				// Recursive propget for svn:global-ignores — populate read-only sections.
				var globalEntries = new List<PropgetEntry>();
				WiseSVNIntegration.Propget(WiseSVNIntegration.ProjectRootNative, GlobalProp, true, globalEntries, WiseSVNIntegration.COMMAND_TIMEOUT, null);

				foreach (var e in globalEntries) {
					if (string.IsNullOrEmpty(e.Value)) continue;
					AddOrUpdateDir(e.Path, e.Value, isGlobal: true, collected);
				}

				m_StatusMessage = string.Empty;
			} catch (Exception ex) {
				m_StatusMessage = "Refresh failed: " + ex.Message;
				Debug.LogException(ex);
			} finally {
				m_IsScanning = false;
				if (m_SelectedDir >= m_Dirs.Count) m_SelectedDir = -1;
				Repaint();
			}
		}

		private void AddOrUpdateDir(string absolutePath, string rawValue, bool isGlobal, HashSet<string> visited)
		{
			string nativeRoot = WiseSVNIntegration.ProjectRootNative;
			string rel = absolutePath;
			if (!string.IsNullOrEmpty(nativeRoot) && rel.StartsWith(nativeRoot)) {
				rel = rel.Substring(nativeRoot.Length).TrimStart('/', '\\');
			}
			rel = rel.Replace('\\', '/');
			if (string.IsNullOrEmpty(rel)) rel = ".";

			var lines = rawValue
				.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(l => l.Trim())
				.Where(l => l.Length > 0)
				.ToList();

			var existing = m_Dirs.FirstOrDefault(d => d.RelativePath == rel);
			if (existing == null) {
				existing = new DirEntry {
					RelativePath = rel,
					AbsolutePath = absolutePath,
				};
				m_Dirs.Add(existing);
			}

			if (isGlobal) {
				existing.GlobalPatterns = lines;
			} else {
				existing.LocalPatterns = lines;
			}
		}

		private void AddDirectoryToManage(string relativePath)
		{
			if (string.IsNullOrEmpty(relativePath)) return;
			if (m_Dirs.Any(d => d.RelativePath == relativePath)) return;

			string nativeRoot = WiseSVNIntegration.ProjectRootNative;
			string abs = Path.Combine(nativeRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

			var entry = new DirEntry {
				RelativePath = relativePath,
				AbsolutePath = abs,
			};

			// Load existing svn:ignore for this dir so users see what's there.
			var local = new List<PropgetEntry>();
			WiseSVNIntegration.Propget(abs, LocalProp, false, local, WiseSVNIntegration.COMMAND_TIMEOUT, null);
			if (local.Count > 0 && !string.IsNullOrEmpty(local[0].Value)) {
				entry.LocalPatterns = local[0].Value
					.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
					.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
			}

			m_Dirs.Add(entry);
			m_SelectedDir = m_Dirs.Count - 1;
		}

		private void ApplyAllChanges()
		{
			int success = 0;
			int failed = 0;

			using (var reporter = WiseSVNIntegration.CreateReporter()) {
				foreach (var d in m_Dirs) {
					if (!d.Dirty) continue;

					PropOperationResult result;
					if (d.LocalPatterns.Count == 0) {
						result = WiseSVNIntegration.Propdel(d.AbsolutePath, LocalProp, false, WiseSVNIntegration.COMMAND_TIMEOUT, reporter);
					} else {
						string newValue = string.Join("\n", d.LocalPatterns);
						result = WiseSVNIntegration.Propset(d.AbsolutePath, LocalProp, newValue, false, WiseSVNIntegration.COMMAND_TIMEOUT, reporter);
					}

					if (result == PropOperationResult.Success) {
						d.Dirty = false;
						success++;
					} else {
						failed++;
					}
				}
			}

			if (failed == 0 && success > 0) {
				m_StatusMessage = Tr("ignoremgr.apply_success");
				SVNStatusesDatabase.Instance.m_GlobalIgnoresCollected = false;
				SVNStatusesDatabase.Instance.InvalidateDatabase();
			} else if (failed > 0) {
				m_StatusMessage = Tr("ignoremgr.apply_failed");
			}

			Repaint();
		}
	}
}
