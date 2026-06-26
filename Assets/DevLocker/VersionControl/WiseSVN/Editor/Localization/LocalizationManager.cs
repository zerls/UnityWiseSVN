// MIT License Copyright(c) 2026 WiseSVN i18n Contributors

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Localization
{
	public enum WiseSVNLanguage
	{
		Auto = 0,
		English = 1,
		ChineseSimplified = 2,
	}

	/// <summary>
	/// Lightweight localization system for WiseSVN.
	/// Loads key=value pairs from Editor/Localization/locale_xx.txt files.
	///
	/// Lookup falls back to English (the fallback locale) when a key is missing,
	/// and finally to the key itself so missing translations are easy to spot.
	/// </summary>
	public static class LocalizationManager
	{
		private const string LocaleDirRelative = "Localization";
		private const string FallbackLocaleName = "locale_en";

		private static Dictionary<string, string> s_Current = new Dictionary<string, string>();
		private static Dictionary<string, string> s_Fallback = new Dictionary<string, string>();

		private static WiseSVNLanguage s_RequestedLanguage = WiseSVNLanguage.Auto;
		private static WiseSVNLanguage s_ResolvedLanguage = WiseSVNLanguage.English;
		private static bool s_Initialized = false;

		/// <summary>The language the user selected (may be Auto).</summary>
		public static WiseSVNLanguage Language => s_RequestedLanguage;

		/// <summary>The concrete language currently loaded (Auto resolved to English/Chinese).</summary>
		public static WiseSVNLanguage ResolvedLanguage => s_ResolvedLanguage;

		/// <summary>Fired after a language switch completes; UI should Repaint().</summary>
		public static event Action OnLanguageChanged;

		/// <summary>Detect locale from Unity's reported system language.</summary>
		public static WiseSVNLanguage DetectSystemLanguage()
		{
			var sys = Application.systemLanguage;
			if (sys == SystemLanguage.ChineseSimplified || sys == SystemLanguage.ChineseTraditional || sys == SystemLanguage.Chinese)
				return WiseSVNLanguage.ChineseSimplified;
			return WiseSVNLanguage.English;
		}

		/// <summary>Set the active language. Reloads locale data and fires OnLanguageChanged.</summary>
		public static void SetLanguage(WiseSVNLanguage lang)
		{
			s_RequestedLanguage = lang;
			s_ResolvedLanguage = (lang == WiseSVNLanguage.Auto) ? DetectSystemLanguage() : lang;

			EnsureFallbackLoaded();

			if (s_ResolvedLanguage == WiseSVNLanguage.English) {
				// Copy, do NOT share the dictionary reference — future reloads must not mutate fallback.
				s_Current = new Dictionary<string, string>(s_Fallback);
			} else {
				s_Current = LoadLocale(LocaleFileName(s_ResolvedLanguage));
			}

			s_Initialized = true;
			OnLanguageChanged?.Invoke();
		}

		/// <summary>Look up a translation key. Returns the translated string,
		/// falling back to English, then to the key itself.</summary>
		public static string Tr(string key)
		{
			if (string.IsNullOrEmpty(key))
				return key;

			if (!s_Initialized) {
				SetLanguage(s_RequestedLanguage);
			}

			if (s_Current.TryGetValue(key, out string value))
				return value;
			if (s_Fallback.TryGetValue(key, out value))
				return value;
			return key;
		}

		public static string Tr(string key, params object[] args)
		{
			string fmt = Tr(key);
			if (args == null || args.Length == 0)
				return fmt;
			try {
				return string.Format(fmt, args);
			} catch (FormatException) {
				return fmt;
			}
		}

		/// <summary>Convenience for EditorGUILayout calls.</summary>
		public static GUIContent TrContent(string labelKey, string tooltipKey = null)
		{
			string label = Tr(labelKey);
			string tooltip = string.IsNullOrEmpty(tooltipKey) ? string.Empty : Tr(tooltipKey);
			return new GUIContent(label, tooltip);
		}

		private static string LocaleFileName(WiseSVNLanguage lang)
		{
			switch (lang) {
				case WiseSVNLanguage.ChineseSimplified: return "locale_zh";
				case WiseSVNLanguage.English: return "locale_en";
				default: return FallbackLocaleName;
			}
		}

		private static void EnsureFallbackLoaded()
		{
			if (s_Fallback.Count == 0)
				s_Fallback = LoadLocale(FallbackLocaleName);
		}

		private static Dictionary<string, string> LoadLocale(string fileNameNoExt)
		{
			var dict = new Dictionary<string, string>();

			string path = FindLocaleFilePath(fileNameNoExt);
			if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
				Debug.LogWarning($"[WiseSVN i18n] Locale file not found: {fileNameNoExt}.txt");
				return dict;
			}

			try {
				foreach (string rawLine in File.ReadAllLines(path, System.Text.Encoding.UTF8)) {
					string line = rawLine;
					if (line.Length == 0 || line[0] == '#') continue;
					int eq = line.IndexOf('=');
					if (eq <= 0) continue;
					string key = line.Substring(0, eq).Trim();
					string value = line.Substring(eq + 1);
					// Allow escape sequences in values. \\ first so we don't re-process escaped backslashes.
					value = UnescapeLocaleValue(value);
					dict[key] = value;
				}
			} catch (Exception ex) {
				Debug.LogError($"[WiseSVN i18n] Failed to load {fileNameNoExt}.txt: {ex.Message}");
			}

			return dict;
		}

		// Cache resolved locale file paths so we don't hammer AssetDatabase.FindAssets on every reload.
		private static readonly Dictionary<string, string> s_LocalePathCache = new Dictionary<string, string>();

		private static string FindLocaleFilePath(string fileNameNoExt)
		{
			if (s_LocalePathCache.TryGetValue(fileNameNoExt, out string cached) && File.Exists(cached))
				return cached;

			// Search via AssetDatabase to locate locale files no matter where the plugin lives.
			string[] guids = AssetDatabase.FindAssets(fileNameNoExt + " t:TextAsset");
			foreach (string guid in guids) {
				string assetPath = AssetDatabase.GUIDToAssetPath(guid);
				if (!assetPath.Contains("/" + LocaleDirRelative + "/")) continue;
				if (!assetPath.EndsWith(fileNameNoExt + ".txt", StringComparison.OrdinalIgnoreCase)) continue;
				string full = Path.Combine(Directory.GetCurrentDirectory(), assetPath);
				s_LocalePathCache[fileNameNoExt] = full;
				return full;
			}
			return null;
		}

		// Manual unescape that handles \\ before \n / \t / \" so escaped backslashes aren't re-processed.
		private static string UnescapeLocaleValue(string raw)
		{
			var sb = new System.Text.StringBuilder(raw.Length);
			for (int i = 0; i < raw.Length; i++) {
				char c = raw[i];
				if (c == '\\' && i + 1 < raw.Length) {
					char next = raw[++i];
					switch (next) {
						case 'n':  sb.Append('\n'); break;
						case 't':  sb.Append('\t'); break;
						case 'r':  sb.Append('\r'); break;
						case '"':  sb.Append('"');  break;
						case '\\': sb.Append('\\'); break;
						default:   sb.Append('\\').Append(next); break;
					}
				} else {
					sb.Append(c);
				}
			}
			return sb.ToString();
		}

		/// <summary>Force reload of the active locale; useful after editing locale files.</summary>
		public static void Reload()
		{
			s_Fallback.Clear();
			s_Initialized = false;
			SetLanguage(s_RequestedLanguage);
		}
	}
}
