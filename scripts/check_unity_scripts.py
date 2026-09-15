#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Compile-check the Unity client scripts without Unity.

Why this exists
---------------
The tutorial animation code lives in `client/Assets/Scripts/` and is normally only
compiled by the Unity editor on Windows.  A syntax or type error therefore costs a
round-trip through the user.  This script builds a throwaway .NET library that
contains the client scripts plus hand-written stubs of exactly the Unity APIs they
touch, so most mistakes are caught here instead.

It is a *compile* check, not a behaviour check: it cannot tell you whether the
animation looks right, only that the C# is valid and self-consistent.

Usage
-----
    python scripts/check_unity_scripts.py
    python scripts/check_unity_scripts.py --keep     # keep the temp project for inspection
    python scripts/check_unity_scripts.py --json

Exit codes: 0 = compiled, 1 = compile errors, 2 = toolchain missing.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SCRIPTS_DIR = ROOT / "client" / "Assets" / "Scripts"

# Files that depend on the Unity editor / Input System package in ways the stubs
# do not model.  They are the legacy prototype players and are not part of the
# cue-animation path.
EXCLUDED = {
    # Legacy prototype players.  They are dead code for the cue-animation path,
    # depend on heavy Unity UI APIs, and would only add stub surface.
    "TutorialPlayer.cs",
    "TeachingPlayer.cs",
    "TweenLibrary.cs",
}

DOTNET_CANDIDATES = [
    Path.home() / ".dotnet" / "dotnet",
    Path("/usr/share/dotnet/dotnet"),
    Path("/usr/lib/dotnet/dotnet"),
]

UNITY_STUBS = r"""
// Auto-generated compile-only stubs of the Unity APIs used by the client scripts.
// They are intentionally minimal: just enough surface to type-check.
using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 one => new Vector2(1, 1);
        public static float Distance(Vector2 a, Vector2 b) => 0f;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a;
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float s) => new Vector2(a.x * s, a.y * s);
        public float magnitude => 0f;
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public Vector3(float x, float y) { this.x = x; this.y = y; this.z = 0f; }
        public static Vector3 zero => new Vector3(0, 0, 0);
        public static Vector3 one => new Vector3(1, 1, 1);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
        public static Vector3 operator *(float s, Vector3 a) => a * s;
        public static Vector3 operator /(Vector3 a, float s) => new Vector3(a.x / s, a.y / s, a.z / s);
        public static Vector3 LerpUnclamped(Vector3 a, Vector3 b, float t) => a;
        public float magnitude => 0f;
        public float sqrMagnitude => 0f;
        public Vector3 normalized => this;
        public void Normalize() { }
        public static Vector3 Cross(Vector3 a, Vector3 b) => a;
        public static float Dot(Vector3 a, Vector3 b) => 0f;
        public static bool operator ==(Vector3 a, Vector3 b) => false;
        public static bool operator !=(Vector3 a, Vector3 b) => false;
        public override bool Equals(object o) => false;
        public override int GetHashCode() => 0;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a;
        public static float Distance(Vector3 a, Vector3 b) => 0f;
    }

    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b) { this.r = r; this.g = g; this.b = b; this.a = 1f; }
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public static Color white => new Color(1, 1, 1);
        public static Color black => new Color(0, 0, 0);
        public static Color clear => new Color(0, 0, 0, 0);
        public static Color magenta => new Color(1, 0, 1);
        public static Color cyan => new Color(0, 1, 1);
        public static Color transparent => new Color(0, 0, 0, 0);
        public bool IsWhite() => true;
        public static Color red => new Color(1, 0, 0);
        public static Color green => new Color(0, 1, 0);
        public static Color blue => new Color(0, 0, 1);
        public static Color yellow => new Color(1, 1, 0);
        public static Color gray => new Color(.5f, .5f, .5f);
        public static Color grey => new Color(.5f, .5f, .5f);
        public static Color Lerp(Color a, Color b, float t) => a;
        public static Color LerpUnclamped(Color a, Color b, float t) => a;
        public static Color operator *(Color a, float s) => a;
        public static bool operator ==(Color a, Color b) => false;
        public static bool operator !=(Color a, Color b) => false;
        public override bool Equals(object o) => false;
        public override int GetHashCode() => 0;
        public static Color operator *(Color a, Color b) => a;
    }

    public static class ColorUtility
    {
        public static bool TryParseHtmlString(string html, out Color color) { color = Color.white; return true; }
        public static string ToHtmlStringRGB(Color color) => "FFFFFF";
    }

    public struct Quaternion
    {
        public static Quaternion identity => new Quaternion();
        public static Quaternion Euler(float x, float y, float z) => new Quaternion();
        public static Quaternion Euler(Vector3 v) => new Quaternion();
        public static Quaternion SlerpUnclamped(Quaternion a, Quaternion b, float t) => a;
        public static Quaternion operator *(Quaternion a, Quaternion b) => a;
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float w, float h) { this.x = x; this.y = y; width = w; height = h; }
    }

    public static class Mathf
    {
        public const float PI = 3.14159265f;
        public const float Deg2Rad = 0.0174532924f;
        public const float Rad2Deg = 57.29578f;
        public static float Clamp(float v, float a, float b) => v;
        public static int Clamp(int v, int a, int b) => v;
        public static float Clamp01(float v) => v;
        public static float Min(float a, float b) => a;
        public static int Min(int a, int b) => a;
        public static float Max(float a, float b) => a;
        public static int Max(int a, int b) => a;
        public static float Abs(float v) => v;
        public static float Sqrt(float v) => v;
        public static float Sin(float v) => v;
        public static float Cos(float v) => v;
        public static float Tan(float v) => v;
        public static float Atan2(float y, float x) => 0f;
        public static float Lerp(float a, float b, float t) => a;
        public static float LerpUnclamped(float a, float b, float t) => a;
        public static float InverseLerp(float a, float b, float v) => 0f;
        public static float MoveTowards(float a, float b, float d) => a;
        public static float PerlinNoise(float x, float y) => 0f;
        public static float Pow(float a, float b) => a;
        public static float Exp(float a) => a;
        public static float Log(float a) => a;
        public static float Log10(float a) => a;
        public static float Floor(float a) => a;
        public static float Ceil(float a) => a;
        public static float Round(float a) => a;
        public static int FloorToInt(float a) => 0;
        public static int CeilToInt(float a) => 0;
        public static int RoundToInt(float a) => 0;
        public static float Sign(float a) => a;
        public static float SmoothStep(float a, float b, float t) => a;
        public static float PingPong(float t, float len) => t;
        public static bool Approximately(float a, float b) => false;
        public static float Repeat(float t, float len) => t;
        public static float DeltaAngle(float a, float b) => 0f;
    }

    public class Object
    {
        public string name;
        public static T FindFirstObjectByType<T>() where T : Object => null;
        public static T[] FindObjectsByType<T>(int sortMode) where T : Object => new T[0];
        public static T[] FindObjectsByType<T>(FindObjectsSortMode sortMode) where T : Object => new T[0];
        public static T FindObjectOfType<T>() where T : Object => null;
        public static T[] FindObjectsOfType<T>() where T : Object => new T[0];
        public static Object Instantiate(Object original) => original;
        public static void Destroy(Object o) { }
        public static void DestroyImmediate(Object o) { }
        public static void DontDestroyOnLoad(Object o) { }
        public static implicit operator bool(Object o) => false;
    }

    public class Component : Object
    {
        public Transform transform;
        public GameObject gameObject;
        public T GetComponent<T>() => default(T);
        public T AddComponent<T>() where T : Component => null;
        public T GetComponentInChildren<T>() => default(T);
    }

    public class Behaviour : Component { public bool enabled; }

    public class MonoBehaviour : Behaviour
    {
        public Coroutine StartCoroutine(IEnumerator routine) => null;
        public void StopCoroutine(Coroutine c) { }
        public void StopCoroutine(IEnumerator c) { }
        public void StopAllCoroutines() { }
        public void Invoke(string method, float time) { }
        public void CancelInvoke() { }
    }

    public class Coroutine { }

    public class GameObject : Object
    {
        public GameObject() { }
        public GameObject(string name) { }
        public GameObject(string name, params Type[] components) { }
        public string tag;
        public bool activeSelf;
        public int layer;
        public Transform transform;
        public T AddComponent<T>() where T : Component => null;
        public T GetComponent<T>() => default(T);
        public T[] GetComponentsInChildren<T>() => new T[0];
        public T[] GetComponentsInChildren<T>(bool includeInactive) => new T[0];
        public void SetActive(bool v) { }
        public static GameObject Find(string name) => null;
    }

    public class Transform : Component
    {
        public Vector3 position;
        public Vector3 localPosition;
        public Vector3 localScale;
        public Vector3 localEulerAngles;
        public Quaternion rotation;
        public Quaternion localRotation;
        public Transform parent;
        public int childCount;
        public Transform GetChild(int i) => null;
        public void SetParent(Transform p) { }
        public void SetParent(Transform p, bool worldPositionStays) { }
        public void SetPositionAndRotation(Vector3 p, Quaternion r) { }
        public void LookAt(Vector3 v) { }
        public void Translate(Vector3 v) { }
        public Transform Find(string n) => null;
        public Vector3 TransformPoint(Vector3 v) => v;
        public Vector3 InverseTransformPoint(Vector3 v) => v;
    }

    public struct Bounds
    {
        public Vector3 center;
        public Vector3 size;
        public Vector3 extents;
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; extents = size; }
    }

    public class Sprite : Object
    {
        public Rect rect;
        public Bounds bounds;
        public Vector2 pivot;
        public float pixelsPerUnit;
        public Texture2D texture;
        public static Sprite Create(Texture2D tex, Rect rect, Vector2 pivot) => null;
        public static Sprite Create(Texture2D tex, Rect rect, Vector2 pivot, float ppu) => null;
    }

    public class RenderTexture : Texture
    {
        public static RenderTexture active;
        public static RenderTexture GetTemporary(int width, int height, int depth) => null;
        public static RenderTexture GetTemporary(int width, int height, int depth, RenderTextureFormat format) => null;
        public static void ReleaseTemporary(RenderTexture rt) { }
    }

    public enum RenderTextureFormat { ARGB32, RGB24, Default }

    public class Texture : Object { public int width, height; }
    public class Texture2D : Texture
    {
        public Texture2D(int w, int h) { }
        public Texture2D(int w, int h, TextureFormat format, bool mipChain) { }
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public bool isReadable => true;
        public TextureFormat format => TextureFormat.RGBA32;
        public int GetInstanceID() => 0;
        public void SetPixel(int x, int y, Color c) { }
        public void SetPixels(Color[] colors) { }
        public Color[] GetPixels() => new Color[0];
        public void ReadPixels(Rect rect, int destX, int destY) { }
        public byte[] EncodeToPNG() => new byte[0];
        public byte[] EncodeToJPG() => new byte[0];
        public void Apply() { }
        public void Apply(bool updateMipmaps) { }
        public void Apply(bool updateMipmaps, bool makeNoLongerReadable) { }
    }

    public enum TextureFormat { RGBA32, ARGB32, RGB24 }
    public enum FilterMode { Point, Bilinear, Trilinear }
    public enum TextureWrapMode { Repeat, Clamp }
    public enum SpriteMeshType { FullRect, Tight }
    public enum CameraClearFlags { Skybox, SolidColor, Depth, Nothing }

    public class Renderer : Component
    {
        public bool enabled;
        public int sortingOrder;
        public string sortingLayerName;
        public Material material;
        public Material sharedMaterial;
        public Material GetMaterial() => null;
        public Material GetSharedMaterial() => null;
    }

    public class SpriteRenderer : Renderer
    {
        public Sprite sprite;
        public Color color;
        public bool flipX;
        public bool flipY;
    }

    public class Material : Object
    {
        public Material(Shader s) { }
        public Color color;
        public Texture mainTexture;
        public Texture2D mainTextureAsTexture2D => null;
        public bool HasProperty(string name) => true;
        public Texture GetTexture(string name) => null;
    }

    public class Shader : Object { public static Shader Find(string name) => null; }

    public class Camera : Behaviour
    {
        public static Camera main => null;
        public RenderTexture targetTexture;
        public float depth;
        public Rect rect;
        public void Render() { }
        public bool orthographic;
        public float orthographicSize;
        public float fieldOfView;
        public float aspect;
        public CameraClearFlags clearFlags;
        public Color backgroundColor;
        public Vector3 WorldToScreenPoint(Vector3 v) => v;
        public Vector3 ScreenToWorldPoint(Vector3 v) => v;
        public Vector3 ScreenToWorldPoint(Vector2 v) => Vector3.zero;
    }

    public class AudioClip : Object { public float length; }
    public class AudioSource : Behaviour
    {
        public AudioClip clip;
        public bool playOnAwake;
        public bool loop;
        public bool isPlaying;
        public bool mute;
        public float volume;
        public float pitch;
        public float time;
        public void Play() { }
        public void Stop() { }
        public void Pause() { }
        public void UnPause() { }
    }

    public class TextAsset : Object { public string text; }

    public static class Resources
    {
        public static T Load<T>(string path) where T : Object => null;
        public static Object Load(string path) => null;
        public static T GetBuiltinResource<T>(string path) where T : Object => null;
        public static T[] LoadAll<T>(string path) where T : Object => new T[0];
    }

    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) { }
        public static void LogException(Exception e) { }
    }

    public static class Application
    {
        public static string dataPath => "";
        public static string persistentDataPath => "";
        public static string streamingAssetsPath => "";
        public static bool isEditor => true;
        public static int targetFrameRate;
    }

    public static class Screen
    {
        public static int width => 1920;
        public static int height => 1080;
    }

    public static class Time
    {
        public static float deltaTime => 0f;
        public static float unscaledDeltaTime => 0f;
        public static float time => 0f;
        public static float fixedDeltaTime => 0f;
        public static float timeScale;
    }

    public static class Random
    {
        public static float value => 0f;
        public static float Range(float a, float b) => a;
        public static int Range(int a, int b) => a;
        public static void InitState(int seed) { }
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class HeaderAttribute : Attribute { public HeaderAttribute(string header) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public class TooltipAttribute : Attribute { public TooltipAttribute(string tooltip) { } }

    [AttributeUsage(AttributeTargets.Field)]
    public class SerializeFieldAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class RuntimeInitializeOnLoadMethodAttribute : Attribute
    {
        public RuntimeInitializeOnLoadMethodAttribute() { }
        public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType type) { }
    }

    public enum RuntimeInitializeLoadType { AfterSceneLoad, BeforeSceneLoad, SubsystemRegistration, AfterAssembliesLoaded, BeforeSplashScreen }

    public class GUIStyle
    {
        public GUIStyle() { }
        public GUIStyle(GUIStyle other) { }
        public int fontSize;
        public bool wordWrap;
        public TextAnchor alignment;
        public GUIStyleState normal = new GUIStyleState();
        public RectOffset margin = new RectOffset();
        public RectOffset padding = new RectOffset();
    }

    public class GUIStyleState { public Color textColor; public Texture2D background; }
    public class RectOffset { public RectOffset() { } public RectOffset(int l, int r, int t, int b) { } }
    public enum TextAnchor { UpperLeft, UpperCenter, UpperRight, MiddleLeft, MiddleCenter, MiddleRight, LowerLeft, LowerCenter, LowerRight }

    public class GUIContent
    {
        public static GUIContent none = new GUIContent();
        public GUIContent() { }
        public GUIContent(string text) { }
    }

    public class GUISkin { public GUIStyle label; public GUIStyle box; public GUIStyle button; }

    public static class GUI
    {
        public static GUISkin skin = new GUISkin();
        public static Color backgroundColor;
        public static Color color;
        public static Color contentColor;
        public static void Label(Rect r, string text) { }
        public static void Label(Rect r, string text, GUIStyle style) { }
        public static void Box(Rect r, string text) { }
        public static void Box(Rect r, GUIContent content) { }
        public static bool Button(Rect r, string text) => false;
        public static void DrawTexture(Rect r, Texture2D tex) { }
    }

    public class WaitForSeconds : YieldInstruction { public WaitForSeconds(float seconds) { } }
    public class WaitForSecondsRealtime : CustomYieldInstruction { public WaitForSecondsRealtime(float seconds) { } public override bool keepWaiting => false; }
    public class WaitForEndOfFrame : YieldInstruction { }
    public class YieldInstruction { }
    public abstract class CustomYieldInstruction : IEnumerator
    {
        public abstract bool keepWaiting { get; }
        public object Current => null;
        public bool MoveNext() => false;
        public void Reset() { }
    }
}

namespace UnityEditor
{
    public static class EditorApplication
    {
        public static bool isPlaying;
        public static void EnterPlaymode() { }
        public static void ExitPlaymode() { }
        public static void Exit(int code) { }
    }

    public class MenuItem : System.Attribute { public MenuItem(string path) { } }

    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class InitializeOnLoadAttribute : System.Attribute { }
}

namespace UnityEngine
{
    public enum FindObjectsSortMode { None, InstanceID }
}

namespace UnityEngine.Networking
{
    public class UnityWebRequestAsyncOperation : UnityEngine.YieldInstruction { }

    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }
        public float timeout;
        public Result result;
        public string error;
        public DownloadHandler downloadHandler;
        public string url;
        public static UnityWebRequestMultimedia GetAudioClip(string uri, UnityEngine.AudioType audioType) => null;
        public UnityWebRequestAsyncOperation SendWebRequest() => null;
        public void Dispose() { }
    }

    public class DownloadHandler { }
    public class DownloadHandlerAudioClip : DownloadHandler
    {
        public static UnityEngine.AudioClip GetContent(UnityWebRequest request) => null;
    }

    public class UnityWebRequestMultimedia : UnityWebRequest
    {
        public static UnityWebRequestMultimedia GetAudioClip(string uri, UnityEngine.AudioType audioType) => null;
    }
}

namespace UnityEngine
{
    public enum AudioType { MPEG, WAV, OGG }
}

namespace UnityEngine.UI
{
    public class Graphic : UnityEngine.Behaviour { public UnityEngine.Color color; }
    public class Text : Graphic
    {
        public string text;
        public int fontSize;
        public UnityEngine.Font font;
        public UnityEngine.TextAnchor alignment;
        public UnityEngine.RectTransform rectTransform;
    }
    public class Image : Graphic { public UnityEngine.Sprite sprite; public UnityEngine.RectTransform rectTransform; }
    public class Canvas : UnityEngine.Behaviour { public UnityEngine.RenderMode renderMode; }
    public class CanvasScaler : UnityEngine.Behaviour
    {
        public enum ScaleMode { ConstantPixelSize, ScaleWithScreenSize, ConstantPhysicalSize }
        public ScaleMode uiScaleMode;
        public UnityEngine.Vector2 referenceResolution;
        public float matchWidthOrHeight;
    }
    public class GraphicRaycaster : UnityEngine.Behaviour { }
}

namespace UnityEngine
{
    public class Font : Object { public static Font CreateDynamicFontFromOSFont(string name, int size) => null; }
    public enum RenderMode { ScreenSpaceOverlay, ScreenSpaceCamera, WorldSpace }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin;
        public Vector2 anchorMax;
        public Vector2 offsetMin;
        public Vector2 offsetMax;
        public Vector2 pivot;
        public Vector2 anchoredPosition;
        public Vector2 sizeDelta;
    }
}

namespace UnityEngine.InputSystem
{
    public class KeyControl { public bool wasPressedThisFrame; public bool isPressed; }
    public class Keyboard
    {
        public static Keyboard current => null;
        public KeyControl spaceKey = new KeyControl();
        public KeyControl rKey = new KeyControl();
        public KeyControl aKey = new KeyControl();
        public KeyControl gKey = new KeyControl();
        public KeyControl bKey = new KeyControl();
        public KeyControl nKey = new KeyControl();
        public KeyControl pKey = new KeyControl();
        public KeyControl leftArrowKey = new KeyControl();
        public KeyControl rightArrowKey = new KeyControl();
        public KeyControl upArrowKey = new KeyControl();
        public KeyControl downArrowKey = new KeyControl();
        public KeyControl leftBracketKey = new KeyControl();
        public KeyControl rightBracketKey = new KeyControl();
    }
}

namespace UnityEngine
{
    public static class JsonUtility
    {
        public static T FromJson<T>(string json) => default(T);
        public static object FromJson(string json, Type type) => null;
        public static string ToJson(object obj) => "";
        public static string ToJson(object obj, bool prettyPrint) => "";
        public static void FromJsonOverwrite(string json, object objectToOverwrite) { }
    }

    public static class ImageConversion
    {
        public static byte[] EncodeToPNG(Texture2D tex) => new byte[0];
        public static byte[] EncodeToJPG(Texture2D tex) => new byte[0];
        public static byte[] EncodeToJPG(Texture2D tex, int quality) => new byte[0];
        public static bool LoadImage(Texture2D tex, byte[] data) => true;
    }

}

namespace UnityEngine.UI
{
    public class MaskableGraphic : Graphic { }
    public class Outline : UnityEngine.Behaviour
    {
        public UnityEngine.Color effectColor;
        public UnityEngine.Vector2 effectDistance;
    }
}

namespace UnityEngine
{
    public static class Input
    {
        public static bool GetKeyDown(KeyCode k) => false;
        public static bool GetKey(KeyCode k) => false;
        public static bool GetMouseButtonDown(int b) => false;
        public static Vector3 mousePosition => Vector3.zero;
    }

    public enum KeyCode { Space, R, A, G, B, N, P, LeftArrow, RightArrow, UpArrow, DownArrow, LeftBracket, RightBracket, Escape, Return }
}
"""


def find_dotnet():
    for candidate in DOTNET_CANDIDATES:
        if candidate.exists() and os.access(candidate, os.X_OK):
            return candidate
    found = shutil.which("dotnet")
    if found:
        return Path(found)
    return None


def read_csproj_template():
    return """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <LangVersion>9.0</LangVersion>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
    <NoWarn>CS0168;CS0219;CS0414;CS0649;CS0067;CS0108;CS0114;CS1998;CS0162;CS8981</NoWarn>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="stubs/UnityStubs.cs" />
{items}
  </ItemGroup>
</Project>
"""


def build_project(tmp: Path, files):
    (tmp / "stubs").mkdir(parents=True, exist_ok=True)
    (tmp / "stubs" / "UnityStubs.cs").write_text(UNITY_STUBS, encoding="utf-8")

    items = []
    for f in files:
        rel = Path(os.path.relpath(f, tmp)).as_posix()
        items.append(f'    <Compile Include="{rel}" />')
    (tmp / "Check.csproj").write_text(read_csproj_template().format(items="\n".join(items)), encoding="utf-8")


def parse_errors(output: str, root: Path):
    errors = []
    for line in output.splitlines():
        m = re.match(r"^(.*)\((\d+),(\d+)\): error (CS\d+): (.*)$", line.strip())
        if m:
            errors.append({
                "file": m.group(1),
                "line": int(m.group(2)),
                "column": int(m.group(3)),
                "code": m.group(4),
                "message": m.group(5),
            })
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    parser.add_argument("--keep", action="store_true", help="keep the temp project directory")
    parser.add_argument("--dir", default=str(SCRIPTS_DIR), help="script directory to check")
    args = parser.parse_args()

    dotnet = find_dotnet()
    if dotnet is None:
        print("dotnet SDK not found; cannot compile-check.", file=sys.stderr)
        return 2

    scripts_dir = Path(args.dir)
    files = sorted(p for p in scripts_dir.rglob("*.cs") if p.name not in EXCLUDED)
    if not files:
        print(f"no .cs files under {scripts_dir}", file=sys.stderr)
        return 2

    tmp = Path(tempfile.mkdtemp(prefix="board-game-unity-check-"))
    try:
        build_project(tmp, files)
        env = dict(os.environ)
        env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        env["DOTNET_NOLOGO"] = "1"
        proc = subprocess.run(
            [str(dotnet), "build", "Check.csproj", "-v", "quiet", "--nologo"],
            cwd=str(tmp), env=env, capture_output=True, text=True,
        )
        output = (proc.stdout or "") + (proc.stderr or "")
        errors = parse_errors(output, tmp)
        ok = proc.returncode == 0 and not errors

        if args.json:
            print(json.dumps({"ok": ok, "files": len(files), "errors": errors}, ensure_ascii=False, indent=2))
        else:
            if ok:
                print(f"OK  {len(files)} 个 C# 文件编译通过")
            else:
                print(f"FAIL  {len(errors)} 个编译错误（{len(files)} 个文件）")
                for e in errors:
                    print(f"  {Path(e['file']).name}:{e['line']}:{e['column']}  {e['code']}  {e['message']}")
                if not errors:
                    print(output[-2000:])
        return 0 if ok else 1
    finally:
        if args.keep:
            print(f"[kept] {tmp}")
        else:
            shutil.rmtree(tmp, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
