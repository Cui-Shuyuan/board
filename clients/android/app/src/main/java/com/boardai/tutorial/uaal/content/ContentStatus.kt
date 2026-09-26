package com.boardai.tutorial.uaal.content

import com.boardai.tutorial.uaal.catalog.GameCatalogEntry

/**
 * Per-game resource state shown on the home screen and in the resource manager.
 */
sealed interface ContentStatus {
    data object NoServerResource : ContentStatus
    data object NotDownloaded : ContentStatus
    data class Paused(val progress: PausedContent) : ContentStatus
    data class InstalledOffline(val version: String) : ContentStatus
    data class InstalledCurrent(val version: String) : ContentStatus
    data class UpdateAvailable(
        val localVersion: String,
        val serverVersion: String
    ) : ContentStatus

    data class UpdatePaused(
        val localVersion: String,
        val serverVersion: String,
        val progress: PausedContent
    ) : ContentStatus
}

val ContentStatus.hasLocalContent: Boolean
    get() = when (this) {
        ContentStatus.NoServerResource,
        ContentStatus.NotDownloaded,
        is ContentStatus.Paused -> false
        is ContentStatus.InstalledOffline,
        is ContentStatus.InstalledCurrent,
        is ContentStatus.UpdateAvailable,
        is ContentStatus.UpdatePaused -> true
    }

fun ContentStatus.shortCardText(): String = when (this) {
    ContentStatus.NoServerResource -> "暂无资源"
    ContentStatus.NotDownloaded -> "未下载"
    is ContentStatus.Paused -> "已暂停 ${progress.percent}%"
    is ContentStatus.InstalledOffline -> "已安装"
    is ContentStatus.InstalledCurrent -> "已是最新"
    is ContentStatus.UpdateAvailable -> "可更新到 v${serverVersion.take(8)}"
    is ContentStatus.UpdatePaused -> "更新暂停 ${progress.percent}%"
}

fun ContentStatus.resourceStatusText(): String = when (this) {
    ContentStatus.NoServerResource -> "暂无教程资源"
    ContentStatus.NotDownloaded -> "未下载"
    is ContentStatus.Paused -> "已暂停 ${progress.percent}%"
    is ContentStatus.InstalledOffline -> "已安装（服务端当前无资源信息）"
    is ContentStatus.InstalledCurrent -> "已是最新"
    is ContentStatus.UpdateAvailable -> "可更新到 v${serverVersion.take(8)}"
    is ContentStatus.UpdatePaused -> "更新暂停 ${progress.percent}%"
}

fun ContentStatus.localVersionText(): String = when (this) {
    ContentStatus.NoServerResource,
    ContentStatus.NotDownloaded,
    is ContentStatus.Paused -> "未安装"
    is ContentStatus.InstalledOffline -> "v${version.take(8)}"
    is ContentStatus.InstalledCurrent -> "v${version.take(8)}"
    is ContentStatus.UpdateAvailable -> "v${localVersion.take(8)}"
    is ContentStatus.UpdatePaused -> "v${localVersion.take(8)}"
}

fun ContentStatus.serverVersionText(game: GameCatalogEntry): String =
    game.contentVersion?.takeIf { it.isNotBlank() }?.let { "v${it.take(8)}" }
        ?: "暂无教程资源"

/**
 * Determines the UI status from the fresh catalog entry and the local content
 * repository.  A local version only counts as installed when both
 * `complete.json` and the real game directory are present.
 */
fun resolveContentStatus(
    game: GameCatalogEntry,
    store: ContentStore
): ContentStatus {
    val serverVersion = game.contentVersion?.trim()?.takeIf { it.isNotBlank() }
    val active = store.readActiveValid(game.id)
    val localVersion = active?.version

    if (serverVersion == null) {
        return if (localVersion != null) {
            ContentStatus.InstalledOffline(localVersion)
        } else {
            ContentStatus.NoServerResource
        }
    }

    val paused = store.readPaused(game.id)
        ?.takeIf { it.version.equals(serverVersion, ignoreCase = true) }

    if (localVersion == null) {
        return if (paused != null) {
            ContentStatus.Paused(paused)
        } else {
            ContentStatus.NotDownloaded
        }
    }

    if (localVersion.equals(serverVersion, ignoreCase = true)) {
        return ContentStatus.InstalledCurrent(localVersion)
    }

    return if (paused != null) {
        ContentStatus.UpdatePaused(
            localVersion = localVersion,
            serverVersion = serverVersion,
            progress = paused
        )
    } else {
        ContentStatus.UpdateAvailable(
            localVersion = localVersion,
            serverVersion = serverVersion
        )
    }
}
