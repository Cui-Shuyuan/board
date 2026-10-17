// Pure .NET frame-evaluation tests.  The harness deliberately has no external
// test-framework dependency so it can run with the same dotnet used by the
// compile check.
using System;
using System.Collections.Generic;
using BoardGameTutorial;
using BoardGameTutorial.Animation;

internal static class Program
{
    private static int failures;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine("FAIL  " + message);
        }
    }

    private static void CheckClose(float expected, float actual, float eps, string message)
    {
        Check(Math.Abs(expected - actual) <= eps, message + " (expected " + expected + ", got " + actual + ")");
    }

    private static void CheckClose(double expected, double actual, double eps, string message)
    {
        Check(Math.Abs(expected - actual) <= eps, message + " (expected " + expected + ", got " + actual + ")");
    }

    private static ComponentState Comp(string id, float x = 0f, float z = 0f, FaceState face = FaceState.Up)
    {
        return new ComponentState
        {
            Id = id,
            TemplateId = "tpl",
            ZoneId = "showcase",
            Order = 0,
            Face = face,
        };
    }

    private static CompiledStageDef Stage()
    {
        return new CompiledStageDef
        {
            stage = "test",
            zones = new List<CompiledZoneDef>
            {
                new CompiledZoneDef
                {
                    zone = "showcase",
                    slots = new List<SlotDef> { new SlotDef { order = 0, x = 0f, z = 0f } },
                },
            },
            templates = new List<CompiledTemplateDef>
            {
                new CompiledTemplateDef { id = "tpl", shape = "card", width = 0.63f, height = 0.88f },
            },
        };
    }

    private static CompiledCueDef Cue(params ComponentState[] components)
    {
        var cue = new CompiledCueDef
        {
            id = "cue",
            camera_in = new CompiledCameraDef { center_x = 1f, center_z = 1f, ortho_size = 5f },
            start_state = new StateSnapshot(),
        };
        if (components != null) cue.start_state.components.AddRange(components);
        return cue;
    }

    private static void TestLogicalStateAndCamera()
    {
        var cue = Cue(Comp("a"), Comp("b"));
        cue.state_ops.Add(new CompiledStateOpDef { op = "put", at = 1f, item = Comp("c") });
        cue.state_ops.Add(new CompiledStateOpDef { op = "remove", at = 2f, item_id = "a" });
        cue.camera_ops.Add(new CompiledCameraOpDef { at = 1f, frame = new CompiledCameraDef { center_x = 2f, center_z = 2f } });

        var atHalf = TimelineEvaluator.Evaluate(cue, Stage(), 0.5f);
        Check(atHalf.Find("a") != null && atHalf.Find("b") != null && atHalf.Find("c") == null,
            "logical state before ops");
        CheckClose(1f, atHalf.Camera.center_x, 1e-6f, "camera before op");

        var atOneHalf = TimelineEvaluator.Evaluate(cue, Stage(), 1.5f);
        Check(atOneHalf.Find("a") != null && atOneHalf.Find("b") != null && atOneHalf.Find("c") != null,
            "logical state after put, before remove");
        CheckClose(2f, atOneHalf.Camera.center_x, 1e-6f, "camera after op");

        var atTwoHalf = TimelineEvaluator.Evaluate(cue, Stage(), 2.5f);
        Check(atTwoHalf.Find("a") == null && atTwoHalf.Find("b") != null && atTwoHalf.Find("c") != null,
            "logical state after remove");
    }

    private static void TestMoveAndFlip()
    {
        var cue = Cue(Comp("a"));
        cue.clips.Add(new CompiledClipDef
        {
            kind = "move", item_id = "a", at = 0f, dur = 1f, easing = "linear",
            from_x = 0f, from_z = 0f, to_x = 10f, to_z = 0f,
        });

        var moving = TimelineEvaluator.Evaluate(cue, Stage(), 0.5f);
        CheckClose(5f, moving.Find("a").X, 1e-5f, "move midpoint");
        var moved = TimelineEvaluator.Evaluate(cue, Stage(), 1f);
        CheckClose(10f, moved.Find("a").X, 1e-5f, "move endpoint");

        var flipCue = Cue(Comp("a"));
        flipCue.clips.Add(new CompiledClipDef
        {
            kind = "flip", item_id = "a", at = 0f, dur = 1f, easing = "linear",
            from_face = "face_up", to_face = "face_down", from_x = 0f, from_z = 0f,
        });
        var midFlip = TimelineEvaluator.Evaluate(flipCue, Stage(), 0.5f);
        CheckClose(0f, midFlip.Find("a").Flip, 1e-5f, "flip midpoint edge-on");
        var endFlip = TimelineEvaluator.Evaluate(flipCue, Stage(), 1f);
        CheckClose(1f, endFlip.Find("a").Flip, 1e-5f, "flip endpoint back");
        Check(endFlip.Find("a").Face == FaceState.Down, "flip endpoint face");
    }

    private static void TestShuffle()
    {
        var cue = Cue(Comp("a"));
        cue.clips.Add(new CompiledClipDef
        {
            kind = "shuffle", item_id = "a", at = 0f, dur = 1f, easing = "linear",
            from_x = 0f, from_z = 0f, sh_amp = 1f, sh_freq = 1f, sh_phase = 0f,
            sh_zamp = 1f, sh_env = 0.45f,
        });

        var shuffled = TimelineEvaluator.Evaluate(cue, Stage(), 0.25f);
        Check(Math.Abs(shuffled.Find("a").X) > 0.01f, "shuffle displaces item");
        var settled = TimelineEvaluator.Evaluate(cue, Stage(), 1f);
        CheckClose(0f, settled.Find("a").X, 1e-5f, "shuffle returns to base");
    }

    private static void TestOverlayShowHideAndModifier()
    {
        var cue = Cue(Comp("a"));
        cue.clips.Add(new CompiledClipDef
        {
            kind = "overlay_show", overlay = "o", at = 0f, dur = 0f,
            template = "tpl", label_x = 0.1f, label_y = 0.2f, label_w = 0.3f, label_h = 0.4f,
            layer = 3,
        });
        cue.clips.Add(new CompiledClipDef
        {
            kind = "fade", object_space = "screen", overlay = "o", at = 0f, dur = 1f,
            easing = "linear", to_alpha = 0f,
        });
        cue.clips.Add(new CompiledClipDef { kind = "overlay_hide", overlay = "o", at = 2f });

        var shown = TimelineEvaluator.Evaluate(cue, Stage(), 1f);
        Check(shown.Overlays.Count == 1 && shown.Overlays[0].Id == "o", "overlay shown");
        CheckClose(0f, shown.Overlays[0].Alpha, 1e-5f, "overlay fade modifier");

        var hidden = TimelineEvaluator.Evaluate(cue, Stage(), 3f);
        Check(hidden.Overlays.Count == 0, "overlay hidden");
    }

    private static void TestAnnotationsAndMagnifier()
    {
        var cue = Cue(Comp("a"));
        cue.clips.Add(new CompiledClipDef
        {
            kind = "shape", object_space = "entity", annotation_space = "world",
            item_id = "a", at = 0f, dur = 0f, indicator = "box", part_u = 0.5f,
            part_v = 0.5f, part_w = 1f, part_h = 1f, has_part_uv = true,
        });
        cue.clips.Add(new CompiledClipDef
        {
            kind = "label", annotation_space = "world", item_id = "a", at = 0f, dur = 0f,
            text = "hello", part_u = 0.5f, part_v = 0.5f,
        });
        cue.clips.Add(new CompiledClipDef
        {
            kind = "magnifier_show", overlay = "lens", at = 0f, dur = 0f,
            mag_shape = "box", mag_mask = "full", mag_x = 0.1f, mag_y = 0.2f,
            mag_w = 0.3f, mag_h = 0.4f, mag_center_x = 1f, mag_center_z = 2f,
            mag_ortho_size = 3f, layer = 9, mag_item_ids = new[] { "a" },
        });
        cue.clips.Add(new CompiledClipDef { kind = "magnifier_hide", overlay = "lens", at = 2f });

        var frame = TimelineEvaluator.Evaluate(cue, Stage(), 0.5f);
        bool hasBox = false, hasLabel = false;
        foreach (var annotation in frame.Annotations)
        {
            if (annotation.Kind == "box") hasBox = true;
            if (annotation.Kind == "label" && annotation.Text == "hello") hasLabel = true;
        }
        Check(frame.Annotations.Count == 2, "world annotations emitted");
        Check(hasBox && hasLabel, "annotation kinds/text");
        Check(frame.Magnifiers.Count == 1 && frame.Magnifiers[0].Shape == "box"
              && frame.Magnifiers[0].MaskMode == "full" && frame.Magnifiers[0].Layer == 9,
            "magnifier state");

        var hidden = TimelineEvaluator.Evaluate(cue, Stage(), 3f);
        Check(hidden.Magnifiers.Count == 0, "magnifier hidden");
    }

    private static void TestSameTimeCompiledOrderIsStable()
    {
        var cue = Cue(Comp("a"));
        cue.clips.Add(new CompiledClipDef
        {
            kind = "move", item_id = "a", at = 0f, dur = 0f, easing = "linear",
            from_x = 0f, from_z = 0f, to_x = 1f, to_z = 0f,
        });
        cue.clips.Add(new CompiledClipDef
        {
            kind = "move", item_id = "a", at = 0f, dur = 0f, easing = "linear",
            from_x = 0f, from_z = 0f, to_x = 2f, to_z = 0f,
        });

        var moved = TimelineEvaluator.Evaluate(cue, Stage(), 0f);
        CheckClose(2f, moved.Find("a").X, 1e-6f, "same-time clip order keeps compiled index");

        var overlayCue = Cue(Comp("a"));
        overlayCue.clips.Add(new CompiledClipDef
        {
            kind = "overlay_show", overlay = "o", at = 0f, dur = 0f, template = "first",
        });
        overlayCue.clips.Add(new CompiledClipDef
        {
            kind = "overlay_show", overlay = "o", at = 0f, dur = 0f, template = "second",
        });
        var overlay = TimelineEvaluator.Evaluate(overlayCue, Stage(), 0f);
        Check(overlay.Overlays.Count == 1 && overlay.Overlays[0].TemplateId == "second",
            "same-time overlay order keeps compiled index");
    }


    private static string Fmt(double value)
    {
        return value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasCall(List<string> calls, string prefix)
    {
        for (int i = 0; i < calls.Count; i++)
            if (calls[i].StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static int CountCalls(List<string> calls, string prefix)
    {
        int count = 0;
        for (int i = 0; i < calls.Count; i++)
            if (calls[i].StartsWith(prefix, StringComparison.Ordinal)) count++;
        return count;
    }

    private static CueTimelineSegment SegmentAt(CueTimelinePlan plan, string kind, double cueTime)
    {
        foreach (var track in plan.tracks)
        {
            if (track.kind != kind) continue;
            foreach (var segment in track.segments)
            {
                if (cueTime + 1e-9 < segment.cue_start) continue;
                if (cueTime >= segment.EndCueTime - 1e-9) continue;
                return segment;
            }
        }
        return null;
    }

    private static double AudioSourcePositionAt(CueTimelinePlan plan, double cueTime)
    {
        var segment = SegmentAt(plan, "audio", cueTime);
        if (segment == null) return -1.0;
        if (segment.type == "audio_clip")
            return segment.source_start + (cueTime - segment.cue_start);
        return 0.0;
    }

    private static CueTimelinePlan CuePlaybackPlan(string cueId)
    {
        var plan = new CueTimelinePlan
        {
            schema = "tutorial-cue-plan/v1",
            cue_id = cueId,
            entry = "fixture/previous",
            course = new CueTimelineCourse { start = 100.0, duration = 10.0 },
            tracks = new List<CueTimelineTrack>(),
        };
        plan.tracks.Add(new CueTimelineTrack
        {
            id = "audio.main",
            kind = "audio",
            tail_policy = "silence",
            segments = new List<CueTimelineSegment>
            {
                new CueTimelineSegment
                {
                    id = "a.voice1", type = "audio_clip", cue_start = 0.0,
                    asset = "fixture.mp3", source_start = 10.0, source_end = 12.0,
                },
                new CueTimelineSegment
                {
                    id = "a.silence1", type = "silence", cue_start = 2.0, duration = 2.0,
                },
                new CueTimelineSegment
                {
                    id = "a.voice2", type = "audio_clip", cue_start = 4.0,
                    asset = "fixture.mp3", source_start = 20.0, source_end = 23.0,
                },
                new CueTimelineSegment
                {
                    id = "a.silence2", type = "silence", cue_start = 7.0, duration = 3.0,
                },
            },
        });
        plan.tracks.Add(new CueTimelineTrack
        {
            id = "visual.main",
            kind = "visual",
            tail_policy = "hold_last_frame",
            segments = new List<CueTimelineSegment>
            {
                new CueTimelineSegment
                {
                    id = "v.anim1", type = "animation", cue_start = 0.0,
                    animation = "fixture#opening", source_start = 0.0, source_end = 2.0,
                    after_end = "hold_last_frame",
                },
                new CueTimelineSegment
                {
                    id = "v.hold1", type = "hold", cue_start = 2.0, duration = 2.0,
                    pose = "last_frame",
                },
                new CueTimelineSegment
                {
                    id = "v.anim2", type = "animation", cue_start = 4.0,
                    animation = "fixture#speech", source_start = 100.0, source_end = 103.0,
                    after_end = "hold_last_frame",
                },
                new CueTimelineSegment
                {
                    id = "v.hold2", type = "hold", cue_start = 7.0, duration = 3.0,
                    pose = "last_frame",
                },
            },
        });
        return plan;
    }

    private static FakeSubtitleExecutor MakeSubtitles()
    {
        var subtitles = new FakeSubtitleExecutor();
        subtitles.Windows.Add(new SubtitleWindow { Start = 0.0, End = 1.5, Text = "voice one" });
        subtitles.Windows.Add(new SubtitleWindow { Start = 4.0, End = 6.0, Text = "voice two" });
        return subtitles;
    }

    private static CueTimelinePlan PlanAt(string cueId, double start, double duration)
    {
        var plan = new CueTimelinePlan
        {
            schema = "tutorial-cue-plan/v1",
            cue_id = cueId,
            entry = "fixture/previous",
            course = new CueTimelineCourse { start = start, duration = duration },
            tracks = new List<CueTimelineTrack>
            {
                new CueTimelineTrack
                {
                    id = "audio.main",
                    kind = "audio",
                    tail_policy = "silence",
                    segments = new List<CueTimelineSegment>
                    {
                        new CueTimelineSegment
                        {
                            id = "a.silence", type = "silence",
                            cue_start = 0.0, duration = duration,
                        },
                    },
                },
            },
        };
        return plan;
    }

    private static CueTimelinePlanSet PlanSetFixture()
    {
        return new CueTimelinePlanSet
        {
            schema = CueTimelinePlanSetValidator.Schema,
            schema_version = CueTimelinePlanSetValidator.SchemaVersion,
            game_id = "fixture",
            track = "quick",
            course = new CueTimelineCourse { start = 0.0, duration = 5.0 },
            cues = new List<CueTimelinePlan>
            {
                PlanAt("q/a", 0.0, 2.0),
                PlanAt("q/b", 2.0, 3.0),
            },
        };
    }

    private static void TestCueTimelinePlanSetValidation()
    {
        var set = PlanSetFixture();
        var expectedIds = new List<string> { "q/a", "q/b" };
        string error;
        Check(CueTimelinePlanSetValidator.TryValidate(
            set, "fixture", "quick", expectedIds, out error),
            "plan set accepts complete track coverage: " + error);

        var wrongVersion = PlanSetFixture();
        wrongVersion.schema_version = 2;
        Check(!CueTimelinePlanSetValidator.TryValidate(
            wrongVersion, "fixture", "quick", expectedIds, out error),
            "plan set rejects schema version mismatch");

        var missingCue = PlanSetFixture();
        missingCue.cues.RemoveAt(1);
        Check(!CueTimelinePlanSetValidator.TryValidate(
            missingCue, "fixture", "quick", expectedIds, out error),
            "plan set rejects partial cue coverage");

        var overlap = PlanSetFixture();
        overlap.cues[1].course.start = 1.5;
        Check(!CueTimelinePlanSetValidator.TryValidate(
            overlap, "fixture", "quick", expectedIds, out error),
            "plan set rejects overlapping cue starts");

        var wrongTotal = PlanSetFixture();
        wrongTotal.course.duration = 9.0;
        Check(!CueTimelinePlanSetValidator.TryValidate(
            wrongTotal, "fixture", "quick", expectedIds, out error),
            "plan set rejects incorrect track duration");
    }

    private static void TestCueStateMachineAndReadyGating()
    {
        Check(CuePlaybackStateMachine.CanTransition(CuePlaybackState.Idle, CuePlaybackState.Preparing),
            "state machine Idle -> Preparing");
        Check(CuePlaybackStateMachine.CanTransition(CuePlaybackState.Preparing, CuePlaybackState.Ready),
            "state machine Preparing -> Ready");
        Check(CuePlaybackStateMachine.CanTransition(CuePlaybackState.Playing, CuePlaybackState.Paused),
            "state machine Playing -> Paused");
        Check(CuePlaybackStateMachine.CanTransition(CuePlaybackState.Paused, CuePlaybackState.Seeking),
            "state machine Paused -> Seeking");
        Check(!CuePlaybackStateMachine.CanTransition(CuePlaybackState.Idle, CuePlaybackState.Playing),
            "state machine rejects Idle -> Playing");
        Check(!CuePlaybackStateMachine.CanTransition(CuePlaybackState.Ready, CuePlaybackState.Completed),
            "state machine rejects Ready -> Completed");

        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock) { AutoPrepare = false };
        var visual = new FakeVisualExecutor { AutoPrepare = false };
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        var plan = CuePlaybackPlan("fixture/ready_gate");

        Check(controller.Prepare(plan), "prepare request accepted");
        Check(controller.State == CuePlaybackState.Preparing, "controller enters Preparing");
        Check(!controller.Play(), "cannot play before resources are ready");
        Check(controller.State == CuePlaybackState.Preparing, "failed play request leaves state preparing");
        Check(HasCall(audio.Calls, "audio.prepare:"), "audio prepare called");
        Check(HasCall(visual.Calls, "visual.prepare:"), "visual prepare called");
        Check(CountCalls(audio.Calls, "audio.play:") == 0, "no audio play while preparing");

        long prepareGeneration = controller.Generation;
        audio.CompletePreparation(prepareGeneration);
        Check(controller.State == CuePlaybackState.Preparing,
            "only one executor ready is not enough");
        visual.CompletePreparation(prepareGeneration);
        Check(controller.State == CuePlaybackState.Ready,
            "both executors prepared -> Ready");
        Check(controller.Play(), "play from Ready accepted");
        Check(controller.State == CuePlaybackState.Playing, "controller enters Playing");
        Check(controller.Generation > prepareGeneration, "play advances playback generation");
    }

    private static void TestPreparationFailureIsNotCompletion()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock) { AutoPrepare = false };
        var visual = new FakeVisualExecutor { AutoPrepare = false };
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        int completionEvents = 0;
        controller.Completed += delegate { completionEvents++; };

        Check(controller.Prepare(CuePlaybackPlan("fixture/load_failure")), "prepare load-failure cue");
        long generation = controller.Generation;
        audio.FailPreparation(generation, "fixture missing");

        Check(controller.State == CuePlaybackState.Failed, "prepare failure -> Failed");
        Check(controller.LastFailure != null
              && controller.LastFailure.Kind == CuePlaybackFailureKind.Preparation,
            "prepare failure records preparation kind");
        Check(controller.CompletionCount == 0 && completionEvents == 0,
            "load failure is not natural completion");
        Check(HasCall(audio.Calls, "audio.stop:"), "prepare failure stops audio executor");
        Check(HasCall(visual.Calls, "visual.stop:"), "prepare failure stops visual executor");
    }

    private static void TestCueClockAdvancesAndPauses()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());

        Check(controller.Prepare(CuePlaybackPlan("fixture/clock")), "prepare clock cue");
        Check(controller.State == CuePlaybackState.Ready, "synchronous preparation -> Ready");
        Check(controller.Play(), "play clock cue");

        clock.Advance(2.25);
        controller.Tick();
        CheckClose(2.25, controller.CueTime, 1e-9, "Playing advances cue-local clock");

        Check(controller.Pause(), "pause playing cue");
        Check(controller.State == CuePlaybackState.Paused, "pause -> Paused");
        clock.Advance(5.0);
        controller.Tick();
        CheckClose(2.25, controller.CueTime, 1e-9, "Paused freezes cue-local clock");
        Check(HasCall(audio.Calls, "audio.pause:"), "audio pause called");
        Check(HasCall(visual.Calls, "visual.pause:"), "visual pause called");

        Check(controller.Resume(), "resume paused cue");
        clock.Advance(1.5);
        controller.Tick();
        CheckClose(3.75, controller.CueTime, 1e-9, "Resume continues from frozen cue time");
        Check(HasCall(audio.Calls, "audio.resume:"), "audio resume called");
    }

    private static void TestDurationCompletesOnce()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        int completionEvents = 0;
        CuePlaybackCompletion seen = null;
        controller.Completed += delegate(CuePlaybackCompletion completion)
        {
            completionEvents++;
            seen = completion;
        };

        Check(controller.Prepare(CuePlaybackPlan("fixture/completion")), "prepare completion cue");
        Check(controller.Play(), "play completion cue");
        long playbackGeneration = controller.Generation;
        clock.Advance(14.0);
        controller.Tick();

        Check(controller.State == CuePlaybackState.Completed, "duration reached -> Completed");
        CheckClose(10.0, controller.CueTime, 1e-9, "completion clamps cue time to duration");
        Check(completionEvents == 1 && controller.CompletionCount == 1,
            "completion notification emitted exactly once");
        Check(seen != null && seen.Generation == playbackGeneration,
            "completion carries playback generation");
        Check(HasCall(audio.Calls, "audio.stop:"), "completion stops audio");

        controller.Tick();
        controller.Tick();
        Check(completionEvents == 1 && controller.CompletionCount == 1,
            "repeated ticks do not repeat completion");
    }

    private static void TestAudioCompletionSignalDoesNotCompleteCue()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        int completionEvents = 0;
        controller.Completed += delegate { completionEvents++; };

        Check(controller.Prepare(CuePlaybackPlan("fixture/audio_signal")), "prepare audio-signal cue");
        Check(controller.Play(), "play audio-signal cue");
        long generation = controller.Generation;

        clock.Advance(1.0);
        controller.Tick();
        audio.Complete(generation);

        Check(controller.State == CuePlaybackState.Playing,
            "audio completion callback does not complete cue");
        Check(controller.CompletionCount == 0 && completionEvents == 0,
            "audio completion callback does not emit cue completion");
        Check(controller.AudioCompletionSignalCount == 1,
            "audio completion signal recorded as diagnostic");
        CheckClose(1.0, controller.CueTime, 1e-9, "cue clock remains authoritative");

        clock.Advance(9.0);
        controller.Tick();
        Check(controller.State == CuePlaybackState.Completed,
            "cue completes only when cue clock reaches duration");
        Check(completionEvents == 1, "natural completion still emits once");
    }

    private static void TestSeekCategoriesAndReplayCalls()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var subtitle = MakeSubtitles();
        var controller = new CuePlaybackController(clock, audio, visual, subtitle);
        var plan = CuePlaybackPlan("fixture/seek_categories");

        Check(controller.Prepare(plan), "prepare seek-category cue");
        Check(controller.Play(), "play seek-category cue");

        Check(controller.SeekTo(0.5), "seek to voice segment");
        Check(controller.State == CuePlaybackState.Playing, "playing seek keeps playing intent");
        CheckClose(0.5, controller.CueTime, 1e-9, "voice seek cue time");
        CheckClose(0.5, visual.LastSeekTime, 1e-9, "voice seek visual time");
        Check(controller.CurrentSubtitle.Visible && controller.CurrentSubtitle.Text == "voice one",
            "voice seek subtitle state");
        Check(SegmentAt(plan, "audio", controller.CueTime).type == "audio_clip",
            "voice seek resolves an audio clip");

        long oldGeneration = controller.Generation;
        Check(controller.SeekTo(3.0), "seek to silence segment");
        long silenceGeneration = controller.Generation;
        Check(controller.State == CuePlaybackState.Playing, "silence seek keeps playing");
        CheckClose(3.0, controller.CueTime, 1e-9, "silence seek cue time");
        Check(SegmentAt(plan, "audio", controller.CueTime).type == "silence",
            "silence seek resolves audio silence");
        Check(!controller.CurrentSubtitle.Visible, "silence seek has no subtitle");
        Check(HasCall(audio.Calls, "audio.cancel:" + oldGeneration),
            "seek cancels previous audio scheduling");
        Check(HasCall(audio.Calls, "audio.seek:" + silenceGeneration + ":3.000"),
            "seek schedules audio at cue-local target");
        Check(HasCall(audio.Calls, "audio.play:" + silenceGeneration + ":3.000"),
            "playing seek resumes audio at target");
        Check(HasCall(visual.Calls, "visual.seek:" + silenceGeneration + ":3.000"),
            "visual executor seeks to same cue time");

        Check(controller.SeekTo(7.0), "seek to hold segment");
        CheckClose(7.0, controller.CueTime, 1e-9, "hold seek cue time");
        Check(visual.LastSeekTime == 7.0, "hold seek visual time");
        Check(SegmentAt(plan, "visual", controller.CueTime).type == "hold",
            "hold seek resolves visual hold segment");
        Check(SegmentAt(plan, "audio", controller.CueTime).type == "silence",
            "hold seek resolves audio silence");

        int completions = 0;
        controller.Completed += delegate { completions++; };
        Check(controller.SeekTo(10.0), "seek to cue boundary");
        Check(controller.State == CuePlaybackState.Completed, "boundary seek completes cue");
        CheckClose(10.0, controller.CueTime, 1e-9, "boundary seek exact cue time");
        Check(completions == 1 && controller.CompletionCount == 1,
            "boundary seek emits one completion");
    }

    private static void TestPausedSeekResumeCallSequence()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());

        Check(controller.Prepare(CuePlaybackPlan("fixture/paused_seek")), "prepare paused-seek cue");
        Check(controller.Play(), "play paused-seek cue");
        clock.Advance(1.0);
        controller.Tick();
        Check(controller.Pause(), "pause before seek");

        int playCallsBeforeSeek = CountCalls(audio.Calls, "audio.play:");
        Check(controller.SeekTo(5.0), "seek while paused");
        Check(controller.State == CuePlaybackState.Paused, "paused seek keeps paused intent");
        CheckClose(5.0, controller.CueTime, 1e-9, "paused seek updates cue time");
        Check(HasCall(audio.Calls, "audio.seek:"), "paused seek repositions audio");
        Check(CountCalls(audio.Calls, "audio.play:") == playCallsBeforeSeek,
            "paused seek does not start audio playback early");

        clock.Advance(3.0);
        controller.Tick();
        CheckClose(5.0, controller.CueTime, 1e-9, "paused seek stays frozen");
        int resumeCallsBefore = CountCalls(audio.Calls, "audio.resume:");
        Check(controller.Resume(), "resume after paused seek");
        Check(CountCalls(audio.Calls, "audio.resume:") == resumeCallsBefore + 1,
            "resume after paused seek uses resume, not a duplicate play");
        Check(CountCalls(audio.Calls, "audio.play:") == playCallsBeforeSeek,
            "resume after paused seek does not duplicate play scheduling");
        clock.Advance(1.0);
        controller.Tick();
        CheckClose(6.0, controller.CueTime, 1e-9, "resumed cue clock continues");
    }

    private static void TestSequentialPlaybackMatchesDirectSeek()
    {
        var plan = CuePlaybackPlan("fixture/equivalence");

        var sequentialClock = new ManualCueClock();
        var sequentialAudio = new FakeAudioExecutor(sequentialClock);
        var sequentialVisual = new FakeVisualExecutor();
        var sequentialSubtitles = MakeSubtitles();
        var sequential = new CuePlaybackController(
            sequentialClock, sequentialAudio, sequentialVisual, sequentialSubtitles);
        Check(sequential.Prepare(plan), "prepare sequential-equivalence cue");
        Check(sequential.Play(), "play sequential-equivalence cue");
        sequentialClock.Advance(3.0);
        sequential.Tick();

        var seekClock = new ManualCueClock();
        var seekAudio = new FakeAudioExecutor(seekClock);
        var seekVisual = new FakeVisualExecutor();
        var seekSubtitles = MakeSubtitles();
        var direct = new CuePlaybackController(seekClock, seekAudio, seekVisual, seekSubtitles);
        Check(direct.Prepare(plan), "prepare direct-seek-equivalence cue");
        Check(direct.Play(), "play direct-seek-equivalence cue");
        Check(direct.SeekTo(3.0), "seek directly to same cue time");

        Check(sequential.State == direct.State, "sequential and direct seek same state");
        CheckClose(sequential.CueTime, direct.CueTime, 1e-9,
            "sequential and direct seek same cue time");
        CheckClose(sequentialVisual.LastSeekTime, seekVisual.LastSeekTime, 1e-9,
            "sequential and direct seek same visual time");
        Check(sequential.CurrentSubtitle.Visible == direct.CurrentSubtitle.Visible
              && sequential.CurrentSubtitle.Text == direct.CurrentSubtitle.Text,
            "sequential and direct seek same subtitle state");
        CheckClose(sequentialAudio.CurrentCueTime, seekAudio.CurrentCueTime, 1e-9,
            "sequential and direct seek same audio cue position");
        CheckClose(AudioSourcePositionAt(plan, sequential.CueTime),
            AudioSourcePositionAt(plan, direct.CueTime), 1e-9,
            "sequential and direct seek same audio material position");
    }

    private static void TestRapidCueSwitchDropsStaleCallbacks()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock) { AutoPrepare = false };
        var visual = new FakeVisualExecutor { AutoPrepare = false };
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());

        Check(controller.Prepare(CuePlaybackPlan("fixture/stale_a")), "prepare stale cue A");
        long generationA = controller.Generation;
        ICueExecutorCallback oldAudioCallback = audio.StoredCallback;
        ICueExecutorCallback oldVisualCallback = visual.StoredCallback;

        Check(controller.Prepare(CuePlaybackPlan("fixture/stale_b")), "switch to cue B while A prepares");
        long generationB = controller.Generation;
        Check(generationB > generationA, "cue switch advances playback generation");
        Check(controller.State == CuePlaybackState.Preparing, "cue B is preparing");
        Check(controller.Plan.cue_id == "fixture/stale_b", "controller consumes cue B plan");

        oldAudioCallback.OnPrepared(generationA);
        oldVisualCallback.OnPrepared(generationA);
        oldAudioCallback.OnFailed(generationA, "stale load failure");
        oldAudioCallback.OnCompleted(generationA);
        Check(controller.State == CuePlaybackState.Preparing,
            "stale preparation callbacks cannot make cue B ready or failed");
        Check(controller.Plan.cue_id == "fixture/stale_b", "stale callbacks cannot switch plan");

        audio.CompletePreparation(generationB);
        visual.CompletePreparation(generationB);
        Check(controller.State == CuePlaybackState.Ready, "cue B becomes ready");
        Check(controller.Play(), "play cue B");
        long playbackB = controller.Generation;

        oldAudioCallback.OnFailed(generationA, "stale runtime error");
        oldAudioCallback.OnCompleted(generationA);
        Check(controller.State == CuePlaybackState.Playing,
            "stale runtime callbacks cannot alter cue B state");
        Check(controller.CompletionCount == 0, "stale callback cannot complete cue B");
        CheckClose(0.0, controller.CueTime, 1e-9, "stale callback cannot move cue B clock");

        clock.Advance(10.0);
        controller.Tick();
        Check(controller.State == CuePlaybackState.Completed, "cue B completes on its own clock");
        Check(controller.LastCompletion != null
              && controller.LastCompletion.Generation == playbackB,
            "cue B completion uses cue B playback generation");
    }

    private static void TestCancelInvalidatesOldCallbacks()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());

        Check(controller.Prepare(CuePlaybackPlan("fixture/cancel_a")), "prepare cancel cue A");
        Check(controller.Play(), "play cancel cue A");
        long generationA = controller.Generation;
        ICueExecutorCallback oldAudioCallback = audio.StoredCallback;
        ICueExecutorCallback oldVisualCallback = visual.StoredCallback;

        Check(controller.Cancel(), "cancel cue A");
        Check(controller.State == CuePlaybackState.Cancelled, "cancel -> Cancelled");
        Check(controller.Generation > generationA, "cancel invalidates playback generation");
        Check(HasCall(audio.Calls, "audio.stop:" + generationA), "cancel stops audio");
        Check(HasCall(audio.Calls, "audio.cancel:" + generationA), "cancel clears audio scheduling");

        Check(controller.Prepare(CuePlaybackPlan("fixture/cancel_b")), "prepare cue B after cancellation");
        Check(controller.State == CuePlaybackState.Ready, "cue B ready after cancellation");
        Check(controller.Play(), "play cue B after cancellation");
        oldAudioCallback.OnFailed(generationA, "old audio error after cancel");
        oldVisualCallback.OnFailed(generationA, "old visual error after cancel");
        oldAudioCallback.OnCompleted(generationA);
        Check(controller.State == CuePlaybackState.Playing,
            "old generation callbacks cannot modify new cue");
        Check(controller.CompletionCount == 0, "old generation completion ignored");

        clock.Advance(10.0);
        controller.Tick();
        Check(controller.State == CuePlaybackState.Completed, "new cue still completes once");
        Check(controller.LastCompletion.CueId == "fixture/cancel_b",
            "completion belongs to new cue");
    }

    private static void TestAudioFailureIsExplicitFailure()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        int completionEvents = 0;
        int failureEvents = 0;
        controller.Completed += delegate { completionEvents++; };
        controller.Failed += delegate { failureEvents++; };

        Check(controller.Prepare(CuePlaybackPlan("fixture/audio_failure")), "prepare audio-failure cue");
        Check(controller.Play(), "play audio-failure cue");
        long generation = controller.Generation;
        audio.Fail(generation, "decode failed");

        Check(controller.State == CuePlaybackState.Failed, "audio executor failure -> Failed");
        Check(controller.LastFailure != null
              && controller.LastFailure.Kind == CuePlaybackFailureKind.AudioExecution
              && controller.LastFailure.Generation == generation,
            "audio failure records generation and kind");
        Check(completionEvents == 0 && controller.CompletionCount == 0,
            "audio executor failure is not natural completion");
        Check(failureEvents == 1, "audio failure emits one failure notification");
        Check(HasCall(audio.Calls, "audio.stop:" + generation),
            "audio failure stops audio execution");
    }

    private static void TestVisualFailureIsExplicitFailure()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        int completionEvents = 0;
        controller.Completed += delegate { completionEvents++; };

        Check(controller.Prepare(CuePlaybackPlan("fixture/visual_failure")), "prepare visual-failure cue");
        Check(controller.Play(), "play visual-failure cue");
        long generation = controller.Generation;
        visual.Fail(generation, "clip decode failed");

        Check(controller.State == CuePlaybackState.Failed, "visual executor failure -> Failed");
        Check(controller.LastFailure != null
              && controller.LastFailure.Kind == CuePlaybackFailureKind.VisualExecution,
            "visual failure has explicit failure kind");
        Check(completionEvents == 0 && controller.CompletionCount == 0,
            "visual failure is not natural completion");
        Check(HasCall(audio.Calls, "audio.stop:" + generation),
            "visual failure stops audio rather than silently continuing");
    }

    private static void TestMultipleCuesNoCumulativeDrift()
    {
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        var completions = new List<CuePlaybackCompletion>();
        controller.Completed += delegate(CuePlaybackCompletion completion)
        {
            completions.Add(completion);
        };

        Check(controller.Prepare(CuePlaybackPlan("fixture/series_a")), "prepare first cue");
        Check(controller.Play(), "play first cue");
        long generationA = controller.Generation;
        clock.Advance(10.0);
        controller.Tick();
        Check(controller.State == CuePlaybackState.Completed, "first cue completes");
        CheckClose(0.0, controller.CueTime - 10.0, 1e-9, "first cue exact duration");

        Check(controller.Prepare(CuePlaybackPlan("fixture/series_b")), "prepare second cue");
        Check(controller.State == CuePlaybackState.Ready, "second cue ready");
        CheckClose(0.0, controller.CueTime, 1e-9, "second cue starts at local zero");
        Check(controller.Play(), "play second cue");
        long generationB = controller.Generation;
        clock.Advance(10.0);
        controller.Tick();

        Check(controller.State == CuePlaybackState.Completed, "second cue completes");
        Check(completions.Count == 2 && controller.CompletionCount == 2,
            "two cues produce exactly two completions");
        Check(completions[0].Generation == generationA
              && completions[1].Generation == generationB
              && generationA != generationB,
            "each cue completion has its own playback generation");
        Check(completions[0].CueId == "fixture/series_a"
              && completions[1].CueId == "fixture/series_b",
            "completion cue ids are correct");
        CheckClose(10.0, completions[0].CueTime, 1e-9, "no drift in first cue completion");
        CheckClose(10.0, completions[1].CueTime, 1e-9, "no drift in second cue completion");
        Check(audioAudioPlayCallsAtZero(audio, generationA)
              && audioAudioPlayCallsAtZero(audio, generationB),
            "each cue starts audio at cue-local zero");
    }

    private static bool audioAudioPlayCallsAtZero(FakeAudioExecutor audio, long generation)
    {
        return audio.Calls.Contains("audio.play:" + generation + ":0.000");
    }


    private static CueTimelineLegacyCueInput SampleTitleInput()
    {
        var input = new CueTimelineLegacyCueInput
        {
            cue_id = "quick_intro/title_1",
            course_start = 0.0,
            runtime_duration = 2.295,
            audio_asset = "media/tts/quick/quick.intro.mp3",
            audio_start = 0.0,
            compiled_visual_duration = 3.08,
            sample_rate = 24000,
        };
        input.audio_plan.Add(new CueTimelineLegacyAudioPlanSegment
        {
            type = "clip", file = input.audio_asset, start = 0.0, end = 1.295,
        });
        input.audio_plan.Add(new CueTimelineLegacyAudioPlanSegment
        {
            type = "silence", duration = 1.0,
        });
        input.subtitles.Add(new CueTimelineSubtitle
        {
            id = "subtitle.title", cue_start = 0.0, duration = 2.295, text = "title",
        });
        return input;
    }

    private static CueTimelineLegacyCueInput SampleBodyInput()
    {
        var input = new CueTimelineLegacyCueInput
        {
            cue_id = "quick_intro/001",
            course_start = 2.295,
            runtime_duration = 9.392,
            audio_asset = "media/tts/quick/quick.intro.mp3",
            audio_start = 2.08,
            section_audio = true,
            compiled_visual_duration = 10.296,
            sample_rate = 24000,
        };
        input.subtitles.Add(new CueTimelineSubtitle
        {
            id = "subtitle.body.0", cue_start = 0.0, duration = 3.025, text = "body one",
        });
        input.subtitles.Add(new CueTimelineSubtitle
        {
            id = "subtitle.body.1", cue_start = 3.38, duration = 6.012, text = "body two",
        });
        return input;
    }

    private static CueTimelineLegacyCueInput SampleLongAnimationInput()
    {
        var input = new CueTimelineLegacyCueInput
        {
            cue_id = "quick_nobles/002",
            course_start = 160.525,
            runtime_duration = 1.66,
            audio_asset = "media/tts/quick/quick.nobles.mp3",
            audio_start = 8.32,
            section_audio = true,
            compiled_visual_duration = 6.2,
            sample_rate = 24000,
        };
        input.subtitles.Add(new CueTimelineSubtitle
        {
            id = "subtitle.nobles.002", cue_start = 0.0, duration = 1.66,
            text = "就拿取这位贵族；",
        });
        return input;
    }

    private static CueTimelineLegacyCueInput SampleCompositeInput()
    {
        var input = new CueTimelineLegacyCueInput
        {
            cue_id = "quick_take_gems/003",
            course_start = 75.977,
            runtime_duration = 9.437,
            audio_asset = "media/tts/quick/quick.take.mp3",
            audio_start = 15.835,
            section_audio = true,
            compiled_visual_duration = 9.648,
            sample_rate = 24000,
            compose_multi_segment_audio = true,
            visual_hold_seconds = 0.75,
        };
        input.subtitles.Add(new CueTimelineSubtitle
        {
            id = "subtitle.composite.0", cue_start = 0.0, duration = 4.41, text = "first",
        });
        input.subtitles.Add(new CueTimelineSubtitle
        {
            id = "subtitle.composite.1", cue_start = 4.825, duration = 4.612, text = "second",
        });
        return input;
    }

    private static void TestFeatureGateAndPlanSelection()
    {
        var gate = new CueTimelineFeatureGate();
        gate.SetAllowlist(new[] { "quick_intro/title_1", "quick_intro/001" });
        string reason;
        Check(!gate.ShouldUse("quick_intro/title_1", out reason),
            "feature flag default off keeps all cues on legacy path");
        gate.Enabled = true;
        Check(gate.ShouldUse("quick_intro/title_1", out reason),
            "flag on and allowlisted selects timeline path");
        Check(!gate.ShouldUse("quick_take_gems/003", out reason),
            "flag on but not allowlisted keeps legacy path");
        Check(!gate.ShouldUse("", out reason),
            "empty cue id never selects timeline path");

        CueTimelinePlan plan;
        string error;
        var invalid = new CueTimelineLegacyCueInput
        {
            cue_id = "quick_intro/001",
            course_start = 0.0,
            runtime_duration = 0.0,
            compiled_visual_duration = 1.0,
        };
        Check(!CueTimelineLegacyPlanFactory.TryBuild(invalid, out plan, out error)
              && !string.IsNullOrEmpty(error),
            "failed plan generation is observable and can fall back to legacy");
    }

    private static void TestLegacyPlanFactorySamples()
    {
        CueTimelinePlan plan;
        string error;

        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleTitleInput(), out plan, out error),
            "title sample plan builds: " + error);
        CueTimelineTrack titleAudio = CueTimelineTrackResolver.FindTrack(plan, "audio");
        CueTimelineTrack titleVisual = CueTimelineTrackResolver.FindTrack(plan, "visual");
        Check(titleAudio != null && titleAudio.segments.Count == 2,
            "title sample has clip + silence");
        Check(titleAudio != null && titleAudio.segments[0].type == "audio_clip"
              && titleAudio.segments[1].type == "silence",
            "title sample keeps explicit title silence");
        CheckClose(3.08, plan.course.duration, 1e-9,
            "title sample uses compiled visual duration as plan duration");
        Check(titleVisual != null && titleVisual.segments.Count == 1
              && titleVisual.segments[0].type == "animation",
            "title sample visual animation wired through TutorialAnimPlayer");
        CheckClose(2.295, titleAudio.segments[1].EndCueTime, 1e-9,
            "title audio plus silence ends before visual");

        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleBodyInput(), out plan, out error),
            "body sample plan builds: " + error);
        CueTimelineTrack bodyAudio = CueTimelineTrackResolver.FindTrack(plan, "audio");
        CueTimelineTrack bodyVisual = CueTimelineTrackResolver.FindTrack(plan, "visual");
        Check(bodyAudio != null && bodyAudio.segments.Count == 1,
            "ordinary body sample starts as one audio clip");
        CheckClose(2.08, bodyAudio.segments[0].source_start, 1e-9,
            "body audio source start preserved");
        CheckClose(2.08 + 9.392, bodyAudio.segments[0].source_end, 1e-9,
            "body audio tail is not trimmed by subtitle word timing");
        Check(bodyVisual != null && bodyVisual.segments[0].type == "animation",
            "body visual animation generated");
        CheckClose(10.296, plan.course.duration, 1e-9,
            "body sample duration uses max of runtime/audio/compiled");
        CheckClose(9.392, CueTimelineTrackResolver.FindSegment(bodyAudio, 9.391).EndCueTime,
            1e-9, "body final clip remains active up to its audio end");

        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleCompositeInput(), out plan, out error),
            "composite sample plan builds: " + error);
        CueTimelineTrack compositeAudio = CueTimelineTrackResolver.FindTrack(plan, "audio");
        CueTimelineTrack compositeVisual = CueTimelineTrackResolver.FindTrack(plan, "visual");
        Check(compositeAudio != null && compositeAudio.segments.Count == 3,
            "composite sample has multi audio + explicit silence");
        Check(compositeAudio.segments[0].type == "audio_clip"
              && compositeAudio.segments[1].type == "silence"
              && compositeAudio.segments[2].type == "audio_clip",
            "composite audio segment order is clip/silence/clip");
        Check(compositeVisual != null && compositeVisual.segments.Count == 2
              && compositeVisual.segments[0].type == "animation"
              && compositeVisual.segments[1].type == "hold",
            "composite visual is one animation segment plus hold");
        CheckClose(10.398, plan.course.duration, 1e-9,
            "composite plan duration includes explicit visual hold");
        CheckClose(9.437, compositeAudio.segments[2].EndCueTime, 1e-9,
            "composite audio ends before visual hold ends");
        CheckClose(10.398, compositeVisual.segments[1].EndCueTime, 1e-9,
            "composite visual hold reaches cue end");
        CheckClose(15.835 + 9.437, compositeAudio.segments[2].source_end, 1e-9,
            "composite final source_end remains the legacy speech boundary");
        Check(CueTimelineTrackResolver.FindSegment(compositeAudio, 4.41).type == "silence",
            "composite silence boundary resolves to silence");
        Check(CueTimelineTrackResolver.FindSegment(compositeVisual, 9.75).type == "hold",
            "composite visual hold boundary resolves to hold");
    }

    private static void TestPlayAtPausedAndAudioSync()
    {
        CueTimelinePlan plan;
        string error;
        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleCompositeInput(), out plan, out error),
            "prepare PlayAt sample: " + error);

        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock);
        var visual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(clock, audio, visual, MakeSubtitles());
        Check(controller.Prepare(plan), "PlayAt sample prepared");
        Check(controller.State == CuePlaybackState.Ready, "PlayAt sample ready");
        long prepareGeneration = controller.Generation;
        Check(controller.PlayAt(5.0, true), "paused PlayAt accepted at target");
        long playbackGeneration = controller.Generation;
        Check(controller.State == CuePlaybackState.Paused,
            "paused PlayAt leaves controller paused");
        Check(HasCall(audio.Calls, "audio.seek:" + playbackGeneration + ":5.000"),
            "paused PlayAt positions audio at cue-local target");
        Check(CountCalls(audio.Calls, "audio.play:" + playbackGeneration + ":") == 0,
            "paused PlayAt never starts audio");
        CheckClose(5.0, visual.LastSeekTime, 1e-9,
            "paused PlayAt updates visual to same cue-local target");
        Check(playbackGeneration > prepareGeneration,
            "PlayAt advances playback generation");

        Check(controller.Resume(), "resume after paused PlayAt");
        clock.Advance(1.0);
        controller.Tick();
        Check(audio.LastSyncCueTime == 6.0,
            "audio sync receives authoritative controller cue time");
        Check(HasCall(audio.Calls, "audio.sync:" + controller.Generation + ":6.000"),
            "audio sync call carries playback generation");
        CheckClose(6.0, controller.CueTime, 1e-9,
            "controller cue time remains the only advancing clock");
    }

    private static void TestSampleControllerSequences()
    {
        CueTimelinePlan title;
        CueTimelinePlan body;
        CueTimelinePlan composite;
        string error;
        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleTitleInput(), out title, out error),
            "title sequence plan: " + error);
        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleBodyInput(), out body, out error),
            "body sequence plan: " + error);
        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleCompositeInput(), out composite, out error),
            "composite sequence plan: " + error);

        var titleClock = new ManualCueClock();
        var titleAudio = new FakeAudioExecutor(titleClock);
        var titleVisual = new FakeVisualExecutor();
        var titleController = new CuePlaybackController(
            titleClock, titleAudio, titleVisual, new CueTimelineSubtitleExecutor());
        Check(titleController.Prepare(title), "title sequence prepare");
        Check(titleController.State == CuePlaybackState.Ready, "title sequence ready");
        Check(titleController.Play(), "title sequence play");
        Check(HasCall(titleAudio.Calls, "audio.prepare:") && HasCall(titleVisual.Calls, "visual.prepare:"),
            "title sequence prepares audio and visual before play");
        Check(titleAudio.Calls.Contains("audio.play:" + titleController.Generation + ":0.000"),
            "title sequence starts audio at cue-local zero");
        titleClock.Advance(1.5);
        titleController.Tick();
        CueTimelineSegment titleAtSilence = CueTimelineTrackResolver.FindSegment(
            CueTimelineTrackResolver.FindTrack(title, "audio"), titleController.CueTime);
        Check(titleAtSilence != null && titleAtSilence.type == "silence",
            "title sequence reaches authored silence before next cue");
        titleClock.Advance(1.58);
        titleController.Tick();
        Check(titleController.State == CuePlaybackState.Completed,
            "title sequence completes on controller duration");

        // The next allowlisted cue starts from cue-local zero.  Its material
        // start (2.080s in the shared MP3) is explicit in the plan and is not
        // derived from the title's subtitle word timing.
        Check(titleController.Prepare(body), "next body cue after title prepares");
        Check(titleController.Play(), "next body cue after title plays");
        CueTimelineSegment nextFirstClip = CueTimelineTrackResolver.FindSegment(
            CueTimelineTrackResolver.FindTrack(body, "audio"), 0.0);
        Check(nextFirstClip != null && nextFirstClip.type == "audio_clip",
            "next cue first-word time is inside its audio clip");
        CheckClose(2.08, nextFirstClip.source_start, 1e-9,
            "next cue first word source range is preserved");
        Check(titleAudio.LastRequestedCueTime == 0.0,
            "next cue audio is scheduled at its own cue-local zero");

        var bodyClock = new ManualCueClock();
        var bodyAudio = new FakeAudioExecutor(bodyClock);
        var bodyVisual = new FakeVisualExecutor();
        var bodyController = new CuePlaybackController(
            bodyClock, bodyAudio, bodyVisual, new CueTimelineSubtitleExecutor());
        Check(bodyController.Prepare(body), "body sequence prepare");
        Check(bodyController.Play(), "body sequence play");
        bodyClock.Advance(9.5);
        bodyController.Tick();
        Check(bodyController.State == CuePlaybackState.Playing,
            "body cue still plays through tail after speech");
        Check(bodyAudio.LastRequestedCueTime == 0.0,
            "body audio was started at cue-local zero, not a subtitle word end");
        CheckClose(9.5, bodyAudio.LastSyncCueTime, 1e-9,
            "body audio segment selection follows authoritative tail cue time");
        bodyClock.Advance(0.8);
        bodyController.Tick();
        Check(bodyController.State == CuePlaybackState.Completed,
            "body sequence completes only at planned cue duration");

        var compositeClock = new ManualCueClock();
        var compositeAudio = new FakeAudioExecutor(compositeClock);
        var compositeVisual = new FakeVisualExecutor();
        var compositeController = new CuePlaybackController(
            compositeClock, compositeAudio, compositeVisual, new CueTimelineSubtitleExecutor());
        Check(compositeController.Prepare(composite), "composite sequence prepare");
        Check(compositeController.Play(), "composite sequence play");
        Check(compositeController.SeekTo(4.41), "composite seek into silence");
        Check(CueTimelineTrackResolver.FindSegment(
                CueTimelineTrackResolver.FindTrack(composite, "audio"),
                compositeController.CueTime).type == "silence",
            "composite controller stays on cue-local silence");
        Check(compositeController.SeekTo(9.75), "composite seek into visual hold");
        CueTimelineSegment hold = CueTimelineTrackResolver.FindSegment(
            CueTimelineTrackResolver.FindTrack(composite, "visual"), compositeController.CueTime);
        Check(hold != null && hold.type == "hold",
            "composite controller resolves visual hold from same cue time");
        Check(compositeController.SeekTo(10.398), "composite seek to cue boundary");
        Check(compositeController.State == CuePlaybackState.Completed,
            "composite cue completes only at planned boundary");
    }

    private static void TestAnimationLongerThanAudioUsesPlanDuration()
    {
        CueTimelinePlan plan;
        string error;
        Check(CueTimelineLegacyPlanFactory.TryBuild(
                SampleLongAnimationInput(), out plan, out error),
            "long-animation sample plan builds: " + error);

        CueTimelineTrack audio = CueTimelineTrackResolver.FindTrack(plan, "audio");
        CueTimelineTrack visual = CueTimelineTrackResolver.FindTrack(plan, "visual");
        CheckClose(6.2, plan.course.duration, 1e-9,
            "animation-longer-than-audio cue duration comes from compiled visual timeline");
        Check(audio != null && audio.segments.Count == 1,
            "long-animation sample keeps its short audio segment");
        CheckClose(1.66, audio.segments[0].EndCueTime, 1e-9,
            "audio ends before the visual timeline");
        Check(visual != null && visual.segments.Count == 1
              && visual.segments[0].type == "animation",
            "long-animation sample is a single animation segment, not a wait segment");
        CheckClose(6.2, visual.segments[0].EndCueTime, 1e-9,
            "visual animation itself reaches the cue duration");

        var clock = new ManualCueClock();
        var fakeAudio = new FakeAudioExecutor(clock);
        var fakeVisual = new FakeVisualExecutor();
        var controller = new CuePlaybackController(
            clock, fakeAudio, fakeVisual, new CueTimelineSubtitleExecutor());
        int completions = 0;
        controller.Completed += delegate { completions++; };
        Check(controller.Prepare(plan), "long-animation cue prepare");
        Check(controller.Play(), "long-animation cue play");
        clock.Advance(1.66);
        controller.Tick();
        Check(controller.State == CuePlaybackState.Playing,
            "audio completion does not cut the longer visual timeline");
        Check(completions == 0, "no completion at audio end");
        clock.Advance(4.54);
        controller.Tick();
        Check(controller.State == CuePlaybackState.Completed,
            "cue completes only when the visual timeline reaches its end");
        Check(completions == 1, "long-animation cue completes exactly once");
    }

    private static void TestFailureCannotAutoAdvance()
    {
        CueTimelinePlan plan;
        string error;
        Check(CueTimelineLegacyPlanFactory.TryBuild(SampleBodyInput(), out plan, out error),
            "failure sample plan: " + error);
        var clock = new ManualCueClock();
        var audio = new FakeAudioExecutor(clock) { AutoPrepare = false };
        var visual = new FakeVisualExecutor { AutoPrepare = false };
        var controller = new CuePlaybackController(clock, audio, visual, new CueTimelineSubtitleExecutor());
        int advanceSignals = 0;
        controller.Completed += delegate { advanceSignals++; };
        Check(controller.Prepare(plan), "failure sample prepare");
        long generation = controller.Generation;
        visual.Fail(generation, "visual load failed");
        Check(controller.State == CuePlaybackState.Failed,
            "visual load failure enters Failed");
        clock.Advance(20.0);
        controller.Tick();
        Check(controller.CompletionCount == 0 && advanceSignals == 0,
            "load failure cannot auto-advance as if the cue completed");
    }

    private sealed class SubtitleWindow
    {
        public double Start;
        public double End;
        public string Text;
    }

    private sealed class FakeSubtitleExecutor : ICueSubtitleExecutor
    {
        public readonly List<SubtitleWindow> Windows = new List<SubtitleWindow>();
        public double LastCueTime = -1.0;
        public CueSubtitleState LastState;

        public CueSubtitleState GetState(CueTimelinePlan plan, double cueTime)
        {
            LastCueTime = cueTime;
            for (int i = 0; i < Windows.Count; i++)
            {
                var window = Windows[i];
                if (cueTime + 1e-9 >= window.Start && cueTime < window.End - 1e-9)
                {
                    LastState = new CueSubtitleState
                    {
                        Visible = true,
                        Text = window.Text,
                        SegmentId = "subtitle." + i,
                    };
                    return LastState;
                }
            }
            LastState = CueSubtitleState.Hidden();
            return LastState;
        }
    }

    private sealed class FakeAudioExecutor : IAudioCueExecutor, IAudioCueExecutorTimeline
    {
        private readonly ManualCueClock clock;
        private double positionAtBase;
        private double clockAtBase;

        public readonly List<string> Calls = new List<string>();
        public ICueExecutorCallback StoredCallback;
        public long StoredGeneration;
        public CueTimelinePlan Plan;
        public bool AutoPrepare = true;
        public bool IsPlaying;
        public double LastRequestedCueTime = -1.0;
        public double LastSyncCueTime = -1.0;

        public FakeAudioExecutor(ManualCueClock clock)
        {
            this.clock = clock;
        }

        public double CurrentCueTime
        {
            get
            {
                if (!IsPlaying) return positionAtBase;
                return positionAtBase + (clock.NowSeconds - clockAtBase);
            }
        }

        public void Prepare(CueTimelinePlan plan, long generation, ICueExecutorCallback callback)
        {
            Plan = plan;
            StoredCallback = callback;
            StoredGeneration = generation;
            Calls.Add("audio.prepare:" + generation);
            if (AutoPrepare) callback.OnPrepared(generation);
        }

        public void Play(long generation, double cueTime)
        {
            Calls.Add("audio.play:" + generation + ":" + Fmt(cueTime));
            LastRequestedCueTime = cueTime;
            positionAtBase = cueTime;
            clockAtBase = clock.NowSeconds;
            IsPlaying = true;
        }

        public void Pause(long generation)
        {
            if (IsPlaying) positionAtBase = CurrentCueTime;
            IsPlaying = false;
            Calls.Add("audio.pause:" + generation);
        }

        public void Resume(long generation, double cueTime)
        {
            Calls.Add("audio.resume:" + generation + ":" + Fmt(cueTime));
            LastRequestedCueTime = cueTime;
            positionAtBase = cueTime;
            clockAtBase = clock.NowSeconds;
            IsPlaying = true;
        }

        public void Seek(long generation, double cueTime)
        {
            Calls.Add("audio.seek:" + generation + ":" + Fmt(cueTime));
            LastRequestedCueTime = cueTime;
            positionAtBase = cueTime;
            clockAtBase = clock.NowSeconds;
            IsPlaying = false;
        }

        public void Stop(long generation)
        {
            Calls.Add("audio.stop:" + generation);
            if (IsPlaying) positionAtBase = CurrentCueTime;
            IsPlaying = false;
        }

        public void Cancel(long generation)
        {
            Calls.Add("audio.cancel:" + generation);
            if (IsPlaying) positionAtBase = CurrentCueTime;
            IsPlaying = false;
        }

        public void SyncToCueTime(long generation, double cueTime)
        {
            LastSyncCueTime = cueTime;
            Calls.Add("audio.sync:" + generation + ":" + Fmt(cueTime));
        }

        public void CompletePreparation(long generation)
        {
            if (StoredCallback != null) StoredCallback.OnPrepared(generation);
        }

        public void FailPreparation(long generation, string message)
        {
            if (StoredCallback != null) StoredCallback.OnFailed(generation, message);
        }

        public void Complete(long generation)
        {
            Calls.Add("audio.callback.completed:" + generation);
            IsPlaying = false;
            if (StoredCallback != null) StoredCallback.OnCompleted(generation);
        }

        public void Fail(long generation, string message)
        {
            Calls.Add("audio.callback.failed:" + generation);
            IsPlaying = false;
            if (StoredCallback != null) StoredCallback.OnFailed(generation, message);
        }
    }

    private sealed class FakeVisualExecutor : IVisualCueExecutor
    {
        public readonly List<string> Calls = new List<string>();
        public ICueExecutorCallback StoredCallback;
        public long StoredGeneration;
        public CueTimelinePlan Plan;
        public bool AutoPrepare = true;
        public double LastSeekTime = -1.0;

        public void Prepare(CueTimelinePlan plan, long generation, ICueExecutorCallback callback)
        {
            Plan = plan;
            StoredCallback = callback;
            StoredGeneration = generation;
            Calls.Add("visual.prepare:" + generation);
            if (AutoPrepare) callback.OnPrepared(generation);
        }

        public void Seek(long generation, double cueTime)
        {
            Calls.Add("visual.seek:" + generation + ":" + Fmt(cueTime));
            LastSeekTime = cueTime;
        }

        public void Pause(long generation)
        {
            Calls.Add("visual.pause:" + generation);
        }

        public void Resume(long generation)
        {
            Calls.Add("visual.resume:" + generation);
        }

        public void Stop(long generation)
        {
            Calls.Add("visual.stop:" + generation);
        }

        public void Cancel(long generation)
        {
            Calls.Add("visual.cancel:" + generation);
        }

        public void CompletePreparation(long generation)
        {
            if (StoredCallback != null) StoredCallback.OnPrepared(generation);
        }

        public void Fail(long generation, string message)
        {
            Calls.Add("visual.callback.failed:" + generation);
            if (StoredCallback != null) StoredCallback.OnFailed(generation, message);
        }
    }

    public static int Main()
    {
        TestLogicalStateAndCamera();
        TestMoveAndFlip();
        TestShuffle();
        TestOverlayShowHideAndModifier();
        TestAnnotationsAndMagnifier();
        TestSameTimeCompiledOrderIsStable();

        TestCueTimelinePlanSetValidation();
        TestCueStateMachineAndReadyGating();
        TestPreparationFailureIsNotCompletion();
        TestCueClockAdvancesAndPauses();
        TestDurationCompletesOnce();
        TestAudioCompletionSignalDoesNotCompleteCue();
        TestSeekCategoriesAndReplayCalls();
        TestPausedSeekResumeCallSequence();
        TestSequentialPlaybackMatchesDirectSeek();
        TestRapidCueSwitchDropsStaleCallbacks();
        TestCancelInvalidatesOldCallbacks();
        TestAudioFailureIsExplicitFailure();
        TestVisualFailureIsExplicitFailure();
        TestMultipleCuesNoCumulativeDrift();
        TestFeatureGateAndPlanSelection();
        TestLegacyPlanFactorySamples();
        TestPlayAtPausedAndAudioSync();
        TestSampleControllerSequences();
        TestAnimationLongerThanAudioUsesPlanDuration();
        TestFailureCannotAutoAdvance();

        if (failures == 0)
        {
            Console.WriteLine("OK   AnimationModelTests: all checks passed");
            return 0;
        }
        Console.WriteLine("FAIL AnimationModelTests: " + failures + " check(s) failed");
        return 1;
    }
}
