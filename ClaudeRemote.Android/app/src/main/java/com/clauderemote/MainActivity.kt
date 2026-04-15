package com.clauderemote

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.*
import androidx.core.content.ContextCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.clauderemote.service.WebSocketService
import com.clauderemote.ui.screens.ConnectionScreen
import com.clauderemote.ui.screens.MainScreen
import com.clauderemote.ui.theme.ClaudeRemoteTheme
import com.clauderemote.viewmodel.MainViewModel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {

    private val notificationPermission = registerForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) { /* granted or denied — service works either way */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        requestNotificationPermission()

        setContent {
            val viewModel: MainViewModel = viewModel()
            val themeMode by viewModel.themeMode.collectAsState()
            var showMain by remember { mutableStateOf(false) }

            // Auto-connect on launch
            LaunchedEffect(Unit) {
                val settings = ClaudeRemoteApp.instance.appSettings
                val autoConnect = settings.autoConnect.first()
                if (autoConnect) {
                    viewModel.connect()
                }
            }

            ClaudeRemoteTheme(themeOverride = themeMode) {
                if (showMain) {
                    MainScreen(
                        viewModel = viewModel,
                        onDisconnect = {
                            showMain = false
                            stopWebSocketService()
                        },
                        onExitApp = {
                            viewModel.disconnect()
                            stopWebSocketService()
                            finish()
                        }
                    )
                } else {
                    ConnectionScreen(
                        viewModel = viewModel,
                        onConnected = {
                            showMain = true
                            startWebSocketService()
                        }
                    )
                }
            }
        }
    }

    override fun onStart() {
        super.onStart()
        sendServiceAction(WebSocketService.ACTION_APP_FOREGROUND)
    }

    override fun onStop() {
        super.onStop()
        sendServiceAction(WebSocketService.ACTION_APP_BACKGROUND)
    }

    private fun startWebSocketService() {
        val intent = Intent(this, WebSocketService::class.java).apply {
            action = WebSocketService.ACTION_START
        }
        ContextCompat.startForegroundService(this, intent)
    }

    private fun stopWebSocketService() {
        val intent = Intent(this, WebSocketService::class.java).apply {
            action = WebSocketService.ACTION_STOP
        }
        startService(intent)
    }

    private fun sendServiceAction(action: String) {
        try {
            val intent = Intent(this, WebSocketService::class.java).apply {
                this.action = action
            }
            startService(intent)
        } catch (_: Exception) { }
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
    }
}
