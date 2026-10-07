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

    public static int Main()
    {
        TestLogicalStateAndCamera();
        TestMoveAndFlip();
        TestShuffle();
        TestOverlayShowHideAndModifier();
        TestAnnotationsAndMagnifier();
        TestSameTimeCompiledOrderIsStable();

        if (failures == 0)
        {
            Console.WriteLine("OK   AnimationModelTests: all checks passed");
            return 0;
        }
        Console.WriteLine("FAIL AnimationModelTests: " + failures + " check(s) failed");
        return 1;
    }
}
