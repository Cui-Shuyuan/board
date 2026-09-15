// BoardGameTutorial
// 占位调色板：数据里只写色板名，真实贴图到位后这里可以整体换成图集。
using System.Collections.Generic;
using UnityEngine;

namespace BoardGameTutorial
{
    public static class Palette
    {
        private static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>
        {
            { "gem_diamond",  new Color(0.85f, 0.90f, 0.93f) },
            { "gem_sapphire", new Color(0.26f, 0.52f, 0.96f) },
            { "gem_emerald",  new Color(0.20f, 0.66f, 0.33f) },
            { "gem_ruby",     new Color(0.82f, 0.22f, 0.26f) },
            { "gem_onyx",     new Color(0.22f, 0.23f, 0.27f) },
            { "gem_gold",     new Color(0.95f, 0.79f, 0.22f) },
            { "panel_supply", new Color(0.26f, 0.36f, 0.52f) },
            { "panel_player", new Color(0.24f, 0.42f, 0.31f) },
            { "panel_card",   new Color(0.45f, 0.38f, 0.29f) },
            { "shadow",       new Color(0f, 0f, 0f) },
            { "white",        Color.white },
        };

        public static Color Resolve(string name)
        {
            if (!string.IsNullOrEmpty(name) && Colors.TryGetValue(name, out var color)) return color;
            return Color.white;
        }

        public static bool TryResolveRgb(string hex, out Color color)
        {
            if (ColorUtility.TryParseHtmlString(hex, out color))
            {
                color.a = 1f;
                return true;
            }
            color = Color.white;
            return false;
        }
    }
}
