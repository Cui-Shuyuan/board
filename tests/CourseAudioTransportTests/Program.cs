using System.Reflection;
using System.Text.Json;
using BoardGameTutorial.Animation;
using UnityEngine;

class DspClock : ICueClock { public double NowSeconds=>AudioSettings.dspTime; }
class Program {
 static int failures;
 static void Check(bool condition,string label) { if(!condition){failures++;Console.Error.WriteLine("FAIL "+label);} }
 static CueTimelinePlanSet Set(params (string asset,double length)[] specs) {
  double cursor=0;
  var set=new CueTimelinePlanSet { schema="tutorial-cue-plan-set/v1",schema_version=1,game_id="review",track="test",course=new(),cues=new() };
  for(int i=0;i<specs.Length;i++) {
   var (asset,len)=specs[i];
   set.cues.Add(new CueTimelinePlan { schema="tutorial-cue-plan/v1",cue_id="cue"+i,course=new() {start=cursor,duration=len},tracks=new() {
    new CueTimelineTrack {id="audio.main",kind="audio",tail_policy="silence",segments=new() { new CueTimelineSegment {id="audio.slice.0",type="audio_clip",asset=asset,source_start=0,source_end=len,cue_start=0} } }
   } }); cursor+=len;
  }
  set.course.duration=cursor; return set;
 }
 static (UnityCourseAudioTransport transport,CueCoursePlaybackController controller,AudioSource a,AudioSource b) Setup(CueTimelinePlanSet set,Func<string,int> frames=null) {
  AudioSettings.dspTime=0; AudioClip.CreationDelay=0;
  var a=new AudioSource();var b=new AudioSource();
  var t=new UnityCourseAudioTransport(new MonoBehaviour(),a,b,"D:/review");
  var cache=(Dictionary<string,AudioClip>)typeof(UnityCourseAudioTransport).GetField("sourceClipCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(t);
  foreach(var asset in set.cues.SelectMany(c=>c.tracks).SelectMany(tr=>tr.segments).Select(s=>s.asset).Distinct()) cache[asset]=AudioClip.Create(asset,frames?.Invoke(asset)??10000,1,1000,false);
  var c=new CueCoursePlaybackController(new DspClock(),t,new CueTimelineSubtitleExecutor());
  return (t,c,a,b);
 }
 static void Main() {
  var s=Set(("a",1)); var x=Setup(s,_=>990);
  int errors=0;x.controller.Failed+=_=>errors++;
  var accepted=x.controller.Prepare(s,0,false);
  AudioSettings.dspTime=.58; x.controller.Tick();
  Check(errors==1 && !accepted && x.controller.State==CuePlaybackState.Failed,"prewarm failure is terminal");
  Check(x.a.Appointments.Count+x.b.Appointments.Count==0 && x.controller.CourseTime==0,"failure cannot advance clock without audio");

  s=Set(("a",2),("b",2));x=Setup(s);x.controller.Prepare(s,0,false);
  AudioSettings.dspTime=2.58;x.controller.Tick();
  Check(x.controller.State==CuePlaybackState.Buffering && Math.Abs(x.controller.CourseTime-2)<1e-7,"stall rewinds to earliest unheard interval");
  AudioSettings.dspTime=8;x.controller.Tick();
  Check(x.controller.State==CuePlaybackState.Playing && Math.Abs(x.controller.CourseTime-2)<1e-7,"buffer recovers automatically without consuming speech");

  s=Set(("a",.01),("b",.01),("c",1));x=Setup(s);x.controller.Prepare(s,0,false);
  AudioSettings.dspTime=.08;x.controller.Tick();
  AudioSettings.dspTime=.0966667;x.controller.Tick();
  AudioSettings.dspTime=.1133334;x.controller.Tick();
  Check(x.controller.State==CuePlaybackState.Playing && x.a.Appointments.Count+x.b.Appointments.Count==1,"dense edits are pre-rendered, not missed at 60fps");

  s=Set(("a",4));x=Setup(s);x.controller.Prepare(s,0,false);
  AudioSettings.dspTime=.5;AudioClip.CreationDelay=.2;
  x.controller.SeekTo(1,false);
  var last=x.a.Appointments.Concat(x.b.Appointments).Last();
  Check(last.request>last.called && Math.Abs(x.controller.CourseTime-1)<1e-7,"slow seek commits a fresh shared epoch after PCM preparation");
  Check(!x.controller.HasStarted,"native progress stays frozen before scheduled audio starts");
  AudioClip.CreationDelay=0;

  // A pause/seek retires audible voices, but cancels future appointments
  // immediately. Audio-thread release is independent of Update frame rate.
  s=Set(("a",4));x=Setup(s);x.controller.Prepare(s,0,false);
  AudioSettings.dspTime=.5;var stops=x.a.StopCount;
  x.controller.Pause();
  Check(x.a.StopCount==stops && x.a.clip!=null,"pause keeps the audible source alive for its release");
  var filter=x.a.GetComponent<CueAudioReleaseEnvelope>();
  var tail=Enumerable.Repeat(1f,512*2).ToArray();
  typeof(CueAudioReleaseEnvelope).GetMethod("OnAudioFilterRead",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(filter,new object[]{tail,2});
  Check(tail[0]==1 && tail[382]==0 && tail.Skip(384).All(v=>v==0),"4ms stereo release reaches zero and holds silence");
  Check(Enumerable.Range(0,192).All(i=>tail[2*i]==tail[2*i+1]),"release preserves stereo channel balance");
  x.transport.ReleaseFinishedSlots();
  Check(x.a.clip==null && x.a.StopCount>stops,"released voice is reclaimed after audio-thread acknowledgement");
  x.controller.Resume();
  Check(x.controller.State==CuePlaybackState.Playing,"resume schedules after the retired tail");
  AudioSettings.dspTime+=.2; x.controller.SeekTo(2,false);
  Check(x.controller.State==CuePlaybackState.Playing && x.controller.CourseTime==2,"seek can schedule while old voice retires");
  for(int i=0;i<12;i++)x.controller.SeekTo(1+i*.01,false);
  Check(x.controller.State==CuePlaybackState.Playing,"rapid seeks cancel future appointments without exhausting voices");

  var ramp=new CueAudioReleaseRamp();ramp.Release(1000);
  var head=new[]{1f,1f};ramp.Process(head,1);
  var rest=new[]{1f,1f,1f};ramp.Process(rest,1);
  Check(head[0]==1 && head[1]<1 && rest[0]>0 && rest[1]==0 && rest[2]==0 && ramp.IsSilent,"release spans callback buffers without restarting");
  ramp.Reset();var unchanged=new[]{.25f,-.75f};ramp.Process(unchanged,1);
  Check(unchanged.SequenceEqual(new[]{.25f,-.75f}) && !ramp.IsSilent,"new voice resets release without affecting continuous PCM");

  s=Set(("a",.015),("a",.015));
  s.cues[1].tracks[0].segments[0].source_start=.015;s.cues[1].tracks[0].segments[0].source_end=.030;
  var fm=new CueAudioAssetFormatMap();fm.Add("a",44100,1);
  try { var p=CueAudioTimelinePlanner.Build(s,fm,new()); Check(p.Blocks.Count==1 && p.Blocks[0].FrameCount==1323,"fractional source boundaries merge without invented overlap"); }
  catch(Exception e) { Check(false,"fractional boundary: "+e.Message); }

  // A one-clip timeline and the same PCM split at a fractional-frame cue
  // boundary must render identically, including across arbitrary seek offsets.
  var split=CueAudioTimelinePlanner.Build(s,fm,new());
  var whole=CueAudioTimelinePlanner.Build(Set(("a",.030)),fm,new());
  var signal=Enumerable.Range(0,1323).Select(i=>(float)Math.Sin(i*.1)).ToArray();
  var splitPcm=CueAudioRenderGroup.Build(split)[0].Render(0,.030,_=>signal);
  var wholePcm=CueAudioRenderGroup.Build(whole)[0].Render(0,.030,_=>signal);
  Check(splitPcm.SequenceEqual(signal) && splitPcm.SequenceEqual(wholePcm),"cue partition leaves continuous PCM bit-identical");
  Check(CueAudioRenderGroup.Build(split)[0].Render(.0075,.0265,_=>signal)
      .SequenceEqual(CueAudioRenderGroup.Build(whole)[0].Render(.0075,.0265,_=>signal)),
      "fractional seek/cut keeps the same PCM phase and envelope regardless of cue partition");

  var mixed=Set(("mono",.01),("stereo",.01));
  fm=new();fm.Add("mono",1000,1);fm.Add("stereo",2000,2);
  var mixedGroup=CueAudioRenderGroup.Build(CueAudioTimelinePlanner.Build(mixed,fm,new())).Single();
  var mono=Enumerable.Repeat(.75f,10).ToArray();
  var stereo=Enumerable.Range(0,40).Select(i=>i%2==0?.25f:-.5f).ToArray();
  var mixedPcm=mixedGroup.Render(0,.02,asset=>asset=="mono"?mono:stereo);
  Check(mixedPcm.Length==80 && mixedPcm[12]==.75f && mixedPcm[13]==.75f
      && mixedPcm[60]==.25f && mixedPcm[61]==-.5f,
      "dense mixed-rate mono/stereo edits preserve duration and channel mapping");

  s=Set(("a",1)); x=Setup(s); x.controller.Prepare(s,0,true);
  var oldOutput=(Dictionary<string,AudioClip>)typeof(UnityCourseAudioTransport).GetField("outputClipCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(x.transport);
  var originalClip=oldOutput.Values.Single();
  var replacement=Set(("b",1));
  var sources=(Dictionary<string,AudioClip>)typeof(UnityCourseAudioTransport).GetField("sourceClipCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(x.transport);
  sources["b"]=AudioClip.Create("b",1000,1,1000,false);
  Array.Fill(sources["b"].Data,.5f);
  x.controller.Prepare(replacement,0,true);
  Check(oldOutput.Count==1 && !ReferenceEquals(originalClip,oldOutput.Values.Single())
      && oldOutput.Values.Single().Data[500]==.5f,
      "new plan cannot reuse a different asset's output at the same group/frame key");

  s=Set(("long",80));x=Setup(s);
  sources=(Dictionary<string,AudioClip>)typeof(UnityCourseAudioTransport).GetField("sourceClipCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(x.transport);
  sources["long"]=AudioClip.Create("long",80*24000,1,24000,false);
  x.controller.Prepare(s,0,false);
  for(int i=1;i<=8;i++)x.controller.SeekTo(i,false);
  long cachedBytes=(long)typeof(UnityCourseAudioTransport).GetField("outputCacheBytes",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(x.transport);
  Check(cachedBytes<=16L*1024*1024 && x.controller.State==CuePlaybackState.Playing,
      "repeated long-block seeks evict old suffix clips by byte budget");

  // Device reset may reset dspTime; preserve the last observed course position.
  s=Set(("a",4));x=Setup(s);x.controller.Prepare(s,0,false);
  AudioSettings.dspTime=1.08;x.controller.Tick();
  var saved=x.controller.CourseTime;AudioSettings.dspTime=0;
  // Exercise clock contract with the core fake in AnimationModelTests; the
  // real transport intentionally requires media reload after invalidation.
  x.transport.InvalidateDeviceResources();
  var cache=(Dictionary<string,AudioClip>)typeof(UnityCourseAudioTransport).GetField("outputClipCache",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(x.transport);
  Check(cache.Count==0,"device reset invalidates all script-generated clips");

  foreach(var track in new[]{"quick","full"}) {
   var json=File.ReadAllText($"content/games/splendor/tutorial/{track}.cue-timeline.json");
   var real=JsonSerializer.Deserialize<CueTimelinePlanSet>(json,new JsonSerializerOptions{IncludeFields=true});
   fm=new();foreach(var asset in real.cues.SelectMany(c=>c.tracks).Where(t=>t.kind=="audio").SelectMany(t=>t.segments).Where(seg=>seg.type=="audio_clip").Select(seg=>seg.asset).Distinct())fm.Add(asset,24000,1);
   var plan=CueAudioTimelinePlanner.Build(real,fm,new());
   var max=plan.Blocks.OrderByDescending(b=>b.FrameCount).First();
   Console.WriteLine($"CONTENT_{track}: blocks={plan.Blocks.Count}, merged boundaries={plan.Boundaries.Count(b=>b.Merged)}, longest block={max.CourseDuration:F3}s, float PCM={max.FrameCount*4/1048576.0:F2} MiB");
  }
  Console.WriteLine(failures==0 ? "OK CourseAudioTransportTests: all checks passed" : $"FAILED {failures} checks");
  Environment.ExitCode=failures==0?0:1;
 }
}
