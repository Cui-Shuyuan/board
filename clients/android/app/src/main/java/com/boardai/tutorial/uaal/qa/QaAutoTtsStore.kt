package com.boardai.tutorial.uaal.qa

import android.content.Context

interface QaAutoTtsStore {
    fun isEnabled(): Boolean
    fun setEnabled(enabled: Boolean)
}

class SharedPrefsQaAutoTtsStore(context: Context) : QaAutoTtsStore {
    private val prefs = context.applicationContext.getSharedPreferences(
        QA_PREFS,
        Context.MODE_PRIVATE
    )

    override fun isEnabled(): Boolean = prefs.getBoolean(KEY_AUTO_TTS, true)

    override fun setEnabled(enabled: Boolean) {
        prefs.edit()
            .putBoolean(KEY_AUTO_TTS, enabled)
            .apply()
    }

    private companion object {
        const val QA_PREFS = "qa_prefs"
        const val KEY_AUTO_TTS = "auto_tts_enabled"
    }
}
