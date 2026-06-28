// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace DevLocker.VersionControl.WiseSVN.Utils
{
	/// <summary>
	/// Shared static GUI and icon-helpers across WiseSVN Editor windows.
	/// </summary>
	public static class WiseSVNGUIUtils
	{
		// ── Emoji 绘制 ─────────────────────────────────────────────────────────

		private static GUIStyle s_EmojiStyle;

		/// <summary>
		/// 使用动态字体大小在指定矩形中绘制 Emoji。
		/// scaleFactor 默认为 0.82，已针对 Windows (Segoe UI Emoji) 和 macOS (Apple Color Emoji) 测试。
		/// </summary>
		public static void DrawEmoji(Rect rect, GUIContent content, float scaleFactor = 0.82f)
		{
			if (s_EmojiStyle == null) {
				s_EmojiStyle = new GUIStyle(GUIStyle.none) {
					alignment = TextAnchor.MiddleCenter,
					padding   = new RectOffset(0, 0, 0, 0),
					richText  = false,
				};
			}
			s_EmojiStyle.fontSize = Mathf.Max(8, Mathf.RoundToInt(rect.height * scaleFactor));
			GUI.Label(rect, content, s_EmojiStyle);
		}

		// ── 日期格式化 ─────────────────────────────────────────────────────────

		/// <summary>
		/// 将 SVN 日期字符串格式化为 "yyyy-MM-dd HH:mm:ss"。
		/// 容忍形如 "2020-09-08 23:32:13 +0300 (??, 08 ??? 2020)" 的奇特格式。
		/// 解析失败时原样返回输入字符串，不抛异常。
		/// </summary>
		public static string FormatDate(string svnDate)
		{
			if (string.IsNullOrEmpty(svnDate)) return svnDate;

			if (DateTime.TryParse(svnDate, out var date))
				return date.ToString("yyyy-MM-dd HH:mm:ss");

			int parenIdx = svnDate.IndexOf('(');
			if (parenIdx > 0 && DateTime.TryParse(svnDate.Substring(0, parenIdx), out date))
				return date.ToString("yyyy-MM-dd HH:mm:ss");

			return svnDate;
		}

		// ── 路径处理 ───────────────────────────────────────────────────────────

		/// <summary>
		/// 若 path 以 ".meta" 结尾，返回去掉该后缀的路径；否则原样返回。
		/// </summary>
		public static string StripMetaSuffix(string path)
		{
			if (string.IsNullOrEmpty(path)) return path;

			if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
				return path.Substring(0, path.LastIndexOf(".meta", StringComparison.OrdinalIgnoreCase));

			return path;
		}

		// ── 图标加载辅助 ──────────────────────────────────────────────────────

		/// <summary>
		/// 包装 EditorGUIUtility.IconContent(name)。若结果 image 为 null，设置 text 为 fallback。
		/// 常用于内置图标缺失时的文本回退。
		/// </summary>
		public static GUIContent CreateIconWithTextFallback(string iconName, string fallback)
		{
			var content = EditorGUIUtility.IconContent(iconName);
			if (content.image == null)
				content.text = fallback;
			return content;
		}

		/// <summary>
		/// Load a texture from Resources by path (same as SVNPreferencesManager.LoadTexture).
		/// </summary>
		public static GUIContent LoadTexture(string path, string tooltip = null)
		{
			return new GUIContent(Resources.Load<Texture2D>(path), tooltip);
		}

		// ── GUI 样式工厂 ───────────────────────────────────────────────────────

		/// <summary>
		/// 创建一个无背景的迷你按钮样式，清除 normal/hover 背景纹理、padding 和 margin。
		/// 基于 GUI.skin.button，但移除了所有视觉背景，仅保留文本/图标交互。
		/// 返回一个新的 GUIStyle，调用方可进一步定制。
		/// </summary>
		public static GUIStyle MakeMiniButtonlessStyle()
		{
			var style = new GUIStyle(GUI.skin.button);
			style.hover.background = style.normal.background;
			style.hover.scaledBackgrounds = style.normal.scaledBackgrounds;
			style.hover.textColor = GUI.skin.label.hover.textColor;
			style.normal.background = null;
			style.normal.scaledBackgrounds = null;
			style.padding = new RectOffset();
			style.margin = new RectOffset();
			return style;
		}

		// ── TortoiseSVN / TortoiseOverlays 图标辅助 ────────────────────────────

		/// <summary>Returns a GUIContent loaded from TortoiseOverlays, or null if unavailable (falls back to Emoji).</summary>
		public static GUIContent TryTortoiseIcon(bool tortoise, string iconsDir, string theme, string iconFile, string tooltip)
		{
			if (!tortoise || string.IsNullOrEmpty(iconsDir)) return null;
			string path = Path.Combine(iconsDir, theme, iconFile);
			if (!File.Exists(path)) return null;

			try {
				byte[] data = File.ReadAllBytes(path);
				var tex = ExtractBestImageFromIco(data);
				if (tex != null) {
					tex.filterMode = FilterMode.Trilinear;
					tex.wrapMode   = TextureWrapMode.Clamp;
					tex.anisoLevel = 4;
					tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
					return new GUIContent(tex, tooltip);
				}
			} catch (Exception ex) {
				Debug.LogWarning($"[WiseSVN] Failed to load TortoiseSVN icon {path}: {ex.Message}");
			}
			return null;
		}

		/// <summary>Reads TortoiseOverlays installation directory at %CommonProgramFiles%\TortoiseOverlays\icons\</summary>
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

		/// <summary>Returns sorted list of theme folder names available in TortoiseOverlays (e.g. "Win10", "Flat", ...).</summary>
		public static string[] GetAvailableTortoiseThemes()
		{
			string iconsDir = GetTortoiseOverlaysIconsDir();
			if (string.IsNullOrEmpty(iconsDir)) return Array.Empty<string>();
			return Directory.GetDirectories(iconsDir)
				.Select(Path.GetFileName)
				.OrderBy(n => n)
				.ToArray();
		}

		// ── ICO 解析 ──────────────────────────────────────────────────────────

		/// <summary>Parses a .ico file and returns the best available image as a Texture2D.</summary>
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

			if (bestPngOffset >= 0) {
				byte[] png = new byte[bestPngSize];
				Array.Copy(data, bestPngOffset, png, 0, bestPngSize);
				var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true, linear: true);
				if (tex.LoadImage(png)) return tex;
				UnityEngine.Object.DestroyImmediate(tex);
			}

			if (bestBmpOffset >= 0 && bestBmpBpp == 32)
				return ParseDib32(data, bestBmpOffset, bestBmpW);

			return null;
		}

		/// <summary>Parses a 32bpp BMP DIB (as embedded in an ICO) into a Texture2D.</summary>
		private static Texture2D ParseDib32(byte[] data, int offset, int width)
		{
			int biSize   = data[offset] | (data[offset+1]<<8) | (data[offset+2]<<16) | (data[offset+3]<<24);
			int biHeight = data[offset+8] | (data[offset+9]<<8) | (data[offset+10]<<16) | (data[offset+11]<<24);
			bool topDown     = biHeight < 0;
			int actualHeight = Math.Abs(biHeight) / 2;
			if (actualHeight <= 0) actualHeight = width;

			int pixelBase = offset + biSize;
			int stride    = width * 4;

			var pixels = new Color32[width * actualHeight];
			for (int row = 0; row < actualHeight; row++) {
				int srcRow  = topDown ? row : (actualHeight - 1 - row);
				int srcBase = pixelBase + srcRow * stride;
				int dstBase = row * width;
				for (int col = 0; col < width; col++) {
					int src = srcBase + col * 4;
					if (src + 3 >= data.Length) { pixels[dstBase + col] = new Color32(0, 0, 0, 0); continue; }
					pixels[dstBase + col] = new Color32(data[src+2], data[src+1], data[src], data[src+3]);
				}
			}

			var tex = new Texture2D(width, actualHeight, TextureFormat.RGBA32, mipChain: true, linear: true);
			tex.SetPixels32(pixels);
			tex.Apply(updateMipmaps: true, makeNoLongerReadable: false);
			return tex;
		}
	}
}
