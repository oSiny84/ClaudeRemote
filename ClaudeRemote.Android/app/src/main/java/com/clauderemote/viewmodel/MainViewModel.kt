package com.clauderemote.viewmodel

import android.app.DownloadManager
import android.content.Context
import android.net.Uri
import android.os.Environment
import android.util.Log
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

    // Chat-bubble rendering (Phase 13) — preferred UI source.
    // `_claudeOutput` remains for clipboard "copy all" and for chunk reassembly.
    private val _chatMessages = MutableStateFlow<List<ChatMessage>>(emptyList())
    val chatMessages: StateFlow<List<ChatMessage>> = _chatMessages

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

    // Usage Dashboard
    private val _usageDashboard = MutableStateFlow<UsageDashboard?>(null)
    val usageDashboard: StateFlow<UsageDashboard?> = _usageDashboard

    private val _usageDashboardLoading = MutableStateFlow(false)
    val usageDashboardLoading: StateFlow<Boolean> = _usageDashboardLoading

    private val _showUsageDashboard = MutableStateFlow(false)
    val showUsageDashboard: StateFlow<Boolean> = _showUsageDashboard

    // File Browser
    private val _fileEntries = MutableStateFlow<List<FileEntry>>(emptyList())
    val fileEntries: StateFlow<List<FileEntry>> = _fileEntries

    private val _currentPath = MutableStateFlow("")
    val currentPath: StateFlow<String> = _currentPath

    private val _parentPath = MutableStateFlow<String?>(null)
    val parentPath: StateFlow<String?> = _parentPath

    private val _showFileBrowser = MutableStateFlow(false)
    val showFileBrowser: StateFlow<Boolean> = _showFileBrowser

    private val _fileBrowserLoading = MutableStateFlow(false)
    val fileBrowserLoading: StateFlow<Boolean> = _fileBrowserLoading

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
        _chatMessages.value = emptyList()
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

    // --- Usage Dashboard ---

    fun requestUsageDashboard() {
        _usageDashboardLoading.value = true
        webSocketClient.sendCommand(action = MessageAction.GET_USAGE_DASHBOARD)
    }

    fun openUsageDashboard() {
        _showUsageDashboard.value = true
        requestUsageDashboard()
    }

    fun closeUsageDashboard() {
        _showUsageDashboard.value = false
    }

    // --- File Browser ---

    fun openFileBrowser() {
        _showFileBrowser.value = true
        browseFiles("")
    }

    fun closeFileBrowser() {
        _showFileBrowser.value = false
    }

    fun browseFiles(path: String) {
        Log.d(TAG_FILE, "browseFiles → sending path='$path'")
        _fileBrowserLoading.value = true
        webSocketClient.sendCommand(
            action = MessageAction.BROWSE_FILES,
            payload = mapOf("path" to path)
        )
    }

    /**
     * Navigate into a directory. The caller MUST pass the full filesystem
     * path (from `FileEntry.path`) — we do NOT reconstruct it from name +
     * currentPath because the server's name is a display label that may not
     * match the actual directory name (e.g. drives: name="C: (Local Disk)",
     * path="C:\\").
     */
    fun browseDirectory(fullPath: String) {
        Log.d(TAG_FILE, "browseDirectory → fullPath='$fullPath'")
        browseFiles(fullPath)
    }

    fun browseParent() {
        val parent = _parentPath.value
        Log.d(TAG_FILE, "browseParent → parent='$parent'")
        parent?.let { browseFiles(it) }
    }

    /** Request a download URL — caller passes the server-supplied full path. */
    fun requestDownload(fullPath: String) {
        Log.d(TAG_FILE, "requestDownload → fullPath='$fullPath'")
        webSocketClient.sendCommand(
            action = MessageAction.REQUEST_DOWNLOAD,
            payload = mapOf("path" to fullPath)
        )
    }

    private fun startDownload(downloadUrl: String, fileName: String) {
        Log.d(TAG_FILE, "startDownload: fileName='$fileName' url='$downloadUrl'")
        try {
            val context = ClaudeRemoteApp.instance
            val dm = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
            val request = DownloadManager.Request(Uri.parse(downloadUrl)).apply {
                setDestinationInExternalPublicDir(Environment.DIRECTORY_DOWNLOADS, fileName)
                setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
                setTitle(fileName)
                setDescription("ClaudeRemote file download")
                // Allow mobile + wifi. Servers on LAN are reached over wifi.
                setAllowedNetworkTypes(
                    DownloadManager.Request.NETWORK_WIFI or
                        DownloadManager.Request.NETWORK_MOBILE
                )
            }
            val downloadId = dm.enqueue(request)
            Log.d(TAG_FILE, "enqueue → id=$downloadId")
            _snackbarEvent.tryEmit("Downloading $fileName...")
            pollDownloadStatus(dm, downloadId, fileName)
        } catch (e: Exception) {
            Log.e(TAG_FILE, "startDownload exception", e)
            _snackbarEvent.tryEmit("Download failed: ${e.message}")
        }
    }

    /**
     * Polls DownloadManager for status transitions and surfaces failures as
     * snackbars + logs. Needed because DownloadManager fails silently on
     * unreachable URLs / HTTP errors / network issues — no exception from enqueue.
     */
    private fun pollDownloadStatus(
        dm: DownloadManager,
        downloadId: Long,
        fileName: String
    ) {
        viewModelScope.launch {
            val query = DownloadManager.Query().setFilterById(downloadId)
            // Poll up to 60s for a terminal state
            repeat(120) {
                kotlinx.coroutines.delay(500)
                val cursor = dm.query(query) ?: return@repeat
                cursor.use { c ->
                    if (!c.moveToFirst()) return@repeat
                    val statusCol = c.getColumnIndex(DownloadManager.COLUMN_STATUS)
                    val reasonCol = c.getColumnIndex(DownloadManager.COLUMN_REASON)
                    val bytesCol = c.getColumnIndex(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR)
                    val totalCol = c.getColumnIndex(DownloadManager.COLUMN_TOTAL_SIZE_BYTES)
                    val status = if (statusCol >= 0) c.getInt(statusCol) else -1
                    val reason = if (reasonCol >= 0) c.getInt(reasonCol) else -1
                    val bytes = if (bytesCol >= 0) c.getLong(bytesCol) else -1
                    val total = if (totalCol >= 0) c.getLong(totalCol) else -1

                    when (status) {
                        DownloadManager.STATUS_SUCCESSFUL -> {
                            Log.d(TAG_FILE, "download[$downloadId] SUCCESS: $fileName ($bytes bytes)")
                            _snackbarEvent.tryEmit("Saved: $fileName")
                            return@launch
                        }
                        DownloadManager.STATUS_FAILED -> {
                            val msg = downloadReasonToText(reason)
                            Log.w(
                                TAG_FILE,
                                "download[$downloadId] FAILED: reason=$reason ($msg) " +
                                    "bytes=$bytes/$total"
                            )
                            _snackbarEvent.tryEmit("Download failed: $msg")
                            return@launch
                        }
                        DownloadManager.STATUS_PAUSED -> {
                            val pausedMsg = pausedReasonToText(reason)
                            Log.d(
                                TAG_FILE,
                                "download[$downloadId] PAUSED: reason=$reason ($pausedMsg) " +
                                    "bytes=$bytes/$total"
                            )
                        }
                        DownloadManager.STATUS_PENDING,
                        DownloadManager.STATUS_RUNNING -> {
                            // progress — don't spam logs
                        }
                        else -> {
                            Log.d(TAG_FILE, "download[$downloadId] status=$status (unknown)")
                        }
                    }
                }
            }
            Log.w(TAG_FILE, "download polling timed out after 60s (id=$downloadId)")
        }
    }

    private fun pausedReasonToText(reason: Int): String = when (reason) {
        DownloadManager.PAUSED_WAITING_TO_RETRY -> "waiting to retry (server/HTTP error)"
        DownloadManager.PAUSED_WAITING_FOR_NETWORK -> "waiting for network"
        DownloadManager.PAUSED_QUEUED_FOR_WIFI -> "queued for Wi-Fi"
        DownloadManager.PAUSED_UNKNOWN -> "unknown"
        else -> "code=$reason"
    }

    private fun downloadReasonToText(reason: Int): String = when (reason) {
        DownloadManager.ERROR_CANNOT_RESUME -> "Cannot resume"
        DownloadManager.ERROR_DEVICE_NOT_FOUND -> "Storage not found"
        DownloadManager.ERROR_FILE_ALREADY_EXISTS -> "File already exists"
        DownloadManager.ERROR_FILE_ERROR -> "File error"
        DownloadManager.ERROR_HTTP_DATA_ERROR -> "HTTP data error (server closed connection?)"
        DownloadManager.ERROR_INSUFFICIENT_SPACE -> "Not enough space"
        DownloadManager.ERROR_TOO_MANY_REDIRECTS -> "Too many redirects"
        DownloadManager.ERROR_UNHANDLED_HTTP_CODE -> "Unhandled HTTP code"
        DownloadManager.ERROR_UNKNOWN -> "Unknown error"
        in 400..599 -> "HTTP $reason"
        else -> "reason=$reason"
    }

    fun selectProject(projectId: String) {
        webSocketClient.sendCommand(
            action = MessageAction.SELECT_PROJECT,
            mode = "code",
            payload = mapOf("projectId" to projectId)
        )
        // Optimistic UI: exclusive selection — selected project becomes
        // the ONLY active/expanded one; all others collapse. Server uses
        // the same semantics (expand target + collapse others), so the
        // authoritative response will match this optimistic state.
        _projects.value = _projects.value.map { project ->
            if (project.id == projectId) {
                project.copy(active = true, expanded = true)
            } else {
                project.copy(active = false, expanded = false)
            }
        }
        // Auto-refresh sessions — server also pushes updates, but this guarantees
        // the client sees the new selection's sessions immediately
        requestSessions()
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
                // Fallback path: no messages[] → treat the full text as one assistant reply
                _chatMessages.value = listOf(ChatMessage(role = "assistant", content = content))
            }
            MessageAction.OUTPUT_FULL -> {
                val messages = payload?.get("messages")?.jsonArray
                if (messages != null) {
                    val parsed = messages.map { el ->
                        val obj = el.jsonObject
                        ChatMessage(
                            role = obj["role"]?.jsonPrimitive?.content ?: "assistant",
                            content = obj["content"]?.jsonPrimitive?.content ?: ""
                        )
                    }
                    _chatMessages.value = parsed
                    // Legacy joined form — kept for the "copy all" clipboard action
                    _claudeOutput.value = parsed.joinToString("\n\n") {
                        "[${it.role}]\n${it.content}"
                    }
                } else {
                    // Back-compat: server sent plain content only
                    val content = payload?.get("content")?.jsonPrimitive?.content
                    if (content != null) {
                        _claudeOutput.value = content
                        _chatMessages.value =
                            listOf(ChatMessage(role = "assistant", content = content))
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
                val joined = buffer.joinToString("")
                _claudeOutput.value = joined
                _chatMessages.value =
                    listOf(ChatMessage(role = "assistant", content = joined))
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
            MessageAction.GET_PROJECTS, MessageAction.SELECT_PROJECT -> {
                if (success) {
                    // Both GET_PROJECTS and SELECT_PROJECT responses carry
                    // the authoritative project list (with expand/active state).
                    // SELECT_PROJECT also carries updated sessions.
                    _projects.value = payload?.get("projects")?.jsonArray?.map { el ->
                        val obj = el.jsonObject
                        ProjectInfo(
                            id = obj["id"]?.jsonPrimitive?.content ?: "",
                            name = obj["name"]?.jsonPrimitive?.content ?: "",
                            path = obj["path"]?.jsonPrimitive?.content ?: "",
                            active = obj["active"]?.jsonPrimitive?.boolean ?: false,
                            expanded = obj["expanded"]?.jsonPrimitive?.boolean ?: false
                        )
                    } ?: emptyList()
                    // SELECT_PROJECT also returns sessions for the expanded project
                    payload?.get("sessions")?.jsonArray?.let { sessionsArr ->
                        _sessions.value = sessionsArr.map { el ->
                            val obj = el.jsonObject
                            SessionInfo(
                                id = obj["id"]?.jsonPrimitive?.content ?: "",
                                name = obj["name"]?.jsonPrimitive?.content ?: "",
                                active = obj["active"]?.jsonPrimitive?.boolean ?: false,
                                lastMessage = obj["lastMessage"]?.jsonPrimitive?.content ?: ""
                            )
                        }
                    }
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
            MessageAction.GET_USAGE_DASHBOARD -> {
                _usageDashboardLoading.value = false
                if (success) {
                    // Try nested "dashboard" object first, fall back to payload itself
                    val dashObj = payload?.get("dashboard") as? JsonObject ?: payload
                    if (dashObj != null) {
                        _usageDashboard.value = parseUsageDashboard(dashObj)
                    } else {
                        _snackbarEvent.tryEmit("Usage data missing")
                    }
                } else {
                    val error = payload?.get("error")?.jsonPrimitive?.content
                    _snackbarEvent.tryEmit("Failed to load usage: ${error ?: "unknown"}")
                }
            }
            MessageAction.BROWSE_FILES -> {
                _fileBrowserLoading.value = false
                if (success) {
                    // Server payload keys (Phase 12 Hotfix 1):
                    //   currentPath, parentPath, entries[{name,path,type,size,modified}]
                    _currentPath.value =
                        (payload?.get("currentPath") as? JsonPrimitive)?.contentOrNull ?: ""
                    _parentPath.value =
                        (payload?.get("parentPath") as? JsonPrimitive)?.contentOrNull
                            ?.takeIf { it.isNotEmpty() }
                    val parsed = payload?.get("entries")?.jsonArray?.map { el ->
                        val obj = el.jsonObject
                        val name = obj["name"]?.jsonPrimitive?.content ?: ""
                        FileEntry(
                            name = name,
                            // Server always sends `path` in Phase 12 Hotfix 1+.
                            // Fallback to `name` for backwards compat if missing.
                            path = (obj["path"] as? JsonPrimitive)?.contentOrNull
                                ?.takeIf { it.isNotEmpty() } ?: name,
                            type = obj["type"]?.jsonPrimitive?.content ?: "file",
                            size = obj["size"]?.jsonPrimitive?.long ?: 0L,
                            modified = (obj["modified"] as? JsonPrimitive)?.contentOrNull
                        )
                    } ?: emptyList()
                    Log.d(
                        TAG_FILE,
                        "browse_files response: currentPath='${_currentPath.value}' " +
                            "parent='${_parentPath.value}' entries=${parsed.size} " +
                            "first=${parsed.take(3).map { "${it.name}(${it.type})→${it.path}" }}"
                    )
                    _fileEntries.value = parsed
                } else {
                    val error = payload?.get("error")?.jsonPrimitive?.content
                    Log.w(TAG_FILE, "browse_files failed: $error")
                    _snackbarEvent.tryEmit("Browse failed: ${error ?: "unknown"}")
                }
            }
            MessageAction.REQUEST_DOWNLOAD -> {
                if (success) {
                    // Server sends "url" (not "downloadUrl") per Phase 12 protocol
                    val downloadUrl = payload?.get("url")?.jsonPrimitive?.content
                    val fileName = payload?.get("fileName")?.jsonPrimitive?.content
                    if (downloadUrl != null && fileName != null) {
                        startDownload(downloadUrl, fileName)
                    } else {
                        _snackbarEvent.tryEmit("Invalid download response")
                    }
                } else {
                    val error = payload?.get("error")?.jsonPrimitive?.content
                    _snackbarEvent.tryEmit("Download failed: ${error ?: "unknown"}")
                }
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

    private fun parseUsageDashboard(obj: JsonObject): UsageDashboard {
        return UsageDashboard(
            contextWindow = (obj["contextWindow"] as? JsonObject)?.let { parseContextWindow(it) },
            fiveHourLimit = (obj["fiveHourLimit"] as? JsonObject)?.let { parseUsageLimit(it) },
            weeklyAllModels = (obj["weeklyAllModels"] as? JsonObject)?.let { parseUsageLimit(it) },
            weeklySonnetOnly = (obj["weeklySonnetOnly"] as? JsonObject)?.let { parseUsageLimit(it) },
            modelName = (obj["modelName"] as? JsonPrimitive)?.contentOrNull,
            planName = (obj["planName"] as? JsonPrimitive)?.contentOrNull,
            fetchedAt = (obj["fetchedAt"] as? JsonPrimitive)?.contentOrNull
        )
    }

    private fun parseContextWindow(obj: JsonObject): ContextWindow {
        return ContextWindow(
            usedText = (obj["usedText"] as? JsonPrimitive)?.contentOrNull,
            totalText = (obj["totalText"] as? JsonPrimitive)?.contentOrNull,
            percentUsed = (obj["percentUsed"] as? JsonPrimitive)?.intOrNull
        )
    }

    private fun parseUsageLimit(obj: JsonObject): UsageLimit {
        return UsageLimit(
            label = (obj["label"] as? JsonPrimitive)?.contentOrNull,
            percentUsed = (obj["percentUsed"] as? JsonPrimitive)?.intOrNull,
            resetText = (obj["resetText"] as? JsonPrimitive)?.contentOrNull
        )
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

    companion object {
        private const val TAG_FILE = "FileBrowserVM"
    }
}
