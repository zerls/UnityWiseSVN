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

		// On cache miss, DON'T block the main thread with a pipe query — the Project window
		// calls GetStatus() for every visible asset per frame, so a single cold miss would
		// cascade into N×800ms hangs. Instead enqueue the miss and have a background worker
		// drain the queue → fill the cache → fire StatusesChanged → repaint Project window.
		// 100ms cooldown smooths bursts during rapid scrolling.
		private readonly HashSet<string> m_PendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private readonly object m_PendingLock = new object();
		private bool m_FillRunning;
		private double m_LastCacheMissTime;
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
					// Sanity check: textStatus must be in [1..14] (svn_wc_status_kind range);
					// kind must be 1 (file) or 2 (dir).
					if (resp.textStatus < 1 || resp.textStatus > 14 || (resp.kind != svn_node_file && resp.kind != svn_node_dir)) {
						failureReason = $"Invalid response (kind={resp.kind}, textStatus={resp.textStatus}). " +
							$"First 16 bytes: " + DumpFirstWords(resp);
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
		// Cache timestamps use Environment.TickCount-based seconds so the background filler
		// (which can't read EditorApplication.timeSinceStartup off the main thread) and the
		// main-thread reader share the same monotonic clock.
		private static double NowSeconds() => System.Environment.TickCount / 1000.0;

		public SVNStatusData GetStatus(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
				return new SVNStatusData { Status = VCFileStatus.None };

			string nativePath = ToNativePath(assetPath);
			double now = NowSeconds();

			lock (m_CacheLock) {
				if (m_Cache.TryGetValue(nativePath, out var entry) && now - entry.timestamp < k_CacheTTL)
					return entry.status;
			}

			// Cache miss — don't block Unity's GUI thread with a sync pipe query.
			// Queue this path for the background filler and return None for now.
			// Once the worker fills it, StatusesChanged repaints the Project window
			// and the next ItemOnGUI tick will hit the cache.
			lock (m_PendingLock) {
				m_PendingPaths.Add(nativePath);
			}
			ScheduleCacheMissFill();
			return new SVNStatusData { Status = VCFileStatus.None };
		}

		private void ScheduleCacheMissFill()
		{
			double now = NowSeconds();
			lock (m_PendingLock) {
				if (m_FillRunning) return;
				if (now - m_LastCacheMissTime < 0.1) return;
				m_LastCacheMissTime = now;
				m_FillRunning = true;
			}
			System.Threading.ThreadPool.QueueUserWorkItem(_ => RunCacheMissFill());
		}

		// Background worker: drain m_PendingPaths through QueryPipe, fill cache, then repaint.
		// Bounded per run (max 64 paths) so a single burst can't hog the worker thread; remaining
		// paths get picked up on the next miss-driven schedule.
		private void RunCacheMissFill()
		{
			try {
				const int kMaxPerRun = 64;
				int filled = 0;
				while (filled < kMaxPerRun) {
					string path;
					lock (m_PendingLock) {
						if (m_PendingPaths.Count == 0) break;
						var enumerator = m_PendingPaths.GetEnumerator();
						enumerator.MoveNext();
						path = enumerator.Current;
						m_PendingPaths.Remove(path);
					}
					if (string.IsNullOrEmpty(path)) continue;

					var status = QueryPipe(path);

					lock (m_CacheLock) {
						m_Cache[path] = (status, NowSeconds());
					}
					filled++;
				}
			} catch (Exception ex) {
				Debug.LogWarning($"[WiseSVN] TSVNCache fill worker error: {ex.GetType().Name}: {ex.Message}");
			} finally {
				lock (m_PendingLock) {
					m_FillRunning = false;
				}
				// Re-fire on the main thread so consumers can refresh the Project window.
				EditorApplication.delayCall += () => StatusesChanged?.Invoke();
			}
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
		// Source of truth: TortoiseSVN trunk/src/TSVNCache/CacheInterface.h.
		//
		// Request struct TSVNCacheRequest { DWORD flags; WCHAR path[MAX_PATH]; }
		//   → 4 + 260*2 = 524 bytes
		private const int k_RequestSize = 4 + 260 * 2;

		// TSVNCache request flag bits (CacheInterface.h):
		private const uint TSVNCACHE_FLAGS_FOLDERISKNOWN   = 0x01;
		private const uint TSVNCACHE_FLAGS_ISFOLDER        = 0x02;
		private const uint TSVNCACHE_FLAGS_RECURSIVE_STATUS = 0x04;
		private const uint TSVNCACHE_FLAGS_NONOTIFICATIONS = 0x08;

		// Response struct TSVNCacheResponse {
		//   INT8  m_kind;         // svn_node_kind  (1=file, 2=dir)
		//   bool  m_needsLock;
		//   bool  m_treeConflict;
		//   bool  m_hasLockOwner;
		//   INT8  m_textStatus;   // svn_wc_status_kind 1..14
		//   INT8  m_propStatus;
		//   INT8  m_status;       // overall (combined) status
		//   INT64 m_cmtRev;       // last-committed revision  (8-byte aligned → 1 byte pad before)
		// }
		// MSVC default packing aligns INT64 to its size → total sizeof = 16 bytes (8 byte head + 8 byte cmtRev).
		// Confirmed against real wire: 02 00 00 00 03 01 03 00 16 00 00 00 00 00 00 00 ← Assets/Scenes (dir, normal, rev 22).
		[StructLayout(LayoutKind.Sequential, Pack = 8)]
		private struct TSVNCacheResponseHeader
		{
			public sbyte  kind;             // 0  INT8
			[MarshalAs(UnmanagedType.U1)]
			public bool   needsLock;        // 1  bool (C++ bool = 1 byte on MSVC)
			[MarshalAs(UnmanagedType.U1)]
			public bool   treeConflict;     // 2  bool
			[MarshalAs(UnmanagedType.U1)]
			public bool   hasLockOwner;     // 3  bool
			public sbyte  textStatus;       // 4  INT8
			public sbyte  propStatus;       // 5  INT8
			public sbyte  status;           // 6  INT8
			public byte   _pad7;            // 7  padding (manually declared so layout is explicit)
			public long   cmtRev;           // 8  INT64
		}

		// TSVNCache command IDs (CacheInterface.h).
		private const int TSVNCACHECOMMAND_CRAWL       = 1;
		private const int TSVNCACHECOMMAND_REFRESHALL  = 2;

		// svn_node_kind values:
		private const sbyte svn_node_file = 1;
		private const sbyte svn_node_dir  = 2;

		private static bool WriteRequest(Stream pipe, string nativePath, bool recursive)
		{
			try {
				var buffer = new byte[k_RequestSize];
				// flags: tell the cache whether we want a recursive (folder-rollup) status.
				uint flags = recursive ? TSVNCACHE_FLAGS_RECURSIVE_STATUS : 0u;
				BitConverter.GetBytes(flags).CopyTo(buffer, 0);
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
				// NamedPipeClientStream does NOT support .ReadTimeout. Use BeginRead/EndRead with a deadline.
				int size = Marshal.SizeOf<TSVNCacheResponseHeader>();
				var buffer = new byte[size];
				int read = 0;
				const int deadlineMs = 800;
				int deadlineTick = System.Environment.TickCount + deadlineMs;
				while (read < size) {
					int remaining = deadlineTick - System.Environment.TickCount;
					if (remaining <= 0) {
						error = $"Read timeout ({read}/{size})";
						return false;
					}
					var ar = pipe.BeginRead(buffer, read, size - read, null, null);
					if (!ar.AsyncWaitHandle.WaitOne(remaining)) {
						error = $"Read timeout ({read}/{size})";
						return false;
					}
					int n = pipe.EndRead(ar);
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
				// Bounded by a short deadline so we don't block.
				try {
					int drainDeadline = System.Environment.TickCount + 100;
					var drain = new byte[256];
					while (System.Environment.TickCount < drainDeadline) {
						int remaining = drainDeadline - System.Environment.TickCount;
						if (remaining <= 0) break;
						var ar = pipe.BeginRead(drain, 0, drain.Length, null, null);
						if (!ar.AsyncWaitHandle.WaitOne(remaining)) break;
						if (pipe.EndRead(ar) <= 0) break;
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
			// Prefer textStatus (per-entry literal state) over m_status (TortoiseSVN's
			// recursive-rollup status that bubbles parent state down).
			//
			// Real-world failure mode this avoids:
			//   - You have unversioned/Foo/bar.txt inside an unversioned folder unversioned/Foo
			//   - TortoiseSVN rolls m_status up so bar.txt's m_status reports the parent's
			//     synthesized state, which can be Normal (3) for the project root view.
			//   - textStatus on the same response is 2 (svn_wc_status_unversioned) — correct.
			// Falling back to m_status only when textStatus is uninformative (0/None/9-Merged)
			// matches what TortoiseSVN's shell extension does for individual file icons.
			sbyte overall = resp.textStatus;
			if (overall <= 0 || overall == 1 /*svn_wc_status_none*/ || overall == 9 /*merged*/) {
				if (resp.status > 0) overall = resp.status;
			}
			VCFileStatus fileStatus = (overall >= 0 && overall < k_StatusKindMap.Length)
				? k_StatusKindMap[overall] : VCFileStatus.None;
			VCPropertiesStatus propStatus = (resp.propStatus >= 0 && resp.propStatus < k_PropStatusMap.Length)
				? k_PropStatusMap[resp.propStatus] : VCPropertiesStatus.None;

			// svn:needs-lock semantics: when a file has the svn:needs-lock property set AND
			// the working copy does not currently hold a lock, TortoiseSVN's shell extension
			// shows a "readonly / needs-lock" overlay (the same visual treatment as a generic
			// read-only file). Mirror that here so the user has a visible signal that this is
			// a lock-required file they cannot edit until they `svn lock` it.
			//
			// Only override Normal — if the file is also Modified / Added / etc., the more
			// specific state is more useful. needsLock is essentially a clean-state decorator.
			if (resp.needsLock && !resp.hasLockOwner && fileStatus == VCFileStatus.Normal) {
				fileStatus = VCFileStatus.ReadOnly;
			}

			// TSVNCacheResponse doesn't expose repo / out-of-date info — that requires "check repository"
			// which TSVNCache only does on demand. Leave RemoteStatus None; the CLI database still
			// supplies remote-changes info when "Check repo for changes" is enabled there.
			VCRemoteFileStatus remoteStatus = VCRemoteFileStatus.None;

			VCLockStatus lockStatus = resp.hasLockOwner ? VCLockStatus.LockedOther : VCLockStatus.NoLock;
			VCTreeConflictStatus treeStatus = resp.treeConflict ? VCTreeConflictStatus.TreeConflict : VCTreeConflictStatus.Normal;

			return new SVNStatusData {
				Status = fileStatus,
				PropertiesStatus = propStatus,
				LockStatus = lockStatus,
				RemoteStatus = remoteStatus,
				SwitchedExternalStatus = VCSwitchedExternal.Normal,  // not exposed via this protocol slice
				TreeConflictStatus = treeStatus,
				Path = NativeToAssetPath(nativePath),
				LockDetails = LockDetails.Empty,
			};
		}

		// ── Diagnostics ──────────────────────────────────────────────────────
		private static string DumpFirstWords(TSVNCacheResponseHeader h)
		{
			return $"kind={h.kind} needsLock={h.needsLock} treeConflict={h.treeConflict} hasLockOwner={h.hasLockOwner} " +
			       $"textStatus={h.textStatus} propStatus={h.propStatus} status={h.status} cmtRev={h.cmtRev}";
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
