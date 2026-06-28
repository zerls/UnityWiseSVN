// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using System;
using System.Collections.Generic;
using System.IO;
using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Preferences;
using DevLocker.VersionControl.WiseSVN.Utils;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN
{
	/// <summary>
	/// Renders scene or prefab overlay indicating that the asset is locked or out of date.
	/// </summary>
	class SVNLockedOverlay : EditorPersistentSingleton<SVNLockedOverlay>
	{
		[InitializeOnLoad]
		class SVNLockedOverlayStarter
		{
			// HACK: If this was the SVNLockPromptDatabase itself it causes exceptions on assembly reload.
			//		 The static constructor gets called during reload because the instance exists.
			static SVNLockedOverlayStarter()
			{
				Instance.PreferencesChanged();
			}
		}

		[SerializeField]
		private string m_SceneMessage;
		[SerializeField]
		private float m_SceneMessageWidth;
		[SerializeField]
		private GUIContent m_SceneMessageIcon;

		[SerializeField]
		private string m_PrefabMessage;
		[SerializeField]
		private float m_PrefabMessageWidth;
		[SerializeField]
		private GUIContent m_PrefabMessageIcon;



		[SerializeField]
		private List<Scene> m_CurrentScenes = new List<Scene>();

		[SerializeField]
		private string m_CurrentPrefabPath = string.Empty;

		[NonSerialized]
		private GUIStyle m_MessageStyle;

		[SerializeField]
		private bool m_UserClosedOverlay = false;

		[NonSerialized]
		private bool m_DatabaseChanged = false;

		private const float CloseButtonSize = 18f;
		private const float CloseButtonPadding = 6f;
		private const float IconVerticalAdjust = -4f;

		private struct OverlayData
		{
			public string Message;
			public float MessageWidth;
			public GUIContent Icon;
			public Rect MessageRect;
			public Rect CloseRect;
			public Rect IconRect;
		}

		private SVNPreferencesManager.PersonalPreferences m_PersonalPrefs => SVNPreferencesManager.Instance.PersonalPrefs;

		private bool IsActive => m_PersonalPrefs.EnableCoreIntegration
		                         && m_PersonalPrefs.PopulateStatusesDatabase
		                         && SVNPreferencesManager.Instance.DownloadRepositoryChanges
		                         && !SVNPreferencesManager.Instance.NeedsToAuthenticate
								 && m_PersonalPrefs.WarnForPotentialConflicts;

		public override void Initialize(bool freshlyCreated)
		{
			SVNPreferencesManager.Instance.PreferencesChanged += PreferencesChanged;
			SVNStatusesDatabase.Instance.DatabaseChanged += OnDatabaseChanged;
		}

		public void ClearCache()
		{
			m_CurrentScenes.Clear();
			m_CurrentPrefabPath = string.Empty;
		}

		private GUIStyle GetMessageStyle()
		{
			if (m_MessageStyle == null) {
				m_MessageStyle = new GUIStyle(GUI.skin.box);
				m_MessageStyle.alignment = TextAnchor.MiddleCenter;
				m_MessageStyle.normal.textColor = Color.white;
				m_MessageStyle.active.textColor = Color.white;
				m_MessageStyle.focused.textColor = Color.white;
				m_MessageStyle.hover.textColor = Color.white;
				m_MessageStyle.contentOffset = new Vector2(0f, -2f);
			}

			return m_MessageStyle;
		}

		private void PreferencesChanged()
		{
			if (IsActive) {
#if UNITY_2019_1_OR_NEWER
				SceneView.duringSceneGui -= SceneViewOnGUI;
				SceneView.duringSceneGui += SceneViewOnGUI;
#else
				SceneView.onSceneGUIDelegate -= SceneViewOnGUI;
				SceneView.onSceneGUIDelegate += SceneViewOnGUI;
#endif
			} else {
#if UNITY_2019_1_OR_NEWER
				SceneView.duringSceneGui -= SceneViewOnGUI;
#else
				SceneView.onSceneGUIDelegate -= SceneViewOnGUI;
#endif
			}

			OnDatabaseChanged();
		}

		private void OnDatabaseChanged()
		{
			m_DatabaseChanged = true;
			EditorApplication.RepaintProjectWindow();
		}

		private void CheckScenes()
		{
			if (m_CurrentScenes.Count != SceneManager.sceneCount) {
				RefreshScenesMessage();
				return;
			}

			for (int i = 0; i < SceneManager.sceneCount; ++i) {
				if (m_CurrentScenes[i].handle != SceneManager.GetSceneAt(i).handle) {
					RefreshScenesMessage();
					return;
				}
			}
		}

		private void RefreshScenesMessage()
		{
			m_CurrentScenes.Clear();
			m_SceneMessage = string.Empty;

			for (int i = 0; i < SceneManager.sceneCount; ++i) {
				Scene scene = SceneManager.GetSceneAt(i);

				m_CurrentScenes.Add(scene);

				if (string.IsNullOrEmpty(scene.path))
					continue;

				var guid = AssetDatabase.AssetPathToGUID(scene.path);
				var statusData = SVNStatusesDatabase.Instance.GetKnownStatusData(guid);

				if (statusData.RemoteStatus != VCRemoteFileStatus.None) {
					m_SceneMessage += Tr("sceneview.scene_outofdate", scene.name) + "\n";
					// Remote-out-of-date: use a neutral info icon (no per-status texture available since
					// GetRemoteStatusIconContent was removed; the remote emoji is only suitable for the
					// small Project-window overlay slot, not for SceneView banners).
					m_SceneMessageIcon = EditorGUIUtility.IconContent("console.infoicon");

				} else if (statusData.LockStatus == VCLockStatus.LockedOther || statusData.LockStatus == VCLockStatus.LockedButStolen) {
					m_SceneMessage += Tr("sceneview.scene_locked", scene.name, statusData.LockDetails.Owner) + "\n";
					m_SceneMessageIcon = SVNPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.LockedOther);

				} else if (statusData.LockStatus == VCLockStatus.BrokenLock) {
					m_SceneMessage += Tr("sceneview.scene_lockbroken", scene.name) + "\n";
					m_SceneMessageIcon = SVNPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.BrokenLock);
				}

			}

			m_SceneMessage = m_SceneMessage.TrimEnd('\n');

			m_SceneMessageWidth = GetMessageStyle().CalcSize(new GUIContent(m_SceneMessage)).x;

			m_UserClosedOverlay = false;
		}

		private void CheckPrefab()
		{
			string prefabPath = GetOpenedPrefabPath();

			bool prefabIsOpen = !string.IsNullOrEmpty(prefabPath);
			bool prefabWasOpen = !string.IsNullOrEmpty(m_CurrentPrefabPath);

			if (prefabWasOpen != prefabIsOpen) {
				RefreshPrefabMessage(prefabPath);
				return;
			}

			if (prefabIsOpen && m_CurrentPrefabPath != prefabPath) {
				RefreshPrefabMessage(prefabPath);
				return;
			}
		}

		private string GetOpenedPrefabPath()
		{
#if UNITY_2021_3_OR_NEWER
			var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
#else
			var stage = UnityEditor.Experimental.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
#endif

#if UNITY_2020_1_OR_NEWER
			return stage?.assetPath ?? string.Empty;
#else
			return stage?.prefabAssetPath ?? string.Empty;
#endif
		}

		private void RefreshPrefabMessage(string prefabPath)
		{
			m_CurrentPrefabPath = prefabPath;
			m_PrefabMessage = String.Empty;

			if (!string.IsNullOrEmpty(m_CurrentPrefabPath)) {
				var guid = AssetDatabase.AssetPathToGUID(m_CurrentPrefabPath);
				var statusData = SVNStatusesDatabase.Instance.GetKnownStatusData(guid);

				if (statusData.RemoteStatus != VCRemoteFileStatus.None) {
					m_PrefabMessage = Tr("sceneview.prefab_outofdate", Path.GetFileNameWithoutExtension(prefabPath));
					m_PrefabMessageIcon = EditorGUIUtility.IconContent("console.infoicon");

				} else if (statusData.LockStatus == VCLockStatus.LockedOther || statusData.LockStatus == VCLockStatus.LockedButStolen) {
					m_PrefabMessage = Tr("sceneview.prefab_locked", Path.GetFileNameWithoutExtension(prefabPath), statusData.LockDetails.Owner);
					m_PrefabMessageIcon = SVNPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.LockedOther);

				} else if (statusData.LockStatus == VCLockStatus.BrokenLock) {
					m_PrefabMessage = Tr("sceneview.prefab_lockbroken", Path.GetFileNameWithoutExtension(prefabPath));
					m_PrefabMessageIcon = SVNPreferencesManager.Instance.GetLockStatusIconContent(VCLockStatus.BrokenLock);
				}

			}

			m_PrefabMessageWidth = GetMessageStyle().CalcSize(new GUIContent(m_PrefabMessage)).x;

			m_UserClosedOverlay = false;
		}

		private void BuildOverlayRects(float targetWidth, float sceneViewWidth, out Rect messageRect, out Rect closeRect, out Rect iconRect)
		{
			const float height = 70f;
			float width = Mathf.Max(300, targetWidth + 40f);

			messageRect = new Rect();
			messageRect.x = sceneViewWidth / 2f - width / 2f;
			messageRect.y = 32;
			messageRect.width = width;
			messageRect.height = height;

			closeRect = new Rect();
			closeRect.x = messageRect.x + messageRect.width - CloseButtonSize + CloseButtonPadding;
			closeRect.y = messageRect.y - CloseButtonPadding;
			closeRect.width = closeRect.height = CloseButtonSize;

			iconRect = new Rect();
			iconRect.width = iconRect.height = 40f;
			iconRect.x = messageRect.x + messageRect.width / 2f - iconRect.width / 2f;
			iconRect.y = messageRect.y + messageRect.height - iconRect.height / 2f + IconVerticalAdjust;
		}

		private OverlayData? GetOverlayMessage()
		{
			CheckScenes();
			CheckPrefab();

			if (m_DatabaseChanged) {
				if (!m_UserClosedOverlay) {
					RefreshScenesMessage();
					RefreshPrefabMessage(GetOpenedPrefabPath());
				}
				m_DatabaseChanged = false;
			}

			bool hasMessage = (!string.IsNullOrEmpty(m_SceneMessage) && string.IsNullOrEmpty(m_CurrentPrefabPath)) || !string.IsNullOrEmpty(m_PrefabMessage);

			if (m_UserClosedOverlay || !hasMessage)
				return null;

			OverlayData data = new OverlayData();

			if (!string.IsNullOrEmpty(m_PrefabMessage)) {
				data.Message = m_PrefabMessage;
				data.MessageWidth = m_PrefabMessageWidth;
				data.Icon = m_PrefabMessageIcon;
			} else {
				data.Message = m_SceneMessage;
				data.MessageWidth = m_SceneMessageWidth;
				data.Icon = m_SceneMessageIcon;
			}

			return data;
		}

		private void DrawOverlayPanel(Rect messageRect, Rect closeRect, Rect iconRect, string message, GUIContent icon)
		{
			var prevBackgroundColor = GUI.backgroundColor;
			GUI.backgroundColor = Color.red;

			GUI.Box(messageRect, message, GetMessageStyle());
			GUI.Label(iconRect, icon);

			var prevColor = GUI.color;
			GUI.color = Color.white;

			if (GUI.Button(closeRect, "X")) {
				m_UserClosedOverlay = true;
			}

			GUI.color = prevColor;
			GUI.backgroundColor = prevBackgroundColor;
		}

		private void SceneViewOnGUI(SceneView sceneView)
		{
			if (Application.isPlaying || !SVNStatusesDatabase.Instance.IsReady)
				return;

			Handles.BeginGUI();

			var overlay = GetOverlayMessage();

			if (overlay.HasValue) {
				BuildOverlayRects(overlay.Value.MessageWidth, sceneView.position.width,
					out Rect messageRect, out Rect closeRect, out Rect iconRect);

				DrawOverlayPanel(messageRect, closeRect, iconRect, overlay.Value.Message, overlay.Value.Icon);
			}

			Handles.EndGUI();
		}
	}
}
