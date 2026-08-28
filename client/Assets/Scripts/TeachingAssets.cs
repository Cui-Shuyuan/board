using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 教学素材加载层：读 sprites.json（slot→贴图）和 slots.json（slot→坐标/scale/order），
/// 用真实贴图替换程序化占位色块。缺素材的 slot 由调用方用 GameSpriteFactory 占位兜底。
/// 全部静态数据，改素材 = 改 json，零代码。
/// </summary>
[Serializable]
public class TeachingSpriteMap { public List<TeachingSlotSprite> sprites; }
[Serializable]
public class TeachingSlotSprite
{
    public string slot;
    public string png;
}

[Serializable]
public class TeachingSlotMap { public List<TeachingSlotInfo> slots; }
[Serializable]
public class TeachingSlotInfo
{
    public string slot;
    public float[] at;
    public float scale;
    public int order;
}

public static class TeachingAssets
{
    /// <summary>读 sprites.json：slot → 贴图路径。</summary>
    public static Dictionary<string, string> LoadSprites(string game)
    {
        var map = new Dictionary<string, string>();
        var asset = Resources.Load<TextAsset>("teaching/" + game + "/sprites");
        if (asset == null) return map;
        var data = JsonUtility.FromJson<TeachingSpriteMap>(asset.text);
        if (data?.sprites == null) return map;
        foreach (var s in data.sprites)
            if (!string.IsNullOrEmpty(s.slot) && !string.IsNullOrEmpty(s.png))
                map[s.slot] = s.png;
        return map;
    }

    /// <summary>读 slots.json：slot → 坐标/scale/order。</summary>
    public static Dictionary<string, TeachingSlotInfo> LoadSlots(string game)
    {
        var map = new Dictionary<string, TeachingSlotInfo>();
        var asset = Resources.Load<TextAsset>("teaching/" + game + "/slots");
        if (asset == null) return map;
        var data = JsonUtility.FromJson<TeachingSlotMap>(asset.text);
        if (data?.slots == null) return map;
        foreach (var s in data.slots)
            if (!string.IsNullOrEmpty(s.slot))
                map[s.slot] = s;
        return map;
    }

    /// <summary>按 Resources 路径加载贴图（路径不含扩展名），失败返回 null。</summary>
    public static Sprite LoadPng(string pngPath)
    {
        if (string.IsNullOrEmpty(pngPath)) return null;
        var tex = Resources.Load<Texture2D>(pngPath);
        if (tex == null) return null;
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
    }
}
