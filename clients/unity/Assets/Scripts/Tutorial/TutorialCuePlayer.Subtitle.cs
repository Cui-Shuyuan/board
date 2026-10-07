using System;
// BoardGameTutorial
// 音频/字幕/导航外壳（v2-only）。
//
// 动画本身由 BoardGameTutorial.Animation.TutorialAnimPlayer 提供：
// 运行时只读 {track}.compiled.json，按 audioSource.time 调 Seek(t)。
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BoardGameTutorial.Animation;
using UnityEngine;
using UnityEngine.Networking;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BoardGameTutorial
{
    public partial class TutorialCuePlayer : MonoBehaviour
    {
        private void DrawSubtitle()
        {
            string text = SubtitleText();
            if (string.IsNullOrEmpty(text)) return;

            if (subtitleStyle == null)
            {
                subtitleStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 34
                };
                subtitleStyle.normal.textColor = Color.white;

                // 不要从 subtitleStyle 复制后再改色，避免 GUIStyleState 被共享。
                subtitleOutlineStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    fontSize = 34
                };
                subtitleOutlineStyle.normal.textColor = Color.black;
            }

            float width = Mathf.Min(1100f, Screen.width - 64f);
            float height = 120f;
            float x = (Screen.width - width) * 0.5f;
            float bottomInset = 28f;
            var touchControls = GetComponent<TutorialTouchControls>();
            if (touchControls != null && touchControls.enabled)
                bottomInset += touchControls.PanelHeightPixels;
            float y = Screen.height - height - bottomInset;

            foreach (var offset in SubtitleOutlineOffsets)
                GUI.Label(new Rect(x + offset.x, y + offset.y, width, height), text, subtitleOutlineStyle);
            GUI.Label(new Rect(x, y, width, height), text, subtitleStyle);
        }

        private string SubtitleText()
        {
            if (!string.IsNullOrEmpty(currentSubtitle)) return currentSubtitle;
            var cue = CurrentCue;
            if (cue == null) return "";
            if (cue.subtitles == null || cue.subtitles.Count == 0) return cue.text ?? "";
            return "";
        }
    }
}
