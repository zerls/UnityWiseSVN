// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.Preferences;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Providers
{
	/// <summary>
	/// Diagnostic window for the status provider system.
	/// "Dump Raw Bytes" sends one request to TSVNCache and logs the raw hex response so we
	/// can confirm/fix the struct layout without needing the C++ source.
	/// </summary>
	internal class StatusProviderInfoWindow : EditorWindow
	{
		[MenuItem("Window/Version Control/SVN/🐛 Debug/Status Provider Info", false, ContextMenus.SVNContextMenusManager.WindowMenuPriority + 100)]
		public static void Open()
		{
			GetWindow<StatusProviderInfoWindow>("WiseSVN Status Source").Show();
		}

		private Vector2 m_Scroll;

		private void OnEnable()  { EditorApplication.update += Repaint; }
		private void OnDisable() { EditorApplication.update -= Repaint; }

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
					EditorGUILayout.LabelField("Last IPC latency:",
						$"{tsvn.LastQueryLatencyTicks / (double)System.Diagnostics.Stopwatch.Frequency * 1000.0:F2} ms");
					EditorGUILayout.LabelField("Errors since start:", tsvn.LastQueryErrors.ToString());
				}
#endif
			}

			EditorGUILayout.Space(8);
			EditorGUILayout.LabelField("Current Project selection", EditorStyles.boldLabel);
			using (var scope = new EditorGUILayout.ScrollViewScope(m_Scroll)) {
				m_Scroll = scope.scrollPosition;

				var selection = Selection.assetGUIDs;
				if (selection == null || selection.Length == 0) {
					EditorGUILayout.HelpBox("Select an asset in the Project window to see its provider-reported status.", MessageType.Info);
				} else {
					using (new EditorGUI.IndentLevelScope()) {
						foreach (var guid in selection) {
							string path = AssetDatabase.GUIDToAssetPath(guid);
							if (string.IsNullOrEmpty(path)) continue;

							EditorGUILayout.LabelField(path, EditorStyles.miniBoldLabel);
							var status = provider.GetStatus(path);
							// VCFileStatus.None means "not in the changed-files DB" — for a ready
							// CLI database this equals "tracked by SVN and clean (Normal sync state)".
							string statusLabel = (status.Status == VCFileStatus.None && provider.IsReady)
								? "Normal  (not in DB — tracked & clean)"
								: status.Status.ToString();
							using (new EditorGUI.IndentLevelScope()) {
								EditorGUILayout.LabelField("Status:",        statusLabel);
								EditorGUILayout.LabelField("Properties:",    status.PropertiesStatus.ToString());
								EditorGUILayout.LabelField("Lock:",          status.LockStatus.ToString());
								EditorGUILayout.LabelField("Remote:",        status.RemoteStatus.ToString());
								EditorGUILayout.LabelField("Tree conflict:", status.TreeConflictStatus.ToString());
								EditorGUILayout.LabelField("Switched/Ext:",  status.SwitchedExternalStatus.ToString());
								EditorGUILayout.LabelField("Path:",          string.IsNullOrEmpty(status.Path) ? "(not in DB)" : status.Path);
							}
							EditorGUILayout.Space(4);
						}
					}
				}
			}

			EditorGUILayout.Space(8);
			using (new EditorGUILayout.HorizontalScope()) {
				if (GUILayout.Button("Force refresh"))
					provider.InvalidateAll();

				if (GUILayout.Button("Invalidate selected")) {
					foreach (var guid in Selection.assetGUIDs) {
						string path = AssetDatabase.GUIDToAssetPath(guid);
						if (!string.IsNullOrEmpty(path)) provider.InvalidatePath(path);
					}
				}
			}

#if UNITY_EDITOR_WIN
			// ── Diagnostic buttons ────────────────────────────────────────────
			EditorGUILayout.Space(6);
			EditorGUILayout.LabelField("Protocol Debug", EditorStyles.boldLabel);
			using (new EditorGUILayout.HorizontalScope()) {
				if (GUILayout.Button("Run TSVNCache Diagnostic")) {
					RunFullDiagnostic();
				}
				if (GUILayout.Button("Retry Probe")) {
					SVNPreferencesManager.Instance.GetType()
						.GetMethod("ProbeAndUpgradeProvider",
							System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
						?.Invoke(SVNPreferencesManager.Instance, null);
				}
			}
			if (GUILayout.Button("Dump Raw Response Bytes to Console")) {
				string targetPath = null;
				foreach (var guid in Selection.assetGUIDs) {
					string p = AssetDatabase.GUIDToAssetPath(guid);
					if (!string.IsNullOrEmpty(p)) { targetPath = p; break; }
				}
				if (string.IsNullOrEmpty(targetPath))
					targetPath = "Assets";
				DumpRawBytes(targetPath);
			}
			EditorGUILayout.HelpBox(
				"Run TSVNCache Diagnostic — tests every possible pipe-name variant; logs everything.\n" +
				"Run after starting Unity to see why TSVNCache isn't connecting.",
				MessageType.None);
#endif
		}

#if UNITY_EDITOR_WIN
		// ── Pipe name discovery ───────────────────────────────────────────────
		// New TortoiseSVN versions append the Windows session ID to the pipe name:
		//   \\.\pipe\TSVNCache_<sessionId>
		// Enumerate all pipes whose name starts with "TSVNCache" so we find the right one.
		private static string FindTSVNCachePipeName()
		{
			try {
				// The Win32 pipe directory is accessible as a special filesystem path.
				string[] pipes = System.IO.Directory.GetFiles(@"\\.\pipe\");
				foreach (var p in pipes) {
					// GetFiles returns full paths like \\.\pipe\TSVNCache or \\.\pipe\TSVNCache_1
					string name = System.IO.Path.GetFileName(p);
					if (name.StartsWith("TSVNCache", System.StringComparison.OrdinalIgnoreCase)
					    && !name.StartsWith("TSVNCacheCommand", System.StringComparison.OrdinalIgnoreCase))
						return name;
				}
			} catch (System.Exception ex) {
				Debug.LogWarning($"[WiseSVN] Could not enumerate pipes: {ex.Message}");
			}
			return "TSVNCache"; // best-guess fallback
		}

		// List ALL pipe names starting with "TSVN" — shown in the console to help diagnose.
		private static void LogAvailableTSVNPipes()
		{
			try {
				string[] pipes = System.IO.Directory.GetFiles(@"\\.\pipe\");
				var sb = new StringBuilder();
				sb.AppendLine("[WiseSVN-Pipes] Named pipes matching 'TSVN*':");
				bool any = false;
				foreach (var p in pipes) {
					string name = System.IO.Path.GetFileName(p);
					if (name.StartsWith("TSVN", System.StringComparison.OrdinalIgnoreCase)) {
						sb.AppendLine($"  \\\\.\\ pipe\\{name}");
						any = true;
					}
				}
				if (!any) sb.AppendLine("  (none found — is TortoiseSVN running?)");
				Debug.Log(sb.ToString());
			} catch (System.Exception ex) {
				Debug.LogError($"[WiseSVN-Pipes] Enumeration failed: {ex.Message}");
			}
		}

		private static void DumpRawBytes(string assetPath)
		{
			const int requestSize = 4 + 260 * 2;   // DWORD flags + WCHAR[260] path

			// Step 1: log all TSVN-prefixed pipes so we know what's available.
			LogAvailableTSVNPipes();

			string pipeName = FindTSVNCachePipeName();
			Debug.Log($"[WiseSVN-RawDump] Using pipe name: \"{pipeName}\"");

			// Build absolute native path.
			string nativePath = System.IO.Path.IsPathRooted(assetPath)
				? assetPath.Replace('/', '\\')
				: System.IO.Path.Combine(WiseSVNIntegration.ProjectRootNative, assetPath).Replace('/', '\\');

			try {
				using (var pipe = new System.IO.Pipes.NamedPipeClientStream(
						".", pipeName,
						System.IO.Pipes.PipeDirection.InOut,
						System.IO.Pipes.PipeOptions.Asynchronous)) {

					pipe.Connect(2000);

					// Write request: flags=0, path=nativePath.
					var req = new byte[requestSize];
					System.BitConverter.GetBytes(0).CopyTo(req, 0);
					string padded = nativePath.Length > 259 ? nativePath.Substring(0, 259) : nativePath;
					Encoding.Unicode.GetBytes(padded, 0, padded.Length, req, 4);
					pipe.Write(req, 0, req.Length);
					pipe.Flush();

					// Read with a deadline — NamedPipeClientStream in async mode supports ReadTimeout.
					pipe.ReadTimeout = 800;
					var buf = new System.Collections.Generic.List<byte>();
					var tmp = new byte[1024];
					try {
						int n;
						while ((n = pipe.Read(tmp, 0, tmp.Length)) > 0)
							for (int i = 0; i < n; i++) buf.Add(tmp[i]);
					} catch (System.TimeoutException) { /* server sent all it has — done */ }
					catch (System.IO.IOException) { /* pipe closed by server — done */ }

					byte[] raw = buf.ToArray();
					int len = raw.Length;

					var sb = new StringBuilder();
					sb.AppendLine($"[WiseSVN-RawDump] pipe=\"{pipeName}\"  path=\"{nativePath}\"  total_bytes={len}");
					if (len == 0) {
						sb.AppendLine("  WARNING: received 0 bytes — check pipe name or request format");
						Debug.LogWarning(sb.ToString());
						return;
					}

					// Hex rows, 16 bytes each.
					sb.AppendLine("  Offset  | 00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F  | ASCII");
					sb.AppendLine("  --------+-------------------------------------------------+------------------");
					for (int row = 0; row < len; row += 16) {
						int rowEnd = System.Math.Min(row + 16, len);
						sb.Append($"  {row:X6}  | ");
						for (int i = row; i < row + 16; i++) {
							if (i < rowEnd) sb.Append($"{raw[i]:X2} ");
							else            sb.Append("   ");
							if (i == row + 7) sb.Append(" ");
						}
						sb.Append(" | ");
						for (int i = row; i < rowEnd; i++) {
							char c = (char)raw[i];
							sb.Append(c >= 0x20 && c < 0x7F ? c : '.');
						}
						sb.AppendLine();
					}

					// Int32 words — easy to overlay against a struct definition.
					sb.AppendLine();
					sb.AppendLine("  Int32 words (little-endian):");
					for (int i = 0; i + 3 < len; i += 4) {
						int word = System.BitConverter.ToInt32(raw, i);
						sb.AppendLine($"    [byte {i,3}] word[{i/4,2}] = {word,12}  (0x{word:X8})");
					}

					Debug.Log(sb.ToString());
				}
			} catch (System.Exception ex) {
				Debug.LogError($"[WiseSVN-RawDump] Failed on pipe \"{pipeName}\": {ex.GetType().Name}: {ex.Message}");
			}
		}

		// ── Comprehensive diagnostic ──────────────────────────────────────────
		// Tries every reasonable angle to talk to TSVNCache and logs the result of each step.
		// Designed to be safe to run on the main thread — every blocking call has a tight timeout.
		private static void RunFullDiagnostic()
		{
			var sb = new StringBuilder();
			sb.AppendLine("======== WiseSVN TSVNCache Diagnostic ========");

			// Step 1: confirm process is running.
			sb.AppendLine();
			sb.AppendLine("Step 1: TSVNCache.exe process check");
			try {
				var procs = System.Diagnostics.Process.GetProcessesByName("TSVNCache");
				if (procs == null || procs.Length == 0) {
					sb.AppendLine("  ✗ No TSVNCache.exe process found.");
					sb.AppendLine("    → Start TortoiseSVN, or right-click any folder in Explorer to wake the cache.");
					Debug.LogWarning(sb.ToString());
					return;
				}
				foreach (var p in procs) {
					sb.AppendLine($"  ✓ PID={p.Id}  Session={p.SessionId}  StartTime=(can't read for security)");
				}
			} catch (System.Exception ex) {
				sb.AppendLine($"  ✗ Process check threw: {ex.GetType().Name}: {ex.Message}");
			}

			// Step 2: enumerate \\.\pipe\ to find TSVN-related pipes.
			sb.AppendLine();
			sb.AppendLine(@"Step 2: Enumerate \\.\pipe\");
			System.Collections.Generic.List<string> tsvnPipes = new System.Collections.Generic.List<string>();
			try {
				var enumTask = System.Threading.Tasks.Task.Run(() => {
					var found = new System.Collections.Generic.List<string>();
					try {
						foreach (var p in System.IO.Directory.GetFiles(@"\\.\pipe\")) {
							string n = System.IO.Path.GetFileName(p);
							if (n.IndexOf("TSVN", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
							    n.IndexOf("Tortoise", System.StringComparison.OrdinalIgnoreCase) >= 0)
								found.Add(n);
						}
					} catch (System.Exception ex) {
						found.Add("__error__:" + ex.Message);
					}
					return found;
				});

				if (!enumTask.Wait(2000)) {
					sb.AppendLine("  ✗ Pipe enumeration timed out (2s) — Directory.GetFiles hung");
				} else {
					tsvnPipes = enumTask.Result;
					if (tsvnPipes.Count == 0) {
						sb.AppendLine("  ✗ No pipes matching TSVN* or Tortoise* found.");
					} else {
						foreach (var n in tsvnPipes) {
							if (n.StartsWith("__error__:")) {
								sb.AppendLine($"  ✗ Enumeration error: {n.Substring(10)}");
							} else {
								sb.AppendLine($"  ✓ \\\\.\\pipe\\{n}");
							}
						}
					}
				}
			} catch (System.Exception ex) {
				sb.AppendLine($"  ✗ Enumeration setup threw: {ex.Message}");
			}

			// Step 3: try connecting to every plausible pipe name. Each attempt has its own 300ms timeout.
			sb.AppendLine();
			sb.AppendLine("Step 3: Try connecting (300ms each)");

			var candidates = new System.Collections.Generic.List<string> {
				"TSVNCache",
				"tsvncache",
			};
			// Also try every name we actually enumerated that looks like a status pipe (not the command one).
			foreach (var n in tsvnPipes) {
				if (n.StartsWith("__error__:")) continue;
				if (n.StartsWith("TSVNCacheCommand", System.StringComparison.OrdinalIgnoreCase)) continue;
				if (!candidates.Contains(n)) candidates.Add(n);
			}

			foreach (var pipeName in candidates) {
				sb.Append($"  Connect(\"{pipeName}\")... ");
				try {
					using (var pipe = new System.IO.Pipes.NamedPipeClientStream(
							".", pipeName,
							System.IO.Pipes.PipeDirection.InOut,
							System.IO.Pipes.PipeOptions.Asynchronous)) {
						pipe.Connect(300);
						sb.Append("connected. ");

						// Send a probe request for the project root.
						string nativePath = WiseSVNIntegration.ProjectRootNative.Replace('/', '\\');
						const int requestSize = 4 + 260 * 2;
						var req = new byte[requestSize];
						System.BitConverter.GetBytes(0).CopyTo(req, 0);
						string padded = nativePath.Length > 259 ? nativePath.Substring(0, 259) : nativePath;
						Encoding.Unicode.GetBytes(padded, 0, padded.Length, req, 4);
						pipe.Write(req, 0, req.Length);
						pipe.Flush();
						sb.Append("wrote request. ");

						pipe.ReadTimeout = 500;
						var buf = new byte[2048];
						int n = 0;
						try { n = pipe.Read(buf, 0, buf.Length); }
						catch (System.TimeoutException) { sb.AppendLine("read timeout (0 bytes)"); continue; }

						if (n <= 0) {
							sb.AppendLine("read 0 bytes (server closed)");
							continue;
						}
						sb.AppendLine($"read {n} bytes ✓");
						sb.AppendLine($"    first 32 bytes hex:");
						sb.Append("      ");
						for (int i = 0; i < System.Math.Min(n, 32); i++) sb.Append($"{buf[i]:X2} ");
						sb.AppendLine();
						sb.AppendLine($"    first 8 int32 words:");
						for (int i = 0; i + 3 < System.Math.Min(n, 32); i += 4) {
							sb.AppendLine($"      [{i/4}] = {System.BitConverter.ToInt32(buf, i)}");
						}
					}
				} catch (System.TimeoutException) {
					sb.AppendLine("connect timeout");
				} catch (System.Exception ex) {
					sb.AppendLine($"{ex.GetType().Name}: {ex.Message}");
				}
			}

			sb.AppendLine();
			sb.AppendLine("======== End Diagnostic ========");
			Debug.Log(sb.ToString());
		}
#endif
	}
}
