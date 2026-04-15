package com.clauderemote.data.network

import com.clauderemote.data.model.ClaudeMessage
import com.clauderemote.data.model.ConnectionState
import com.clauderemote.data.model.MessageType
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.*
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.TimeUnit

class WebSocketClient {

    private var webSocket: WebSocket? = null
    private var client: OkHttpClient? = null
    private var reconnectJob: Job? = null
    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val messageQueue = ConcurrentLinkedQueue<String>()
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }

    private var serverHost: String = ""
    private var serverPort: Int = 8765
    private var reconnectAttempts = 0
    private val maxReconnectDelay = 30_000L
    private var isUserDisconnect = false

    private val _connectionState = MutableStateFlow(ConnectionState.DISCONNECTED)
    val connectionState: StateFlow<ConnectionState> = _connectionState

    private val _messages = MutableSharedFlow<ClaudeMessage>(extraBufferCapacity = 64)
    val messages: SharedFlow<ClaudeMessage> = _messages

    private val _rawMessages = MutableSharedFlow<String>(extraBufferCapacity = 64)
    val rawMessages: SharedFlow<String> = _rawMessages

    fun connect(host: String, port: Int) {
        isUserDisconnect = false
        serverHost = host
        serverPort = port
        _connectionState.value = ConnectionState.CONNECTING

        client = OkHttpClient.Builder()
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .pingInterval(30, TimeUnit.SECONDS)
            .build()

        val request = Request.Builder()
            .url("ws://$host:$port")
            .build()

        webSocket = client?.newWebSocket(request, createListener())
    }

    fun disconnect() {
        isUserDisconnect = true
        reconnectJob?.cancel()
        reconnectJob = null
        webSocket?.close(1000, "User disconnect")
        webSocket = null
        client?.dispatcher?.executorService?.shutdown()
        client = null
        _connectionState.value = ConnectionState.DISCONNECTED
        reconnectAttempts = 0
    }

    fun send(message: String): Boolean {
        if (_connectionState.value == ConnectionState.CONNECTED) {
            return webSocket?.send(message) ?: false
        } else {
            messageQueue.add(message)
            return false
        }
    }

    fun sendCommand(action: String, mode: String? = null, payload: Map<String, String>? = null) {
        val jsonPayload = payload?.let {
            buildJsonObject {
                it.forEach { (k, v) -> put(k, v) }
            }
        }
        val message = ClaudeMessage(
            type = MessageType.COMMAND,
            action = action,
            mode = mode,
            payload = jsonPayload
        )
        val jsonStr = json.encodeToString(ClaudeMessage.serializer(), message)
        send(jsonStr)
    }

    private fun createListener() = object : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            _connectionState.value = ConnectionState.CONNECTED
            reconnectAttempts = 0
            flushQueue()
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            scope.launch {
                _rawMessages.emit(text)
                try {
                    val message = json.decodeFromString(ClaudeMessage.serializer(), text)
                    _messages.emit(message)
                } catch (_: Exception) { }
            }
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
            if (!isUserDisconnect) {
                // Server-initiated close: go to RECONNECTING, not DISCONNECTED
                _connectionState.value = ConnectionState.RECONNECTING
                scheduleReconnect()
            } else {
                _connectionState.value = ConnectionState.DISCONNECTED
            }
        }

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
            if (!isUserDisconnect) {
                // Connection failure: go to RECONNECTING, not DISCONNECTED
                _connectionState.value = ConnectionState.RECONNECTING
                scheduleReconnect()
            } else {
                _connectionState.value = ConnectionState.DISCONNECTED
            }
        }
    }

    private fun scheduleReconnect() {
        reconnectJob?.cancel()
        reconnectJob = scope.launch {
            val delay = calculateReconnectDelay()
            delay(delay)
            reconnectAttempts++

            if (serverHost.isNotEmpty() && !isUserDisconnect) {
                // Recreate client if needed (previous one may have been shut down)
                if (client == null) {
                    client = OkHttpClient.Builder()
                        .readTimeout(0, TimeUnit.MILLISECONDS)
                        .pingInterval(30, TimeUnit.SECONDS)
                        .build()
                }
                val request = Request.Builder()
                    .url("ws://$serverHost:$serverPort")
                    .build()
                webSocket = client?.newWebSocket(request, createListener())
            }
        }
    }

    private fun calculateReconnectDelay(): Long {
        // Exponential backoff: 1s, 2s, 4s, 8s, 16s, 32s → capped at 30s
        val delay = 1000L * (1L shl minOf(reconnectAttempts, 5))
        return minOf(delay, maxReconnectDelay)
    }

    private fun flushQueue() {
        var sent = 0
        while (messageQueue.isNotEmpty()) {
            val msg = messageQueue.poll() ?: break
            webSocket?.send(msg)
            sent++
        }
    }

    fun destroy() {
        disconnect()
        scope.cancel()
    }
}
