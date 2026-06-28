// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.Preferences;
using DevLocker.VersionControl.WiseSVN.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN
{
	// HACK: This should be internal, but due to inheritance issues it can't be.
	[Serializable]
	public class GuidStatusDatasBind
	{
		[UnityEngine.Serialization.FormerlySerializedAs("Guid")]
		public string Key;	// Guid or Path (if deleted).

		[UnityEngine.Serialization.FormerlySerializedAs("Data")]
		public SVNStatusData MergedStatusData;	// Merged data

		public SVNStatusData AssetStatusData;
		public SVNStatusData MetaStatusData;

		public string AssetPath => MergedStatusData.Path;

		public IEnumerable<SVNStatusData> GetSourceStatusDatas()
		{
			yield return AssetStatusData;
			yield return MetaStatusData;
		}
	}

	/// <summary>
	/// Caches known statuses for files and folders.
	/// Refreshes periodically or if file was modified or moved.
	/// Status extraction happens in another thread so overhead should be minimal.
	///
	/// NOTE: Keep in mind that this cache can be out of date.
	///		 If you want up to date information, use the WiseSVNIntegration API for direct SVN queries.
	/// </summary>
	public class SVNStatusesDatabase : Utils.DatabasePersistentSingleton<SVNStatusesDatabase, GuidStatusDatasBind>
	{
		public const string INVALID_GUID = "00000000000000000000000000000000";
		public const string ASSETS_FOLDER_GUID = "00000000000000001000000000000000";


		// Note: not all of these are rendered. Check the Database icons.
		private readonly static Dictionary<VCFileStatus, int> m_StatusPriority = new Dictionary<VCFileStatus, int> {
			{ VCFileStatus.Conflicted, 10 },
			{ VCFileStatus.Obstructed, 10 },
			{ VCFileStatus.Modified, 8},
			{ VCFileStatus.Added, 6},
			{ VCFileStatus.Deleted, 6},
			{ VCFileStatus.Missing, 6},
			{ VCFileStatus.Replaced, 5},
			{ VCFileStatus.Ignored, 3},
			{ VCFileStatus.Unversioned, 1},
			{ VCFileStatus.External, 0},
			{ VCFileStatus.Normal, 0},
		};

		private SVNPreferencesManager.PersonalPreferences m_PersonalPrefs => SVNPreferencesManager.Instance.PersonalPrefs;
		private SVNPreferencesManager.ProjectPreferences m_ProjectPrefs => SVNPreferencesManager.Instance.ProjectPrefs;

		private volatile SVNPreferencesManager.PersonalPreferences m_PersonalCachedPrefs;
		private volatile SVNPreferencesManager.ProjectPreferences m_ProjectCachedPrefs;
		private volatile bool m_DownloadRepositoryChangesCached = false;


		/// <summary>
		/// The database update can be enabled, but the SVN integration to be disabled as a whole.
		/// </summary>
		public override bool IsActive => SVNPreferencesManager.Instance.IsIntegrationEnabled && m_PersonalPrefs.PopulateStatusesDatabase;
		public override bool TemporaryDisabled => WiseSVNIntegration.TemporaryDisabled || WiseSVNIntegration.IsBuildingPlayer;
		public override bool DoTraceLogs => (m_PersonalCachedPrefs.TraceLogs & SVNTraceLogs.DatabaseUpdates) != 0;

		public override double RefreshInterval => m_PersonalPrefs.AutoRefreshDatabaseInterval;

		// Any assets contained in these folders are considered unversioned.
		//
		// Written from the background gather thread (line ~328), read from main-thread
		// ItemOnGUI via GetKnownStatusData. We rely on .NET's atomic-reference-write
		// guarantee (reference assignment is atomic on Unity's supported platforms) for
		// the cross-thread visibility — no `volatile` needed. (Earlier code had `volatile`
		// but Unity's serializer ignores volatile fields, which is why these arrays were
		// resetting to empty on every assembly reload even with [SerializeField] applied.)
		//
		// [SerializeField]: across Unity assembly reload these are the ONLY source of
		// Unversioned/Ignored synthesis for paths that don't have an entry in m_Data
		// (svn status only emits non-Normal entries, so untracked files outside m_Data
		// rely on these folder-prefix lists). Without serialization they reset to empty
		// on every reload and the user sees no Unversioned icons until they toggle a
		// preference, which triggers InvalidateDatabase and a rescan.
		[SerializeField] private string[] m_UnversionedFolders = new string[0];

		// Nested SVN repositories (that have ".svn" in them). NOTE: These are not external, just check-out inside check-out.
		public IReadOnlyCollection<string> NestedRepositories => Array.AsReadOnly(m_NestedRepositories);
		[SerializeField] private string[] m_NestedRepositories = new string[0];

		// SVN-Ignored files and folders. Same reload-survival rationale as m_UnversionedFolders.
		[SerializeField] private string[] m_IgnoredEntries = new string[0];
		// SVN-Global-ignored entries are stored separately as they are checked only once, because they are much slower.
		[SerializeField] private string[] m_GlobalIgnoredEntries = new string[0];

		// Serialized alongside m_GlobalIgnoredEntries — if the entries survive reload, the
		// flag that says "we already collected them" must survive too, otherwise the next
		// post-reload gather will re-run the expensive global-ignores collection and (worse)
		// race-overwrite the still-valid serialized array with a partial scan.
		// Plain bool, not volatile — Unity ignores volatile when serializing, and bool reads
		// are atomic in .NET anyway; the previous read happens before the next gather thread
		// starts, so no memory barrier is needed.
		[SerializeField] public bool m_GlobalIgnoresCollected = false;

		// ─────────────────────────────────────────────────────────────────────────
		// guid → m_Data index accelerator.
		//
		// Why: with the parent class's m_Data as a List<GuidStatusDatasBind>, every
		// GetKnownStatusData / SetStatusData / RemoveStatusData call does a linear scan.
		// On a 80GB monorepo with the raised SanityStatusesLimit (4000) and 50 visible
		// Project window entries per frame, that's 50 × 4000 = 200k string comparisons
		// per frame — measurable on the main thread. With the accelerator each lookup
		// is one Dictionary hash + compare.
		//
		// We keep the List (required for [SerializeField] persistence + base-class APIs)
		// and add a transient Dictionary accelerator built lazily on first read after
		// the data changes. Rebuilds are cheap (O(N) one-shot) and happen at most once
		// per InvalidateDatabase / SetStatusData / RemoveStatusData cycle.
		//
		// NOT [SerializeField] — rebuilt from m_Data on demand. NonSerialized so Unity's
		// serializer doesn't try to roundtrip a Dictionary (which it can't).
		[NonSerialized] private Dictionary<string, int> m_GuidIndex;
		[NonSerialized] private bool m_GuidIndexDirty = true;

		// Mark the accelerator stale. Called from every site that mutates m_Data.
		private void InvalidateGuidIndex() => m_GuidIndexDirty = true;

		// Build (or rebuild) the guid→index lookup. Cheap O(N) — runs at most once after
		// each batch of mutations. Indices are List positions, NOT struct refs, because
		// the entries themselves are mutated in place via GuidStatusDatasBind reference type.
		private Dictionary<string, int> GetGuidIndex()
		{
			if (m_GuidIndex == null) {
				m_GuidIndex = new Dictionary<string, int>(m_Data.Count, StringComparer.Ordinal);
			}
			if (m_GuidIndexDirty) {
				m_GuidIndex.Clear();
				for (int i = 0; i < m_Data.Count; i++) {
					var k = m_Data[i].Key;
					if (!string.IsNullOrEmpty(k)) m_GuidIndex[k] = i;
				}
				m_GuidIndexDirty = false;
			}
			return m_GuidIndex;
		}

		/// <summary>
		/// The collected statuses are not complete due to some reason (for example, they were too many).
		/// </summary>
		public bool DataIsIncomplete { get; private set; }

		/// <summary>
		/// Last error encountered. Will be set in a worker thread.
		/// </summary>
		public StatusOperationResult LastError { get; private set; }

		[SerializeField]
		private bool m_PartialBranchingWarned = false;

		//
		//=============================================================================
		//
		#region Initialize

		public override void Initialize(bool freshlyCreated)
		{
			// HACK: Force WiseSVN initialize first, so it doesn't happen in the thread.
			WiseSVNIntegration.ProjectRootUnity.StartsWith(string.Empty);

			SVNPreferencesManager.Instance.PreferencesChanged += RefreshActive;
			RefreshActive();

			// Copy on init, RefreshActive() doesn't do it anymore.
			m_PersonalCachedPrefs = m_PersonalPrefs.Clone();
			m_ProjectCachedPrefs = m_ProjectPrefs.Clone();

			// Refresh when the editor regains focus — closes the up-to-60s staleness window
			// caused by external TortoiseSVN/CLI operations. 2-second debounce defeats
			// alt-tab spam. Gated by RefreshDatabaseOnFocus personal preference.
			EditorApplication.focusChanged -= OnEditorFocusChanged;
			EditorApplication.focusChanged += OnEditorFocusChanged;

			base.Initialize(freshlyCreated);
		}

		private void OnEditorFocusChanged(bool focused)
		{
			if (!focused || !IsActive || TemporaryDisabled || IsUpdating) return;
			if (!SVNPreferencesManager.Instance.PersonalPrefs.RefreshDatabaseOnFocus) return;
			// Debounce: skip if we refreshed in the last ~2 seconds.
			if (EditorApplication.timeSinceStartup - m_LastFocusRefresh < 2.0) return;
			m_LastFocusRefresh = EditorApplication.timeSinceStartup;
			InvalidateDatabase();
		}

		[NonSerialized] private double m_LastFocusRefresh;

		protected override void RefreshActive()
		{
			base.RefreshActive();
			// base.RefreshActive() may have cleared m_Data when transitioning to !IsActive.
			// Drop the guid index in lockstep so it doesn't return stale indices.
			InvalidateGuidIndex();

			if (!IsActive) {
				DataIsIncomplete = false;
			}

			// Copy them so they can be safely accessed from the worker thread.
			//m_PersonalCachedPrefs = m_PersonalPrefs.Clone();
			//m_ProjectCachedPrefs = m_ProjectPrefs.Clone();
			// Bad idea - can still be changed while thread is working causing bugs.
		}

		#endregion


		//
		//=============================================================================
		//
		#region Populate Data

		// Sanity caps to keep memory + ItemOnGUI cost bounded.
		//
		// Originally 600 / 250 / 250 — adequate for typical small/medium projects but
		// too conservative for the 80GB monorepo regime we now target. The cost driver
		// is m_UnversionedFolders / m_IgnoredEntries prefix-match scans per visible
		// Project window entry per frame. With strings averaging 64 chars, even at 2000
		// folders each match is ~50ns × 2000 = 100µs / call; 50 visible entries = 5ms/
		// frame, still under a third of a 60Hz frame.
		//
		// SanityStatusesLimit doesn't materially impact ItemOnGUI (it's O(1) via the
		// guid index), only memory + serialization cost. 4000 entries × ~256 bytes per
		// GuidStatusDatasBind ≈ 1MB. Acceptable.
		//
		// DataIsIncomplete still triggers if we overflow these — telling the user the
		// working copy is too noisy to fully reflect. Raising the bar pushes that
		// warning rarer for big-project setups.
		private const int SanityStatusesLimit = 4000;
		private const int SanityUnversionedFoldersLimit = 2000;
		private const int SanityIgnoresLimit = 2000;

		protected override void StartDatabaseUpdate()
		{
			// Copy them so they can be safely accessed from the worker thread.
			m_PersonalCachedPrefs = m_PersonalPrefs.Clone();
			m_ProjectCachedPrefs = m_ProjectPrefs.Clone();

			m_DownloadRepositoryChangesCached = SVNPreferencesManager.Instance.DownloadRepositoryChanges && !SVNPreferencesManager.Instance.NeedsToAuthenticate;

			LastError = StatusOperationResult.Success;

			base.StartDatabaseUpdate();
		}

		// Executed in a worker thread.
		protected override GuidStatusDatasBind[] GatherDataInThread()
		{
			List<SVNStatusData> statuses = new List<SVNStatusData>();
			List<string> unversionedFolders = new List<string>();
			List<string> nestedRepositories = new List<string>();
			List<string> ignoredEntries = new List<string>();
			List<string> globalIgnoredEntries = new List<string>();
			GuidStatusDatasBind[] pendingData;

			var timings = new StringBuilder("SVNStatusesDatabase Gathering Data Timings:\n");
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();

			using (var reporter = new WiseSVNIntegration.ResultConsoleReporter(true, WiseSVNIntegration.Silent, "SVNStatusesDatabase Operations:")) {

				//GatherStatusDataInThreadRecursive("Assets", statuses, unversionedFolders, nestedRepositories, reporter);
				//GatherStatusDataInThreadRecursive("Packages", statuses, unversionedFolders, nestedRepositories, reporter);

				// Instead of asking twice, do it once for everything and filter by path.
				GatherStatusDataInThreadRecursive("", statuses, unversionedFolders, nestedRepositories, reporter);
				statuses.RemoveAll(s => !s.Path.StartsWith("Assets/") && !s.Path.StartsWith("Packages/"));

				// NTFS junctions (mklink /J): the main scan sees each junction root as
				// "? Unversioned" (the .svn metadata is in the REAL target directory, not the
				// link path). Keeping these entries causes two problems:
				//
				//   1. The junction folder gets Unversioned status in m_Data. When files inside
				//      ARE modified, AddModifiedFolders overwrites that with Modified (correct).
				//      After commit, the next scan adds Unversioned again — folder shows
				//      Unversioned even though the real WC is clean. Icon is wrong.
				//
				//   2. GetKnownStatusData falls back to m_UnversionedFolders for any GUID
				//      under the junction prefix, returning Unversioned for committed Normal
				//      files because the junction root was in that list.
				//
				// Fix: strip junction root entries from both lists here. The junction scan
				// below will query the REAL working-copy path and get the correct status.
				// AddModifiedFolders propagation from modified files inside will still set
				// the folder icon to Modified when appropriate; if all files are clean the
				// junction folder simply has no entry (Normal / no icon).
				if (Utils.JunctionResolver.HasJunctions) {
					var junctionRoots = Utils.JunctionResolver.EnumerateJunctionRoots();
					statuses.RemoveAll(s => {
						string bare = s.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
							? s.Path.Substring(0, s.Path.Length - 5)
							: s.Path;
						foreach (var root in junctionRoots) {
							if (string.Equals(bare, root, StringComparison.OrdinalIgnoreCase))
								return true;
						}
						return false;
					});
					for (int i = unversionedFolders.Count - 1; i >= 0; i--) {
						foreach (var root in junctionRoots) {
							if (string.Equals(unversionedFolders[i], root, StringComparison.OrdinalIgnoreCase)) {
								unversionedFolders.RemoveAt(i);
								break;
							}
						}
					}
					GatherJunctionStatusesInThread(statuses, unversionedFolders, reporter);
				}

				var slashes = new char[] { '/', '\\' };

				// Add excluded items explicitly so their icon shows even when "Normal status green icon" is disabled.
				foreach (string excludedPath in m_PersonalCachedPrefs.Exclude.Concat(m_ProjectCachedPrefs.Exclude)) {
					if (excludedPath.IndexOfAny(slashes) != -1) {   // Only paths
						statuses.Add(new SVNStatusData() { Path = excludedPath, Status = VCFileStatus.Excluded, LockDetails = LockDetails.Empty });
					}
				}

				timings.AppendLine($"Gather {statuses.Count} Status Data - {stopwatch.ElapsedMilliseconds / 1000f}s");
				stopwatch.Restart();


				if (m_PersonalCachedPrefs.PopulateIgnoresDatabase) {
					//GatherIgnoresInThread("Assets", ignoredEntries, reporter);
					//GatherIgnoresInThread("Packages", ignoredEntries, reporter);

					// Instead of asking twice, do it once for everything and filter by path.
					GatherIgnoresInThread("", ignoredEntries, reporter);
					ignoredEntries.RemoveAll(p => !p.StartsWith("Assets/") && !p.StartsWith("Packages/"));


					timings.AppendLine($"Gather {ignoredEntries.Count} ignores - {stopwatch.ElapsedMilliseconds / 1000f}s");
					stopwatch.Restart();

					if (!m_GlobalIgnoresCollected) {
						GatherGlobalIgnoresInThread(globalIgnoredEntries, reporter);

						timings.AppendLine($"Gather {globalIgnoredEntries.Count} svn:global-ignores - {stopwatch.ElapsedMilliseconds / 1000f}s");
						stopwatch.Restart();
					}
				}

				// Non-lossy trimming: keep every status that carries signal (non-Normal, or has lock/remote info,
				// or is a scene — those are always relevant for the editor). Drop only Normal+clean entries when
				// over the limit. DataIsIncomplete fires only when the SIGNAL-carrying set itself overflows;
				// otherwise the user sees a complete picture even with many clean Normal noise entries.
				var signalful = statuses
					.Where(s => s.Status != VCFileStatus.Normal
						|| s.LockStatus != VCLockStatus.NoLock
						|| s.RemoteStatus != VCRemoteFileStatus.None
						|| s.Path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
					.ToList();

				DataIsIncomplete = signalful.Count > SanityStatusesLimit
					|| unversionedFolders.Count >= SanityUnversionedFoldersLimit
					|| ignoredEntries.Count > SanityIgnoresLimit
					|| globalIgnoredEntries.Count > SanityIgnoresLimit;

				// Sort unversioned folders shallowest-first so the cap keeps the broadest coverage —
				// ArePathsNested makes a shallow ancestor entry cover every descendant for free.
				if (unversionedFolders.Count >= SanityUnversionedFoldersLimit) {
					unversionedFolders.Sort((a, b) => a.Count(c => c == '/' || c == '\\').CompareTo(b.Count(c => c == '/' || c == '\\')));
					unversionedFolders.RemoveRange(SanityUnversionedFoldersLimit, unversionedFolders.Count - SanityUnversionedFoldersLimit);
				}

				if (ignoredEntries.Count >= SanityIgnoresLimit) {
					ignoredEntries.RemoveRange(SanityIgnoresLimit, ignoredEntries.Count - SanityIgnoresLimit);
				}

				if (globalIgnoredEntries.Count >= SanityIgnoresLimit) {
					globalIgnoredEntries.RemoveRange(SanityIgnoresLimit, globalIgnoredEntries.Count - SanityIgnoresLimit);
				}

				// HACK: the base class works with the DataType for pending data. Guid won't be used.
				IEnumerable<SVNStatusData> finalStatuses = signalful;
				if (signalful.Count > SanityStatusesLimit) {
					finalStatuses = signalful.Take(SanityStatusesLimit);
				}
				pendingData = finalStatuses
					.Select(s => new GuidStatusDatasBind() { MergedStatusData = s })
					.ToArray();

				// Strip project-root prefix so entries become relative (e.g. "Assets/Library").
				// Strip with both slash flavours because GatherIgnoresInThread converts '\\' → '/'
				// before the paths reach here, so the native-backslash projectRootPath won't match.
				string projectRootPath  = WiseSVNIntegration.ProjectRootNative + '\\';
				string projectRootPathF = WiseSVNIntegration.ProjectRootNative.Replace('\\', '/') + '/';

				m_IgnoredEntries = ignoredEntries
					.Select(path => path.Replace(projectRootPath, ""))      // strip if backslash root matched
					.Select(path => path.Replace(projectRootPathF, ""))     // strip if forward-slash root matched
					.Select(path => path.Replace('\\', '/'))
					.Where(path => path.Contains('/'))                      // must be a proper relative sub-path, not a bare name
					.Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
					            || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
					.Distinct()
					.ToArray();

				// Log first-time populate for diagnostics (one-shot so no spam).
				if (DoTraceLogs && m_IgnoredEntries.Length > 0) {
					Debug.Log($"[WiseSVN] m_IgnoredEntries ({m_IgnoredEntries.Length}): " + string.Join(", ", m_IgnoredEntries.Take(10)));
				}

				if (!m_GlobalIgnoresCollected) {
					m_GlobalIgnoredEntries = globalIgnoredEntries
						.Select(path => path.Replace(projectRootPath, ""))
						.Select(path => path.Replace(projectRootPathF, ""))
						.Select(path => path.Replace('\\', '/'))
						.Where(path => path.Contains('/'))
						.Where(path => path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
						            || path.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
						.Distinct()
						.ToArray();

					m_GlobalIgnoresCollected = true;
				}

				m_UnversionedFolders = unversionedFolders.ToArray();
				m_NestedRepositories = nestedRepositories.ToArray();

				if (!DoTraceLogs && LastError != StatusOperationResult.UnknownError) {
					reporter.ClearLogsAndErrorFlag();
				}
			} // Dispose reporter.

			timings.AppendLine("Gather Processing Data - " + (stopwatch.ElapsedMilliseconds / 1000f));
			stopwatch.Restart();

			if (DoTraceLogs) {
				Debug.Log(timings.ToString());
			}

			return pendingData;
		}

		private void GatherStatusDataInThreadRecursive(string repositoryPath, List<SVNStatusData> foundStatuses, List<string> foundUnversionedFolders, List<string> nestedRepositories, IShellMonitor shellMonitor)
		{
			bool offline = !m_DownloadRepositoryChangesCached && !m_ProjectCachedPrefs.EnableLockPrompt;
			var excludes = m_PersonalCachedPrefs.Exclude.Concat(m_ProjectCachedPrefs.Exclude);

			// Will get statuses of all added / modified / deleted / conflicted / unversioned files. Only normal files won't be listed.
			var statuses = new List<SVNStatusData>();
			StatusOperationResult result = WiseSVNIntegration.GetStatuses(repositoryPath, true, offline, statuses, true, WiseSVNIntegration.ONLINE_COMMAND_TIMEOUT * 2, shellMonitor);

			// Keep VCFileStatus.Missing — svn-tracked files that the user deleted locally still need a `!` icon
			// in the Project window to match TortoiseSVN's Explorer overlay.
			statuses.RemoveAll(
				s => SVNPreferencesManager.ShouldExclude(excludes, s.Path)); // TODO: This will skip overlay icons for excludes by filename.

			if (result != StatusOperationResult.Success) {
				LastError = result;
				return;
			}

			for (int i = 0; i < statuses.Count; ++i) {
				var statusData = statuses[i];

				// Statuses for entries under unversioned directories are not returned so we need to keep track of them.
				if (statusData.Status == VCFileStatus.Unversioned && Directory.Exists(statusData.Path)) {

					// Nested repositories return unknown status, but are hidden in the TortoiseSVN commit window.
					// Add their statuses to support them. Also removing this folder data should display it as normal status.
					if (Directory.Exists($"{statusData.Path}/.svn")) {

						nestedRepositories.Add(statusData.Path);

						GatherStatusDataInThreadRecursive(statusData.Path, foundStatuses, foundUnversionedFolders, nestedRepositories, shellMonitor);

						// Folder meta file could also be unversioned. This will force unversioned overlay icon to show, even though the folder status is removed.
						// Remove the meta file status as well.
						var metaIndex = statuses.FindIndex(sd => sd.Status == VCFileStatus.Unversioned && sd.Path == statusData.Path + ".meta");
						if (metaIndex != -1) {

							foundStatuses.RemoveAll(sd => sd.Path == statusData.Path + ".meta");

							statuses.RemoveAt(metaIndex);

							if (metaIndex < i) {
								--i;
							}
						}

						statuses.RemoveAt(i);
						--i;
						continue;
					}

					foundUnversionedFolders.Add(statusData.Path);
				}

				foundStatuses.Add(statusData);
			}
		}

		// Scans each NTFS junction (mklink /J) under the project as an independent SVN
		// working copy.
		//
		// Why this is a separate pass:
		//   `svn status` against the project root doesn't descend into reparse points
		//   (TortoiseSVN docs: "junctions are treated as the boundary of a working copy").
		//   Without an explicit per-junction scan, files reachable only via junctions
		//   are invisible to the Project window overlay icons and to the project-wide
		//   modified-count badge.
		//
		// Implementation notes:
		//   * Status results come back with paths relative to the SVN command's cwd
		//     (i.e. the real junction target). We rewrite them to asset paths so
		//     downstream consumers (AssetDatabase.AssetPathToGUID, m_UnversionedFolders
		//     prefix-match) keep working unchanged.
		//   * SVNFormatPath already translates link → real for us at the command level
		//     (see WiseSVNIntegration.SVNFormatPath); the work here is purely the
		//     REVERSE translation on output.
		//   * We re-use the same GatherStatusDataInThreadRecursive routine by passing
		//     the junction's asset-relative root as repositoryPath; the funnel handles
		//     translation under the hood, then we post-process to fix any output paths
		//     that came back in real-path form instead of asset-path form.
		private void GatherJunctionStatusesInThread(List<SVNStatusData> foundStatuses, List<string> foundUnversionedFolders, IShellMonitor shellMonitor)
		{
			// Use the public asset-path snapshot from JunctionResolver — we walk every link
			// root it knows about. The resolver scan is debounced; calling here is cheap.
			foreach (var linkRoot in Utils.JunctionResolver.EnumerateJunctionRoots()) {
				// Snapshot count so any new entries from this scan that come back with
				// real-FS paths get translated to asset-paths below.
				int sliceStart = foundStatuses.Count;
				int unversionedSliceStart = foundUnversionedFolders.Count;

				try {
					GatherStatusDataInThreadRecursive(linkRoot, foundStatuses, foundUnversionedFolders, new List<string>(), shellMonitor);
				} catch (System.Exception ex) {
					if (DoTraceLogs) {
						Debug.LogWarning($"[WiseSVN] Junction scan failed for {linkRoot}: {ex.GetType().Name}: {ex.Message}");
					}
					continue;
				}

				// Translate any output paths that came back as the REAL filesystem path
				// (some SVN versions emit absolute paths when invoked against a junction
				// target). Idempotent on paths that are already asset-relative.
				for (int i = sliceStart; i < foundStatuses.Count; i++) {
					var s = foundStatuses[i];
					s.Path = Utils.JunctionResolver.ToAssetPath(s.Path);
					foundStatuses[i] = s;
				}
				for (int i = unversionedSliceStart; i < foundUnversionedFolders.Count; i++) {
					foundUnversionedFolders[i] = Utils.JunctionResolver.ToAssetPath(foundUnversionedFolders[i]);
				}
			}
		}

		private void GatherIgnoresInThread(string repositoryPath, List<string> foundIgnoredEntries, IShellMonitor shellMonitor)
		{
			var propgets = new List<PropgetEntry>();

			PropOperationResult result = WiseSVNIntegration.Propget(repositoryPath, "svn:ignore", true, propgets, WiseSVNIntegration.COMMAND_TIMEOUT, shellMonitor);
			if (result != PropOperationResult.Success) {
				if (DoTraceLogs) {
					Debug.LogError($"SVN: Failed to collect svn ignored entries for \"{repositoryPath}\". Type: \"svn:ignore\".");
				}
				return;
			}

			// Keep in mind that "svn:ignore" values may include wildcards (* and ?).
			// Also all ignored entries do not appear in the "svn status" command, which we consider as "normal" status.
			// This is why we need to collect actual ignored files, as there is no good other way to recognize them.
			foreach (PropgetEntry propget in propgets) {
				foreach (string line in propget.Lines) {

					// Skip hidden folders starting with ".". Some people put comments starting with "#".
					// Example: # ---------------[ Unity generated ]------------------ #
					if (line.StartsWith(".", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#", StringComparison.OrdinalIgnoreCase) || line.StartsWith("/", StringComparison.OrdinalIgnoreCase) || line.StartsWith(":", StringComparison.OrdinalIgnoreCase))
						continue;

					// SVN ignores don't support folder paths - only names to direct files and names.
					if (line.Contains('/') || line.Contains('\\'))
						continue;

					var matchedEntries = Directory.EnumerateFileSystemEntries(propget.Path, line, SearchOption.TopDirectoryOnly)
						.Select(p => p.Replace('\\', '/'));

					foundIgnoredEntries.AddRange(matchedEntries);
				}
			}
		}

		private void GatherGlobalIgnoresInThread(List<string> foundIgnoredEntries, IShellMonitor shellMonitor)
		{
			var propgets = new List<PropgetEntry>();

			PropOperationResult result = WiseSVNIntegration.Propget(WiseSVNIntegration.ProjectRootNative, "svn:global-ignores", true, propgets, WiseSVNIntegration.COMMAND_TIMEOUT, shellMonitor);
			if (result != PropOperationResult.Success) {
				if (DoTraceLogs) {
					Debug.LogError($"SVN: Failed to collect svn ignored entries for \"{WiseSVNIntegration.ProjectRootNative}\". Type: \"svn:global-ignores\".");
				}
				return;
			}

			foreach (PropgetEntry propget in propgets) {

				// Start folder is returned as "."
				if (propget.Path == ".") {

					// Enumerating the root folder would be too expensive (Library is huge). Just enumerate meaningful folders.
					// "svn:global-ignores" are applied recursively to all sub-folders.
					foreach (string line in propget.Lines) {

						// Skip hidden folders starting with ".". Some people put comments starting with "#".
						// Example: # ---------------[ Unity generated ]------------------ #
						if (line.StartsWith(".", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#", StringComparison.OrdinalIgnoreCase) || line.StartsWith("/", StringComparison.OrdinalIgnoreCase) || line.StartsWith(":", StringComparison.OrdinalIgnoreCase))
							continue;

						// SVN ignores don't support folder paths - only names to direct files and names.
						if (line.Contains('/') || line.Contains('\\'))
							continue;

						var matchedEntries = Directory.EnumerateFileSystemEntries("Assets", line, SearchOption.AllDirectories);
						foundIgnoredEntries.AddRange(matchedEntries);
					}

#if UNITY_2018_4_OR_NEWER
					foreach (string line in propget.Lines) {

						// Skip hidden folders starting with ".". Some people put comments starting with "#".
						// Example: # ---------------[ Unity generated ]------------------ #
						if (line.StartsWith(".", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#", StringComparison.OrdinalIgnoreCase) || line.StartsWith("/", StringComparison.OrdinalIgnoreCase) || line.StartsWith(":", StringComparison.OrdinalIgnoreCase))
							continue;

						// SVN ignores don't support folder paths - only names to direct files and names.
						if (line.Contains('/') || line.Contains('\\'))
							continue;

						var matchedEntries = Directory.EnumerateFileSystemEntries("Packages", line, SearchOption.AllDirectories);
						foundIgnoredEntries.AddRange(matchedEntries);
					}
#endif

					continue;
				}

				foreach (string line in propget.Lines) {

					// Skip hidden folders starting with ".". Some people put comments starting with "#".
					// Example: # ---------------[ Unity generated ]------------------ #
					if (line.StartsWith(".", StringComparison.OrdinalIgnoreCase) || line.StartsWith("#", StringComparison.OrdinalIgnoreCase) || line.StartsWith("/", StringComparison.OrdinalIgnoreCase) || line.StartsWith(":", StringComparison.OrdinalIgnoreCase))
						continue;

					// SVN ignores don't support folder paths - only names to direct files and names.
					if (line.Contains('/') || line.Contains('\\'))
						continue;

					var matchedEntries = Directory.EnumerateFileSystemEntries(propget.Path, line, SearchOption.AllDirectories);
					foundIgnoredEntries.AddRange(matchedEntries);
				}
			}
		}

		protected override void WaitAndFinishDatabaseUpdate(GuidStatusDatasBind[] pendingData)
		{
			// The base class cleared m_Data before calling us. The guid index accelerator must
			// follow — mark dirty so the first read after this batch rebuilds against the new
			// rows that SetStatusData is about to add. Done at entry so any early-return paths
			// below leave the accelerator in a consistent state.
			InvalidateGuidIndex();
			// Handle error here, to avoid multi-threaded issues.
			if (LastError != StatusOperationResult.Success) {

				// Always log the error - if repeated it will be skipped inside.
				WiseSVNIntegration.LogStatusErrorHint(LastError);

				if (LastError == StatusOperationResult.AuthenticationFailed) {
					SVNPreferencesManager.Instance.NeedsToAuthenticate = true;
				}
			}

			// Sanity check!
			if (pendingData.Length > SanityStatusesLimit) {
				// No more logging, displaying an icon.
				if (DoTraceLogs) {
					Debug.LogWarning($"SVNStatusDatabase gathered {pendingData.Length} changes which is waay to much. Ignoring gathered changes to avoid slowing down the editor!");
				}

				return;
			}

			bool hasPartialBranch = false;

			// Process the gathered statuses in the main thread, since Unity API is not thread-safe.
			foreach (var pair in pendingData) {

				// HACK: Guid is not used here.
				var statusData = pair.MergedStatusData;

				var assetPath = statusData.Path;
				bool isMeta = false;

				// Meta statuses are also considered. They are shown as the asset status.
				if (statusData.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) {
					assetPath = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
					isMeta = true;
				}

				// Conflicted is with priority.
				if (statusData.IsConflicted) {
					statusData.Status = VCFileStatus.Conflicted;
				}

				var guid = AssetDatabase.AssetPathToGUID(assetPath);
				if (string.IsNullOrEmpty(guid)) {
					// Files were added in the background without Unity noticing.
					// When the user focuses on Unity, it will refresh them as well.
					if (statusData.Status != VCFileStatus.Deleted)
						continue;

					// HACK: Deleted assets don't have guids, but we still want to keep track of them (Lock prompt for example).
					//		 As long as this is unique it will work.
					guid = assetPath;
				}

				// File was added to the repository but is missing in the working copy.
				// The proper way to check this is to parse the working revision from the svn output (when used with -u)
				if (statusData.RemoteStatus == VCRemoteFileStatus.Modified
					&& statusData.Status == VCFileStatus.Normal
					&& string.IsNullOrEmpty(guid)
					)
					continue;

				SetStatusData(guid, statusData, false, true, isMeta);

				AddModifiedFolders(statusData);

				if (statusData.SwitchedExternalStatus == VCSwitchedExternal.Switched) {
					hasPartialBranch = true;
				}
			}

			if (!m_PartialBranchingWarned && hasPartialBranch != m_PartialBranchingWarned && !WiseSVNIntegration.Silent) {
				EditorUtility.DisplayDialog("SVN Partial Branching", "Project has some files coming from a different branch. This is most likely a mistake.\n\nWhen switching between branches always do it from the top folder of the checkout and select \"Fully recursive\"!", "Ok");
			}
			m_PartialBranchingWarned = hasPartialBranch;
		}

		private void AddModifiedFolders(SVNStatusData statusData)
		{
			var status = statusData.Status;
			// Early-return on clean leaves UNLESS they have remote out-of-date data — in that case
			// we still want to propagate a remote-only signal up the folder tree so the user sees
			// "this folder contains files newer on the server" without having to drill in.
			// (Without this, a Normal-on-disk file with RemoteStatus=Modified gives the user no
			// folder-level signal that an svn update would do something here.)
			bool remoteOnly = false;
			if (status == VCFileStatus.Unversioned || status == VCFileStatus.Ignored || status == VCFileStatus.Normal || status == VCFileStatus.Excluded || status == VCFileStatus.External || status == VCFileStatus.ReadOnly) {
				if (statusData.RemoteStatus != VCRemoteFileStatus.None) {
					remoteOnly = true;
				} else {
					return;
				}
			}

			if (statusData.IsConflicted) {
				statusData.Status = VCFileStatus.Conflicted;
			} else if (remoteOnly) {
				// Keep folder status Normal — render layer will pick the Remote icon (top-right)
				// rather than the Modified icon (bottom-left). The whole point of this branch is
				// to tell the user "remote has new changes" *without* falsely claiming local
				// modifications.
				statusData.Status = VCFileStatus.Normal;
			} else if (status != VCFileStatus.Modified) {
				statusData.Status = VCFileStatus.Modified;
			}

			// Folders don't have locks.
			statusData.LockStatus = VCLockStatus.NoLock;

			statusData.Path = Path.GetDirectoryName(statusData.Path);

			while (!string.IsNullOrEmpty(statusData.Path)) {
				// "Packages" folder doesn't have valid guid. "Assets" do have a special guid.
				if (statusData.Path == "Packages")
					break;

				var guid = AssetDatabase.AssetPathToGUID(statusData.Path);

				// Folder may be deleted.
				if (string.IsNullOrWhiteSpace(guid))
					return;

				// Added folders should not be shown as modified.
				if (GetKnownStatusData(guid).Status == VCFileStatus.Added)
					return;

				bool moveToNext = SetStatusData(guid, statusData, false, true, false);

				// If already exists, upper folders should be added as well.
				if (!moveToNext)
					return;

				statusData.Path = Path.GetDirectoryName(statusData.Path);
			}
		}

		#endregion


		//
		//=============================================================================
		//
		#region Invalidate Database

		internal void PostProcessAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets)
		{
			if (!IsActive)
				return;

			// Moving & deleting unversioned assets will trigger database refresh, but we can live with that. Should be a rare operation.
			if (deletedAssets.Length > 0 || movedAssets.Length > 0) {
				InvalidateDatabase();
				return;
			}

			// It will probably be faster.
			if (importedAssets.Length > 10) {
				InvalidateDatabase();
				return;
			}

			foreach (var path in importedAssets) {

				// ProjectSettings, Packages are imported too but we're not interested.
				if (!path.StartsWith("Assets", StringComparison.Ordinal))
					continue;

				var statusData = WiseSVNIntegration.GetStatus(path, DoTraceLogs);
				bool isMeta = false;

				// If status is normal but asset was imported, maybe the meta changed. Use that status instead.
				if (statusData.Status == VCFileStatus.Normal && !statusData.IsConflicted) {
					statusData = WiseSVNIntegration.GetStatus(path + ".meta", DoTraceLogs);
					isMeta = true;
				}

				var guid = AssetDatabase.AssetPathToGUID(path);

				// Conflicted file got reimported? Fuck this, just refresh.
				if (statusData.IsConflicted) {
					SetStatusData(guid, statusData, true, false, isMeta);
					InvalidateDatabase();
					return;
				}


				if (statusData.Status == VCFileStatus.Normal) {

					// O(1) lookup via guid index instead of FirstOrDefault linear scan.
					var pIdx = GetGuidIndex();
					GuidStatusDatasBind knownStatusBind = pIdx.TryGetValue(guid, out int kPos)
						? m_Data[kPos]
						: new GuidStatusDatasBind();
					var knownMergedData = knownStatusBind.MergedStatusData;

					// Check if just switched to normal from something else.
					// Normal might be present in the database if it is locked.
					if (knownMergedData.Status != VCFileStatus.None && knownMergedData.Status != VCFileStatus.Normal) {
						if (knownMergedData.LockStatus == VCLockStatus.NoLock && knownMergedData.RemoteStatus == VCRemoteFileStatus.None) {
							RemoveStatusData(guid);
						} else {
							bool knownIsMeta = knownStatusBind.AssetStatusData.Status == VCFileStatus.Normal;	// Meta, not asset.
							knownMergedData = knownIsMeta ? knownStatusBind.MetaStatusData : knownStatusBind.AssetStatusData;
							knownMergedData.Status = VCFileStatus.Normal;

							SetStatusData(guid, knownMergedData, true, false, knownIsMeta);
						}

						InvalidateDatabase();
						return;
					}

					continue;
				}

				// Files inside ignored folder are returned as Unversioned. Check if they are ignored and change the status.
				if (statusData.Status == VCFileStatus.Unversioned) {
					statusData.Status = CheckForIgnoredOrExcludedStatus(statusData.Status, path);
				}

				// Every time the user saves a file it will get reimported. If we already know it is modified, don't refresh every time.
				bool changed = SetStatusData(guid, statusData, true, false, isMeta);

				if (changed) {
					InvalidateDatabase();
					return;
				}
			}
		}

		private VCFileStatus CheckForIgnoredOrExcludedStatus(VCFileStatus originalStatus, string path)
		{
			if (SVNPreferencesManager.ShouldExclude(m_PersonalCachedPrefs.Exclude.Concat(m_ProjectCachedPrefs.Exclude), path))
				return VCFileStatus.Excluded;

			foreach (string ignoredPath in m_IgnoredEntries) {
				if (WiseSVNIntegration.ArePathsNested(ignoredPath, path)) {
					return VCFileStatus.Ignored;
				}
			}

			foreach (string ignoredPath in m_GlobalIgnoredEntries) {
				if (WiseSVNIntegration.ArePathsNested(ignoredPath, path)) {
					return VCFileStatus.Ignored;
				}
			}

			return originalStatus;
		}

		#endregion


		//
		//=============================================================================
		//
		#region Manage status data

		/// <summary>
		/// Get known status for guid.
		/// Unversioned files should return unversioned status.
		/// If status is not known, the file should be versioned unmodified or still undetected.
		/// </summary>
		public SVNStatusData GetKnownStatusData(string guid)
		{
			if (string.IsNullOrEmpty(guid)) {
				Debug.LogError($"Asking for status with empty guid");
				return new SVNStatusData() { Status = VCFileStatus.None };
			}

			// O(1) lookup via guid index (was O(N) linear scan over m_Data — hot path called
			// per visible Project window entry per frame).
			var idx = GetGuidIndex();
			if (idx.TryGetValue(guid, out int dataPos)) {
				return m_Data[dataPos].MergedStatusData;
			}

			string path = null;
			if (m_UnversionedFolders.Length > 0) {
				path = AssetDatabase.GUIDToAssetPath(guid);

				foreach (string unversionedFolder in m_UnversionedFolders) {
					if (WiseSVNIntegration.ArePathsNested(unversionedFolder, path))
						return new SVNStatusData() { Path = path, Status = VCFileStatus.Unversioned, LockDetails = LockDetails.Empty };
				}
			}

			if (m_IgnoredEntries.Length > 0) {
				path = path ?? AssetDatabase.GUIDToAssetPath(guid);

				foreach (string ignoredPath in m_IgnoredEntries) {
					if (WiseSVNIntegration.ArePathsNested(ignoredPath, path)) {
						LogIgnoreMatch(ignoredPath, path, "svn:ignore");
						return new SVNStatusData() { Path = path, Status = VCFileStatus.Ignored, LockDetails = LockDetails.Empty };
					}
				}
			}

			if (m_GlobalIgnoredEntries.Length > 0) {
				path = path ?? AssetDatabase.GUIDToAssetPath(guid);

				foreach (string ignoredPath in m_GlobalIgnoredEntries) {
					if (WiseSVNIntegration.ArePathsNested(ignoredPath, path)) {
						LogIgnoreMatch(ignoredPath, path, "svn:global-ignores");
						return new SVNStatusData() { Path = path, Status = VCFileStatus.Ignored, LockDetails = LockDetails.Empty };
					}
				}
			}

			return new SVNStatusData() { Status = VCFileStatus.None };
		}

		// Fires once per unique (ignoredPath, source) combination so the log isn't spammed every frame.
		private static HashSet<string> s_LoggedIgnoreMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private static void LogIgnoreMatch(string ignoredPath, string assetPath, string source)
		{
			string key = $"{source}|{ignoredPath}";
			if (s_LoggedIgnoreMatches.Add(key)) {
				Debug.LogWarning($"[WiseSVN] {source} entry \"{ignoredPath}\" matched asset \"{assetPath}\" → showing as Ignored. " +
					$"If this is wrong, check your svn:ignore / svn:global-ignores on the parent folder.");
			}
		}

		public IEnumerable<SVNStatusData> GetAllKnownStatusData(string guid, bool mergedData, bool assetData, bool metaData)
		{
			foreach(var pair in m_Data) {
				if (pair.Key.Equals(guid, StringComparison.Ordinal)) {
					if (mergedData && pair.MergedStatusData.IsValid) yield return pair.MergedStatusData;
					if (assetData && pair.AssetStatusData.IsValid) yield return pair.AssetStatusData;
					if (metaData && pair.MetaStatusData.IsValid) yield return pair.MetaStatusData;

					break;
				}
			}
		}

		public IEnumerable<SVNStatusData> GetAllKnownStatusData(bool mergedData, bool assetData, bool metaData)
		{
			foreach(var pair in m_Data) {
				if (mergedData && pair.MergedStatusData.IsValid) yield return pair.MergedStatusData;
				if (assetData && pair.AssetStatusData.IsValid) yield return pair.AssetStatusData;
				if (metaData && pair.MetaStatusData.IsValid) yield return pair.MetaStatusData;
			}
		}

		private bool SetStatusData(string guid, SVNStatusData statusData, bool skipPriorityCheck, bool compareOnlineStatuses, bool isMeta)
		{
			if (string.IsNullOrEmpty(guid)) {
				Debug.LogError($"SVN: Trying to add empty guid for \"{statusData.Path}\" with status {statusData.Status}");
				return false;
			}

			// O(1) lookup via guid index. Was a linear `foreach var bind in m_Data` scan —
			// fine at <100 entries, but pathological at SanityStatusesLimit (600) on hot
			// PostProcessAssets paths where this can be called per imported asset.
			var idx = GetGuidIndex();
			if (idx.TryGetValue(guid, out int existingPos)) {
				var bind = m_Data[existingPos];

				if (!isMeta && bind.AssetStatusData.EqualStatuses(statusData, !compareOnlineStatuses))
					return false;

				if (isMeta && bind.MetaStatusData.EqualStatuses(statusData, !compareOnlineStatuses))
					return false;

				if (!isMeta) {
					bind.AssetStatusData = statusData;
				} else {
					bind.MetaStatusData = statusData;
				}

				// This is needed because the status of the meta might differ. In that case take the stronger status.
				if (!skipPriorityCheck) {
					if (m_StatusPriority[bind.MergedStatusData.Status] > m_StatusPriority[statusData.Status]) {
						// Merge any other data.
						if (bind.MergedStatusData.PropertiesStatus == VCPropertiesStatus.Normal) {
							bind.MergedStatusData.PropertiesStatus = statusData.PropertiesStatus;
						}
						if (bind.MergedStatusData.TreeConflictStatus == VCTreeConflictStatus.Normal) {
							bind.MergedStatusData.TreeConflictStatus = statusData.TreeConflictStatus;
						}
						if (bind.MergedStatusData.SwitchedExternalStatus == VCSwitchedExternal.Normal) {
							bind.MergedStatusData.SwitchedExternalStatus = statusData.SwitchedExternalStatus;
						}
						if (bind.MergedStatusData.LockStatus == VCLockStatus.NoLock) {
							bind.MergedStatusData.LockStatus = statusData.LockStatus;
							bind.MergedStatusData.LockDetails = statusData.LockDetails;
						}
						if (bind.MergedStatusData.RemoteStatus == VCRemoteFileStatus.None) {
							bind.MergedStatusData.RemoteStatus= statusData.RemoteStatus;
						}

						return false;
					}
				}

				// Merged should always display lock and remote status.
				if (statusData.LockStatus == VCLockStatus.NoLock) {
					statusData.LockStatus = bind.MergedStatusData.LockStatus;
					statusData.LockDetails = bind.MergedStatusData.LockDetails;
				}
				if (statusData.RemoteStatus == VCRemoteFileStatus.None) {
					statusData.RemoteStatus= bind.MergedStatusData.RemoteStatus;
				}

				bind.MergedStatusData = statusData;
				if (isMeta) {
					bind.MergedStatusData.Path = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
				}
				return true;
			}

			m_Data.Add(new GuidStatusDatasBind() {
				Key = guid,
				MergedStatusData = statusData,

				AssetStatusData = isMeta ? new SVNStatusData() : statusData,
				MetaStatusData = isMeta ? statusData : new SVNStatusData(),
			});

			if (isMeta) {
				m_Data.Last().MergedStatusData.Path = statusData.Path.Substring(0, statusData.Path.LastIndexOf(".meta"));
			}

			// Patch the index incrementally — no full rebuild needed for a single insert.
			GetGuidIndex()[guid] = m_Data.Count - 1;

			return true;
		}


		private bool RemoveStatusData(string guid)
		{
			if (string.IsNullOrEmpty(guid)) {
				Debug.LogError($"Trying to remove empty guid");
			}

			// O(1) lookup; List.RemoveAt is still O(N) for the shift, but unavoidable without
			// switching to a swap-remove which would break callers iterating m_Data in order.
			var idx = GetGuidIndex();
			if (idx.TryGetValue(guid, out int pos)) {
				m_Data.RemoveAt(pos);
				// Removing in the middle invalidates every index >= pos in the dictionary —
				// cheapest to mark the whole accelerator dirty for next-read rebuild.
				InvalidateGuidIndex();
				return true;
			}

			return false;
		}
		#endregion
	}


	internal class SVNStatusesDatabaseAssetPostprocessor : AssetPostprocessor
	{
		private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
		{
			if (!WiseSVNIntegration.TemporaryDisabled) {
				SVNStatusesDatabase.Instance.PostProcessAssets(importedAssets, deletedAssets, movedAssets);
			}
		}
	}
}
