// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using System;
using System.Collections.Generic;

namespace DevLocker.VersionControl.WiseSVN.Providers
{
	/// <summary>
	/// Abstraction over how WiseSVN obtains per-file SVN status. Two implementations:
	///   - CLIDatabaseStatusProvider: wraps SVNStatusesDatabase (periodic `svn status` scan).
	///   - TSVNCacheStatusProvider:   queries TortoiseSVN's TSVNCache.exe via named pipe IPC.
	///
	/// Selected once at startup by SVNPreferencesManager based on platform + user preference + probe.
	/// </summary>
	internal interface ISVNStatusProvider
	{
		/// Human-readable name shown in the diagnostic UI ("TSVNCache" / "CLI Database").
		string DisplayName { get; }

		/// True once the underlying data source has produced data at least once.
		bool IsReady { get; }

		/// True when this data source had to drop entries (sanity limit, etc.).
		bool DataIsIncomplete { get; }

		/// Raised when the underlying data refreshes — consumers should repaint icons / recount.
		event Action StatusesChanged;

		/// Synchronous query for the given asset path (relative to the project root, e.g. "Assets/Foo.cs").
		/// Must return quickly (TTL-cached). Returns a status with Path=empty and Status=None when unknown.
		SVNStatusData GetStatus(string assetPath);

		/// Enumerate everything currently known to be non-clean (Added/Modified/Conflicted/Deleted/Missing/etc.,
		/// or with a lock or remote-modification flag). Used by the status-bar badge for counts.
		IEnumerable<SVNStatusData> EnumerateInteresting();

		/// Forget cached state for one specific path (no full rescan).
		void InvalidatePath(string assetPath);

		/// Force a full refresh of the data source.
		void InvalidateAll();
	}
}
