// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using DevLocker.VersionControl.WiseSVN.ContextMenus;
using DevLocker.VersionControl.WiseSVN.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

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
		[SerializeField] private GUIContent RemoteStatusIcons = null;

		[SerializeField] private bool m_RetryTextures = false;

		[Serializable]
		internal class PersonalPreferences
		{
			public bool EnableCoreIntegration = true;		// Sync file operations with SVN
			public bool PopulateStatusesDatabase = true;    // For overlay icons etc.
			public bool PopulateIgnoresDatabase = true;    // For svn-ignored icons etc.
			public bool ShowNormalStatusOverlayIcon = false;
			public bool ShowExcludedStatusOverlayIcon = true;

			public string SvnCLIPath = string.Empty;

			// When populating the database, should it check for server changes as well (locks & modified files).
			public BoolPreference DownloadRepositoryChanges = BoolPreference.SameAsProjectPreference;
			public bool AutoLockOnModified = false;
			public bool WarnForPotentialConflicts = true;
			public bool AskOnMovingFolders = true;

			public int AutoRefreshDatabaseInterval = 60;    // seconds; Less than 0 will disable it.
			public ContextMenusClient ContextMenusClient = ContextMenusClient.TortoiseSVN;
			public SVNTraceLogs TraceLogs = SVNTraceLogs.SVNOperations;

			// UI display language. Auto follows Unity's Application.systemLanguage.
			public WiseSVNLanguage Language = WiseSVNLanguage.Auto;

			public WiseSVNIconStyle IconStyle = WiseSVNIconStyle.Classic;
			public string TortoiseSVNTheme = "Win10";

#if UNITY_2020_2_OR_NEWER
			[NonReorderable]
#endif
			public List<string> Exclude = new List<string>();

			public const string AutoLockOnModifiedHint = "Will automatically lock assets if possible when they become modified, instead of prompting the user.\nIf assets have newer version or are locked by someone else, prompt will still be displayed.\n\nNotification will be displayed. Check the logs to know what was locked.";

			public PersonalPreferences Clone()
			{
				var clone = (PersonalPreferences) MemberwiseClone();
				clone.Exclude = new List<string>(Exclude);
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

		public bool TemporarySilenceLockPrompts = false;


		[SerializeField] private long m_ProjectPrefsLastModifiedTime = 0;

		public event Action PreferencesChanged;

		public bool NeedsToAuthenticate { get; internal set; }

		public bool DownloadRepositoryChanges =>
			PersonalPrefs.DownloadRepositoryChanges == BoolPreference.SameAsProjectPreference
			? ProjectPrefs.DownloadRepositoryChanges
			: PersonalPrefs.DownloadRepositoryChanges == BoolPreference.Enabled;


		public override void Initialize(bool freshlyCreated)
		{
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

		public GUIContent GetRemoteStatusIconContent(VCRemoteFileStatus status)
		{
			return status == VCRemoteFileStatus.Modified ? RemoteStatusIcons : null;
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
			bool tortoise = PersonalPrefs.IconStyle == WiseSVNIconStyle.TortoiseSVN;
			string iconsDir = tortoise ? GetTortoiseOverlaysIconsDir() : string.Empty;
			string theme = PersonalPrefs.TortoiseSVNTheme;

			FileStatusIcons = new GUIContent[Enum.GetValues(typeof(VCFileStatus)).Length];
			FileStatusIcons[(int)VCFileStatus.Normal]      = TryTortoiseIcon(tortoise, iconsDir, theme, "NormalIcon.ico",      null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNNormalIcon");
			FileStatusIcons[(int)VCFileStatus.Added]       = TryTortoiseIcon(tortoise, iconsDir, theme, "AddedIcon.ico",       null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNAddedIcon");
			FileStatusIcons[(int)VCFileStatus.Modified]    = TryTortoiseIcon(tortoise, iconsDir, theme, "ModifiedIcon.ico",    null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNModifiedIcon");
			FileStatusIcons[(int)VCFileStatus.Replaced]    = TryTortoiseIcon(tortoise, iconsDir, theme, "ModifiedIcon.ico",    null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNModifiedIcon");
			FileStatusIcons[(int)VCFileStatus.Deleted]     = TryTortoiseIcon(tortoise, iconsDir, theme, "DeletedIcon.ico",     null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNDeletedIcon");
			FileStatusIcons[(int)VCFileStatus.Conflicted]  = TryTortoiseIcon(tortoise, iconsDir, theme, "ConflictIcon.ico",    null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNConflictIcon");
			FileStatusIcons[(int)VCFileStatus.Ignored]     = TryTortoiseIcon(tortoise, iconsDir, theme, "IgnoredIcon.ico",     LocalizationManager.Tr("overlay.tooltip.ignored"))     ?? LoadTexture("SVNOverlayIcons/SVNIgnoredIcon",     LocalizationManager.Tr("overlay.tooltip.ignored"));
			FileStatusIcons[(int)VCFileStatus.Unversioned] = TryTortoiseIcon(tortoise, iconsDir, theme, "UnversionedIcon.ico", null)                                                   ?? LoadTexture("SVNOverlayIcons/SVNUnversionedIcon");
			FileStatusIcons[(int)VCFileStatus.Excluded]    = TryTortoiseIcon(tortoise, iconsDir, theme, "ReadOnlyIcon.ico",    LocalizationManager.Tr("overlay.tooltip.excluded"))    ?? LoadTexture("SVNOverlayIcons/SVNReadOnlyIcon",    LocalizationManager.Tr("overlay.tooltip.excluded"));

			LockStatusIcons = new GUIContent[Enum.GetValues(typeof(VCLockStatus)).Length];
			LockStatusIcons[(int)VCLockStatus.LockedHere]      = TryTortoiseIcon(tortoise, iconsDir, theme, "LockedIcon.ico",  LocalizationManager.Tr("overlay.tooltip.locked_here"))  ?? LoadTexture("SVNOverlayIcons/Locks/SVNLockedHereIcon",  LocalizationManager.Tr("overlay.tooltip.locked_here"));
			LockStatusIcons[(int)VCLockStatus.BrokenLock]      = TryTortoiseIcon(tortoise, iconsDir, theme, "LockedIcon.ico",  LocalizationManager.Tr("overlay.tooltip.broken_lock"))  ?? LoadTexture("SVNOverlayIcons/Locks/SVNLockedOtherIcon", LocalizationManager.Tr("overlay.tooltip.broken_lock"));
			LockStatusIcons[(int)VCLockStatus.LockedOther]     = TryTortoiseIcon(tortoise, iconsDir, theme, "ReadOnlyIcon.ico",LocalizationManager.Tr("overlay.tooltip.locked_other")) ?? LoadTexture("SVNOverlayIcons/Locks/SVNLockedOtherIcon", LocalizationManager.Tr("overlay.tooltip.locked_other"));
			LockStatusIcons[(int)VCLockStatus.LockedButStolen] = TryTortoiseIcon(tortoise, iconsDir, theme, "LockedIcon.ico",  LocalizationManager.Tr("overlay.tooltip.locked_stolen")) ?? LoadTexture("SVNOverlayIcons/Locks/SVNLockedOtherIcon", LocalizationManager.Tr("overlay.tooltip.locked_stolen"));

			// TortoiseOverlays has no remote-changes icon; always use the bundled PNG.
			RemoteStatusIcons = LoadTexture("SVNOverlayIcons/Others/SVNRemoteChangesIcon", LocalizationManager.Tr("overlay.tooltip.remote_changes"));
		}

		// Returns a GUIContent loaded from TortoiseOverlays, or null if unavailable (falls back to Classic).
		private static GUIContent TryTortoiseIcon(bool tortoise, string iconsDir, string theme, string iconFile, string tooltip)
		{
			if (!tortoise || string.IsNullOrEmpty(iconsDir)) return null;
			string path = Path.Combine(iconsDir, theme, iconFile);
			if (!File.Exists(path)) return null;

			try {
				byte[] data = File.ReadAllBytes(path);
				var tex = ExtractBestImageFromIco(data);
				if (tex != null) {
					tex.filterMode = FilterMode.Bilinear;
					return new GUIContent(tex, tooltip);
				}
			} catch (Exception ex) {
				Debug.LogWarning($"[WiseSVN] Failed to load TortoiseSVN icon {path}: {ex.Message}");
			}
			return null;
		}

		// Reads the directory at %CommonProgramFiles%\TortoiseOverlays\icons\.
		public static string GetTortoiseOverlaysIconsDir()
		{
#if UNITY_EDITOR_WIN
			string commonFiles = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles);
			string path = Path.Combine(commonFiles, "TortoiseOverlays", "icons");
			if (Directory.Exists(path)) return path;
			string commonFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86);
			if (!string.IsNullOrEmpty(commonFilesX86)) {
				string pathX86 = Path.Combine(commonFilesX86, "TortoiseOverlays", "icons");
				if (Directory.Exists(pathX86)) return pathX86;
			}
#endif
			return string.Empty;
		}

		// Returns sorted list of theme folder names available in TortoiseOverlays (e.g. "Win10", "Flat", ...).
		public static string[] GetAvailableTortoiseThemes()
		{
			string iconsDir = GetTortoiseOverlaysIconsDir();
			if (string.IsNullOrEmpty(iconsDir)) return new string[0];
			return Directory.GetDirectories(iconsDir)
				.Select(Path.GetFileName)
				.OrderBy(n => n)
				.ToArray();
		}

		// Parses a .ico file and returns the best available image as a Texture2D.
		// Prefers embedded PNG (e.g. 256×256 in Win10/Flat themes); falls back to 32bpp BMP DIB.
		private static Texture2D ExtractBestImageFromIco(byte[] data)
		{
			if (data == null || data.Length < 6) return null;

			int count = data[4] | (data[5] << 8);
			if (count == 0) return null;

			int bestPngOffset = -1, bestPngSize = 0, bestPngW = 0;
			int bestBmpOffset = -1, bestBmpW = 0, bestBmpBpp = 0;

			for (int i = 0; i < count; i++) {
				int e = 6 + i * 16;
				if (e + 16 > data.Length) break;

				int w        = data[e] == 0 ? 256 : data[e];
				int dataSize = data[e+8]  | (data[e+9] <<8) | (data[e+10]<<16) | (data[e+11]<<24);
				int imgOff   = data[e+12] | (data[e+13]<<8) | (data[e+14]<<16) | (data[e+15]<<24);
				if (imgOff + 4 > data.Length) continue;

				bool isPng = data[imgOff]==0x89 && data[imgOff+1]==0x50 && data[imgOff+2]==0x4E && data[imgOff+3]==0x47;
				if (isPng) {
					if (w > bestPngW) { bestPngW = w; bestPngOffset = imgOff; bestPngSize = dataSize; }
				} else if (imgOff + 16 <= data.Length) {
					int bpp = data[imgOff+14] | (data[imgOff+15] << 8); // biBitCount at offset 14 in BITMAPINFOHEADER
					if (bpp == 32 && (bestBmpBpp < 32 || w > bestBmpW)) {
						bestBmpOffset = imgOff; bestBmpW = w; bestBmpBpp = bpp;
					} else if (bestBmpBpp < 32 && w > bestBmpW) {
						bestBmpOffset = imgOff; bestBmpW = w; bestBmpBpp = bpp;
					}
				}
			}

			// Prefer embedded PNG
			if (bestPngOffset >= 0) {
				byte[] png = new byte[bestPngSize];
				Array.Copy(data, bestPngOffset, png, 0, bestPngSize);
				var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
				if (tex.LoadImage(png)) return tex;
				UnityEngine.Object.DestroyImmediate(tex);
			}

			// Fall back to 32bpp BMP DIB
			if (bestBmpOffset >= 0 && bestBmpBpp == 32)
				return ParseDib32(data, bestBmpOffset, bestBmpW);

			return null;
		}

		// Parses a 32bpp BMP DIB (as embedded in an ICO) into a Texture2D.
		// In ICO format the BITMAPINFOHEADER.biHeight is doubled (pixel rows + AND mask rows).
		private static Texture2D ParseDib32(byte[] data, int offset, int width)
		{
			int biSize   = data[offset] | (data[offset+1]<<8) | (data[offset+2]<<16) | (data[offset+3]<<24);
			// biHeight is a signed 32-bit integer; C# integer shifts already propagate sign via the high byte.
			int biHeight = data[offset+8] | (data[offset+9]<<8) | (data[offset+10]<<16) | (data[offset+11]<<24);
			bool topDown     = biHeight < 0;
			int actualHeight = Math.Abs(biHeight) / 2; // doubled in ICO: pixel-rows + AND-mask rows
			if (actualHeight <= 0) actualHeight = width;

			int pixelBase = offset + biSize;
			int stride    = width * 4;

			var pixels = new Color32[width * actualHeight];
			for (int row = 0; row < actualHeight; row++) {
				int srcRow  = topDown ? row : (actualHeight - 1 - row); // BMP rows are bottom-up by default
				int srcBase = pixelBase + srcRow * stride;
				int dstBase = row * width;
				for (int col = 0; col < width; col++) {
					int src = srcBase + col * 4;
					if (src + 3 >= data.Length) { pixels[dstBase + col] = new Color32(0, 0, 0, 0); continue; }
					pixels[dstBase + col] = new Color32(data[src+2], data[src+1], data[src], data[src+3]); // BGRA→RGBA
				}
			}

			var tex = new Texture2D(width, actualHeight, TextureFormat.RGBA32, false, true);
			tex.SetPixels32(pixels);
			tex.Apply(false, false);
			return tex;
		}

		public static GUIContent LoadTexture(string path, string tooltip = null)
		{
			return new GUIContent(Resources.Load<Texture2D>(path), tooltip);

			//var texture = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(path))
			//	.Select(AssetDatabase.GUIDToAssetPath)
			//	.Select(AssetDatabase.LoadAssetAtPath<Texture2D>)
			//	.FirstOrDefault()
			//	;
			//
			//return new GUIContent(texture, tooltip);
		}


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
