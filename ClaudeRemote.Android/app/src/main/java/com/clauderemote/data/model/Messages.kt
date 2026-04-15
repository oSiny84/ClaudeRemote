package com.clauderemote.data.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID

@Serializable
data class ClaudeMessage(
    val id: String = UUID.randomUUID().toString(),
    val type: String,
    val action: String,
    val mode: String? = null,
    val payload: JsonObject? = null,
    val timestamp: String = isoTimestamp()
)

@Serializable
data class SessionInfo(
    val id: String,
    val name: String,
    val active: Boolean = false,
    val lastMessage: String = ""
)

@Serializable
data class ProjectInfo(
    val id: String,
    val name: String,
    val path: String = "",
    val active: Boolean = false
)

data class ButtonInfo(
    val id: String,
    val text: String
)

object MessageType {
    const val COMMAND = "command"
    const val RESPONSE = "response"
    const val CONTENT = "content"
    const val STATUS = "status"
}

object MessageAction {
    const val SEND_INPUT = "send_input"
    const val GET_OUTPUT = "get_output"
    const val SWITCH_MODE = "switch_mode"
    const val GET_SESSIONS = "get_sessions"
    const val SELECT_SESSION = "select_session"
    const val ADD_SESSION = "add_session"
    const val GET_PROJECTS = "get_projects"
    const val SELECT_PROJECT = "select_project"
    const val OUTPUT_UPDATE = "output_update"
    const val OUTPUT_FULL = "output_full"
    const val OUTPUT_CHUNK = "output_chunk"
    const val ACTION_BUTTONS = "action_buttons"
    const val CLICK_BUTTON = "click_button"
    const val CLAUDE_STATUS = "claude_status"
    const val HEARTBEAT = "heartbeat"
}

enum class ConnectionState {
    DISCONNECTED,
    CONNECTING,
    CONNECTED,
    RECONNECTING
}

data class ClaudeStatus(
    val claudeRunning: Boolean = false,
    val currentMode: String = "chat",
    val isGenerating: Boolean = false,
    val activeSession: String? = null,
    val activeProject: String? = null
)

private fun isoTimestamp(): String {
    val sdf = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.US)
    sdf.timeZone = TimeZone.getTimeZone("UTC")
    return sdf.format(Date())
}
