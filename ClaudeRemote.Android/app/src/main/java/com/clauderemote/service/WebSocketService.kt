package com.clauderemote.service

import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import com.clauderemote.ClaudeRemoteApp
import com.clauderemote.MainActivity
import com.clauderemote.R
import com.clauderemote.data.model.ConnectionState
import com.clauderemote.data.model.MessageAction
import com.clauderemote.data.model.MessageType
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

class WebSocketService : Service() {

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())
    private val json = Json { ignoreUnknownKeys = true; isLenient = true }
    private var isAppInForeground = true
    private var previousConnectionState = ConnectionState.DISCONNECTED

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> startForegroundService()
            ACTION_STOP -> stopSelf()
            ACTION_APP_FOREGROUND -> isAppInForeground = true
            ACTION_APP_BACKGROUND -> isAppInForeground = false
        }
        return START_STICKY
    }

    private fun startForegroundService() {
        val notification = buildServiceNotification("Connecting...")
        startForeground(NOTIFICATION_ID_SERVICE, notification)
        observeState()
    }

    private fun observeState() {
        val app = ClaudeRemoteApp.instance
        val client = app.webSocketClient
        val settings = app.appSettings

        // Observe connection state → update persistent notification + disconnect alert
        scope.launch {
            client.connectionState.collect { state ->
                updateServiceNotification(state)

                // Notify on disconnect (only if was previously connected)
                if (state == ConnectionState.RECONNECTING &&
                    previousConnectionState == ConnectionState.CONNECTED &&
                    !isAppInForeground
                ) {
                    val notifyDisconnect = settings.notifyDisconnect.first()
                    if (notifyDisconnect) {
                        showDisconnectNotification()
                    }
                }
                previousConnectionState = state
            }
        }

        // Observe messages → notify on new output when in background
        scope.launch {
            client.rawMessages.collect { rawJson ->
                if (!isAppInForeground) {
                    handleBackgroundMessage(rawJson)
                }
            }
        }
    }

    private fun handleBackgroundMessage(rawJson: String) {
        try {
            val element = json.parseToJsonElement(rawJson).jsonObject
            val type = element["type"]?.jsonPrimitive?.content ?: return
            val action = element["action"]?.jsonPrimitive?.content ?: return

            if (type == MessageType.CONTENT) {
                if (action == MessageAction.OUTPUT_UPDATE || action == MessageAction.OUTPUT_FULL) {
                    scope.launch {
                        val notifyOutput = ClaudeRemoteApp.instance.appSettings.notifyOutput.first()
                        if (notifyOutput) {
                            val payload = element["payload"]?.jsonObject
                            val content = payload?.get("content")?.jsonPrimitive?.content
                            val preview = content?.take(100) ?: "New output received"
                            showOutputNotification(preview)
                        }
                    }
                } else if (action == MessageAction.ACTION_BUTTONS) {
                    val payload = element["payload"]?.jsonObject
                    val category = payload?.get("category")?.jsonPrimitive?.content
                    if (category == "permission") {
                        val prompt = payload?.get("prompt")?.jsonPrimitive?.content
                        val preview = prompt?.take(120) ?: "Tool execution requested"
                        showPermissionNotification(preview)
                    }
                }
            }
        } catch (_: Exception) { }
    }

    private fun buildServiceNotification(text: String): Notification {
        val pendingIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, ClaudeRemoteApp.CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("ClaudeRemote")
            .setContentText(text)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .setSilent(true)
            .build()
    }

    private fun updateServiceNotification(state: ConnectionState) {
        val text = when (state) {
            ConnectionState.CONNECTED -> "Connected to server"
            ConnectionState.CONNECTING -> "Connecting..."
            ConnectionState.RECONNECTING -> "Reconnecting..."
            ConnectionState.DISCONNECTED -> "Disconnected"
        }
        val notification = buildServiceNotification(text)
        try {
            NotificationManagerCompat.from(this).notify(NOTIFICATION_ID_SERVICE, notification)
        } catch (_: SecurityException) { }
    }

    private fun showOutputNotification(preview: String) {
        val pendingIntent = PendingIntent.getActivity(
            this, 1,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(this, ClaudeRemoteApp.CHANNEL_OUTPUT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("New Claude Output")
            .setContentText(preview)
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .build()

        try {
            NotificationManagerCompat.from(this).notify(NOTIFICATION_ID_OUTPUT, notification)
        } catch (_: SecurityException) { }
    }

    private fun showPermissionNotification(preview: String) {
        val pendingIntent = PendingIntent.getActivity(
            this, 3,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(this, ClaudeRemoteApp.CHANNEL_ALERT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("Permission Required")
            .setContentText(preview)
            .setStyle(NotificationCompat.BigTextStyle().bigText(preview))
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .build()

        try {
            NotificationManagerCompat.from(this).notify(NOTIFICATION_ID_PERMISSION, notification)
        } catch (_: SecurityException) { }
    }

    private fun showDisconnectNotification() {
        val pendingIntent = PendingIntent.getActivity(
            this, 2,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(this, ClaudeRemoteApp.CHANNEL_ALERT)
            .setSmallIcon(R.drawable.ic_launcher_foreground)
            .setContentTitle("Connection Lost")
            .setContentText("Attempting to reconnect to server...")
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .build()

        try {
            NotificationManagerCompat.from(this).notify(NOTIFICATION_ID_ALERT, notification)
        } catch (_: SecurityException) { }
    }

    override fun onDestroy() {
        super.onDestroy()
        scope.cancel()
    }

    companion object {
        const val ACTION_START = "com.clauderemote.service.START"
        const val ACTION_STOP = "com.clauderemote.service.STOP"
        const val ACTION_APP_FOREGROUND = "com.clauderemote.service.APP_FOREGROUND"
        const val ACTION_APP_BACKGROUND = "com.clauderemote.service.APP_BACKGROUND"

        private const val NOTIFICATION_ID_SERVICE = 1
        private const val NOTIFICATION_ID_OUTPUT = 2
        private const val NOTIFICATION_ID_ALERT = 3
        private const val NOTIFICATION_ID_PERMISSION = 4
    }
}
