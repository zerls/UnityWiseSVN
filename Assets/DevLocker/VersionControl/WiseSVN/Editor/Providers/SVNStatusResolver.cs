// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.Preferences;
using DevLocker.VersionControl.WiseSVN.Utils;
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Providers
{
	/// <summary>
	/// Layer 2 — the single authoritative source of display-ready SVN status for every
	/// asset GUID visible in the Project window.
	///
	/// Architecture (Phase 0 refactor, 2026-06-28):
	///
	///   Layer 1 — raw data sources
	///     TSVNCacheStatusProvider     (fast, per-path IPC cache, TTL 5 s)
	///     SVNStatusesDatabase          (batch, `svn status` every 60 s)
	///
	///   Layer 2 — THIS CLASS
	///     Subscribes to events from both sources.
	///     Merges the two per GUID using CLI-as-ground-truth rules.
	///     Caches the merged result inside a Dictionary{guid → ResolvedStatusData}.
	///     Fires ResolvedChanged when the cached result actually differs.
	///
	///   Layer 3 — pure rendering
	///     SVNOverlayIcons.ItemOnGUI   calls GetResolved(guid) — O(1) read, zero data logic
	///     SVNStatusBarOverlay         calls EnumerateResolved() for badge counts
	///
	/// DB rebuild suppression:
	///   When SVNStatusesDatabase fires DatabaseChangeStarting, we flip a flag that
	///   tells the resolver to HOLD the previous merge result until DatabaseChanged
	///   fires (signalling that the refill is complete). This closes the "empty
	///   m_Data" gap that previously caused the Normal-Modified folder flicker.
	/// </summary>
	[InitializeOnLoad]
	internal sealed class SVNStatusResolver : ScriptableObject
	{
		// ── Singleton (HideAndDontSave ScriptableObject, survives domain reload) ──
		private static SVNStatusResolver s_Instance;

		public static SVNStatusResolver Instance
		{
			get
			{
				if (s_Instance == null)
				{
					s_Instance = CreateInstance<SVNStatusResolver>();
					s_Instance.hideFlags = HideFlags.HideAndDontSave;
					s_Instance.Initialize();
				}
				return s_Instance;
			}
		}

		// ── Cached merged results ──────────────────────────────────────────────
		// NOT [SerializeField] — cheaper to rebuild on domain reload than to serialize.
		// Also avoids Texture2D serialization issues for icon content embedded in some
		// downstream consumers that might memoize GUIContent from us.
		private readonly Dictionary<string, ResolvedStatusData> m_Resolved
			= new Dictionary<string, ResolvedStatusData>(4096, StringComparer.Ordinal);

		// ── DB rebuild suppression ──────────────────────────────────────────────
		// When true, the CLI database is between Clear and refill — we suppress
		// OnSourceChanged processing to avoid transient "everything is Normal" frames.
		private bool m_DBRebuilding;

		// ── Startup flag — set after the initial full pass is done. Used to defer
		//     the first ResolvedChanged fire until both sources have loaded at least once.
		private bool m_InitialPassDone;

		// ── Public events ───────────────────────────────────────────────────────
		/// <summary>
		/// Fire when the resolved data changes. Consumers (SVNOverlayIcons, SVNStatusBarOverlay)
		/// should repaint / recalc counts. NOT fired during DB rebuild suppression.
		/// </summary>
		public event Action ResolvedChanged;

		private void Initialize()
		{
			if (s_Instance != null && s_Instance != this)
			{
				DestroyImmediate(this);
				return;
			}
			s_Instance = this;

			// Subscribe to both data sources.
			var provider = SVNPreferencesManager.Instance.StatusProvider;
			provider.StatusesChanged += OnSourceChanged;

			SVNStatusesDatabase.Instance.DatabaseChangeStarting += OnDBRebuildStarting;
			SVNStatusesDatabase.Instance.DatabaseChanged      += OnDBRebuildFinished;

			SVNPreferencesManager.Instance.StatusProviderChanged += () =>
			{
				SVNPreferencesManager.Instance.StatusProvider.StatusesChanged += OnSourceChanged;
				RebuildAll();
			};

			// Do an initial pass once the editor is idle.
			EditorApplication.delayCall += () =>
			{
				RebuildAll();
				m_InitialPassDone = true;
			};
		}

		// ════════════════════════════════════════════════════════════════════════
		//  Public API
		// ════════════════════════════════════════════════════════════════════════

		/// <summary>
		/// O(1) cache read — intended for per-frame, per-visible-entry calls
		/// inside SVNOverlayIcons.ItemOnGUI. Never allocates, never merges on the fly.
		/// </summary>
		public ResolvedStatusData GetResolved(string guid)
		{
			if (string.IsNullOrEmpty(guid))
				return ResolvedStatusData.Empty;

			lock (m_Resolved)
			{
				m_Resolved.TryGetValue(guid, out var result);
				return result;
			}
		}

		/// <summary>
		/// Enumerate every non-Normal resolved entry. Used by SVNStatusBarOverlay
		/// for the modified/remote/conflict counts in the toolbar badge.
		/// </summary>
		public IEnumerable<ResolvedStatusData> EnumerateResolved()
		{
			lock (m_Resolved)
			{
				foreach (var kv in m_Resolved)
				{
					var r = kv.Value;
					if (r.FileStatus == VCFileStatus.None || r.FileStatus == VCFileStatus.Normal)
					{
						if (r.LockStatus == VCLockStatus.NoLock
							&& r.RemoteStatus == VCRemoteFileStatus.None)
							continue;
					}
					yield return r;
				}
			}
		}

		/// <summary>
		/// Force a full rebuild of all cached entries. Called after InvalidateAll,
		/// provider upgrade, or on initial editor idle.
		/// </summary>
		public void RebuildAll()
		{
			lock (m_Resolved) { m_Resolved.Clear(); }
			OnSourceChanged();
		}

		// ════════════════════════════════════════════════════════════════════════
		//  Event handlers
		// ════════════════════════════════════════════════════════════════════════

		private void OnDBRebuildStarting()
		{
			m_DBRebuilding = true;
		}

		private void OnDBRebuildFinished()
		{
			m_DBRebuilding = false;
			OnSourceChanged();
		}

		private void OnSourceChanged()
		{
			if (m_DBRebuilding) return;

			bool changed = false;

			// Lock the dictionary just long enough to snapshot keys that exist.
			// Merge is compute-only (no side effects) so we can run it outside the lock,
			// then re-lock briefly to commit the results.
			string[] existingKeys;
			lock (m_Resolved)
			{
				existingKeys = new string[m_Resolved.Count];
				m_Resolved.Keys.CopyTo(existingKeys, 0);
			}

			foreach (var guid in existingKeys)
			{
				var newResult = Merge(guid);
				lock (m_Resolved)
				{
					if (m_Resolved.TryGetValue(guid, out var old)
						&& old.Equals(newResult))
						continue;

					m_Resolved[guid] = newResult;
				}
				changed = true;
			}

			// Walk the CLI m_Data to pick up NEW entries that aren't yet in the cache.
			var db = SVNStatusesDatabase.Instance;
			if (db.IsReady)
			{
				foreach (var s in db.GetAllKnownStatusData(true, false, false))
				{
					string guid = AssetDatabase.AssetPathToGUID(s.Path);
					if (string.IsNullOrEmpty(guid)) continue;

					lock (m_Resolved)
					{
						if (m_Resolved.ContainsKey(guid)) continue; // already processed above
					}

					var newResult = Merge(guid);
					lock (m_Resolved)
					{
						if (m_Resolved.TryGetValue(guid, out var old)
							&& old.Equals(newResult))
							continue;

						m_Resolved[guid] = newResult;
					}
					changed = true;
				}
			}

			if (changed)
				ResolvedChanged?.Invoke();
		}

		// ════════════════════════════════════════════════════════════════════════
		//  Merge logic — CLI is ground truth; TSVNCache is low-latency front cache.
		//  Same rules that were previously embedded in SVNOverlayIcons.MergeCliStatus.
		// ════════════════════════════════════════════════════════════════════════

		private static ResolvedStatusData Merge(string guid)
		{
			string assetPath = AssetDatabase.GUIDToAssetPath(guid);

			// Primary source (fast path)
			var a = SVNPreferencesManager.Instance.StatusProvider.GetStatus(assetPath);

			// Ground truth source
			var b = SVNStatusesDatabase.Instance.GetKnownStatusData(guid);

			// ── File status ─────────────────────────────────────────────────
			VCFileStatus fileStatus = a.Status;
			if (b.IsValid && b.Status != VCFileStatus.None)
				fileStatus = b.Status;

			VCPropertiesStatus propStatus = a.PropertiesStatus;
			if (b.IsValid && b.PropertiesStatus != VCPropertiesStatus.None)
				propStatus = b.PropertiesStatus;

			VCTreeConflictStatus treeConflict = a.TreeConflictStatus;
			if (b.IsValid && b.TreeConflictStatus != VCTreeConflictStatus.Normal)
				treeConflict = b.TreeConflictStatus;

			if (SVNPreferencesManager.Instance.PersonalPrefs.ShowNormalStatusOverlayIcon
				&& !b.IsValid)
			{
				fileStatus = VCFileStatus.Normal;
			}

			// ── Conflict escalation (P0) ────────────────────────────────────
			if (propStatus == VCPropertiesStatus.Conflicted
				|| treeConflict == VCTreeConflictStatus.TreeConflict)
			{
				fileStatus = VCFileStatus.Conflicted;
			}
			else if (propStatus == VCPropertiesStatus.Modified
				&& fileStatus == VCFileStatus.Normal)
			{
				fileStatus = VCFileStatus.Modified;
			}

			// ── Lock status ─────────────────────────────────────────────────
			VCLockStatus lockStatus = a.LockStatus;
			LockDetails lockDetails = a.LockDetails;
			if (b.IsValid && b.LockStatus != VCLockStatus.NoLock)
			{
				lockStatus  = b.LockStatus;
				lockDetails = b.LockDetails;
			}

			// ── Remote status (CLI-only) ─────────────────────────────────────
			VCRemoteFileStatus remoteStatus = a.RemoteStatus;
			if (b.IsValid && b.RemoteStatus != VCRemoteFileStatus.None)
				remoteStatus = b.RemoteStatus;

			// ── Junction root flag ───────────────────────────────────────────
			bool isJunction = JunctionResolver.HasJunctions
				&& JunctionResolver.IsJunctionRoot(assetPath);

			// ── Path (display / debug) ───────────────────────────────────────
			string path = b.IsValid ? b.Path : a.Path;

			return new ResolvedStatusData(fileStatus, lockStatus, lockDetails,
				remoteStatus, isJunction, path);
		}
	}

	// ════════════════════════════════════════════════════════════════════════════
	//  ResolvedStatusData — lightweight readonly struct, stack-only, never null.
	// ════════════════════════════════════════════════════════════════════════════

	/// <summary>
	/// Fully-resolved display status for a single asset. Produced by SVNStatusResolver,
	/// consumed read-only by SVNOverlayIcons.ItemOnGUI and SVNStatusBarOverlay.
	///
	/// This is the only struct that Layer 3 rendering code should touch —
	/// never ISVNStatusProvider or SVNStatusesDatabase directly.
	/// </summary>
	public readonly struct ResolvedStatusData : IEquatable<ResolvedStatusData>
	{
		/// <summary>File status with all escalation rules already applied.</summary>
		public readonly VCFileStatus FileStatus;

		/// <summary>Lock status (LockedHere / LockedOther / Broken / Stolen) with full details.</summary>
		public readonly VCLockStatus LockStatus;
		public readonly LockDetails LockDetails;

		/// <summary>Remote out-of-date status — only from CLI, TSVNCache cannot provide this.</summary>
		public readonly VCRemoteFileStatus RemoteStatus;

		/// <summary>True when this asset's path is exactly a known NTFS junction root (mklink /J).</summary>
		public readonly bool IsJunctionRoot;

		/// <summary>The asset-relative or native path, for debug display only.</summary>
		public readonly string Path;

		public ResolvedStatusData(VCFileStatus fileStatus, VCLockStatus lockStatus,
			LockDetails lockDetails, VCRemoteFileStatus remoteStatus,
			bool isJunctionRoot, string path)
		{
			FileStatus     = fileStatus;
			LockStatus     = lockStatus;
			LockDetails    = lockDetails;
			RemoteStatus   = remoteStatus;
			IsJunctionRoot = isJunctionRoot;
			Path           = path ?? string.Empty;
		}

		/// <summary>Sentinel for unresolvable GUIDs.</summary>
		public static readonly ResolvedStatusData Empty = new ResolvedStatusData(
			VCFileStatus.None, VCLockStatus.NoLock, LockDetails.Empty,
			VCRemoteFileStatus.None, false, string.Empty);

		public bool Equals(ResolvedStatusData other)
		{
			return FileStatus     == other.FileStatus
				&& LockStatus     == other.LockStatus
				&& LockDetails.Equals(other.LockDetails)
				&& RemoteStatus   == other.RemoteStatus
				&& IsJunctionRoot == other.IsJunctionRoot;
		}

		public override bool Equals(object obj)
			=> obj is ResolvedStatusData other && Equals(other);

		public override int GetHashCode()
		{
			unchecked
			{
				int h = (int)FileStatus;
				h = (h * 397) ^ (int)LockStatus;
				h = (h * 397) ^ (int)RemoteStatus;
				h = (h * 397) ^ (IsJunctionRoot ? 1 : 0);
				return h;
			}
		}
	}
}
