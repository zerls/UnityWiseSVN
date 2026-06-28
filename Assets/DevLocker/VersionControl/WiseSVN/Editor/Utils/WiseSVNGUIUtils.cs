// MIT License Copyright(c) 2022 Filip Slavov, https://github.com/NibbleByte/UnityWiseSVN

using UnityEditor;
using UnityEngine;
using System;

namespace DevLocker.VersionControl.WiseSVN.Utils
{
	/// <summary>
	/// 跨 WiseSVN Editor 窗口共用的静态 GUI 辅助方法。
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
	}
}
