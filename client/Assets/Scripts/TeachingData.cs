using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>teaching.json 的数据模型。字段与 docs/teaching-json-format.md 对应。</summary>

[Serializable]
public class TeachingData
{
    public TeachingMeta meta;
    public List<TeachingChunk> chunks;
}

[Serializable]
public class TeachingMeta
{
    public string game;
    public TeachingTitle title;
    public string version;
    public TeachingCamera camera;
}

[Serializable]
public class TeachingTitle
{
    public string zh;
    public string en;
}

[Serializable]
public class TeachingCamera
{
    public float orthographicSize;
    public float angle;
    public float[] position;
}

[Serializable]
public class TeachingChunk
{
    public string id;
    public TeachingTitle title;
    public TeachingTitle text;
    public List<TeachingShot> shots;
}

[Serializable]
public class TeachingShot
{
    public string type;          // move/rotate/scale/flip/appear/group/tell
    public string target;        // GameObject 名 / 相对路径
    public float[] from;         // move 起始（可选）
    public float[] to;           // move/scale 目标（可选）
    public float[] angle;        // rotate/flip 欧拉角（可选）
    public float duration;       // 秒
    public string easing;        // 缓动名
    public float delay;          // 前置等待
    public float hold;           // 完成后停留
    public List<TeachingShot> items; // group 用：并行子 shot

    // 便捷转换（约定数组长度 3）
    public Vector3? From => from != null && from.Length >= 3 ? new Vector3(from[0], from[1], from[2]) : (Vector3?)null;
    public Vector3? To => to != null && to.Length >= 3 ? new Vector3(to[0], to[1], to[2]) : (Vector3?)null;
    public Vector3? Angle => angle != null && angle.Length >= 3 ? new Vector3(angle[0], angle[1], angle[2]) : (Vector3?)null;
}
