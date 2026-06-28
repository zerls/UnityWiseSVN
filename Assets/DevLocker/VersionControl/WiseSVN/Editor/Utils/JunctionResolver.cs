// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Utils
{
	/// <summary>
	/// Resolves NTFS directory junctions (created via <c>mklink /J</c>) under the Unity
	/// project root, so that SVN operations can be issued against the REAL working-copy
	/// path that owns the <c>.svn</c> metadata.
	///
	/// Why this exists
	/// ───────────────
	/// Large monorepos (80GB+) frequently share asset directories between multiple
	/// Unity projects via <c>mklink /J Assets/SharedArt D:/CompanyShared/Art</c>. To
	/// Unity, <c>Assets/SharedArt/foo.png</c> looks like a normal asset. To SVN, the
	/// <c>.svn</c> metadata lives at <c>D:/CompanyShared/Art/.svn</c> — running
	/// <c>svn status Assets/SharedArt/foo.png</c> with TortoiseSVN frequently behaves
	/// inconsistently across versions (some treat the junction transparently, some
	/// return E155007 "not a working copy"). To make the integration robust regardless
	/// of how the underlying CLI handles junctions, WE TRANSLATE PATHS to the real
	/// location before invoking <c>svn</c> commands, and translate back when storing
	/// results into the in-memory database keyed by Unity asset path.
	///
	/// Behavior summary
	/// ────────────────
	/// • Scans top-level entries under <c>Assets/</c>, <c>Packages/</c>, and
	///   <c>ProjectSettings/</c> for the <c>FileAttributes.ReparsePoint</c> bit.
	/// • For each junction, resolves the real path via
	///   <c>GetFinalPathNameByHandle</c> on a CreateFile handle opened with
	///   <c>FILE_FLAG_BACKUP_SEMANTICS</c> (the only way to get a directory handle
	///   on Windows).
	/// • Caches the result for the session. Re-scans on
	///   <c>EditorApplication.focusChanged</c> (debounced) — junctions almost never
	///   change at runtime.
	/// • Provides <c>ToRealPath</c> / <c>ToAssetPath</c> APIs that callers use to
	///   convert path strings around svn invocations.
	///
	/// Platform notes
	/// ──────────────
	/// • This functionality is Windows-only. On macOS/Linux the resolver returns the
	///   input path unchanged, and <c>HasJunctions</c> is always false.
	/// • Symbolic links (<c>mklink /D</c> directory symlinks, <c>mklink</c> file
	///   symlinks) are also handled — <c>FileAttributes.ReparsePoint</c> matches all
	///   reparse-point types, and <c>GetFinalPathNameByHandle</c> resolves them all.
	/// </summary>
	internal static class JunctionResolver
	{
		// Asset-relative junction-root path (e.g. "Assets/SharedArt") → real native path
		// (e.g. "D:/CompanyShared/Art"). Both keys and values use forward slashes
		// internally so the lookups in ToRealPath / ToAssetPath are slash-agnostic.
		// Sorted by descending key length so the longest-match prefix wins (in case
		// of nested junctions, though those are pathological).
		private static readonly List<KeyValuePair<string, string>> s_LinkToReal = new List<KeyValuePair<string, string>>();
		private static readonly List<KeyValuePair<string, string>> s_RealToLink = new List<KeyValuePair<string, string>>();
		private static readonly ReaderWriterLockSlim s_Lock = new ReaderWriterLockSlim();

		// Cheap fast-path flag — set when scan finds at least one junction. Callers
		// short-circuit translation when there are no junctions to consider.
		public static bool HasJunctions { get; private set; }

		// Editor-session caches for the last full-scan timestamp; cheap re-scan on focus.
		private static double s_LastScanTime;
		private const double k_ScanDebounceSeconds = 5.0;

		[InitializeOnLoadMethod]
		private static void InitializeOnLoad()
		{
			// Initial scan deferred to delayCall so static-constructor ordering with
			// SVNPreferencesManager / WiseSVNIntegration doesn't deadlock.
			EditorApplication.delayCall += () => Rescan(force: true);

			// Rescan on focus regain — covers external mklink/rmdir while editor was bg.
			EditorApplication.focusChanged += focused => {
				if (focused) Rescan(force: false);
			};
		}

		/// <summary>
		/// Translates an asset-relative path (e.g. <c>"Assets/SharedArt/foo.png"</c>) to
		/// the real native path that the SVN working-copy metadata resolves under
		/// (e.g. <c>"D:/CompanyShared/Art/foo.png"</c>). Falls through unchanged when
		/// no junction prefix matches.
		///
		/// Callers should pass this result to <c>svn</c> commands so the working copy
		/// is found regardless of how the SVN CLI handles reparse points on the
		/// running platform.
		/// </summary>
		public static string ToRealPath(string assetPath)
		{
			if (!HasJunctions || string.IsNullOrEmpty(assetPath)) return assetPath;
			string p = NormalizeSlashes(assetPath);
			s_Lock.EnterReadLock();
			try {
				foreach (var kv in s_LinkToReal) {
					if (PathStartsWith(p, kv.Key)) {
						// kv.Key always has a trailing '/'. When p is the exact junction root
						// (no trailing slash, p.Length == kv.Key.Length - 1), Substring(kv.Key.Length)
						// would be out of range. Guard: return the real root path directly in that case.
						return p.Length >= kv.Key.Length
							? kv.Value + p.Substring(kv.Key.Length)
							: kv.Value;
					}
				}
			} finally { s_Lock.ExitReadLock(); }
			return assetPath;
		}

		/// <summary>
		/// Inverse of <see cref="ToRealPath"/> — given a real-FS path returned by SVN
		/// (e.g. <c>"D:/CompanyShared/Art/foo.png"</c>), produces the asset-relative
		/// path Unity knows (e.g. <c>"Assets/SharedArt/foo.png"</c>). Falls through
		/// unchanged when no junction target matches.
		/// </summary>
		public static string ToAssetPath(string nativePath)
		{
			if (!HasJunctions || string.IsNullOrEmpty(nativePath)) return nativePath;
			string p = NormalizeSlashes(nativePath);
			s_Lock.EnterReadLock();
			try {
				foreach (var kv in s_RealToLink) {
					if (PathStartsWith(p, kv.Key)) {
						// Same guard as ToRealPath — exact root match has no suffix to extract.
						return p.Length >= kv.Key.Length
							? kv.Value + p.Substring(kv.Key.Length)
							: kv.Value;
					}
				}
			} finally { s_Lock.ExitReadLock(); }
			return nativePath;
		}

		/// <summary>
		/// Returns true when the given asset-relative path lives under a known
		/// junction root (used by the optional overlay-icon layer to draw a
		/// junction badge).
		/// </summary>
		public static bool IsUnderJunction(string assetPath)
		{
			if (!HasJunctions || string.IsNullOrEmpty(assetPath)) return false;
			string p = NormalizeSlashes(assetPath);
			s_Lock.EnterReadLock();
			try {
				foreach (var kv in s_LinkToReal) {
					if (PathStartsWith(p, kv.Key)) return true;
				}
			} finally { s_Lock.ExitReadLock(); }
			return false;
		}

		/// <summary>
		/// Returns true when the given asset-relative path is EXACTLY a junction root
		/// (not just a descendant). Used by the overlay layer to draw the junction
		/// badge only on the link folder itself.
		/// </summary>
		public static bool IsJunctionRoot(string assetPath)
		{
			if (!HasJunctions || string.IsNullOrEmpty(assetPath)) return false;
			string p = NormalizeSlashes(assetPath).TrimEnd('/');
			s_Lock.EnterReadLock();
			try {
				foreach (var kv in s_LinkToReal) {
					if (string.Equals(p, kv.Key.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
						return true;
				}
			} finally { s_Lock.ExitReadLock(); }
			return false;
		}

		/// <summary>
		/// Snapshot of every known junction's asset-relative root path (without trailing
		/// slash), suitable for iterating over from background threads. Returns an array
		/// so the caller can iterate without holding the resolver's read lock.
		/// </summary>
		public static string[] EnumerateJunctionRoots()
		{
			s_Lock.EnterReadLock();
			try {
				if (s_LinkToReal.Count == 0) return Array.Empty<string>();
				var arr = new string[s_LinkToReal.Count];
				for (int i = 0; i < s_LinkToReal.Count; i++) {
					arr[i] = s_LinkToReal[i].Key.TrimEnd('/');
				}
				return arr;
			} finally { s_Lock.ExitReadLock(); }
		}

		/// <summary>
		/// Forces a fresh scan. Called from the diagnostics window for debugging;
		/// also fired automatically from focus-changed and once at startup.
		/// </summary>
		public static void Rescan(bool force = false)
		{
#if !UNITY_EDITOR_WIN
			// Junctions are a Windows concept. macOS/Linux symlinks could be handled
			// similarly via readlink(2) but TortoiseSVN's working-copy model on those
			// platforms makes this much rarer in practice; skip for now.
			return;
#else
			double now = EditorApplication.timeSinceStartup;
			if (!force && (now - s_LastScanTime) < k_ScanDebounceSeconds) return;
			s_LastScanTime = now;

			var linkToReal = new List<KeyValuePair<string, string>>();
			var realToLink = new List<KeyValuePair<string, string>>();

			try {
				string projectRoot = NormalizeSlashes(WiseSVNIntegration.ProjectRootNative);
				ScanTopLevel(projectRoot, "Assets", linkToReal, realToLink);
				ScanTopLevel(projectRoot, "Packages", linkToReal, realToLink);
				ScanTopLevel(projectRoot, "ProjectSettings", linkToReal, realToLink);
			} catch (Exception ex) {
				Debug.LogWarning($"[WiseSVN] JunctionResolver scan failed: {ex.GetType().Name}: {ex.Message}");
			}

			// Sort longest-prefix-first so nested junction roots are matched correctly.
			linkToReal.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
			realToLink.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));

			s_Lock.EnterWriteLock();
			try {
				s_LinkToReal.Clear();
				s_RealToLink.Clear();
				s_LinkToReal.AddRange(linkToReal);
				s_RealToLink.AddRange(realToLink);
				HasJunctions = s_LinkToReal.Count > 0;
			} finally { s_Lock.ExitWriteLock(); }

			if (HasJunctions) {
				Debug.Log($"[WiseSVN] JunctionResolver detected {s_LinkToReal.Count} junction(s) under the project.");
			}
#endif
		}

#if UNITY_EDITOR_WIN
		// Recurse one level deep under each top-level folder (Assets/, Packages/,
		// ProjectSettings/). We don't go deeper because:
		//   1. Deep junctions inside a regular folder are rare in practice.
		//   2. Walking the full Assets/ tree on an 80GB project is 30s+.
		// Add Rescan calls from elsewhere if a specific subdir is suspected.
		private static void ScanTopLevel(string projectRoot, string topName,
			List<KeyValuePair<string, string>> linkToReal,
			List<KeyValuePair<string, string>> realToLink)
		{
			string topDir = Path.Combine(projectRoot, topName);
			if (!Directory.Exists(topDir)) return;

			foreach (string dir in EnumerateImmediateChildDirs(topDir)) {
				try {
					var attrs = File.GetAttributes(dir);
					if ((attrs & FileAttributes.ReparsePoint) == 0) continue;

					string realPath = ResolveReparsePoint(dir);
					if (string.IsNullOrEmpty(realPath)) continue;

					// Stored keys: forward-slash, ending with '/' so prefix-match logic
					// can use `StartsWith(key + '/')` semantics without special cases.
					string linkKey = NormalizeSlashes(Path.Combine(topName, Path.GetFileName(dir))) + "/";
					string realKey = NormalizeSlashes(realPath) + "/";

					linkToReal.Add(new KeyValuePair<string, string>(linkKey, realKey));
					realToLink.Add(new KeyValuePair<string, string>(realKey, linkKey));
				} catch (Exception) {
					// Permission errors / transient FS races — ignore and continue.
				}
			}
		}

		private static IEnumerable<string> EnumerateImmediateChildDirs(string root)
		{
			// EnumerationOptions lets us avoid recursing while keeping the call cheap.
			try {
				return Directory.EnumerateDirectories(root, "*",
					new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0 });
			} catch {
				return Array.Empty<string>();
			}
		}

		// Native helpers: open the directory with FILE_FLAG_BACKUP_SEMANTICS (the only
		// flag combination CreateFile accepts for directories), then resolve via
		// GetFinalPathNameByHandle. Returns the canonical real path or null on failure.
		private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
		private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;  // unused — we WANT to follow
		private const uint OPEN_EXISTING = 3;
		private const uint GENERIC_READ = 0; // 0 = no access; allowed for query handles
		private const uint FILE_SHARE_READ = 1;
		private const uint FILE_SHARE_WRITE = 2;
		private const uint FILE_SHARE_DELETE = 4;
		private const uint VOLUME_NAME_DOS = 0;

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
			IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool CloseHandle(IntPtr hObject);

		[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		private static extern uint GetFinalPathNameByHandleW(IntPtr hFile, System.Text.StringBuilder lpszFilePath,
			uint cchFilePath, uint dwFlags);

		private static string ResolveReparsePoint(string path)
		{
			IntPtr h = CreateFileW(path, GENERIC_READ,
				FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
				IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
			if (h == new IntPtr(-1)) return null;
			try {
				var sb = new System.Text.StringBuilder(1024);
				uint len = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, VOLUME_NAME_DOS);
				if (len == 0 || len > sb.Capacity) return null;
				string result = sb.ToString(0, (int)len);
				// Strip the \\?\ device prefix that GetFinalPathNameByHandle returns.
				if (result.StartsWith(@"\\?\")) result = result.Substring(4);
				return result;
			} finally {
				CloseHandle(h);
			}
		}
#endif

		// Path comparison helpers — Windows paths are case-insensitive; treat keys/values
		// as case-insensitive but preserve their original case for round-trips.
		private static string NormalizeSlashes(string p) => p?.Replace('\\', '/');

		// Returns true when `path` is equal to `prefix` (after trimming trailing '/')
		// OR `path` has `prefix` as its leading directory component.
		private static bool PathStartsWith(string path, string prefix)
		{
			// prefix is stored with trailing '/'. path may or may not match it directly.
			if (path.Length < prefix.Length - 1) return false;
			// Strict prefix: "Assets/SharedArt/foo" startsWith "Assets/SharedArt/"
			if (path.Length >= prefix.Length &&
				string.Compare(path, 0, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) == 0)
				return true;
			// Exact match without trailing slash: "Assets/SharedArt" == "Assets/SharedArt/" - 1
			if (path.Length == prefix.Length - 1 &&
				string.Compare(path, 0, prefix, 0, path.Length, StringComparison.OrdinalIgnoreCase) == 0)
				return true;
			return false;
		}
	}
}
