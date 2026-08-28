using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>自动引导：按 Play 时若无教学播放器则创建一个并自动播放 Splendor 设置动画。</summary>
public static class TeachingBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        if (Object.FindFirstObjectByType<TeachingPlayer>() == null)
        {
            var go = new GameObject("TeachingHost");
            go.AddComponent<TeachingPlayer>();
        }
    }
}

/// <summary>
/// 数据驱动教学播放器：读 teaching_splendor_setup.json，
/// 按 chunk 逐块播放字幕 + 补间动画。
/// 支持：空格=重播当前块，N=下一块，P=暂停/继续（打断/重播的基础）。
/// 搭景在 Start 中进行（50° 俯角，2.5D sprite）。
/// 出图：BatchRender 调用 UpdateForRender(time) 驱动到指定进度再渲帧。
/// </summary>
public class TeachingPlayer : MonoBehaviour
{
    const float PPU = 100f;
    public const string ResourceName = "teaching_splendor_setup";

    // ------- 场景槽位坐标 -------
    // 板图中心 (0,0)。世界单位。
    static readonly Vector3 BoardCenter = new Vector3(0f, 0.01f, 0f);

    // 贵族区（顶部, 3 个），y 高一点
    static readonly float NobleY = 2.55f;
    // 市场区（中部）: 3 行(等级) x 4 列，行距 0.62（卡世界高约0.5），列距 0.78
    // 行 y(从下往上)：等级1 0.55、等级2 1.17、等级3 1.79
    // 牌堆（左, 独立一列）
    // 宝石供应（下）: 6 色一排，y = -2.5（避开市场区）

    TeachingData data;
    int chunkIndex = 0;
    Coroutine playRoutine;
    bool paused = false;

    readonly Dictionary<string, Transform> objectMap = new Dictionary<string, Transform>();
    readonly Dictionary<string, Vector3> baseScales = new Dictionary<string, Vector3>();
    Text subtitle;
    Text statusText;

    void Start()
    {
        InitAndGo(0);
    }

    void Update()
    {
        // 出图模式(batch)下不响应键盘
        if (Input.GetKeyDown(KeyCode.Space)) RestartChunk();
        else if (Input.GetKeyDown(KeyCode.N)) NextChunk();
        else if (Input.GetKeyDown(KeyCode.P)) TogglePause();
        else if (Input.GetKeyDown(KeyCode.LeftBracket)) PrevChunk();
    }

    /// <summary>外部（含 batch 出图）创建对象后调用；搭景 + 加载 + 从第 index 块开始。</summary>
    public void InitAndGo(int index)
    {
        SetupCamera();
        BuildScene();
        SetupUI();
        Load();
        PlayChunk(index);
    }

    /// <summary>清掉场景里所有教学 sprite 子对象，供重复出图前重置。</summary>
    void ClearScene()
    {
        objectMap.Clear();
        baseScales.Clear();
        if (transform != null)
        {
            var kids = new List<Transform>();
            for (int i = 0; i < transform.childCount; i++) kids.Add(transform.GetChild(i));
            foreach (var k in kids) DestroyImmediate(k.gameObject);
        }
    }

    /// <summary>同步把场景置为该 chunk 播完后的状态（供 batch 出图，不依赖协程）。
    /// 第 index 块的终态 = 前面所有 chunk 的终态累积 + 本块终态（数据驱动语义：前块播完的已就位）。</summary>
    public void ApplyChunkFinalStateSynced(int index)
    {
        SetupCamera();
        BuildScene();
        SetupUI();
        Load();
        if (data == null || data.chunks.Count == 0) return;
        chunkIndex = Mathf.Clamp(index, 0, data.chunks.Count - 1);
        // 累积到该 chunk：依次应用 0..chunkIndex 每块的终态（后块可覆盖前块）
        for (int i = 0; i <= chunkIndex; i++)
        {
            var chunk = data.chunks[i];
            if (i == chunkIndex) Say(chunk.text?.zh ?? string.Empty);
            foreach (var shot in chunk.shots)
                ApplyShotFinal(shot);
        }
    }

    void ApplyShotFinal(TeachingShot shot)
    {
        switch (shot.type)
        {
            case "group":
                if (shot.items != null) foreach (var s in shot.items) ApplyShotFinal(s);
                break;
            case "appear":
                Show(shot.target);
                break;
            case "move":
                if (!string.IsNullOrEmpty(shot.target) && objectMap.TryGetValue(shot.target, out var mt))
                    if (shot.To.HasValue) mt.localPosition = shot.To.Value;
                break;
            case "rotate":
            case "flip":
                if (!string.IsNullOrEmpty(shot.target) && objectMap.TryGetValue(shot.target, out var rt))
                    if (shot.Angle.HasValue) rt.localRotation = Quaternion.Euler(shot.Angle.Value);
                break;
            case "scale":
                if (!string.IsNullOrEmpty(shot.target) && objectMap.TryGetValue(shot.target, out var st))
                    if (shot.To.HasValue) st.localScale = shot.To.Value;
                break;
        }
    }

    /// <summary>
    /// 同步把场景推进到 [0..index 的块累积终态 + 第 index 块的进度 t]（确定性，不依赖协程）。
    /// t∈[0,1] 表示当前块动画的全局进度。用于 batch 出图评估动画中间态。
    /// </summary>
    public void ApplyChunkProgressSynced(int index, float t)
    {
        SetupCamera();
        BuildScene();
        SetupUI();
        Load();
        if (data == null || data.chunks.Count == 0) return;
        chunkIndex = Mathf.Clamp(index, 0, data.chunks.Count - 1);
        // 前面的块：全部到终态
        for (int i = 0; i < chunkIndex; i++)
            foreach (var shot in data.chunks[i].shots)
                ApplyShotFinal(shot);
        // 当前块：推进到局部进度 t
        Say(data.chunks[chunkIndex].text?.zh ?? string.Empty);
        foreach (var shot in data.chunks[chunkIndex].shots)
        {
            float local = AdvancedLocalProgress(shot, t);
            ApplyShotProgress(shot, local);
        }
    }

    /// <summary>求某个 shot 在当前块总进度 t 下的局部进度系数（受 delay/hold/duration 影响）。</summary>
    float AdvancedLocalProgress(TeachingShot shot, float t)
    {
        float dur = Mathf.Max(0.001f, shot.duration);
        float start = shot.delay;
        float end = start + dur + shot.hold;
        if (t <= start) return 0f;
        if (t >= end) return 1f;
        return Mathf.InverseLerp(start, start + dur, t);
    }

    void ApplyShotProgress(TeachingShot shot, float k)
    {
        switch (shot.type)
        {
            case "group":
                if (shot.items != null) foreach (var s in shot.items) ApplyShotProgress(s, k);
                break;
            case "tell":
                break;
            case "appear":
                ApplyAppearProgress(shot.target, k);
                break;
            case "move":
                if (!string.IsNullOrEmpty(shot.target) && objectMap.TryGetValue(shot.target, out var mt))
                {
                    Vector3 f = shot.From.HasValue ? shot.From.Value : mt.localPosition;
                    Vector3 to = shot.To.HasValue ? shot.To.Value : mt.localPosition;
                    mt.localPosition = Vector3.LerpUnclamped(f, to, TweenLibrary.Ease(k, shot.easing));
                }
                break;
            case "rotate":
            case "flip":
                if (!string.IsNullOrEmpty(shot.target) && objectMap.TryGetValue(shot.target, out var rt))
                {
                    Quaternion f = rt.localRotation;
                    Quaternion to = shot.Angle.HasValue ? Quaternion.Euler(shot.Angle.Value) : Quaternion.Euler(0, 180, 0);
                    rt.localRotation = Quaternion.SlerpUnclamped(f, to, TweenLibrary.Ease(k, shot.easing));
                }
                break;
            case "scale":
                if (!string.IsNullOrEmpty(shot.target) && objectMap.TryGetValue(shot.target, out var st))
                {
                    Vector3 f = baseScales.TryGetValue(shot.target, out var bs) ? bs * 0.6f : st.localScale;
                    Vector3 to = shot.To.HasValue ? shot.To.Value : st.localScale;
                    st.localScale = Vector3.LerpUnclamped(f, to, TweenLibrary.Ease(k, shot.easing));
                }
                break;
        }
    }

    void ApplyAppearProgress(string name, float k)
    {
        if (string.IsNullOrEmpty(name) || !objectMap.TryGetValue(name, out var t)) return;
        var sr = t.GetComponent<SpriteRenderer>();
        if (sr == null) return;
        float a = TweenLibrary.Ease(k, "easeOutCubic");
        sr.color = new Color(1f, 1f, 1f, a);
        t.localScale = baseScales.TryGetValue(name, out var s) ? s * Mathf.LerpUnclamped(0.4f, 1f, a) : t.localScale;
    }

    void Show(string name)
    {
        if (string.IsNullOrEmpty(name) || !objectMap.TryGetValue(name, out var t)) return;
        var sr = t.GetComponent<SpriteRenderer>();
        if (sr != null) sr.color = new Color(1f, 1f, 1f, 1f);
        t.localScale = baseScales.TryGetValue(name, out var s) ? s : Vector3.one;
    }

    // ---------------- 搭景 ----------------

    void SetupCamera()
    {
        var cam = Camera.main;
        cam.orthographic = true;
        cam.orthographicSize = 3.6f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.11f, 0.12f, 0.16f);
        cam.transform.SetPositionAndRotation(
            new Vector3(0f, 7.66f, -6.43f),
            Quaternion.Euler(50f, 0f, 0f));
    }

    /// <summary>创建一把平躺(绕X转90°)的 sprite 对象，挂在宿主 transform 下（便于一次性清理），注册到 objectMap。</summary>
    Transform AddSprite(string name, Sprite sprite, Vector3 worldPos, float scale, int order, Vector3? euler = null)
    {
        var go = new GameObject(name);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        go.transform.SetParent(transform, false);
        // 先设位置再挂父级会改变坐标系，这里挂在父下后设世界位置
        go.transform.SetPositionAndRotation(worldPos, Quaternion.Euler(euler ?? new Vector3(90f, 0f, 0f)));
        go.transform.localScale = Vector3.one * scale;
        objectMap[name] = go.transform;
        baseScales[name] = Vector3.one * scale;
        return go.transform;
    }

    void AddChildSprite(string parent, string name, Sprite sprite, Vector3 localPos, float scale, int order)
    {
        var go = new GameObject(name);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        go.transform.SetParent(objectMap[parent], false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = Vector3.one * scale;
        objectMap[name] = go.transform;
    }

    void BuildScene()
    {
        ClearScene();
        // 版图
        AddSprite("board", GameSpriteFactory.Board(), BoardCenter, 1f, 0);

        // 贵族区（顶部，3 个）——初始隐藏，待 appear
        for (int i = 0; i < 3; i++)
        {
            float x = -2.1f + i * 2.1f;
            AddSprite("noble_" + i, GameSpriteFactory.Noble(), new Vector3(x, 0.02f, NobleY), 0.5f, 20);
            SetHidden("noble_" + i);
        }

        // 市场区：3 等级 x 4 列（横向小卡，行距 0.62 / 列距 0.80）——初始隐藏，待 appear
        for (int lv = 0; lv < 3; lv++)
        {
            float yRow = 0.55f + lv * 0.62f;
            for (int c = 0; c < 4; c++)
            {
                float x = -1.20f + c * 0.80f;
                AddSprite("market_" + (lv + 1) + "_" + (c + 1), GameSpriteFactory.Card(lv + 1),
                    new Vector3(x, 0.03f, yRow), 0.42f, 10);
                SetHidden("market_" + (lv + 1) + "_" + (c + 1));
            }
        }

        // 牌堆（左，3 个等级，竖着排，独立一列）——初始隐藏
        for (int lv = 0; lv < 3; lv++)
        {
            float z = 0.55f + lv * 0.62f;
            AddSprite("deck_" + (lv + 1), GameSpriteFactory.Card(lv + 1), new Vector3(-3.6f, 0.04f, z), 0.42f, 8);
            SetHidden("deck_" + (lv + 1));
        }

        // 宝石供应（图中一排可先显示：钻石/蓝/绿/红/黑/黄金）
        string[] gemIds = { "diamond", "sapphire", "emerald", "ruby", "onyx", "gold" };
        Color[] gemCols =
        {
            new Color(0.70f, 0.86f, 1f),
            new Color(0.26f, 0.52f, 0.96f),
            new Color(0.20f, 0.66f, 0.33f),
            new Color(0.92f, 0.26f, 0.21f),
            new Color(0.24f, 0.20f, 0.36f),
            new Color(0.98f, 0.74f, 0.02f),
        };
        float gemY = -2.55f;
        for (int i = 0; i < 6; i++)
        {
            float x = -2.8f + i * 1.12f;
            AddSprite("gem_" + gemIds[i], GameSpriteFactory.Gem(gemCols[i]), new Vector3(x, 0.02f, gemY), 0.34f, 12);
            // 接触阴影
            AddChildSprite("gem_" + gemIds[i], "shadow_" + gemIds[i], GameSpriteFactory.Shadow(), Vector3.zero, 0.5f, 11);
            SetHidden("gem_" + gemIds[i]);
        }

        // 起始玩家标记（藏在底下，播放时移到玩家面前）——初始隐藏
        AddSprite("start_marker", GameSpriteFactory.StartMarker(), new Vector3(3.2f, 0.02f, -3.2f), 0.5f, 30);
        SetHidden("start_marker");
    }

    /// <summary>把对象设为初始隐藏（待 appear 时显示）。只隐藏透明度，保留原始 scale 作动画基准。</summary>
    void SetHidden(string name)
    {
        if (!objectMap.TryGetValue(name, out var t)) return;
        var sr = t.GetComponent<SpriteRenderer>();
        if (sr != null) sr.color = new Color(1f, 1f, 1f, 0f);
    }

    // ---------------- UI ----------------

    void SetupUI()
    {
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasGo.transform.SetParent(transform, false);
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
        subtitle.fontSize = 40;
        subtitle.alignment = TextAnchor.MiddleCenter;
        subtitle.color = new Color(1f, 1f, 1f, 0.95f);
        var outline = textGo.AddComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
        outline.effectDistance = new Vector2(2f, -2f);
        var rt = subtitle.rectTransform;
        rt.anchorMin = new Vector2(0.08f, 0.05f);
        rt.anchorMax = new Vector2(0.92f, 0.18f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

        // 状态（chunk 序号）小字，左上
        var statusGo = new GameObject("Status", typeof(Text));
        statusGo.transform.SetParent(canvasGo.transform, false);
        statusText = statusGo.GetComponent<Text>();
        statusText.font = subtitle.font;
        statusText.fontSize = 24;
        statusText.alignment = TextAnchor.UpperLeft;
        statusText.color = new Color(0.8f, 0.8f, 0.8f, 0.9f);
        var srt = statusText.rectTransform;
        srt.anchorMin = new Vector2(0.03f, 0.9f);
        srt.anchorMax = new Vector2(0.6f, 1f);
        srt.offsetMin = Vector2.zero; srt.offsetMax = Vector2.zero;
    }

    Font LoadChineseFont()
    {
        try
        {
            var font = Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 40);
            if (font != null) return font;
        }
        catch { }
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    // ---------------- 加载 & 播放 ----------------

    void Load()
    {
        var asset = Resources.Load<TextAsset>(ResourceName);
        if (asset == null)
        {
            Debug.LogError("[Teaching] 找不到数据: " + ResourceName);
            data = new TeachingData();
            return;
        }
        data = JsonUtility.FromJson<TeachingData>(asset.text);
        if (data == null || data.chunks == null || data.chunks.Count == 0)
        {
            Debug.LogError("[Teaching] 数据为空或格式错误");
            data = new TeachingData();
            return;
        }
    }

    void PlayChunk(int index)
    {
        if (data == null || data.chunks.Count == 0) return;
        chunkIndex = Mathf.Clamp(index, 0, data.chunks.Count - 1);
        if (playRoutine != null) StopCoroutine(playRoutine);
        playRoutine = StartCoroutine(PlayChunkRoutine(data.chunks[chunkIndex]));
    }

    IEnumerator PlayChunkRoutine(TeachingChunk chunk)
    {
        Say(chunk.text.zh);
        UpdateStatus();
        foreach (var shot in chunk.shots)
        {
            yield return TweenLibrary.Run(transform, shot);
            if (paused) { while (paused) yield return null; }
        }
        // 播完当前块：若下一块存在且未暂停，停顿后自动连续播放下一块
        if (!paused && chunkIndex + 1 < data.chunks.Count)
        {
            Say(string.Empty);
            yield return new WaitForSeconds(0.9f);
            PlayChunk(chunkIndex + 1);
        }
        else
        {
            Say(string.Empty);
        }
    }

    void RestartChunk() { PlayChunk(chunkIndex); }
    void NextChunk() { PlayChunk(chunkIndex + 1); }
    void PrevChunk() { PlayChunk(chunkIndex - 1); }
    void TogglePause()
    {
        paused = !paused;
        UpdateStatus();
    }

    void Say(string text) { if (subtitle != null) subtitle.text = text ?? string.Empty; }

    void UpdateStatus()
    {
        string state = paused ? "[暂停] " : "";
        if (data != null && data.chunks.Count > 0)
            statusText.text = state + "第 " + (chunkIndex + 1) + "/" + data.chunks.Count + " 块  (" + data.chunks[chunkIndex].id + ")";
    }

    /// <summary>供 BatchRender 在 batchmode 下按块播到指定进度后再渲帧。</summary>
    public void UpdateForRender(int index, float progress)
    {
        PlayChunk(index);
        // 简单实现：如果 progress>0 则推进内部时间（当前只按块播；复杂进度控后续再加）
    }
}
