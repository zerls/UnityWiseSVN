// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace DevLocker.VersionControl.WiseSVN.Providers
{
	/// <summary>
	/// Status provider backed by SVNStatusesDatabase — the original CLI-scan path.
	/// This is the fallback used on macOS / Linux, when TSVNCache isn't running, or
	/// when the user has explicitly disabled TSVNCache in preferences.
	/// </summary>
	internal sealed class CLIDatabaseStatusProvider : ISVNStatusProvider
	{
		public string DisplayName => "CLI Database";
		public bool IsReady => SVNStatusesDatabase.Instance.IsReady;
		public bool DataIsIncomplete => SVNStatusesDatabase.Instance.DataIsIncomplete;

		public event Action StatusesChanged
		{
			add    { SVNStatusesDatabase.Instance.DatabaseChanged += value; }
			remove { SVNStatusesDatabase.Instance.DatabaseChanged -= value; }
		}

		public SVNStatusData GetStatus(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
				return new SVNStatusData { Status = VCFileStatus.None };

			string guid = AssetDatabase.AssetPathToGUID(assetPath);
			if (string.IsNullOrEmpty(guid))
				return new SVNStatusData { Status = VCFileStatus.None };

			return SVNStatusesDatabase.Instance.GetKnownStatusData(guid);
		}

		public IEnumerable<SVNStatusData> EnumerateInteresting()
		{
			if (!SVNStatusesDatabase.Instance.IsReady)
				yield break;

			foreach (var s in SVNStatusesDatabase.Instance.GetAllKnownStatusData(true, false, false)) {
				bool interesting = s.Status != VCFileStatus.Normal
					&& s.Status != VCFileStatus.Excluded
					&& s.Status != VCFileStatus.Ignored
					&& s.Status != VCFileStatus.None;
				interesting |= s.LockStatus != VCLockStatus.NoLock;
				interesting |= s.RemoteStatus != VCRemoteFileStatus.None;
				if (interesting) yield return s;
			}
		}

		public void InvalidatePath(string assetPath)
		{
			// CLI database doesn't support per-path invalidation — fall back to a full refresh.
			SVNStatusesDatabase.Instance.InvalidateDatabase();
		}

		public void InvalidateAll()
		{
			SVNStatusesDatabase.Instance.InvalidateDatabase();
		}
	}
}
