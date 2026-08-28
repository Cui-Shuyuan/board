using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 讲规模块最小骨架（v0）：
/// 运行时搭建 50° 俯角固定机位 + 棋盘 + 三颗宝石圆片 + 字幕，
/// 播放第一段演示动画「拿取三颗不同颜色的宝石」。
/// 空格 / R 键重播。
/// 本文件是观感验证脚手架，后续由 tutorial.json 数据驱动的播放器取代。
/// </summary>
public static class TutorialBootstrap
{
    // 已由 TeachingPlayer 取代：注释掉自动引导，避免按 Play 时抢建旧的"拿宝石"演示。
    // 想恢复旧演示时，把下面这行取消注释（恢复 [RuntimeInitializeOnLoadMethod]）即可。
    // [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (UnityEngine.Object.FindFirstObjectByType<TutorialPlayer>() == null)
        {
            var go = new GameObject("TutorialPlayer");
            go.AddComponent<TutorialPlayer>();
        }
    }
}

public class TutorialPlayer : MonoBehaviour
{
    const float Ppu = 100f;

    // 与 scripts/generate_placeholder_sprites.py 的槽位坐标保持一致（世界单位）
    static readonly Vector3[] MarketSlots =
    {
        new Vector3(2.40f, 0.02f, -1.60f),
        new Vector3(2.40f, 0.02f,  0.10f),
        new Vector3(2.40f, 0.02f,  1.80f),
    };
    static readonly Vector3[] PlayerSlots =
    {
        new Vector3(-3.30f, 0.02f,  1.60f),
        new Vector3(-3.30f, 0.02f,  0.60f),
        new Vector3(-3.30f, 0.02f, -0.40f),
    };

    static readonly string[] DiscNames = { "disc_blue", "disc_red", "disc_green" };

    readonly Dictionary<string, Transform> discs = new Dictionary<string, Transform>();
    Text subtitle;

    void Start()
    {
        SetupCamera();
        SetupBoard();
        SetupDiscs();
        SetupUI();
        StartCoroutine(PlaySequence());
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.R))
        {
            StopAllCoroutines();
            ResetDiscs();
            StartCoroutine(PlaySequence());
        }
    }

    // ---------- 场景搭建 ----------

    void SetupCamera()
    {
        var cam = Camera.main;
        cam.orthographic = true;
        cam.orthographicSize = 3.6f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.13f, 0.15f, 0.19f);
        cam.transform.SetPositionAndRotation(
            new Vector3(0f, 7.66f, -6.43f),
            Quaternion.Euler(50f, 0f, 0f));
    }

    Sprite LoadSprite(string fileName)
    {
        // 素材以 .png.bytes 导入为 TextAsset（Unity 将 "xxx.png.bytes" 命名为 "xxx.png"），
        // 运行时构造 Sprite，绕开纹理导入设置
        var asset = Resources.Load<TextAsset>("Sprites/" + fileName);
        if (asset == null)
        {
            Debug.LogWarning("[Tutorial] 缺少素材: " + fileName);
            return null;
        }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(tex, asset.bytes))
        {
            Destroy(tex);
            Debug.LogWarning("[Tutorial] 解码失败: " + fileName);
            return null;
        }
        var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
            new Vector2(0.5f, 0.5f), Ppu);
        sprite.name = fileName;
        return sprite;
    }

    /// <summary>创建平躺在棋盘平面上的 sprite（绕 X 轴转 90°），y=0.02 避免与棋盘穿插。</summary>
    Transform CreateFlat(string name, Sprite sprite, Vector3 pos, float scale, int order)
    {
        var go = new GameObject(name);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        go.transform.SetPositionAndRotation(pos, Quaternion.Euler(90f, 0f, 0f));
        go.transform.localScale = Vector3.one * scale;
        return go.transform;
    }

    void SetupBoard()
    {
        CreateFlat("Board", LoadSprite("board.png"), new Vector3(0f, 0.01f, 0f), 1f, 0);
    }

    void SetupDiscs()
    {
        for (int i = 0; i < DiscNames.Length; i++)
        {
            var disc = CreateFlat(DiscNames[i], LoadSprite(DiscNames[i]), MarketSlots[i], 0.32f, 30);
            discs[DiscNames[i]] = disc;

            // 接触阴影：作为圆片子物体跟随移动。
            // 世界偏移 (0.08, 0, -0.22) 换算到父坐标系（绕 X 转 90°）为 (0.08, -0.22, 0)
            var shadow = CreateFlat("shadow_" + DiscNames[i], LoadSprite("shadow.png"),
                Vector3.zero, 0.6f, 29);
            shadow.SetParent(disc, false);
            shadow.localPosition = new Vector3(0.08f, -0.22f, 0f);
        }
    }

    // ---------- 字幕 ----------

    void SetupUI()
    {
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        var textGo = new GameObject("Subtitle", typeof(Text));
        textGo.transform.SetParent(canvasGo.transform, false);
        subtitle = textGo.GetComponent<Text>();
        subtitle.font = LoadChineseFont();
        subtitle.fontSize = 42;
        subtitle.alignment = TextAnchor.MiddleCenter;
        subtitle.color = new Color(1f, 1f, 1f, 0.95f);

        var outline = textGo.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(2f, -2f);

        var rt = subtitle.rectTransform;
        rt.anchorMin = new Vector2(0.08f, 0.05f);
        rt.anchorMax = new Vector2(0.92f, 0.16f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    Font LoadChineseFont()
    {
        try
        {
            var font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 42);
            if (font != null) return font;
        }
        catch
        {
            // 找不到雅黑时退回内置字体（无中文，仅兜底）
        }
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    // ---------- 演示动画 ----------

    IEnumerator PlaySequence()
    {
        Say(string.Empty);
        yield return new WaitForSeconds(0.8f);
        Say("这一回合，你可以拿取三颗颜色不同的宝石。");
        yield return new WaitForSeconds(2.6f);

        Say("先从市场拿一颗蓝色宝石。");
        yield return MoveDisc("disc_blue", MarketSlots[0], PlayerSlots[0], 1.1f);

        Say("再拿一颗红色宝石。");
        yield return MoveDisc("disc_red", MarketSlots[1], PlayerSlots[1], 1.1f);

        Say("最后拿一颗绿色宝石。");
        yield return MoveDisc("disc_green", MarketSlots[2], PlayerSlots[2], 1.1f);

        yield return new WaitForSeconds(0.6f);
        Say("像这样，三颗不同颜色的宝石就归你了。");
        yield return new WaitForSeconds(2.8f);
        Say("按空格或 R 键重播。");
    }

    IEnumerator MoveDisc(string name, Vector3 from, Vector3 to, float duration)
    {
        var t = discs[name];
        float start = Time.time;
        while (Time.time - start < duration)
        {
            float k = Mathf.Clamp01((Time.time - start) / duration);
            t.position = Vector3.LerpUnclamped(from, to, EaseOutCubic(k));
            yield return null;
        }
        t.position = to;
    }

    static float EaseOutCubic(float t)
    {
        return 1f - Mathf.Pow(1f - t, 3f);
    }

    void Say(string text)
    {
        if (subtitle != null) subtitle.text = text;
    }

    void ResetDiscs()
    {
        for (int i = 0; i < DiscNames.Length; i++)
            discs[DiscNames[i]].position = MarketSlots[i];
    }
}
