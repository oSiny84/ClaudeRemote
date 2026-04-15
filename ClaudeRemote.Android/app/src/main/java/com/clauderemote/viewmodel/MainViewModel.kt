package com.clauderemote.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.clauderemote.ClaudeRemoteApp
import com.clauderemote.data.model.*
import com.clauderemote.data.settings.AppSettings
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch
import kotlinx.serialization.json.*

class MainViewModel : ViewModel() {

    private val app = ClaudeRemoteApp.instance
    val webSocketClient = app.webSocketClient
    private val settings: AppSettings = app.appSettings
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    // Connection
    private val _serverHost = MutableStateFlow("192.168.0.1")
    val serverHost: StateFlow<String> = _serverHost

    private val _serverPort = MutableStateFlow("8765")
    val serverPort: StateFlow<String> = _serverPort

    val connectionState = webSocketClient.connectionState

    // Claude State
    private val _claudeStatus = MutableStateFlow(ClaudeStatus())
    val claudeStatus: StateFlow<ClaudeStatus> = _claudeStatus

    private val _claudeOutput = MutableStateFlow("")
    val claudeOutput: StateFlow<String> = _claudeOutput

    private val _outputScope = MutableStateFlow("latest")
    val outputScope: StateFlow<String> = _outputScope

    private val _isLoading = MutableStateFlow(false)
    val isLoading: StateFlow<Boolean> = _isLoading

    // Sessions & Projects
    private val _sessions = MutableStateFlow<List<SessionInfo>>(emptyList())
    val sessions: StateFlow<List<SessionInfo>> = _sessions

    private val _projects = MutableStateFlow<List<ProjectInfo>>(emptyList())
    val projects: StateFlow<List<ProjectInfo>> = _projects

    private val _currentMode = MutableStateFlow("code")
    val currentMode: StateFlow<String> = _currentMode

    private val _actionButtons = MutableStateFlow<List<ButtonInfo>>(emptyList())
    val actionButtons: StateFlow<List<ButtonInfo>> = _actionButtons

    private val _actionButtonsPrompt = MutableStateFlow<String?>(null)
    val actionButtonsPrompt: StateFlow<String?> = _actionButtonsPrompt

    private val _buttonCategory = MutableStateFlow<String?>(null)
    val buttonCategory: StateFlow<String?> = _buttonCategory

    // Command Input
    private val _commandText = MutableStateFlow("")
    val commandText: StateFlow<String> = _commandText

    // Command History (last 10)
    private val _commandHistory = MutableStateFlow<List<String>>(emptyList())
    val commandHistory: StateFlow<List<String>> = _commandHistory

    // Status Messages
    private val _statusMessage = MutableStateFlow("")
    val statusMessage: StateFlow<String> = _statusMessage

    // Snackbar events (one-shot)
    private val _snackbarEvent = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val snackbarEvent: SharedFlow<String> = _snackbarEvent

    // Settings
    private val _autoConnect = MutableStateFlow(false)
    val autoConnect: StateFlow<Boolean> = _autoConnect

    private val _notifyOutput = MutableStateFlow(true)
    val notifyOutput: StateFlow<Boolean> = _notifyOutput

    private val _notifyDisconnect = MutableStateFlow(true)
    val notifyDisconnect: StateFlow<Boolean> = _notifyDisconnect

    private val _themeMode = MutableStateFlow("system")
    val themeMode: StateFlow<String> = _themeMode

    // Chunk reassembly buffer
    private val chunkBuffer = mutableMapOf<String, MutableList<String>>()
    private val chunkTotals = mutableMapOf<String, Int>()

    init {
        loadSettings()
        observeMessages()
    }

    // --- Settings ---

    private fun loadSettings() {
        viewModelScope.launch {
            settings.serverHost.collect { _serverHost.value = it }
        }
        viewModelScope.launch {
            settings.serverPort.collect { _serverPort.value = it }
        }
        viewModelScope.launch {
            settings.autoConnect.collect { _autoConnect.value = it }
        }
        viewModelScope.launch {
            settings.notifyOutput.collect { _notifyOutput.value = it }
        }
        viewModelScope.launch {
            settings.notifyDisconnect.collect { _notifyDisconnect.value = it }
        }
        viewModelScope.launch {
            settings.themeMode.collect { _themeMode.value = it }
        }
    }

    fun saveConnectionSettings() {
        viewModelScope.launch {
            settings.saveConnection(_serverHost.value, _serverPort.value)
            _snackbarEvent.tryEmit("Connection saved")
        }
    }

    fun setAutoConnect(enabled: Boolean) {
        _autoConnect.value = enabled
        viewModelScope.launch { settings.setAutoConnect(enabled) }
    }

    fun setNotifyOutput(enabled: Boolean) {
        _notifyOutput.value = enabled
        viewModelScope.launch { settings.setNotifyOutput(enabled) }
    }

    fun setNotifyDisconnect(enabled: Boolean) {
        _notifyDisconnect.value = enabled
        viewModelScope.launch { settings.setNotifyDisconnect(enabled) }
    }

    fun setThemeMode(mode: String) {
        _themeMode.value = mode
        viewModelScope.launch { settings.setThemeMode(mode) }
    }

    // --- Connection ---

    fun updateHost(host: String) { _serverHost.value = host }
    fun updatePort(port: String) { _serverPort.value = port }
    fun updateCommandText(text: String) { _commandText.value = text }

    fun connect() {
        val port = _serverPort.value.toIntOrNull() ?: 8765
        webSocketClient.connect(_serverHost.value, port)
        _statusMessage.value = "Connecting to ${_serverHost.value}:$port..."
    }

    fun disconnect() {
        webSocketClient.disconnect()
        _statusMessage.value = "Disconnected"
        _claudeOutput.value = ""
        _sessions.value = emptyList()
        _projects.value = emptyList()
    }

    // --- Commands ---

    fun sendInput() {
        val text = _commandText.value.trim()
        if (text.isEmpty()) return

        addToHistory(text)

        webSocketClient.sendCommand(
            action = MessageAction.SEND_INPUT,
            mode = _currentMode.value,
            payload = mapOf("text" to text)
        )
        _commandText.value = ""
        _statusMessage.value = "Input sent"
    }

    fun sendQuickInput(text: String) {
        webSocketClient.sendCommand(
            action = MessageAction.SEND_INPUT,
            mode = _currentMode.value,
            payload = mapOf("text" to text)
        )
        _statusMessage.value = "Sent: $text"
    }

    fun requestOutput(scope: String = "latest") {
        _outputScope.value = scope
        _isLoading.value = true
        webSocketClient.sendCommand(
            action = MessageAction.GET_OUTPUT,
            payload = mapOf("scope" to scope)
        )
    }

    fun requestSessions() {
        _isLoading.value = true
        webSocketClient.sendCommand(
            action = MessageAction.GET_SESSIONS,
            mode = _currentMode.value
        )
    }

    fun selectSession(sessionId: String) {
        webSocketClient.sendCommand(
            action = MessageAction.SELECT_SESSION,
            mode = _currentMode.value,
            payload = mapOf("sessionId" to sessionId)
        )
        // Optimistic UI update
        _sessions.value = _sessions.value.map {
            it.copy(active = it.id == sessionId)
        }
    }

    fun addSession() {
        webSocketClient.sendCommand(
            action = MessageAction.ADD_SESSION,
            mode = _currentMode.value
        )
        _snackbarEvent.tryEmit("New session created")
    }

    fun requestProjects() {
        _isLoading.value = true
        webSocketClient.sendCommand(
            action = MessageAction.GET_PROJECTS,
            mode = "code"
        )
    }

    fun clickButton(buttonId: String) {
        webSocketClient.sendCommand(
            action = MessageAction.CLICK_BUTTON,
            payload = mapOf("buttonId" to buttonId)
        )
    }

    fun selectProject(projectId: String) {
        webSocketClient.sendCommand(
            action = MessageAction.SELECT_PROJECT,
            mode = "code",
            payload = mapOf("projectId" to projectId)
        )
        // Optimistic UI update
        _projects.value = _projects.value.map {
            it.copy(active = it.id == projectId)
        }
    }

    // --- Command History ---

    private fun addToHistory(text: String) {
        val current = _commandHistory.value.toMutableList()
        current.remove(text)
        current.add(0, text)
        if (current.size > 10) current.removeAt(current.lastIndex)
        _commandHistory.value = current
    }

    // --- Message Handling ---

    private fun observeMessages() {
        viewModelScope.launch {
            webSocketClient.rawMessages.collect { rawJson ->
                processMessage(rawJson)
            }
        }

        viewModelScope.launch {
            webSocketClient.connectionState.collect { state ->
                _statusMessage.value = when (state) {
                    ConnectionState.CONNECTED -> "Connected"
                    ConnectionState.CONNECTING -> "Connecting..."
                    ConnectionState.RECONNECTING -> "Reconnecting..."
                    ConnectionState.DISCONNECTED -> "Disconnected"
                }
                // On connect, ensure server is in Code mode and load data
                if (state == ConnectionState.CONNECTED) {
                    webSocketClient.sendCommand(
                        action = MessageAction.SWITCH_MODE,
                        payload = mapOf("targetMode" to "code")
                    )
                    _isLoading.value = true
                }
            }
        }
    }

    private fun processMessage(rawJson: String) {
        try {
            val element = json.parseToJsonElement(rawJson).jsonObject
            val type = element["type"]?.jsonPrimitive?.content ?: return
            val action = element["action"]?.jsonPrimitive?.content ?: return
            val payload = element["payload"]?.jsonObject
            when (type) {
                MessageType.CONTENT -> handleContent(action, payload)
                MessageType.RESPONSE -> handleResponse(action, payload)
                MessageType.STATUS -> handleStatus(action, payload)
            }
        } catch (e: Exception) {
            _statusMessage.value = "Parse error: ${e.message}"
        }
    }

    private fun handleContent(action: String, payload: JsonObject?) {
        _isLoading.value = false
        when (action) {
            MessageAction.OUTPUT_UPDATE -> {
                val content = payload?.get("content")?.jsonPrimitive?.content ?: return
                _claudeOutput.value = content
            }
            MessageAction.OUTPUT_FULL -> {
                val messages = payload?.get("messages")?.jsonArray
                if (messages != null) {
                    _claudeOutput.value = buildString {
                        messages.forEach { el ->
                            val obj = el.jsonObject
                            val role = obj["role"]?.jsonPrimitive?.content ?: "unknown"
                            val content = obj["content"]?.jsonPrimitive?.content ?: ""
                            if (isNotEmpty()) append("\n\n")
                            append("[$role]\n$content")
                        }
                    }
                }
            }
            MessageAction.OUTPUT_CHUNK -> handleChunk(payload)
            MessageAction.ACTION_BUTTONS -> {
                _actionButtons.value = payload?.get("buttons")?.jsonArray?.map { el ->
                    val obj = el.jsonObject
                    ButtonInfo(
                        id = obj["id"]?.jsonPrimitive?.content ?: "",
                        text = obj["text"]?.jsonPrimitive?.content ?: ""
                    )
                } ?: emptyList()
                _actionButtonsPrompt.value = payload?.get("prompt")?.jsonPrimitive?.content
                _buttonCategory.value = payload?.get("category")?.jsonPrimitive?.content
            }
            else -> {
                val content = payload?.get("content")?.jsonPrimitive?.content
                if (content != null) _claudeOutput.value = content
            }
        }
    }

    private fun handleChunk(payload: JsonObject?) {
        val chunkId = payload?.get("chunkId")?.jsonPrimitive?.content ?: return
        val chunkIndex = payload["chunkIndex"]?.jsonPrimitive?.int ?: return
        val totalChunks = payload["totalChunks"]?.jsonPrimitive?.int ?: return
        val data = payload["data"]?.jsonPrimitive?.content ?: return

        if (!chunkBuffer.containsKey(chunkId)) {
            chunkBuffer[chunkId] = MutableList(totalChunks) { "" }
            chunkTotals[chunkId] = totalChunks
        }
        chunkBuffer[chunkId]?.let { buffer ->
            if (chunkIndex < buffer.size) buffer[chunkIndex] = data
            if (buffer.count { it.isNotEmpty() } == totalChunks) {
                _claudeOutput.value = buffer.joinToString("")
                chunkBuffer.remove(chunkId)
                chunkTotals.remove(chunkId)
            }
        }
    }

    private fun handleResponse(action: String, payload: JsonObject?) {
        _isLoading.value = false
        val success = payload?.get("success")?.jsonPrimitive?.boolean ?: false
        when (action) {
            MessageAction.GET_SESSIONS -> {
                if (success) {
                    _sessions.value = payload?.get("sessions")?.jsonArray?.map { el ->
                        val obj = el.jsonObject
                        SessionInfo(
                            id = obj["id"]?.jsonPrimitive?.content ?: "",
                            name = obj["name"]?.jsonPrimitive?.content ?: "",
                            active = obj["active"]?.jsonPrimitive?.boolean ?: false,
                            lastMessage = obj["lastMessage"]?.jsonPrimitive?.content ?: ""
                        )
                    } ?: emptyList()
                } else {
                    _snackbarEvent.tryEmit("Failed to load sessions")
                }
            }
            MessageAction.GET_PROJECTS -> {
                if (success) {
                    _projects.value = payload?.get("projects")?.jsonArray?.map { el ->
                        val obj = el.jsonObject
                        ProjectInfo(
                            id = obj["id"]?.jsonPrimitive?.content ?: "",
                            name = obj["name"]?.jsonPrimitive?.content ?: "",
                            path = obj["path"]?.jsonPrimitive?.content ?: "",
                            active = obj["active"]?.jsonPrimitive?.boolean ?: false
                        )
                    } ?: emptyList()
                } else {
                    _snackbarEvent.tryEmit("Failed to load projects")
                }
            }
            MessageAction.GET_OUTPUT -> {
                if (success) {
                    payload?.get("content")?.jsonPrimitive?.content?.let {
                        _claudeOutput.value = it
                    }
                } else {
                    _snackbarEvent.tryEmit("Failed to get output")
                }
            }
            MessageAction.CLICK_BUTTON -> {
                if (!success) {
                    val error = payload?.get("error")?.jsonPrimitive?.content
                    _snackbarEvent.tryEmit("Button click failed: ${error ?: "unknown"}")
                }
                // Don't clear buttons here — server sends action_buttons with
                // updated state, avoiding race condition with consecutive questions
            }
            MessageAction.SWITCH_MODE -> {
                if (success) {
                    // Server confirmed Code mode — load sessions and projects
                    requestSessions()
                    requestProjects()
                } else {
                    _isLoading.value = false
                    val error = payload?.get("error")?.jsonPrimitive?.content
                    _snackbarEvent.tryEmit("Mode switch failed: ${error ?: "unknown"}")
                }
            }
            else -> {
                val message = payload?.get("message")?.jsonPrimitive?.content
                val error = payload?.get("error")?.jsonPrimitive?.content
                if (!success) {
                    _snackbarEvent.tryEmit(message ?: "Error: ${error ?: "unknown"}")
                } else if (message != null) {
                    _statusMessage.value = message
                }
            }
        }
    }

    private fun handleStatus(action: String, payload: JsonObject?) {
        when (action) {
            MessageAction.CLAUDE_STATUS -> {
                _claudeStatus.value = ClaudeStatus(
                    claudeRunning = payload?.get("claudeRunning")?.jsonPrimitive?.boolean ?: false,
                    currentMode = "code",
                    isGenerating = payload?.get("isGenerating")?.jsonPrimitive?.boolean ?: false,
                    activeSession = payload?.get("activeSession")?.jsonPrimitive?.contentOrNull,
                    activeProject = payload?.get("activeProject")?.jsonPrimitive?.contentOrNull
                )
            }
            MessageAction.HEARTBEAT -> { }
        }
    }

    override fun onCleared() {
        super.onCleared()
        // Don't destroy the singleton client — Service keeps it alive
    }
}
