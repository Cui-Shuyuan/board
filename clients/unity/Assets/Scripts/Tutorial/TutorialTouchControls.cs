// BoardGameTutorial
// 临时移动端触控控制层：底部半透明面板 + 大尺寸触控按钮。
//
// 仅用于 Unity 验证 APK 的真机交互验收；不是原生 Android UI，也不接后端。
// 触摸输入优先走 Input System 的 Pointer.current，同时保留旧 Input 轮询作为无 Input
// System 时的兜底。按钮由 OnGUI 绘制，但点击判定不依赖 GUI.Button，避免 Android
// 触摸事件在 IMGUI 下不生效。
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace BoardGameTutorial
{
    public class TutorialTouchControls : MonoBehaviour
    {
        [Header("Step sizes")]
        public float seekStep = 15f;
        public float volumeStep = 0.1f;

        private TutorialCuePlayer player;
        private Rect panelRect;
        private Rect statusRect;
        private readonly Rect[] buttonRects = new Rect[5];
        private readonly string[] buttonLabels = new string[5];
        private int activeButton = -1;
        private Texture2D panelTexture;
        private Texture2D buttonTexture;
        private Texture2D buttonPressedTexture;
        private GUIStyle buttonStyle;
        private GUIStyle statusStyle;

        /// <summary>
        /// 控制条像素高度；TutorialCuePlayer 用它把字幕上移，避免被面板遮挡。
        /// </summary>
        public float PanelHeightPixels
        {
            get { return Mathf.Clamp(Screen.height * 0.18f, 150f, 260f); }
        }

        private void Awake()
        {
            player = GetComponent<TutorialCuePlayer>();
        }

        public void Bind(TutorialCuePlayer owner)
        {
            player = owner;
        }

        private void OnEnable()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        private void OnDisable()
        {
            RestoreSleepTimeout();
        }

        private void OnApplicationQuit()
        {
            RestoreSleepTimeout();
        }

        private void OnApplicationPause(bool paused)
        {
            // 应用退到后台时恢复系统息屏策略；回到前台且控制层仍启用时再保持常亮。
            if (paused) Screen.sleepTimeout = SleepTimeout.SystemSetting;
            else if (enabled) Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        private void RestoreSleepTimeout()
        {
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
        }

        private bool inputSystemHandledPointer;
        private bool inputSystemHandledRelease;

        private void Update()
        {
            RefreshLayout();
            inputSystemHandledPointer = false;
            inputSystemHandledRelease = false;
#if ENABLE_INPUT_SYSTEM
            PollInputSystem();
#else
            PollLegacyInput();
#endif
        }

#if ENABLE_INPUT_SYSTEM
        private void PollInputSystem()
        {
            // Android 触摸必须读 primaryTouch。Touchscreen 顶层的 Pointer.press/position
            // 是 stateFrom primaryTouch 的合成控件，直接拿 Pointer.current.press 的
            // wasPressedThisFrame 在部分 Unity / Input System 版本上不可靠。
            var touchscreen = Touchscreen.current;
            if (touchscreen != null)
            {
                var touch = touchscreen.primaryTouch;
                Vector2 touchPosition = ToGuiPosition(touch.position.ReadValue());
                if (touch.press.wasPressedThisFrame)
                {
                    inputSystemHandledPointer = true;
                    OnPointerDown(touchPosition);
                }
                if (touch.press.wasReleasedThisFrame)
                {
                    inputSystemHandledRelease = true;
                    OnPointerUp(touchPosition);
                }
                return;
            }

            var pointer = Pointer.current;
            if (pointer == null) return;
            Vector2 position = ToGuiPosition(pointer.position.ReadValue());
            if (pointer.press.wasPressedThisFrame)
            {
                inputSystemHandledPointer = true;
                OnPointerDown(position);
            }
            if (pointer.press.wasReleasedThisFrame)
            {
                inputSystemHandledRelease = true;
                OnPointerUp(position);
            }
        }
#else
        private void PollLegacyInput()
        {
            if (Input.GetMouseButtonDown(0))
                OnPointerDown(ToGuiPosition(new Vector2(Input.mousePosition.x, Input.mousePosition.y)));
            if (Input.GetMouseButtonUp(0))
                OnPointerUp(ToGuiPosition(new Vector2(Input.mousePosition.x, Input.mousePosition.y)));
        }
#endif

        private void OnPointerDown(Vector2 position)
        {
            activeButton = HitTest(position);
            Debug.Log($"[TutorialTouchControls] pointer down pos={position} hit={activeButton}");
        }

        private static Vector2 ToGuiPosition(Vector2 screenPosition)
        {
            // Input System / Input.mousePosition 使用屏幕坐标（原点左下），
            // OnGUI 和 Event.mousePosition 使用 GUI 坐标（原点左上）。
            return new Vector2(screenPosition.x, Screen.height - screenPosition.y);
        }

        private void OnPointerUp(Vector2 position)
        {
            if (activeButton >= 0 && IsInside(buttonRects[activeButton], position))
                InvokeButton(activeButton);
            activeButton = -1;
        }

        private int HitTest(Vector2 position)
        {
            for (int i = 0; i < buttonRects.Length; i++)
            {
                if (IsInside(buttonRects[i], position)) return i;
            }
            return -1;
        }

        private static bool IsInside(Rect rect, Vector2 position)
        {
            return position.x >= rect.x && position.x <= rect.x + rect.width
                && position.y >= rect.y && position.y <= rect.y + rect.height;
        }

        private void InvokeButton(int index)
        {
            if (player == null) player = GetComponent<TutorialCuePlayer>();
            if (player == null) return;

            Debug.Log($"[TutorialTouchControls] tap button {index}: {buttonLabels[index]}");
            switch (index)
            {
                case 0: player.TogglePause(); break;
                case 1: player.SeekRelative(-seekStep); break;
                case 2: player.SeekRelative(seekStep); break;
                case 3: player.AdjustVolume(-volumeStep); break;
                case 4: player.AdjustVolume(volumeStep); break;
            }
        }

        private void RefreshLayout()
        {
            float panelHeight = PanelHeightPixels;
            panelRect = new Rect(0f, Screen.height - panelHeight, Screen.width, panelHeight);

            float pad = Mathf.Clamp(panelHeight * 0.08f, 8f, 18f);
            float statusHeight = Mathf.Clamp(panelHeight * 0.26f, 26f, 46f);
            statusRect = new Rect(pad, panelRect.y + pad * 0.4f, Screen.width - pad * 2f, statusHeight);

            float buttonsTop = statusRect.y + statusRect.height + pad * 0.4f;
            float buttonsHeight = Mathf.Max(48f, panelRect.y + panelRect.height - pad * 0.6f - buttonsTop);
            float gap = Mathf.Clamp(Screen.width * 0.008f, 6f, 16f);
            float buttonWidth = (Screen.width - gap * 6f) / 5f;
            if (buttonWidth < 56f) buttonWidth = 56f;

            float x = gap;
            for (int i = 0; i < buttonRects.Length; i++)
            {
                buttonRects[i] = new Rect(x, buttonsTop, buttonWidth, buttonsHeight);
                x += buttonWidth + gap;
            }

            buttonLabels[0] = player != null && player.IsPaused ? "继续" : "暂停";
            buttonLabels[1] = "-15 秒";
            buttonLabels[2] = "+15 秒";
            buttonLabels[3] = "音量 -";
            buttonLabels[4] = "音量 +";
        }

        private void OnGUI()
        {
            if (!enabled) return;
            EnsureResources();
            RefreshLayout();
            HandleImGuiFallback();

            GUI.DrawTexture(panelRect, panelTexture);
            GUI.Label(statusRect, BuildStatusText(), statusStyle);

            for (int i = 0; i < buttonRects.Length; i++)
            {
                GUI.DrawTexture(buttonRects[i], activeButton == i ? buttonPressedTexture : buttonTexture);
                float fontSize = Mathf.Clamp(Mathf.Min(buttonRects[i].width * 0.22f, buttonRects[i].height * 0.32f), 16f, 42f);
                buttonStyle.fontSize = Mathf.RoundToInt(fontSize);
                GUI.Label(buttonRects[i], buttonLabels[i], buttonStyle);
            }
        }

        private void HandleImGuiFallback()
        {
            // Input System 没有拿到 press/release 时，再用 IMGUI 的鼠标事件兜底。
            // 仍不使用 GUI.Button；这里只做命中测试。
            if (inputSystemHandledPointer || inputSystemHandledRelease) return;
            Event evt = Event.current;
            if (evt == null) return;
            if (evt.type == EventType.MouseDown)
                OnPointerDown(evt.mousePosition);
            else if (evt.type == EventType.MouseUp)
                OnPointerUp(evt.mousePosition);
        }

        private void EnsureResources()
        {
            if (panelTexture == null)
                panelTexture = CreateSolidTexture(new Color(0f, 0f, 0f, 0.58f));
            if (buttonTexture == null)
                buttonTexture = CreateSolidTexture(new Color(0.08f, 0.12f, 0.2f, 0.9f));
            if (buttonPressedTexture == null)
                buttonPressedTexture = CreateSolidTexture(new Color(0.15f, 0.45f, 0.85f, 0.95f));

            if (buttonStyle == null)
            {
                buttonStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                    normal = { textColor = Color.white }
                };
            }

            if (statusStyle == null)
            {
                statusStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false,
                    normal = { textColor = new Color(0.9f, 0.95f, 1f, 1f) }
                };
                statusStyle.fontSize = 22;
            }
        }

        private string BuildStatusText()
        {
            if (player == null) return "触控控制";
            int total = player.TotalCueCount;
            int current = total > 0 ? Mathf.Clamp(player.CurrentCueIndex + 1, 1, total) : 0;
            string state = player.IsPaused ? "已暂停" : (player.IsPlaying ? "播放中" : "停止/加载中");
            return $"cue {current}/{total}    {state}    音量 {Mathf.RoundToInt(player.Volume * 100f)}%";
        }

        private static Texture2D CreateSolidTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            return texture;
        }
    }
}
