// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

#if UNITY_EDITOR_WIN
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Providers
{
	/// <summary>
	/// Queries TortoiseSVN's TSVNCache.exe via named-pipe IPC — same data source the Windows
	/// Explorer shell extension uses, so overlay icons match Explorer 1:1 by construction.
	///
	/// Protocol references TortoiseSVN's public <c>src/TSVNCache/CacheInterface.h</c>:
	///   * Status pipe:  \\.\pipe\TSVNCache             (request → response per file)
	///   * Command pipe: \\.\pipe\TSVNCacheCommand      (invalidate / crawl commands, no response)
	///   * svn_wc_status_kind enum values 1..14 from libsvn_wc.
	///
	/// The exact byte layout of TSVNCacheRequest / TSVNCacheResponse evolves slowly across TortoiseSVN
	/// versions. We pin the layout to the one documented below; if a future TortoiseSVN release breaks
	/// it, Probe() will fail (response bytes don't validate) and the manager falls back to the CLI
	/// provider — no user-visible failure beyond logging.
	/// </summary>
	internal sealed class TSVNCacheStatusProvider : ISVNStatusProvider
	{
		public string DisplayName => "TSVNCache (TortoiseSVN)";

		private bool m_Ready;
		public bool IsReady => m_Ready;
		public bool DataIsIncomplete => false;  // TSVNCache covers the whole working copy by design.

		public event Action StatusesChanged;

		// TTL cache. Absolute native paths → (status, fetch timestamp).
		private readonly Dictionary<string, (SVNStatusData status, double timestamp)> m_Cache
			= new Dictionary<string, (SVNStatusData, double)>(StringComparer.OrdinalIgnoreCase);
		private readonly object m_CacheLock = new object();

		private const double k_CacheTTL = 5.0;          // seconds — TSVNCache itself is reactive, so short TTL is fine
		private const int k_PipeTimeoutMs = 200;        // hard ceiling on a single sync query
		private const int k_ProbeTimeoutMs = 300;       // short — probe runs on worker thread but still don't waste time
		// Pipe names — discovered at runtime because newer TortoiseSVN versions append the
		// Windows session ID: \\.\pipe\TSVNCache_1  / \\.\pipe\TSVNCacheCommand_1.
		// s_StatusPipeName / s_CommandPipeName are resolved once on first use.
		private static string s_StatusPipeName;
		private static string s_CommandPipeName;

		private static string StatusPipeName  => s_StatusPipeName  ?? (s_StatusPipeName  = FindPipe("TSVNCache",        "TSVNCacheCommand"));
		private static string CommandPipeName => s_CommandPipeName ?? (s_CommandPipeName = FindPipe("TSVNCacheCommand", null));

		// Scan \\.\pipe\ for the first pipe whose name starts with `prefix` but NOT with `exclude`.
		// Wrapped in a 500ms watchdog Task — Directory.GetFiles(@"\\.\pipe\") has been known
		// to block on certain machines (AV / sandboxed environments). If it doesn't return
		// quickly we fall back to the bare prefix and let the actual Connect() attempt fail fast.
		private static string FindPipe(string prefix, string exclude)
		{
			string result = null;
			try {
				var task = System.Threading.Tasks.Task.Run(() => {
					try {
						foreach (var p in System.IO.Directory.GetFiles(@"\\.\pipe\")) {
							string n = System.IO.Path.GetFileName(p);
							if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
								if (exclude != null && n.StartsWith(exclude, StringComparison.OrdinalIgnoreCase))
									continue;
								return n;
							}
						}
					} catch { /* fall through */ }
					return null;
				});
				if (task.Wait(500))
					result = task.Result;
			} catch { /* watchdog itself failed — fall through */ }
			return result ?? prefix;  // bare name as last resort
		}

		// Public so the diagnostic window can surface it.
		public long LastQueryLatencyTicks { get; private set; }
		public int LastQueryErrors { get; private set; }

		// ── Probe ─────────────────────────────────────────────────────────────
		/// <summary>
		/// Returns true if TSVNCache.exe is responsive and the wire protocol matches what we expect.
		/// Cached for the session — call once at startup.
		/// </summary>
		public static bool Probe(out string failureReason)
		{
			failureReason = null;

			// Fast-fail check — if TSVNCache.exe isn't running there's nothing to connect to.
			// Skipping the pipe-open call avoids the rare case where Connect() blocks past its timeout.
			try {
				var procs = System.Diagnostics.Process.GetProcessesByName("TSVNCache");
				if (procs == null || procs.Length == 0) {
					failureReason = "TSVNCache.exe process not running";
					return false;
				}
			} catch { /* enumerating processes can rarely throw; fall through to actual pipe probe */ }

			try {
				using (var pipe = new NamedPipeClientStream(".", StatusPipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
					pipe.Connect(k_ProbeTimeoutMs);
					// Send a request for the project root and check we get a coherent response.
					string projectRoot = WiseSVNIntegration.ProjectRootNative;
					if (string.IsNullOrEmpty(projectRoot)) {
						failureReason = "Project root not set yet";
						return false;
					}

					if (!WriteRequest(pipe, projectRoot, recursive: false)) {
						failureReason = "Failed to write request";
						return false;
					}
					if (!TryReadResponse(pipe, out var resp, out var readErr)) {
						failureReason = "Failed to read response: " + readErr;
						return false;
					}
					// Sanity check: text_status must be in [1..14] (svn_wc_status_kind range).
					if (resp.text_status < 1 || resp.text_status > 14) {
						failureReason = $"Invalid text_status={resp.text_status} (expected 1–14). " +
							$"First 8 int-words of response: " +
							DumpFirstWords(resp);
						return false;
					}
					return true;
				}
			} catch (TimeoutException) {
				failureReason = "Connection timeout — TSVNCache.exe not running?";
				return false;
			} catch (FileNotFoundException) {
				failureReason = "Pipe not found — TSVNCache.exe not running?";
				return false;
			} catch (IOException ex) {
				failureReason = "Pipe IO: " + ex.Message;
				return false;
			} catch (Exception ex) {
				failureReason = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
		}

		// ── Construction ─────────────────────────────────────────────────────
		public TSVNCacheStatusProvider()
		{
			m_Ready = true;
			// Notify consumers periodically so they refresh icons even without explicit invalidation —
			// TSVNCache picks up filesystem changes itself, but we have to repaint the Project window.
			EditorApplication.update += TickStatusesChanged;
		}

		private double m_LastChangeTick;
		private void TickStatusesChanged()
		{
			// Fire StatusesChanged once per ~5s so Project window picks up FS-driven changes
			// without needing a Unity import event. Cheap — no work happens unless subscribers care.
			double now = EditorApplication.timeSinceStartup;
			if (now - m_LastChangeTick < k_CacheTTL) return;
			m_LastChangeTick = now;
			StatusesChanged?.Invoke();
		}

		// ── ISVNStatusProvider ───────────────────────────────────────────────
		public SVNStatusData GetStatus(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
				return new SVNStatusData { Status = VCFileStatus.None };

			string nativePath = ToNativePath(assetPath);
			double now = EditorApplication.timeSinceStartup;

			lock (m_CacheLock) {
				if (m_Cache.TryGetValue(nativePath, out var entry) && now - entry.timestamp < k_CacheTTL)
					return entry.status;
			}

			var status = QueryPipe(nativePath);
			lock (m_CacheLock) {
				m_Cache[nativePath] = (status, now);
			}
			return status;
		}

		public IEnumerable<SVNStatusData> EnumerateInteresting()
		{
			// TSVNCache doesn't expose a "list all changed files" query — it's keyed by path.
			// We surface only what we've already cached as interesting. For the project-wide
			// status badge counter, the CLIDatabaseStatusProvider's enumeration is still more
			// useful; consumers wanting that data can read it from SVNStatusesDatabase directly.
			lock (m_CacheLock) {
				foreach (var entry in m_Cache.Values) {
					var s = entry.status;
					bool interesting = s.Status != VCFileStatus.Normal
						&& s.Status != VCFileStatus.None;
					interesting |= s.LockStatus != VCLockStatus.NoLock;
					interesting |= s.RemoteStatus != VCRemoteFileStatus.None;
					if (interesting) yield return s;
				}
			}
		}

		public void InvalidatePath(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath)) return;
			string nativePath = ToNativePath(assetPath);

			lock (m_CacheLock) {
				m_Cache.Remove(nativePath);
			}

			// Also poke TSVNCache to re-crawl this path (no-op if it can't connect).
			SendCommand(TSVNCACHECOMMAND_CRAWL, nativePath);
		}

		public void InvalidateAll()
		{
			lock (m_CacheLock) m_Cache.Clear();
			SendCommand(TSVNCACHECOMMAND_REFRESHALL, string.Empty);
			StatusesChanged?.Invoke();
		}

		// ── Wire format ──────────────────────────────────────────────────────
		// Request struct (matches CacheInterface.h):
		//   DWORD  flags;                         // 4 bytes
		//   WCHAR  path[260];                     // 520 bytes (MAX_PATH wide chars, null-terminated)
		// Total: 524 bytes
		private const int k_RequestSize = 4 + 260 * 2;

		// Response struct (best-effort interpretation of TortoiseSVN's TStatusCacheEntry serialization).
		// We only consume the leading well-defined fields and ignore the tail.
		[StructLayout(LayoutKind.Sequential)]
		private struct TSVNCacheResponseHeader
		{
			public int text_status;     // svn_wc_status_kind
			public int prop_status;
			public int repo_text_status;
			public int repo_prop_status;
			public int locked;          // BOOL (4 bytes)
			public int copied;
			public int switched;
			public int kind;            // svn_node_kind_t: 1=file, 2=dir
		}

		private const int TSVNCACHECOMMAND_CRAWL       = 1;
		private const int TSVNCACHECOMMAND_REFRESHALL  = 2;

		private static bool WriteRequest(Stream pipe, string nativePath, bool recursive)
		{
			try {
				var buffer = new byte[k_RequestSize];
				// flags: 1 = recursive query, 0 = single path. We always use 0 — overlay queries are per-file.
				BitConverter.GetBytes(recursive ? 1 : 0).CopyTo(buffer, 0);
				// path: WCHAR[260], null-terminated. TSVNCache wants absolute paths with backslashes.
				string padded = nativePath ?? string.Empty;
				if (padded.Length > 259) padded = padded.Substring(0, 259);  // leave room for null terminator
				Encoding.Unicode.GetBytes(padded, 0, padded.Length, buffer, 4);
				pipe.Write(buffer, 0, buffer.Length);
				pipe.Flush();
				return true;
			} catch {
				return false;
			}
		}

		private static bool TryReadResponse(Stream pipe, out TSVNCacheResponseHeader header, out string error)
		{
			header = default;
			error = null;
			try {
				// Set a read deadline so we don't block indefinitely if the server sends nothing.
				// Only valid when the pipe was opened with PipeOptions.Asynchronous.
				try { pipe.ReadTimeout = 800; } catch { /* not all streams support this */ }

				int size = Marshal.SizeOf<TSVNCacheResponseHeader>();
				var buffer = new byte[size];
				int read = 0;
				while (read < size) {
					int n = pipe.Read(buffer, read, size - read);
					if (n <= 0) {
						error = $"Short read ({read}/{size})";
						return false;
					}
					read += n;
				}
				var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
				try {
					header = Marshal.PtrToStructure<TSVNCacheResponseHeader>(handle.AddrOfPinnedObject());
				} finally {
					handle.Free();
				}
				// Drain any trailing bytes the server might send for variable-length fields we don't parse.
				// Without this the next request on a fresh pipe handles cleanly.
				try {
					while (pipe.CanRead) {
						var drain = new byte[256];
						if (pipe.Read(drain, 0, drain.Length) <= 0) break;
					}
				} catch { /* end-of-stream is fine */ }
				return true;
			} catch (Exception ex) {
				error = ex.Message;
				return false;
			}
		}

		// ── Query implementation ─────────────────────────────────────────────
		private SVNStatusData QueryPipe(string nativePath)
		{
			var sw = System.Diagnostics.Stopwatch.StartNew();
			try {
				using (var pipe = new NamedPipeClientStream(".", StatusPipeName, PipeDirection.InOut, PipeOptions.Asynchronous)) {
					pipe.Connect(k_PipeTimeoutMs);
					if (!WriteRequest(pipe, nativePath, recursive: false)) {
						LastQueryErrors++;
						return new SVNStatusData { Status = VCFileStatus.None };
					}
					if (!TryReadResponse(pipe, out var resp, out _)) {
						LastQueryErrors++;
						return new SVNStatusData { Status = VCFileStatus.None };
					}
					return ToSVNStatusData(resp, nativePath);
				}
			} catch (TimeoutException) {
				LastQueryErrors++;
				return new SVNStatusData { Status = VCFileStatus.None };
			} catch (Exception ex) {
				LastQueryErrors++;
				if (LastQueryErrors < 5) {
					Debug.LogWarning($"[WiseSVN] TSVNCache query failed for {nativePath}: {ex.Message}");
				}
				return new SVNStatusData { Status = VCFileStatus.None };
			} finally {
				sw.Stop();
				LastQueryLatencyTicks = sw.ElapsedTicks;
			}
		}

		private void SendCommand(int command, string nativePath)
		{
			try {
				using (var pipe = new NamedPipeClientStream(".", CommandPipeName, PipeDirection.Out, PipeOptions.None)) {
					pipe.Connect(k_PipeTimeoutMs);
					var buffer = new byte[4 + 260 * 2];
					BitConverter.GetBytes(command).CopyTo(buffer, 0);
					string padded = nativePath ?? string.Empty;
					if (padded.Length > 259) padded = padded.Substring(0, 259);
					Encoding.Unicode.GetBytes(padded, 0, padded.Length, buffer, 4);
					pipe.Write(buffer, 0, buffer.Length);
					pipe.Flush();
				}
			} catch {
				// Command pipe is fire-and-forget; ignore errors.
			}
		}

		// ── Status mapping ───────────────────────────────────────────────────
		// svn_wc_status_kind → VCFileStatus. Values 1..14 from libsvn_wc public headers.
		private static readonly VCFileStatus[] k_StatusKindMap = {
			VCFileStatus.None,         // 0 unused (svn_wc_status_kind starts at 1)
			VCFileStatus.None,         // 1 svn_wc_status_none
			VCFileStatus.Unversioned,  // 2 svn_wc_status_unversioned
			VCFileStatus.Normal,       // 3 svn_wc_status_normal
			VCFileStatus.Added,        // 4 svn_wc_status_added
			VCFileStatus.Missing,      // 5 svn_wc_status_missing
			VCFileStatus.Deleted,      // 6 svn_wc_status_deleted
			VCFileStatus.Replaced,     // 7 svn_wc_status_replaced
			VCFileStatus.Modified,     // 8 svn_wc_status_modified
			VCFileStatus.Normal,       // 9 svn_wc_status_merged (display as normal)
			VCFileStatus.Conflicted,   // 10 svn_wc_status_conflicted
			VCFileStatus.Ignored,      // 11 svn_wc_status_ignored
			VCFileStatus.Obstructed,   // 12 svn_wc_status_obstructed
			VCFileStatus.External,     // 13 svn_wc_status_external
			VCFileStatus.None,         // 14 svn_wc_status_incomplete
		};

		private static readonly VCPropertiesStatus[] k_PropStatusMap = {
			VCPropertiesStatus.None,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Modified,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Conflicted,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.Normal,
			VCPropertiesStatus.None,
		};

		private static SVNStatusData ToSVNStatusData(TSVNCacheResponseHeader resp, string nativePath)
		{
			VCFileStatus fileStatus = (resp.text_status >= 0 && resp.text_status < k_StatusKindMap.Length)
				? k_StatusKindMap[resp.text_status] : VCFileStatus.None;
			VCPropertiesStatus propStatus = (resp.prop_status >= 0 && resp.prop_status < k_PropStatusMap.Length)
				? k_PropStatusMap[resp.prop_status] : VCPropertiesStatus.None;

			VCRemoteFileStatus remoteStatus = VCRemoteFileStatus.None;
			if (resp.repo_text_status > 1 && resp.repo_text_status <= 14)
				remoteStatus = VCRemoteFileStatus.Modified;

			return new SVNStatusData {
				Status = fileStatus,
				PropertiesStatus = propStatus,
				LockStatus = resp.locked != 0 ? VCLockStatus.LockedHere : VCLockStatus.NoLock,
				RemoteStatus = remoteStatus,
				SwitchedExternalStatus = resp.switched != 0 ? VCSwitchedExternal.Switched : VCSwitchedExternal.Normal,
				TreeConflictStatus = VCTreeConflictStatus.Normal,  // not exposed via this protocol slice
				Path = NativeToAssetPath(nativePath),
				LockDetails = LockDetails.Empty,
			};
		}

		// ── Diagnostics ──────────────────────────────────────────────────────
		private static string DumpFirstWords(TSVNCacheResponseHeader h)
		{
			// Serialise the struct back to bytes so we can show its raw content in the probe failure message.
			int sz = System.Runtime.InteropServices.Marshal.SizeOf<TSVNCacheResponseHeader>();
			byte[] buf = new byte[sz];
			var pin = System.Runtime.InteropServices.GCHandle.Alloc(buf, System.Runtime.InteropServices.GCHandleType.Pinned);
			try { System.Runtime.InteropServices.Marshal.StructureToPtr(h, pin.AddrOfPinnedObject(), false); }
			finally { pin.Free(); }

			var sb = new System.Text.StringBuilder();
			for (int i = 0; i + 3 < sz; i += 4) {
				int w = System.BitConverter.ToInt32(buf, i);
				sb.Append($"[{i/4}]={w} ");
			}
			return sb.ToString().TrimEnd();
		}

		// ── Path helpers ─────────────────────────────────────────────────────		// TSVNCache expects absolute paths with backslashes — never apply ToLower (case-sensitive on the wire).
		private static string ToNativePath(string assetPath)
		{
			if (Path.IsPathRooted(assetPath))
				return assetPath.Replace('/', '\\');
			return Path.Combine(WiseSVNIntegration.ProjectRootNative, assetPath).Replace('/', '\\');
		}

		private static string NativeToAssetPath(string nativePath)
		{
			string root = WiseSVNIntegration.ProjectRootNative.Replace('/', '\\');
			if (nativePath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) {
				string rel = nativePath.Substring(root.Length).TrimStart('\\', '/');
				return rel.Replace('\\', '/');
			}
			return nativePath.Replace('\\', '/');
		}
	}
}
#endif
