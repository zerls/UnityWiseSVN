// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using DevLocker.VersionControl.WiseSVN.Providers;
using DevLocker.VersionControl.WiseSVN.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using static DevLocker.VersionControl.WiseSVN.Localization.LocalizationManager;

namespace DevLocker.VersionControl.WiseSVN.Preferences
{
	internal class SVNPreferencesManager : Utils.EditorPersistentSingleton<SVNPreferencesManager>
	{
		internal enum BoolPreference
		{
			SameAsProjectPreference = 0,
			Enabled = 4,
			Disabled = 8,
		}

		private const string LEGACY_PERSONAL_PREFERENCES_KEY = "WiseSVN";
		private const string PERSONAL_PREFERENCES_PATH = "UserSettings/WiseSVN.prefs";
		private const string PROJECT_PREFERENCES_PATH = "ProjectSettings/WiseSVN.prefs";

		// Icons are stored in the database so we don't reload them every time.
		[SerializeField] private GUIContent[] FileStatusIcons = new GUIContent[0];
		[SerializeField] private GUIContent[] LockStatusIcons = new GUIContent[0];

		[SerializeField] private bool m_RetryTextures = false;

		[Serializable]
		internal class PersonalPreferences
		{
			// Master kill-switch. When true the plugin does nothing — no file hooks, no icons,
			// no SVN calls. Useful when working offline or on non-SVN branches.
			// Default false → plugin runs normally.
			public bool PluginDisabled = false;

			public bool EnableCoreIntegration = true;		// Sync file operations with SVN
			public bool PopulateStatusesDatabase = true;    // For overlay icons etc.
			public bool PopulateIgnoresDatabase = true;    // For svn-ignored icons etc.
			public bool ShowNormalStatusOverlayIcon = false;
			public bool ShowExcludedStatusOverlayIcon = true;
			// Draw a small badge on folders that are NTFS directory junctions (mklink /J).
			// Helps the user visually distinguish junction roots from regular folders,
			// since SVN operates against the real target path under the hood.
			public bool ShowJunctionOverlayIcon = true;

			public string SvnCLIPath = string.Empty;

			// When populating the database, should it check for server changes as well (locks & modified files).
			public BoolPreference DownloadRepositoryChanges = BoolPreference.SameAsProjectPreference;
			public bool AutoLockOnModified = false;
			public bool WarnForPotentialConflicts = true;
			public bool AskOnMovingFolders = true;

			public int AutoRefreshDatabaseInterval = 60;    // seconds; Less than 0 will disable it.
			public bool RefreshDatabaseOnFocus = true;      // Refresh statuses when Unity regains focus (closes the gap when user works in TortoiseSVN/CLI externally).
			public bool PreferTSVNCache = true;             // Windows-only: query TortoiseSVN's TSVNCache.exe via named pipe instead of running `svn status` ourselves.
			public ContextMenusClient ContextMenusClient = ContextMenusClient.TortoiseSVN;
			public SVNTraceLogs TraceLogs = SVNTraceLogs.SVNOperations;

			// UI display language. Auto follows Unity's Application.systemLanguage.
			public WiseSVNLanguage Language = WiseSVNLanguage.Auto;

			public WiseSVNIconStyle IconStyle = WiseSVNIconStyle.Emoji;
			public string TortoiseSVNTheme = "Win10";

			// Status badge display locations (each independently toggled).
			public bool ShowSVNStatusToolbar  = true;   // Unity main toolbar (top bar)
			public bool ShowSVNStatusTitleBar = false;  // Windows title bar (Win32, experimental)
			public bool ShowSVNStatusSceneView = false; // Large semi-transparent branch name in SceneView (opt-in)

			// SceneView label appearance.
			public int   SceneViewBranchFontSize = 22;
			public float SceneViewBranchAlpha    = 0.45f;

			// Badge color: when AdaptiveSVNStatusColor is on, the badge is colored by the first
			// matching branch-name pattern in BranchColorRules (falling back to DefaultBranchColor).
			// Conflict state always overrides to red on the toolbar badge as a safety alert.
			// When off, SVNStatusBadgeColor is used everywhere.
			public bool  AdaptiveSVNStatusColor = true;
			public Color SVNStatusBadgeColor    = new Color(0.18f, 0.38f, 0.62f, 1f);

			public List<SVNBranchColorRule> BranchColorRules = new List<SVNBranchColorRule> {
				new SVNBranchColorRule { Pattern = @"^(trunk|main|master)$", Color = new Color(0.15f, 0.50f, 0.28f, 1f) }, // green – mainline
				new SVNBranchColorRule { Pattern = @"feature",                Color = new Color(0.18f, 0.40f, 0.65f, 1f) }, // blue – feature
				new SVNBranchColorRule { Pattern = @"(release|pubver)",       Color = new Color(0.55f, 0.30f, 0.70f, 1f) }, // purple – release
				new SVNBranchColorRule { Pattern = @"(hotfix|bugfix|patch)",  Color = new Color(0.75f, 0.30f, 0.10f, 1f) }, // orange-red – hotfix
			};
			public Color DefaultBranchColor = new Color(0.35f, 0.35f, 0.40f, 1f);

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			public List<string> Exclude = new List<string>();

			public const string AutoLockOnModifiedHint = "Will automatically lock assets if possible when they become modified, instead of prompting the user.\nIf assets have newer version or are locked by someone else, prompt will still be displayed.\n\nNotification will be displayed. Check the logs to know what was locked.";

			public PersonalPreferences Clone()
			{
				var clone = (PersonalPreferences) MemberwiseClone();
				clone.Exclude = new List<string>(Exclude);
				clone.BranchColorRules = BranchColorRules.Select(r =>
					new SVNBranchColorRule { Pattern = r.Pattern, Color = r.Color }).ToList();
				return clone;
			}
		}

		[Serializable]
		internal class ProjectPreferences
		{
			public bool DownloadRepositoryChanges = true;

			// Use PlatformSvnCLIPath instead as it is platform independent.
			public string SvnCLIPath = string.Empty;
			public string SvnCLIPathMacOS = string.Empty;

#if UNITY_EDITOR_WIN
			public string PlatformSvnCLIPath => SvnCLIPath;
#else
			public string PlatformSvnCLIPath => SvnCLIPathMacOS;
#endif
			public SVNMoveBehaviour MoveBehaviour = SVNMoveBehaviour.NormalSVNMove;

			// Enable lock prompts on asset modify.
			public bool EnableLockPrompt = false;

			public const string LockMessageHint = "Message used when locked after prompting the user.";
			[Tooltip(LockMessageHint)]
			public string LockPromptMessage = "Auto-locked.";

			[Tooltip("Automatically unlock if asset becomes unmodified (i.e. you reverted the asset).")]
			public bool AutoUnlockIfUnmodified = false;

#if UNITY_2020_2_OR_NEWER
			// Because we are rendering this list manually.
			[NonReorderable]
#endif
			// Lock prompt parameters for when asset is modified.
			public List<LockPromptParameters> LockPromptParameters = new List<LockPromptParameters>();


			// Enable svn branches database.
			public bool EnableBranchesDatabase;

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			// SVN parameters used for scanning branches in the SVN repo.
			public List<BranchScanParameters> BranchesDatabaseScanParameters = new List<BranchScanParameters>();

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			// Show these branches on top.
			public List<string> PinnedBranches = new List<string>();

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			public List<string> Exclude = new List<string>();

			public ProjectPreferences Clone()
			{
				var clone = (ProjectPreferences) MemberwiseClone();

				clone.LockPromptParameters = new List<LockPromptParameters>(LockPromptParameters);
				clone.BranchesDatabaseScanParameters = new List<BranchScanParameters>(BranchesDatabaseScanParameters);
				clone.PinnedBranches = new List<string>(PinnedBranches);
				clone.Exclude = new List<string>(Exclude);

				return clone;
			}
		}

		public PersonalPreferences PersonalPrefs;
		public ProjectPreferences ProjectPrefs;

		// Convenience: true when the master kill-switch is OFF and core integration is ON.
		// Use this everywhere instead of reading PersonalPrefs.EnableCoreIntegration directly
		// so PluginDisabled=true shuts everything down in one place.
		public bool IsIntegrationEnabled =>
			PersonalPrefs != null && !PersonalPrefs.PluginDisabled && PersonalPrefs.EnableCoreIntegration;

		public bool TemporarySilenceLockPrompts = false;


		[SerializeField] private long m_ProjectPrefsLastModifiedTime = 0;

		public event Action PreferencesChanged;

		/// Notify all listeners that preferences changed without saving to disk.
		/// Used by SVNOverlayIcons startup replay to re-bind GUI handlers.
		public void NotifyPreferencesChanged()
		{
			if (FileStatusIcons.Length == 0 || FileStatusIcons[(int)VCFileStatus.Modified]?.image == null) {
				LoadTextures();
			}
			PreferencesChanged?.Invoke();
		}

		public bool NeedsToAuthenticate { get; internal set; }

		public bool DownloadRepositoryChanges =>
			PersonalPrefs.DownloadRepositoryChanges == BoolPreference.SameAsProjectPreference
			? ProjectPrefs.DownloadRepositoryChanges
			: PersonalPrefs.DownloadRepositoryChanges == BoolPreference.Enabled;


		// Active status provider — always starts as CLIDatabaseStatusProvider so [InitializeOnLoad]
		// static constructors never block. On Windows with PreferTSVNCache=true, an async probe
		// runs after Unity finishes loading (EditorApplication.delayCall); if it succeeds the
		// provider is upgraded to TSVNCacheStatusProvider and StatusProviderChanged fires.
		private ISVNStatusProvider m_StatusProvider;
		private string m_StatusProviderProbeMessage;

		// Raised when the provider is upgraded (CLI → TSVNCache) after the async probe.
		public event Action StatusProviderChanged;

		public ISVNStatusProvider StatusProvider {
			get {
				// Always has a value — set in Initialize() before any [InitializeOnLoad] ctor runs.
				if (m_StatusProvider == null)
					m_StatusProvider = new CLIDatabaseStatusProvider();
				return m_StatusProvider;
			}
		}
		public string StatusProviderProbeMessage => m_StatusProviderProbeMessage;

		// Async probe — runs the named-pipe connection on a worker thread so it cannot block
		// Unity's main thread, even if TSVNCache.exe hangs or the pipe never responds.
		// On success we re-enter the main thread via EditorApplication.delayCall to swap the
		// provider and raise StatusProviderChanged. Probe failures stay silent (CLI stays active).
		[System.NonSerialized] private volatile bool m_StatusProviderProbeStarted;

		private void ProbeAndUpgradeProvider()
		{
#if UNITY_EDITOR_WIN
			if (m_StatusProviderProbeStarted) return;
			if (PersonalPrefs == null || !PersonalPrefs.PreferTSVNCache) {
				m_StatusProviderProbeMessage = "PreferTSVNCache is off.";
				return;
			}
			m_StatusProviderProbeStarted = true;

			var thread = new System.Threading.Thread(() => {
				bool ok;
				string reason;
				try {
					ok = TSVNCacheStatusProvider.Probe(out reason);
				} catch (System.Exception ex) {
					ok = false;
					reason = "Probe threw: " + ex.GetType().Name + ": " + ex.Message;
				}

				// Hop back to the main thread to swap the provider — touching SVNStatusBadge /
				// SceneView / Unity API from a worker thread would crash.
				EditorApplication.delayCall += () => {
					if (ok) {
						Debug.Log("[WiseSVN] Status source upgraded to TSVNCache (TortoiseSVN shared cache).");
						m_StatusProviderProbeMessage = "Connected.";
						m_StatusProvider = new TSVNCacheStatusProvider();
						StatusProviderChanged?.Invoke();
					} else {
						m_StatusProviderProbeMessage = reason;
						Debug.Log("[WiseSVN] TSVNCache unavailable (" + reason + "); keeping CLI status database.");
					}
				};
			}) {
				Name = "WiseSVN.TSVNCacheProbe",
				IsBackground = true,
			};
			thread.Start();
#else
			m_StatusProviderProbeMessage = "TSVNCache is Windows-only.";
#endif
		}

		public override void Initialize(bool freshlyCreated)
		{
			// Ensure a provider exists immediately — consumers access it inside [InitializeOnLoad]
			// static constructors, so it must never be null and must never block.
			if (m_StatusProvider == null)
				m_StatusProvider = new CLIDatabaseStatusProvider();

			// Probe TSVNCache after Unity finishes loading (avoids blocking the startup path).
			EditorApplication.delayCall += ProbeAndUpgradeProvider;

			var lastModifiedDate = File.Exists(PROJECT_PREFERENCES_PATH)
				? File.GetLastWriteTime(PROJECT_PREFERENCES_PATH).Ticks
				: 0
				;

			if (freshlyCreated || m_ProjectPrefsLastModifiedTime != lastModifiedDate) {
				try {
					LoadPreferences();

				} catch(Exception ex) {
					Debug.LogException(ex);
					PersonalPrefs = new PersonalPreferences();
					ProjectPrefs = new ProjectPreferences();
				}
			}

			if (freshlyCreated || m_RetryTextures) {

				LoadTextures();

				m_RetryTextures = false;

				// If WiseSVN was just added to the project, Unity won't manage to load the textures the first time. Try again next frame.
				if (FileStatusIcons[(int)VCFileStatus.Modified].image == null) {

					// We're using a flag as assembly reload may happen and update callback will be lost.
					m_RetryTextures = true;

					EditorApplication.CallbackFunction reloadTextures = null;
					reloadTextures = () => {
						LoadTextures();
						m_RetryTextures = false;
						EditorApplication.update -= reloadTextures;

						if (FileStatusIcons[(int)VCFileStatus.Modified].image == null) {
							Debug.LogWarning("SVN overlay icons are missing.");
						}
					};

					EditorApplication.update += reloadTextures;
				}

				// Subscribe via a static forwarder so assembly reloads don't leak instance refs on the event.
				LocalizationManager.OnLanguageChanged -= OnLanguageChangedReloadTextures;
				LocalizationManager.OnLanguageChanged += OnLanguageChangedReloadTextures;

				Debug.Log($"Loaded WiseSVN Preferences. WiseSVN is turned {(PersonalPrefs.EnableCoreIntegration ? "on" : "off")}.");

				if (PersonalPrefs.EnableCoreIntegration) {
					CheckSVNSupport();
				}
			}

			SVNContextMenusManager.SetupContextType(PersonalPrefs.ContextMenusClient);
		}

		// Static forwarder so the LocalizationManager.OnLanguageChanged event
		// holds a single delegate across assembly reloads (no instance leaks).
		private static void OnLanguageChangedReloadTextures()
		{
			var inst = Instance;
			if (inst != null) inst.LoadTextures();
		}

		public GUIContent GetFileStatusIconContent(VCFileStatus status)
		{
			// TODO: this is a legacy hack-fix. The enum status got new values and needs to be refreshed on old running clients. Remove someday.
			var index = (int)status;
			if (index >= FileStatusIcons.Length) {
				LoadTextures();
			}

			return FileStatusIcons[(int)status];
		}


		public GUIContent GetLockStatusIconContent(VCLockStatus status)
		{
			return LockStatusIcons[(int)status];
		}

		private void LoadPreferences()
		{
			if (File.Exists(PERSONAL_PREFERENCES_PATH)) {
				PersonalPrefs = JsonUtility.FromJson<PersonalPreferences>(File.ReadAllText(PERSONAL_PREFERENCES_PATH));
			} else if (EditorPrefs.HasKey(LEGACY_PERSONAL_PREFERENCES_KEY)) {
				PersonalPrefs = JsonUtility.FromJson<PersonalPreferences>(EditorPrefs.GetString(LEGACY_PERSONAL_PREFERENCES_KEY, string.Empty));
			} else {
				PersonalPrefs = new PersonalPreferences();

#if UNITY_EDITOR_WIN
				PersonalPrefs.ContextMenusClient = ContextMenusClient.TortoiseSVN;
#elif UNITY_EDITOR_OSX
				PersonalPrefs.ContextMenusClient = ContextMenusClient.SnailSVN;
#else
				PersonalPrefs.ContextMenusClient = ContextMenusClient.RabbitVCS;
#endif
			}

			if (File.Exists(PROJECT_PREFERENCES_PATH)) {
				ProjectPrefs = JsonUtility.FromJson<ProjectPreferences>(File.ReadAllText(PROJECT_PREFERENCES_PATH));
				m_ProjectPrefsLastModifiedTime = File.GetLastWriteTime(PROJECT_PREFERENCES_PATH).Ticks;
			} else {
				ProjectPrefs = new ProjectPreferences();
				m_ProjectPrefsLastModifiedTime = 0;
			}

			LocalizationManager.SetLanguage(PersonalPrefs.Language);
		}

		private void LoadTextures()
		{
			switch (PersonalPrefs.IconStyle)
			{
				case WiseSVNIconStyle.Emoji:
				default:
					LoadEmojiIcons();
					return;

				case WiseSVNIconStyle.TortoiseSVN:
					string dir = WiseSVNGUIUtils.GetTortoiseOverlaysIconsDir();
					if (!string.IsNullOrEmpty(dir))
					{
						LoadTextureIcons(true, dir, PersonalPrefs.TortoiseSVNTheme);
						return;
					}
					LoadEmojiIcons();
					return;
			}
		}

		/// <summary>
		/// Emoji 模式：全部使用 Unicode 字符代替贴图，image==null。
		/// ItemOnGUI 走 DrawEmoji 路径，零外部资源。
		/// </summary>
		private void LoadEmojiIcons()
		{
			FileStatusIcons = new GUIContent[Enum.GetValues(typeof(VCFileStatus)).Length];
			FileStatusIcons[(int)VCFileStatus.Normal]      = new GUIContent("✅", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Added]       = new GUIContent("➕", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Modified]    = new GUIContent("✏️", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Replaced]    = new GUIContent("🔄", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Deleted]     = new GUIContent("❌", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Missing]     = new GUIContent("😶‍🌫", Tr("overlay.tooltip.missing"));
			FileStatusIcons[(int)VCFileStatus.Conflicted]  = new GUIContent("⚠️", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Ignored]     = new GUIContent("🙈", Tr("overlay.tooltip.ignored"));
			FileStatusIcons[(int)VCFileStatus.Unversioned] = new GUIContent("❔️", string.Empty);
			FileStatusIcons[(int)VCFileStatus.Excluded]    = new GUIContent("🚫", Tr("overlay.tooltip.excluded"));
			FileStatusIcons[(int)VCFileStatus.External]    = new GUIContent("🔗", Tr("overlay.tooltip.external"));
			FileStatusIcons[(int)VCFileStatus.Obstructed]  = new GUIContent("💥", Tr("overlay.tooltip.obstructed"));
			FileStatusIcons[(int)VCFileStatus.ReadOnly]    = new GUIContent("👀", Tr("overlay.tooltip.readonly"));
			FileStatusIcons[(int)VCFileStatus.Incomplete]  = new GUIContent("⏳", Tr("overlay.tooltip.incomplete"));
			FileStatusIcons[(int)VCFileStatus.Merged]      = new GUIContent("♻️", Tr("overlay.tooltip.merged"));

			LockStatusIcons = new GUIContent[Enum.GetValues(typeof(VCLockStatus)).Length];
			LockStatusIcons[(int)VCLockStatus.LockedHere]      = new GUIContent("🔒", Tr("overlay.tooltip.locked_here"));
			LockStatusIcons[(int)VCLockStatus.BrokenLock]      = new GUIContent("⛓️", Tr("overlay.tooltip.broken_lock"));
			LockStatusIcons[(int)VCLockStatus.LockedOther]     = new GUIContent("🔐", Tr("overlay.tooltip.locked_other"));
			LockStatusIcons[(int)VCLockStatus.LockedButStolen] = new GUIContent("🗡️", Tr("overlay.tooltip.locked_stolen"));
		}

		/// <summary>
		/// TortoiseSVN 贴图加载逻辑。
		/// </summary>
		private void LoadTextureIcons(bool tortoise, string iconsDir, string theme)
		{
			FileStatusIcons = new GUIContent[Enum.GetValues(typeof(VCFileStatus)).Length];
			FileStatusIcons[(int)VCFileStatus.Normal]      = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "NormalIcon.ico",      null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNNormalIcon");
			FileStatusIcons[(int)VCFileStatus.Added]       = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "AddedIcon.ico",       null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNAddedIcon");
			FileStatusIcons[(int)VCFileStatus.Modified]    = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ModifiedIcon.ico",    null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNModifiedIcon");
			FileStatusIcons[(int)VCFileStatus.Replaced]    = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ModifiedIcon.ico",    null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNModifiedIcon");
			FileStatusIcons[(int)VCFileStatus.Deleted]     = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "DeletedIcon.ico",     null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNDeletedIcon");
			FileStatusIcons[(int)VCFileStatus.Missing]     = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "DeletedIcon.ico",     Tr("overlay.tooltip.missing"))             ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNDeletedIcon",     Tr("overlay.tooltip.missing"));
			FileStatusIcons[(int)VCFileStatus.Conflicted]  = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ConflictIcon.ico",    null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNConflictIcon");
			FileStatusIcons[(int)VCFileStatus.Ignored]     = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "IgnoredIcon.ico",     Tr("overlay.tooltip.ignored"))             ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNIgnoredIcon",     Tr("overlay.tooltip.ignored"));
			FileStatusIcons[(int)VCFileStatus.Unversioned] = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "UnversionedIcon.ico", null)                                   ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNUnversionedIcon");
			FileStatusIcons[(int)VCFileStatus.Excluded]    = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ReadOnlyIcon.ico",    Tr("overlay.tooltip.excluded"))            ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNReadOnlyIcon",    Tr("overlay.tooltip.excluded"));
			FileStatusIcons[(int)VCFileStatus.External]    = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "NormalIcon.ico",      Tr("overlay.tooltip.external"))            ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNNormalIcon",      Tr("overlay.tooltip.external"));
			FileStatusIcons[(int)VCFileStatus.Obstructed]  = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ConflictIcon.ico",    Tr("overlay.tooltip.obstructed"))          ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNConflictIcon",    Tr("overlay.tooltip.obstructed"));
			FileStatusIcons[(int)VCFileStatus.ReadOnly]    = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ReadOnlyIcon.ico",    Tr("overlay.tooltip.readonly"))            ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNReadOnlyIcon",    Tr("overlay.tooltip.readonly"));
			FileStatusIcons[(int)VCFileStatus.Incomplete]  = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ConflictIcon.ico",    Tr("overlay.tooltip.incomplete"))          ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNConflictIcon",    Tr("overlay.tooltip.incomplete"));
			FileStatusIcons[(int)VCFileStatus.Merged]      = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ModifiedIcon.ico",    Tr("overlay.tooltip.merged"))              ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/SVNModifiedIcon",    Tr("overlay.tooltip.merged"));

			LockStatusIcons = new GUIContent[Enum.GetValues(typeof(VCLockStatus)).Length];
			LockStatusIcons[(int)VCLockStatus.LockedHere]      = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "LockedIcon.ico",  Tr("overlay.tooltip.locked_here"))  ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/Locks/SVNLockedHereIcon",  Tr("overlay.tooltip.locked_here"));
			LockStatusIcons[(int)VCLockStatus.BrokenLock]      = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "LockedIcon.ico",  Tr("overlay.tooltip.broken_lock"))  ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/Locks/SVNLockedOtherIcon", Tr("overlay.tooltip.broken_lock"));
			LockStatusIcons[(int)VCLockStatus.LockedOther]     = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "ReadOnlyIcon.ico",Tr("overlay.tooltip.locked_other")) ?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/Locks/SVNLockedOtherIcon", Tr("overlay.tooltip.locked_other"));
			LockStatusIcons[(int)VCLockStatus.LockedButStolen] = WiseSVNGUIUtils.TryTortoiseIcon(tortoise, iconsDir, theme, "LockedIcon.ico",  Tr("overlay.tooltip.locked_stolen"))?? WiseSVNGUIUtils.LoadTexture("SVNOverlayIcons/Locks/SVNLockedOtherIcon", Tr("overlay.tooltip.locked_stolen"));
		}


		// Helper methods migrated to WiseSVNGUIUtils

		public void SavePreferences(PersonalPreferences personalPrefs, ProjectPreferences projectPrefs)
		{
			PersonalPrefs = personalPrefs.Clone();
			ProjectPrefs = projectPrefs.Clone();

			try {
				Directory.CreateDirectory(Path.GetDirectoryName(PERSONAL_PREFERENCES_PATH));
				File.WriteAllText(PERSONAL_PREFERENCES_PATH, JsonUtility.ToJson(PersonalPrefs, true));
			}
			catch (Exception ex) {
				Debug.LogException(ex);
			}

			try {
				File.WriteAllText(PROJECT_PREFERENCES_PATH, JsonUtility.ToJson(ProjectPrefs, true));
			}
			catch (Exception ex) {
				Debug.LogException(ex);
				EditorUtility.DisplayDialog("Error", $"Failed to write file:\n\"{PROJECT_PREFERENCES_PATH}\"\n\nData not saved! Check the logs for more info.", "Ok");
			}

			SVNContextMenusManager.SetupContextType(PersonalPrefs.ContextMenusClient);

			// Only reload locale if the requested language actually changed —
			// the Personal tab UI already called SetLanguage on change.
			if (LocalizationManager.Language != PersonalPrefs.Language) {
				LocalizationManager.SetLanguage(PersonalPrefs.Language);
			}

			// Reload textures (icon style may have changed).
			LoadTextures();

			PreferencesChanged?.Invoke();
		}

		// NOTE: Copy pasted from SearchAssetsFilter.
		public static bool ShouldExclude(IEnumerable<string> excludes, string path)
		{
			if (path.EndsWith(".meta")) {
				path = path.Substring(0, path.Length - ".meta".Length);
			}

			foreach(var exclude in excludes) {

				bool isExcludePath = exclude.Contains("/");    // Check if this is a path or just a filename

				if (isExcludePath) {
					if (WiseSVNIntegration.ArePathsNested(exclude, path))
						return true;

				} else {

					var filename = Path.GetFileName(path);
					if (filename.IndexOf(exclude, StringComparison.OrdinalIgnoreCase) != -1)
						return true;
				}
			}

			return false;
		}

		public static string SanitizeUnityPath(string path)
		{
			return path
				.Trim()
				.TrimEnd('\\', '/')
				.Replace('\\', '/')
				;
		}

		public void CheckSVNSupport()
		{
#if UNITY_EDITOR_OSX
			// The terminal runs with PATH environment variable that has more paths included than Unity (or any GUI app).
			// It reads additional paths from '/etc/paths' and '/etc/paths.d' and any user profile at ~.
			// Read more here: https://forum.unity.com/threads/modifing-path-variable-in-macos-for-unity.500616/#post-9810975
			//
			// Unity PATH variable by default: /usr/bin:/bin:/usr/sbin:/sbin
			// Homebrew spits out binaries at '/usr/local/bin' for Intel or '/opt/homebrew/bin' for ARM.
			// MacPorts spits out binaries at '/opt/local/bin' (not tested).
			// Add all these paths.
			string pathEnvVariable = Environment.GetEnvironmentVariable("PATH");

			if (!pathEnvVariable.Contains("/usr/local/bin")) {
				pathEnvVariable += ":/usr/local/bin";
				Environment.SetEnvironmentVariable("PATH", pathEnvVariable);
			}

			if (!pathEnvVariable.Contains("/opt/homebrew/bin")) {
				pathEnvVariable += ":/opt/homebrew/bin";
				Environment.SetEnvironmentVariable("PATH", pathEnvVariable);
			}

			if (!pathEnvVariable.Contains("/opt/local/bin")) {
				pathEnvVariable += ":/opt/local/bin";
				Environment.SetEnvironmentVariable("PATH", pathEnvVariable);
			}
#endif

			string svnError;
			try {
				svnError = WiseSVNIntegration.CheckForSVNErrors();

			}
			catch (Exception ex) {
				svnError = ex.ToString();
			}

			if (string.IsNullOrEmpty(svnError)) {
				if (DownloadRepositoryChanges || ProjectPrefs.EnableLockPrompt) {
					WiseSVNIntegration.CheckForSVNAuthErrors().Completed += CheckForSVNAuthErrorsResponse;
				}
				return;
			}

			PersonalPrefs.EnableCoreIntegration = false;

			// NOTE: check for SVN binaries first, as it tries to recover and may get other errors!

			// System.ComponentModel.Win32Exception (0x80004005): ApplicationName='...', CommandLine='...', Native error= The system cannot find the file specified.
			// Could not find the command executable. The user hasn't installed their CLI (Command Line Interface) so we're missing an "svn.exe" in the PATH environment.
			// This is allowed only if there isn't ProjectPreference specified CLI path.
			if (svnError.Contains("0x80004005") || svnError.Contains("IOException")) {

#if UNITY_EDITOR_OSX
				// For some reason OSX doesn't have the svn binaries set to the PATH environment by default.
				// If that is the case and we find them at the usual place, just set it as a personal preference.
				if (string.IsNullOrWhiteSpace(PersonalPrefs.SvnCLIPath)) {

					// Just shooting in the dark where SVN could be installed.
					string[] osxDefaultBinariesPaths = new string[] {
						"/usr/local/bin/svn",
						"/usr/bin/svn",
						"/Applications/Xcode.app/Contents/Developer/usr/bin/svn",
						"/opt/subversion/bin/svn",
						"/opt/local/bin/svn",
						"/opt/homebrew/bin/svn",

						// SnailSVN comes with bundled up svn binaries. Don't use them as they don't actually work. Running those executables produces errors.
					};

					foreach(string osxPath in osxDefaultBinariesPaths) {
						if (!File.Exists(osxPath))
							continue;

						PersonalPrefs.SvnCLIPath = osxPath;

						try {
							string secondSvnError = WiseSVNIntegration.CheckForSVNErrors();
							// Exclude "not a working copy". Check below.
							if (!string.IsNullOrEmpty(secondSvnError) && !secondSvnError.Contains("W155007"))
								continue;

							PersonalPrefs.EnableCoreIntegration = true;	// Save this enabled!
							SavePreferences(PersonalPrefs, ProjectPrefs);
							Debug.Log($"SVN binaries missing in PATH environment variable. Found them at \"{osxPath}\". Setting this as personal preference.\n\n{svnError}");

							CheckSVNSupport();

							return;

						} catch(Exception) {
						}
					}

					// Failed to find binaries.
					PersonalPrefs.SvnCLIPath = string.Empty;
				}
#endif

				WiseSVNIntegration.LogStatusErrorHint(StatusOperationResult.ExecutableNotFound, $"\nTemporarily disabling WiseSVN integration. Please fix the error and restart Unity.\n\n{svnError}");
#if UNITY_EDITOR_OSX
				// DEPRECATED: setting PATH environment variable should have handled this. If not let user report it.
				//Debug.LogError($"If you installed SVN via Homebrew or similar, you may need to add \"/usr/local/bin\" (or wherever svn binaries can be found) to your PATH environment variable and restart. Example:\nsudo launchctl config user path /usr/local/bin\nAlternatively, you may add SVN CLI path in your WiseSVN preferences at:\n{SVNPreferencesWindow.PROJECT_PREFERENCES_MENU}");
#endif
				return;
			}

			// svn: warning: W155007: '...' is not a working copy!
			// This can be returned when project is not a valid svn checkout. (Probably)
			if (svnError.Contains("W155007")) {
				Debug.LogError($"This project is NOT under version control (not a proper SVN checkout). Temporarily disabling WiseSVN integration.\n\n{svnError}");
				return;
			}

			// Any other error.
			if (!string.IsNullOrEmpty(svnError)) {
				Debug.LogError($"Calling SVN CLI (Command Line Interface) caused fatal error!\nTemporarily disabling WiseSVN integration. Please fix the error and restart Unity.\n{svnError}\n\n");
			} else {
				// Recovered from error, enable back integration.
				PersonalPrefs.EnableCoreIntegration = true;
			}
		}

		private void CheckForSVNAuthErrorsResponse(SVNAsyncOperation<StatusOperationResult> operation)
		{
			if (operation.Result == StatusOperationResult.AuthenticationFailed) {
				NeedsToAuthenticate = true;
			}

			WiseSVNIntegration.LogStatusErrorHint(operation.Result);
		}

		internal void TryToAuthenticate()
		{
			if (EditorUtility.DisplayDialog(
				LocalizationManager.Tr("prefs.svn_authenticate.title"),
				LocalizationManager.Tr("prefs.svn_authenticate.message"),
				LocalizationManager.Tr("prefs.svn_authenticate.proceed"),
				LocalizationManager.Tr("common.cancel"))) {

				WiseSVNIntegration.PromptForAuth(WiseSVNIntegration.ProjectRootNative);

				foreach(string repositoryPath in SVNStatusesDatabase.Instance.NestedRepositories) {
					WiseSVNIntegration.PromptForAuth(Path.Combine(WiseSVNIntegration.ProjectRootNative, repositoryPath));
				}

				NeedsToAuthenticate = false;

				WiseSVNIntegration.CheckForSVNAuthErrors().Completed += CheckForSVNAuthErrorsResponse;
			}
		}
	}
}
