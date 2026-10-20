// Deterministic API boundary doubles, NOT an audio mixer or a Unity integration test.
using System.Collections;
namespace UnityEngine {
 public class Object { public static void Destroy(Object obj) {} }
 public class Coroutine {}
 public class Transform { public void SetParent(Transform parent,bool worldPositionStays) {} }
 public class GameObject : Object {
  public Transform transform=new(); private readonly Dictionary<Type,Component> components=new();
  public GameObject(string name="test") {}
  public T AddComponent<T>() where T:Component,new() {var c=new T {gameObject=this};components[typeof(T)]=c;return c;}
  public T GetComponent<T>() where T:Component => components.TryGetValue(typeof(T),out var c)?(T)c:null;
 }
 public class Component : Object {
  private GameObject owner; public GameObject gameObject {get=>owner??=new GameObject();set=>owner=value;}
  public Transform transform=>gameObject.transform;
  public T GetComponent<T>() where T:Component => gameObject.GetComponent<T>();
 }
 public class MonoBehaviour : Component { public Coroutine StartCoroutine(IEnumerator routine) { while(routine.MoveNext()) {} return new Coroutine(); } public void StopCoroutine(Coroutine c) {} }
 public static class AudioSettings {
  public static double dspTime;
  public static int outputSampleRate = 48000;
  public static void GetDSPBufferSize(out int frames,out int buffers) {frames=256;buffers=4;}
 }
 public static class Mathf { public static float Clamp01(float n)=>Math.Clamp(n,0,1); }
 public static class Debug { public static void Log(object o) {} public static void LogError(object o) {} public static void LogWarning(object o) {} }
 public enum AudioType { WAV, OGGVORBIS, AIFF, MPEG }
 public class AudioClip : Object {
  public int samples,channels,frequency; public float length=>(float)samples/frequency; public float[] Data;
  public static double CreationDelay;
  public static AudioClip Create(string name,int frames,int chans,int rate,bool stream) { AudioSettings.dspTime+=CreationDelay; return new AudioClip { samples=frames,channels=chans,frequency=rate,Data=new float[frames*chans] }; }
  public bool SetData(float[] data,int offset) { Array.Copy(data,0,Data,offset*channels,data.Length); return true; }
  public bool GetData(float[] data,int offset) { Array.Copy(Data,offset*channels,data,0,data.Length); return true; }
 }
 public class AudioSource : Component {
  public bool playOnAwake,loop; public AudioClip clip; public int timeSamples; public float volume;
  public readonly List<(double request,double called)> Appointments=new();
  public int StopCount;
  public void Stop() {StopCount++;}
  public void PlayScheduled(double dsp) { Appointments.Add((dsp,AudioSettings.dspTime)); }
 }
}
namespace UnityEngine.Networking {
 public class UnityWebRequest { public enum Result { Success }; public Result result; public string error; public int timeout; public object SendWebRequest()=>throw new NotSupportedException("Network must not be used"); public void Abort() {} public void Dispose() {} }
 public static class UnityWebRequestMultimedia { public static UnityWebRequest GetAudioClip(string path,UnityEngine.AudioType t)=>throw new NotSupportedException("All test assets must be injected"); }
 public static class DownloadHandlerAudioClip { public static UnityEngine.AudioClip GetContent(UnityWebRequest r)=>throw new NotSupportedException(); }
}
