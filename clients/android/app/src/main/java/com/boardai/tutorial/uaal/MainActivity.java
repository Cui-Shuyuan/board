package com.boardai.tutorial.uaal;

import android.graphics.Color;
import android.os.Bundle;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.FrameLayout;

import com.unity3d.player.UnityPlayer;
import com.unity3d.player.UnityPlayerGameActivity;

/**
 * Native Android shell for the UaaL POC.
 *
 * Unity 6 exports {@code com.unity3d.player.UnityPlayerGameActivity}; this class
 * extends it instead of the older {@code UnityPlayerActivity}. After Unity has
 * created its surface view, it adds a normal Android Button on top. The button
 * talks to Unity through the stable {@code AndroidTutorialBridge} GameObject.
 */
public class MainActivity extends UnityPlayerGameActivity {
    private static final String BRIDGE_OBJECT = "AndroidTutorialBridge";
    private static final String BRIDGE_METHOD = "TogglePause";

    private Button pauseButton;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        addPauseOverlay();
    }

    private void addPauseOverlay() {
        pauseButton = new Button(this);
        pauseButton.setText("暂停");
        pauseButton.setAllCaps(false);
        pauseButton.setTextColor(Color.WHITE);
        pauseButton.setBackgroundColor(Color.argb(220, 20, 80, 180));
        pauseButton.setElevation(dp(6));
        pauseButton.setPadding(dp(18), dp(8), dp(18), dp(8));
        pauseButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                UnityPlayer.UnitySendMessage(BRIDGE_OBJECT, BRIDGE_METHOD, "");
                pauseButton.setText("暂停".contentEquals(pauseButton.getText()) ? "继续" : "暂停");
            }
        });

        FrameLayout.LayoutParams params = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT);
        params.gravity = Gravity.TOP | Gravity.END;
        params.setMargins(0, dp(24), dp(24), 0);

        FrameLayout content = findViewById(android.R.id.content);
        if (content != null) {
            content.addView(pauseButton, params);
        } else {
            // Should not happen for a normal Activity, but keep the POC robust.
            addContentView(pauseButton, params);
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
