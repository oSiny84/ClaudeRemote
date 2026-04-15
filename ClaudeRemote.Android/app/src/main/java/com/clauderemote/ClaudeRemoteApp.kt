package com.clauderemote

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.clauderemote.data.network.WebSocketClient
import com.clauderemote.data.settings.AppSettings

class ClaudeRemoteApp : Application() {

    lateinit var appSettings: AppSettings
        private set

    /** Singleton WebSocketClient shared between ViewModel and Service */
    val webSocketClient = WebSocketClient()

    override fun onCreate() {
        super.onCreate()
        instance = this
        appSettings = AppSettings(this)
        createNotificationChannels()
    }

    private fun createNotificationChannels() {
        val manager = getSystemService(NotificationManager::class.java) ?: return

        // Connection status channel (foreground service)
        val serviceChannel = NotificationChannel(
            CHANNEL_SERVICE,
            "Connection Status",
            NotificationManager.IMPORTANCE_LOW
        ).apply {
            description = "Shows WebSocket connection status"
            setShowBadge(false)
        }

        // Output notification channel
        val outputChannel = NotificationChannel(
            CHANNEL_OUTPUT,
            "Claude Output",
            NotificationManager.IMPORTANCE_DEFAULT
        ).apply {
            description = "Notifications for new Claude output"
        }

        // Alert channel (disconnect, errors)
        val alertChannel = NotificationChannel(
            CHANNEL_ALERT,
            "Alerts",
            NotificationManager.IMPORTANCE_HIGH
        ).apply {
            description = "Connection lost and error alerts"
        }

        manager.createNotificationChannels(listOf(serviceChannel, outputChannel, alertChannel))
    }

    companion object {
        lateinit var instance: ClaudeRemoteApp
            private set

        const val CHANNEL_SERVICE = "channel_service"
        const val CHANNEL_OUTPUT = "channel_output"
        const val CHANNEL_ALERT = "channel_alert"
    }
}
