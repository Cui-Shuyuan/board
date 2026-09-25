package com.boardai.tutorial.uaal.content

import androidx.compose.runtime.mutableStateOf

/**
 * UI-facing update state.  The updater reports progress through these values;
 * MainActivity always writes them on the Android main thread so Compose can
 * recompose safely.
 */
data class ContentUpdateUiState(
    val activeVersion: String? = null,
    val status: ContentUpdateStatus = ContentUpdateStatus.Idle
)

sealed interface ContentUpdateStatus {
    data object Idle : ContentUpdateStatus
    data object Checking : ContentUpdateStatus
    data class Downloading(
        val version: String,
        val completed: Int,
        val total: Int,
        val currentPath: String
    ) : ContentUpdateStatus

    data class UpToDate(val version: String) : ContentUpdateStatus
    data class Updated(val version: String) : ContentUpdateStatus
    data class Failed(val message: String) : ContentUpdateStatus
}

object ContentUpdateStateHolder {
    val state = mutableStateOf(ContentUpdateUiState())
}

fun ContentUpdateStatus.toDisplayText(): String = when (this) {
    ContentUpdateStatus.Idle -> "未检查"
    ContentUpdateStatus.Checking -> "检查更新中"
    is ContentUpdateStatus.Downloading ->
        if (total <= 0) "下载中" else "下载中 $completed/$total · $currentPath"
    is ContentUpdateStatus.UpToDate -> "已是最新"
    is ContentUpdateStatus.Updated -> "已更新"
    is ContentUpdateStatus.Failed -> "更新失败：$message"
}

data class ContentUpdateResult(
    val status: ContentUpdateStatus,
    val versionRoot: java.io.File? = null,
    val gameRoot: java.io.File? = null,
    val changed: Boolean = false,
    val error: Throwable? = null
)
