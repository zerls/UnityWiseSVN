// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

#if UNITY_2020_2_OR_NEWER || UNITY_2019_4_OR_NEWER || (UNITY_2018_4_OR_NEWER && !UNITY_2018_4_19 && !UNITY_2018_4_18 && !UNITY_2018_4_17 && !UNITY_2018_4_16 && !UNITY_2018_4_15)
#define CAN_DISABLE_REFRESH
#endif

using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Shell;
using UnityEngine;
using UnityEditor;
using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN.ContextMenus.Implementation
{
	/// <summary>
	/// Window to display SVN command that is about to be executed. User can tweak it.
	/// </summary>
	public class CLIContextWindow : EditorWindow
	{
		private bool m_AutoRun;
		private string m_CommandArgs;
		private Vector2 m_CommandArgsScroll;

		private string m_CombinedOutput = "";
		private string m_StateLabel = "Idle";
		private Color m_StateColor = Color.white;
		private Vector2 m_OutputScroll;

		private bool m_IsWorking => m_SVNOperation != null && !m_SVNOperation.HasFinished;

		private SVNAsyncOperation<ShellUtils.ShellResult> m_SVNOperation;

		public static void Show(string commandArgs, bool autoRun)
		{
			var window = CreateInstance<CLIContextWindow>();

			window.position = new Rect(window.position.xMin + 100f, window.position.yMin + 100f, 700f, 600f);
			window.minSize = new Vector2(700f, 400f);
			window.titleContent = new GUIContent(Tr("cli.window.title"));

			window.m_CommandArgs = commandArgs;
			window.m_AutoRun = autoRun;
			window.ShowUtility();
		}

		void OnEnable()
		{
#if CAN_DISABLE_REFRESH
			AssetDatabase.DisallowAutoRefresh();
#endif
			LocalizationManager.OnLanguageChanged -= OnLanguageChanged;
			LocalizationManager.OnLanguageChanged += OnLanguageChanged;
		}

		void OnDisable()
		{
			LocalizationManager.OnLanguageChanged -= OnLanguageChanged;
#if CAN_DISABLE_REFRESH
			AssetDatabase.AllowAutoRefresh();
			AssetDatabase.Refresh();
#endif
		}

		void OnDestroy()
		{
			if (m_IsWorking) {
				m_SVNOperation.Abort(true);
			}
		}

		private void OnLanguageChanged()
		{
			titleContent = new GUIContent(Tr("cli.window.title"));
			Repaint();
		}

		void OnGUI()
		{
			var textAreaStyle = new GUIStyle(EditorStyles.textArea);
			textAreaStyle.wordWrap = false;
			Color prevColor = GUI.color;

			EditorGUI.BeginDisabledGroup(true);
			{
				EditorGUILayout.TextField(Tr("cli.command"), "svn");
			}
			EditorGUI.EndDisabledGroup();

			EditorGUI.BeginDisabledGroup(m_IsWorking);
			{
				var textSize = textAreaStyle.CalcSize(new GUIContent(m_CommandArgs));
				m_CommandArgsScroll = EditorGUILayout.BeginScrollView(m_CommandArgsScroll, GUILayout.Height(100f));
				m_CommandArgs = EditorGUILayout.TextArea(m_CommandArgs, textAreaStyle, GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true), GUILayout.MinWidth(textSize.x), GUILayout.MinHeight(textSize.y));
				EditorGUILayout.EndScrollView();
			}
			EditorGUI.EndDisabledGroup();


			EditorGUILayout.LabelField(Tr("cli.output"));
			m_OutputScroll = EditorGUILayout.BeginScrollView(m_OutputScroll);
			EditorGUILayout.TextArea(m_CombinedOutput, textAreaStyle, GUILayout.ExpandHeight(true));
			EditorGUILayout.EndScrollView();




			EditorGUILayout.BeginHorizontal();
			{
				EditorGUI.BeginDisabledGroup(!m_IsWorking);

				if (GUILayout.Button(Tr("cli.abort"))) {
					m_SVNOperation.Abort(false);
					m_CombinedOutput += Tr("common.aborting") + "\n";
					m_StateLabel = Tr("common.aborting");
					m_StateColor = Color.red;
					GUI.FocusControl("");
				}

				if (GUILayout.Button(Tr("cli.kill"))) {
					m_SVNOperation.Abort(true);
					m_CombinedOutput += Tr("cli.killing") + "\n";
					m_StateLabel = Tr("cli.killing");
					m_StateColor = Color.red;
					GUI.FocusControl("");
				}

				EditorGUI.EndDisabledGroup();

				GUILayout.Space(8f);

				GUI.color = m_StateColor;
				EditorGUILayout.LabelField(m_StateLabel, GUILayout.ExpandWidth(false));
				GUI.color = prevColor;

				GUILayout.FlexibleSpace();

				GUI.color = Color.yellow;
				GUILayout.Label(Tr("cli.assets_auto_refresh_disabled"), GUILayout.ExpandWidth(false));
				GUI.color = prevColor;

				if (GUILayout.Button(Tr("cli.clear_output"))) {
					m_CombinedOutput = "";
					GUI.FocusControl("");
				}
				if (GUILayout.Button(Tr("cli.copy_output"))) {
					GUIUtility.systemCopyBuffer = m_CombinedOutput;
					GUI.FocusControl("");
				}

				EditorGUI.BeginDisabledGroup(m_IsWorking || string.IsNullOrWhiteSpace(m_CommandArgs));

				GUILayout.Space(8f);


				if (Event.current.shift) {
					m_AutoRun = false;
				}

				if (GUILayout.Button(Tr("cli.run")) || m_AutoRun) {
					m_AutoRun = false;
					GUI.FocusControl("");
					m_CommandArgs = m_CommandArgs.Trim();
					m_SVNOperation = SVNAsyncOperation<ShellUtils.ShellResult>.Start(
						op => ShellUtils.ExecuteCommand("svn", m_CommandArgs.Replace("\n", " "), op)
						);

					m_SVNOperation.AnyOutput += (line) => {
						m_CombinedOutput += line + "\n";

						// In case window got closed.
						if (this) {
							Repaint();
						}
					};

					m_SVNOperation.Completed += (op) => {
						if (op.Result.HasErrors) {
							m_StateLabel = Tr("common.failed");
							m_StateColor = Color.red;
						} else {
							m_StateLabel = op.AbortRequested ? Tr("common.aborted") : Tr("common.completed");
							m_StateColor = op.AbortRequested ? Color.red : Color.green;
						}

						m_SVNOperation = null;
						m_CombinedOutput += "\n";

						// In case window got closed.
						if (this) {
							SVNStatusesDatabase.Instance.InvalidateDatabase();
							Repaint();
						}
					};

					m_StateLabel = Tr("common.working");
					m_StateColor = Color.yellow;
				}

				EditorGUI.EndDisabledGroup();
			}
			EditorGUILayout.EndHorizontal();
		}
	}
}
