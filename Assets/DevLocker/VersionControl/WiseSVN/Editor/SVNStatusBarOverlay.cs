// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

#if UNITY_2021_2_OR_NEWER

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Overlays;
using UnityEngine;
using UnityEngine.UIElements;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN
{
	/// <summary>
	/// SceneView overlay showing SVN branch name, pending local changes, and remote changes.
	/// Requires Unity 2021.2 or newer for the Overlay API.
	/// </summary>
	[Overlay(typeof(SceneView), "svn-status-bar", "SVN Status")]
	public class SVNStatusBarOverlay : Overlay
	{
		private string m_BranchName = string.Empty;
		private SVNAsyncOperation<string> m_BranchOp;

		public override VisualElement CreatePanelContent()
		{
			if (string.IsNullOrEmpty(m_BranchName)) {
				RefreshBranchName();
			}

			return new IMGUIContainer(DrawOverlayGUI);
		}

		private void DrawOverlayGUI()
		{
			var prefs = SVNPreferencesManager.Instance?.PersonalPrefs;
			if (prefs == null || !prefs.EnableCoreIntegration || !prefs.PopulateStatusesDatabase) {
				GUILayout.Label(Tr("overlay.svnstatus.disabled"));
				return;
			}

			int modifiedCount = 0;
			int remoteCount = 0;

			if (SVNStatusesDatabase.Initialized) {
				var statuses = SVNStatusesDatabase.Instance.GetAllKnownStatusData(true, false, false);
				foreach (var s in statuses) {
					if (s.Status != VCFileStatus.Normal
					    && s.Status != VCFileStatus.Excluded
					    && s.Status != VCFileStatus.Ignored
					    && s.Status != VCFileStatus.None) {
						modifiedCount++;
					}
					if (s.RemoteStatus != VCRemoteFileStatus.None) {
						remoteCount++;
					}
				}
			}

			bool downloadChanges =
				prefs.DownloadRepositoryChanges == SVNPreferencesManager.BoolPreference.Enabled ||
				(prefs.DownloadRepositoryChanges == SVNPreferencesManager.BoolPreference.SameAsProjectPreference
				 && SVNPreferencesManager.Instance.ProjectPrefs.DownloadRepositoryChanges);

			string branchDisplay = string.IsNullOrEmpty(m_BranchName) ? "?" : m_BranchName;
			string status = downloadChanges
				? $"[{branchDisplay}]  {Tr("overlay.svnstatus.modified")}: {modifiedCount}  {Tr("overlay.svnstatus.remote")}: {remoteCount}"
				: $"[{branchDisplay}]  {Tr("overlay.svnstatus.modified")}: {modifiedCount}";

			EditorGUILayout.BeginHorizontal();
			GUILayout.Label(status, EditorStyles.miniLabel);

			if (GUILayout.Button("…", EditorStyles.miniButton, GUILayout.Width(22f))) {
				var menu = new GenericMenu();
				menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.update_all")), false, SVNContextMenusManager.UpdateAll);
				menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.commit_all")), false, SVNContextMenusManager.CommitAll);
				menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.refresh")), false, () => {
					if (SVNStatusesDatabase.Initialized)
						SVNStatusesDatabase.Instance.InvalidateDatabase();
				});
				menu.AddSeparator(string.Empty);
				menu.AddItem(new GUIContent(Tr("overlay.svnstatus.menu.refresh_branch")), false, () => {
					m_BranchName = string.Empty;
					RefreshBranchName();
				});
				menu.ShowAsContext();
			}

			EditorGUILayout.EndHorizontal();
		}

		private void RefreshBranchName()
		{
			if (m_BranchOp != null) return;

			m_BranchOp = SVNAsyncOperation<string>.Start(_ => WiseSVNIntegration.GetWorkingCopyRootURL());
			m_BranchOp.Completed += op => {
				m_BranchOp = null;
				m_BranchName = ParseBranchFromURL(op.Result ?? string.Empty);
			};
		}

		private static string ParseBranchFromURL(string url)
		{
			if (string.IsNullOrEmpty(url)) return string.Empty;

			int idx = url.IndexOf("/branches/", StringComparison.OrdinalIgnoreCase);
			if (idx >= 0) {
				string after = url.Substring(idx + "/branches/".Length);
				int slash = after.IndexOf('/');
				return slash >= 0 ? after.Substring(0, slash) : after;
			}

			if (url.IndexOf("/trunk", StringComparison.OrdinalIgnoreCase) >= 0)
				return "trunk";

			idx = url.IndexOf("/tags/", StringComparison.OrdinalIgnoreCase);
			if (idx >= 0) {
				string after = url.Substring(idx + "/tags/".Length);
				int slash = after.IndexOf('/');
				return "tags/" + (slash >= 0 ? after.Substring(0, slash) : after);
			}

			// Fallback: last URL segment
			string trimmed = url.TrimEnd('/');
			int lastSlash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
			return lastSlash >= 0 ? trimmed.Substring(lastSlash + 1) : trimmed;
		}
	}
}

#endif
