// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.Preferences;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Providers
{
	/// <summary>
	/// Diagnostic window for the status provider system.
	/// Shows which provider is active, what the probe reported, and live status for the current Project selection —
	/// useful for confirming the icons match what the data source actually returns.
	/// </summary>
	internal class StatusProviderInfoWindow : EditorWindow
	{
		[MenuItem("Assets/SVN/Debug/Status Provider Info", false, ContextMenus.SVNContextMenusManager.MenuItemPriorityStart + 950)]
		public static void Open()
		{
			GetWindow<StatusProviderInfoWindow>("WiseSVN Status Source").Show();
		}

		private Vector2 m_Scroll;

		private void OnEnable()
		{
			EditorApplication.update += Repaint;
		}

		private void OnDisable()
		{
			EditorApplication.update -= Repaint;
		}

		private void OnGUI()
		{
			var provider = SVNPreferencesManager.Instance.StatusProvider;
			var probeMsg = SVNPreferencesManager.Instance.StatusProviderProbeMessage;

			EditorGUILayout.LabelField("Active source", EditorStyles.boldLabel);
			using (new EditorGUI.IndentLevelScope()) {
				EditorGUILayout.LabelField("Provider:", provider.DisplayName);
				EditorGUILayout.LabelField("IsReady:", provider.IsReady ? "yes" : "no");
				EditorGUILayout.LabelField("DataIsIncomplete:", provider.DataIsIncomplete ? "yes (sanity limits hit)" : "no");
				if (!string.IsNullOrEmpty(probeMsg))
					EditorGUILayout.LabelField("Probe message:", probeMsg);

#if UNITY_EDITOR_WIN
				if (provider is TSVNCacheStatusProvider tsvn) {
					EditorGUILayout.LabelField("Last IPC latency:", $"{tsvn.LastQueryLatencyTicks / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0:F2} ms");
					EditorGUILayout.LabelField("Errors since start:", tsvn.LastQueryErrors.ToString());
				}
#endif
			}

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Current Project selection", EditorStyles.boldLabel);
			using (new EditorGUI.IndentLevelScope())
			using (var scope = new EditorGUILayout.ScrollViewScope(m_Scroll)) {
				m_Scroll = scope.scrollPosition;

				var selection = Selection.assetGUIDs;
				if (selection == null || selection.Length == 0) {
					EditorGUILayout.HelpBox("Select an asset in the Project window to see its provider-reported status.", MessageType.Info);
				} else {
					foreach (var guid in selection) {
						string path = AssetDatabase.GUIDToAssetPath(guid);
						if (string.IsNullOrEmpty(path)) continue;

						EditorGUILayout.LabelField(path, EditorStyles.miniBoldLabel);
						var status = provider.GetStatus(path);
						using (new EditorGUI.IndentLevelScope()) {
							EditorGUILayout.LabelField("Status:",        status.Status.ToString());
							EditorGUILayout.LabelField("Properties:",    status.PropertiesStatus.ToString());
							EditorGUILayout.LabelField("Lock:",          status.LockStatus.ToString());
							EditorGUILayout.LabelField("Remote:",        status.RemoteStatus.ToString());
							EditorGUILayout.LabelField("Tree conflict:", status.TreeConflictStatus.ToString());
							EditorGUILayout.LabelField("Switched/Ext:",  status.SwitchedExternalStatus.ToString());
							EditorGUILayout.LabelField("Path reported:", string.IsNullOrEmpty(status.Path) ? "(empty — not in cache)" : status.Path);
						}
						EditorGUILayout.Space(4);
					}
				}
			}

			EditorGUILayout.Space(8);
			using (new EditorGUILayout.HorizontalScope()) {
				if (GUILayout.Button("Force refresh")) {
					provider.InvalidateAll();
				}
				if (GUILayout.Button("Invalidate selected")) {
					foreach (var guid in Selection.assetGUIDs) {
						string path = AssetDatabase.GUIDToAssetPath(guid);
						if (!string.IsNullOrEmpty(path)) provider.InvalidatePath(path);
					}
				}
			}
		}
	}
}
