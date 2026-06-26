// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN.LockPrompting
{
	/// <summary>
	/// Popup that prompts user should it force-lock changed assets.
	/// </summary>
	public class SVNLockPromptWindow : EditorWindow
	{
		[Serializable]
		private class LockEntryData
		{
			public SVNStatusData StatusData;
			public bool IsMeta = false;

#pragma warning disable CA2235 // Field is a member of Serializable but is not of such type. Unity will handle this.
			public UnityEngine.Object TargetObject;
#pragma warning restore CA2235

			public bool ShouldLock = true;

			public string AssetName => System.IO.Path.GetFileName(StatusData.Path);
			public VCLockStatus LockStatus => StatusData.LockStatus;
			public string Owner => StatusData.LockDetails.Owner;

			public bool LockedByOther => LockStatus == VCLockStatus.LockedOther
			                             || LockStatus == VCLockStatus.LockedButStolen;

			public LockEntryData() { }

			public LockEntryData(SVNStatusData statusData)
			{
				var assetPath = statusData.Path;
				if (statusData.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) {
					assetPath = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
					IsMeta = true;
				}

				StatusData = statusData;
				TargetObject = AssetDatabase.LoadMainAssetAtPath(assetPath);

				// Can't lock if there is newer version at the repository or locked by others.
				ShouldLock = statusData.RemoteStatus == VCRemoteFileStatus.None && !LockedByOther;
			}
		}

		private bool m_Initialized = false;

		private bool m_WhatAreLocksHintShown = false;
		private bool m_WhatIsForceLocksHintShown = false;

		private SVNPreferencesManager.PersonalPreferences m_PersonalPrefs => SVNPreferencesManager.Instance.PersonalPrefs;
		private SVNPreferencesManager.ProjectPreferences m_ProjectPrefs => SVNPreferencesManager.Instance.ProjectPrefs;

		private bool m_AllowStealingLocks = false;
		private List<LockEntryData> m_LockEntries = new List<LockEntryData>();
		private Vector2 m_LockEntriesScroll;

		private GUIContent m_RevertContent;
		private GUIContent m_DiffContent;
		private GUIStyle MiniIconButtonlessStyle;

		public static void PromptLock(IEnumerable<SVNStatusData> shouldLockEntries, IEnumerable<SVNStatusData> lockedByOtherEntries)
		{
			if (SVNPreferencesManager.Instance.TemporarySilenceLockPrompts)
				return;

			if (SVNPreferencesManager.Instance.PersonalPrefs.AutoLockOnModified) {
				SVNLockPromptDatabase.Instance.LockEntries(shouldLockEntries, false);

				string notificationMessage = Tr("lockprompt.auto_locking_notification", shouldLockEntries.Count());

				if (focusedWindow && !(focusedWindow is SceneView)) {
					focusedWindow.ShowNotification(new GUIContent(notificationMessage));
				}

				foreach(SceneView sceneView in SceneView.sceneViews) {
					sceneView.ShowNotification(new GUIContent(notificationMessage));
				}

				shouldLockEntries = new List<SVNStatusData>();

				if (!lockedByOtherEntries.Any()) {
					return;
				}
			}

			var window = GetWindow<SVNLockPromptWindow>(true, Tr("lockprompt.window.title"));
			window.minSize = new Vector2(600f, 500f);
			var center = new Vector2(Screen.currentResolution.width, Screen.currentResolution.height) / 2f;
			window.position = new Rect(center - window.position.size / 2, window.position.size);
			window.AppendEntriesToLock(lockedByOtherEntries);
			window.AppendEntriesToLock(shouldLockEntries);

		}

		private void AppendEntriesToLock(IEnumerable<SVNStatusData> entries)
		{
			var toAdd = entries
				.Where(sd => m_LockEntries.All(e => e.StatusData.Path != sd.Path))
				.Select(sd => new LockEntryData(sd))
				;

			m_LockEntries.AddRange(toAdd);
		}

		void OnEnable()
		{
			// Resets on assembly reload.
			wantsMouseMove = true;  // Needed for the hover effects.
			LocalizationManager.OnLanguageChanged -= OnLanguageChangedHandler;
			LocalizationManager.OnLanguageChanged += OnLanguageChangedHandler;
		}

		private void OnDisable()
		{
			LocalizationManager.OnLanguageChanged -= OnLanguageChangedHandler;
		}

		private void OnLanguageChangedHandler()
		{
			titleContent = new GUIContent(Tr("lockprompt.window.title"));
			m_Initialized = false;
			Repaint();
		}

		private void InitializeStyles()
		{
			m_RevertContent = SVNPreferencesManager.LoadTexture("BranchesIcons/SVN-Revert", Tr("lockprompt.revert_asset"));
			m_DiffContent = SVNPreferencesManager.LoadTexture("BranchesIcons/SVN-ConflictsScan-Pending", Tr("lockprompt.check_changes"));

			// Copied from SVNBranchSelectorWindow.
			MiniIconButtonlessStyle = new GUIStyle(GUI.skin.button);
			MiniIconButtonlessStyle.hover.background = MiniIconButtonlessStyle.normal.background;
			MiniIconButtonlessStyle.hover.scaledBackgrounds = MiniIconButtonlessStyle.normal.scaledBackgrounds;
			MiniIconButtonlessStyle.hover.textColor = GUI.skin.label.hover.textColor;
			MiniIconButtonlessStyle.normal.background = null;
			MiniIconButtonlessStyle.normal.scaledBackgrounds = null;
			MiniIconButtonlessStyle.padding = new RectOffset();
			MiniIconButtonlessStyle.margin = new RectOffset();

			SVNPreferencesWindow.MigrateButtonStyleToUIElementsIfNeeded(MiniIconButtonlessStyle);
		}

		void OnGUI()
		{
			if (!m_Initialized) {
				InitializeStyles();

				m_Initialized = true;
			}

			// For hover effects to work.
			if (Event.current.type == EventType.MouseMove) {
				Repaint();
			}

			EditorGUILayout.BeginHorizontal();

			EditorGUILayout.LabelField(Tr("lockprompt.header"), EditorStyles.boldLabel);

			GUILayout.FlexibleSpace();

			string silenceMenuText = SVNOverlayIcons.InvalidateDatabaseMenuText.Replace("&&", "&");
			string silenceTooltip = Tr("lockprompt.silence.tooltip", silenceMenuText);
			var silenceContent = new GUIContent(Tr("lockprompt.silence"), silenceTooltip);
			if (GUILayout.Button(silenceContent, EditorStyles.toolbarButton)) {
				if (EditorUtility.DisplayDialog(
					Tr("lockprompt.silence.confirm_title"),
					Tr("lockprompt.silence.confirm_msg", silenceTooltip),
					Tr("common.yes"), Tr("common.no"))) {
					SVNPreferencesManager.Instance.TemporarySilenceLockPrompts = true;
					SVNLockPromptDatabase.Instance.ClearKnowledge();
					Close();
				}
			}

			EditorGUILayout.EndHorizontal();

			m_WhatAreLocksHintShown = EditorGUILayout.Foldout(m_WhatAreLocksHintShown, Tr("lockprompt.what_are_locks"));
			if (m_WhatAreLocksHintShown) {
				EditorGUILayout.HelpBox(Tr("lockprompt.what_are_locks.help"), MessageType.Info, true);
				EditorGUILayout.Space();
			}

			m_AllowStealingLocks = EditorGUILayout.Toggle(Tr("lockprompt.steal_locks"), m_AllowStealingLocks);

			if (m_AllowStealingLocks) {
				m_WhatIsForceLocksHintShown = EditorGUILayout.Foldout(m_WhatIsForceLocksHintShown, Tr("lockprompt.what_is_steal"));
				if (m_WhatIsForceLocksHintShown) {
					EditorGUILayout.HelpBox(Tr("lockprompt.what_is_steal.help"), MessageType.Info, true);
				}
			}

			bool autoLock = EditorGUILayout.Toggle(new GUIContent(Tr("lockprompt.auto_lock"), SVNPreferencesManager.PersonalPreferences.AutoLockOnModifiedHint + Tr("lockprompt.auto_lock.tooltip_extra")), m_PersonalPrefs.AutoLockOnModified);
			if (m_PersonalPrefs.AutoLockOnModified != autoLock) {
				m_PersonalPrefs.AutoLockOnModified = autoLock;
				SVNPreferencesManager.Instance.SavePreferences(m_PersonalPrefs, m_ProjectPrefs);
			}

			EditorGUILayout.HelpBox(
				Tr("lockprompt.skip_help", SVNOverlayIcons.InvalidateDatabaseMenuText.Replace("&&", "&")),
				MessageType.Warning, true);

			const float LockColumnSize = 34;
			const float OwnerSize = 140f;

			#if UNITY_2019_4_OR_NEWER
			const float RevertSize = 20f;
			#else
			const float RevertSize = 18f;
			#endif

			bool needsUpdate = false;

			EditorGUILayout.BeginHorizontal();

			GUILayout.Label(Tr("lockprompt.col.lock"), EditorStyles.boldLabel, GUILayout.Width(LockColumnSize));
			GUILayout.Label(Tr("lockprompt.col.asset"), EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
			GUILayout.Label(Tr("lockprompt.col.revert"), EditorStyles.boldLabel, GUILayout.Width(RevertSize * 2 + 12f));
			GUILayout.Label(Tr("lockprompt.col.owner"), EditorStyles.boldLabel, GUILayout.Width(OwnerSize));

			EditorGUILayout.EndHorizontal();

			if (m_LockEntries.Count == 0) {
				GUILayout.Label(Tr("lockprompt.scanning"));
			}

			m_LockEntriesScroll = EditorGUILayout.BeginScrollView(m_LockEntriesScroll);

			bool hasSelected = false;

			foreach (var lockEntry in m_LockEntries) {

				SVNStatusData statusData = lockEntry.StatusData;

				EditorGUILayout.BeginHorizontal();

				bool shouldDisableRow = statusData.RemoteStatus != VCRemoteFileStatus.None;
				if (!m_AllowStealingLocks) {
					shouldDisableRow = shouldDisableRow || lockEntry.LockedByOther;
				}

				// NOTE: This is copy-pasted below.
				EditorGUI.BeginDisabledGroup(shouldDisableRow);

				const float LockCheckBoxWidth = 14;
				GUILayout.Space(LockColumnSize - LockCheckBoxWidth);
				lockEntry.ShouldLock = EditorGUILayout.Toggle(lockEntry.ShouldLock, GUILayout.Width(LockCheckBoxWidth)) && !shouldDisableRow;

				hasSelected |= lockEntry.ShouldLock;

				// NOTE: This is copy-pasted below.
				EditorGUI.BeginDisabledGroup(!lockEntry.ShouldLock);

				if (lockEntry.TargetObject == null || lockEntry.IsMeta) {
					var assetComment = (statusData.Status == VCFileStatus.Deleted) ? Tr("lockprompt.deleted") : Tr("lockprompt.meta");
					EditorGUILayout.BeginHorizontal();
					EditorGUILayout.TextField($"({assetComment}) {lockEntry.AssetName}", GUILayout.ExpandWidth(true));

					if (statusData.IsMovedFile) {
						UnityEngine.Object movedToObject = AssetDatabase.LoadMainAssetAtPath(statusData.MovedTo);

						GUILayout.Label(new GUIContent("=>", Tr("lockprompt.moved_to_tooltip")), GUILayout.ExpandWidth(false));
						if (movedToObject) {
							EditorGUILayout.ObjectField(movedToObject, movedToObject.GetType(), false, GUILayout.MaxWidth(100f));
						} else {
							EditorGUILayout.TextField(statusData.MovedTo, GUILayout.MaxWidth(100f));
						}
					}
					EditorGUILayout.EndHorizontal();
				}

				// Marked for deletion file can still exist on disk. In that case - show it.
				if (statusData.Status != VCFileStatus.Deleted || lockEntry.TargetObject) {
					if (lockEntry.IsMeta) {
						EditorGUILayout.ObjectField(lockEntry.TargetObject,
							lockEntry.TargetObject ? lockEntry.TargetObject.GetType() : typeof(UnityEngine.Object),
							false, GUILayout.MaxWidth(100f));
					} else {
						EditorGUILayout.ObjectField(lockEntry.TargetObject,
							lockEntry.TargetObject ? lockEntry.TargetObject.GetType() : typeof(UnityEngine.Object),
							false, GUILayout.ExpandWidth(true));
					}
				}

				EditorGUI.EndDisabledGroup();

				EditorGUI.EndDisabledGroup();

				if (GUILayout.Button(m_RevertContent, MiniIconButtonlessStyle, GUILayout.Width(RevertSize), GUILayout.Height(RevertSize))) {
					if (statusData.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && (statusData.Status == VCFileStatus.Added || statusData.Status == VCFileStatus.Deleted)) {
						if (!EditorUtility.DisplayDialog(Tr("lockprompt.revert_meta.title"), Tr("lockprompt.revert_meta.msg"), Tr("lockprompt.revert_meta.confirm"), Tr("common.cancel"))) {
							GUIUtility.ExitGUI();
						}
					}

					using (var reporter = WiseSVNIntegration.CreateReporter()) {

						if (statusData.Status == VCFileStatus.Deleted
							&& !string.IsNullOrEmpty(statusData.MovedTo)
							&& !statusData.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) {

							int choice = EditorUtility.DisplayDialogComplex(
								Tr("lockprompt.revert_moved.title"),
								Tr("lockprompt.revert_moved.msg", statusData.MovedTo),
								Tr("lockprompt.revert_moved.move_back"), Tr("common.cancel"), Tr("lockprompt.revert_moved.revert_deleted")
								);

							if (choice == 0) {
								string error = AssetDatabase.ValidateMoveAsset(statusData.MovedTo, statusData.Path);
								if (!string.IsNullOrEmpty(error)) {
									EditorUtility.DisplayDialog(Tr("lockprompt.revert_error.title"), Tr("lockprompt.revert_error.msg", error), Tr("common.ok"));
									GUIUtility.ExitGUI();
								}
								AssetDatabase.MoveAsset(statusData.MovedTo, statusData.Path);

								m_LockEntries.Remove(lockEntry);
								m_LockEntries.RemoveAll(e => e.StatusData.Path == statusData.Path + ".meta");

								GUIUtility.ExitGUI();
							}

							if (choice == 1) {
								GUIUtility.ExitGUI();
							}
						}
						WiseSVNIntegration.Revert(new string[] { statusData.Path }, false, true, false, "", -1, reporter);
					}

					AssetDatabase.Refresh();
					//SVNStatusesDatabase.Instance.InvalidateDatabase();	// Change will trigger this automatically.

					m_LockEntries.Remove(lockEntry);

					if (m_LockEntries.Count == 0) {
						Close();
					}

					GUIUtility.ExitGUI();
				}

				GUILayout.Space(4f);

				MiniIconButtonlessStyle.contentOffset = new Vector2(0f, -2f);
				if (GUILayout.Button(m_DiffContent, MiniIconButtonlessStyle, GUILayout.Width(RevertSize), GUILayout.Height(RevertSize))) {
					if (!string.IsNullOrEmpty(statusData.MovedTo)) {
						SVNContextMenusManager.DiffAsset(statusData.MovedTo);
					} else {
						SVNContextMenusManager.DiffAsset(statusData.Path);
					}
				}
				MiniIconButtonlessStyle.contentOffset = new Vector2(0f, 0f);

				GUILayout.Space(4f);


				EditorGUI.BeginDisabledGroup(shouldDisableRow);

				EditorGUI.BeginDisabledGroup(!lockEntry.ShouldLock);

				if (statusData.RemoteStatus == VCRemoteFileStatus.None) {
					if (lockEntry.LockedByOther) {
						EditorGUILayout.TextField(lockEntry.Owner, GUILayout.Width(OwnerSize));
					} else {
						EditorGUILayout.LabelField("", GUILayout.Width(OwnerSize));
					}
				} else {
					Color prevColor = GUI.color;
					GUI.color = Color.yellow;

					EditorGUILayout.LabelField(new GUIContent(Tr("lockprompt.out_of_date"), Tr("lockprompt.out_of_date.tooltip")), GUILayout.Width(OwnerSize));
					needsUpdate = true;

					GUI.color = prevColor;
				}

				EditorGUI.EndDisabledGroup();

				EditorGUI.EndDisabledGroup();

				EditorGUILayout.EndHorizontal();
			}

			EditorGUILayout.EndScrollView();



			EditorGUILayout.BeginHorizontal();

			if (GUILayout.Button(Tr("lockprompt.btn.toggle_selected"))) {
				foreach(var lockEntry in m_LockEntries) {

					bool lockConflict = lockEntry.StatusData.RemoteStatus != VCRemoteFileStatus.None;
					if (!m_AllowStealingLocks) {
						lockConflict = lockConflict || lockEntry.LockedByOther;
					}

					lockEntry.ShouldLock = !lockEntry.ShouldLock && !lockConflict;
				}
			}

			if (GUILayout.Button(Tr("lockprompt.btn.refresh_all"))) {
				SVNStatusesDatabase.Instance.InvalidateDatabase();
				SVNLockPromptDatabase.Instance.ClearKnowledge();
				m_LockEntries.Clear();
			}

			GUILayout.FlexibleSpace();

			var prevBackgroundColor = GUI.backgroundColor;

			GUI.backgroundColor = Color.yellow;
			if (needsUpdate && GUILayout.Button(Tr("lockprompt.btn.update_all"))) {
				SVNContextMenusManager.UpdateAll();
				SVNLockPromptDatabase.Instance.ClearKnowledge();
				Close();
			}

			GUI.backgroundColor = prevBackgroundColor;

			if (GUILayout.Button(Tr("lockprompt.btn.revert_all_window"))) {
				SVNContextMenusManager.RevertAll();
				AssetDatabase.Refresh();
				//SVNStatusesDatabase.Instance.InvalidateDatabase();	// Change will trigger this automatically.
				SVNLockPromptDatabase.Instance.ClearKnowledge();
				Close();
			}

			if (GUILayout.Button(Tr("lockprompt.btn.skip_all"))) {
				Close();
			}

			EditorGUI.BeginDisabledGroup(!hasSelected);

			GUI.backgroundColor = m_AllowStealingLocks ? Color.red : Color.green;
			var lockSelectedButtonText = m_AllowStealingLocks ? Tr("lockprompt.btn.lock_or_steal_selected") : Tr("lockprompt.btn.lock_selected");

			if (GUILayout.Button(lockSelectedButtonText)) {
				var selectedStatusData = m_LockEntries
					.Where(e => e.ShouldLock)
					.Select(e => e.StatusData)
					.ToList();

				if (selectedStatusData.Any()) {
					SVNLockPromptDatabase.Instance.LockEntries(selectedStatusData, m_AllowStealingLocks);
				}
				Close();
			}
			GUI.backgroundColor = prevBackgroundColor;

			EditorGUI.EndDisabledGroup();

			EditorGUILayout.EndHorizontal();
		}

	}
}
