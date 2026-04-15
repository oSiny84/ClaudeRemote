package com.clauderemote.ui.screens

import androidx.compose.animation.Crossfade
import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clauderemote.data.model.ConnectionState
import com.clauderemote.viewmodel.MainViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MainScreen(
    viewModel: MainViewModel,
    onDisconnect: () -> Unit,
    onExitApp: () -> Unit = {}
) {
    val connectionState by viewModel.connectionState.collectAsState()
    val snackbarMessage by viewModel.snackbarEvent.collectAsState(initial = null)
    var selectedTab by remember { mutableIntStateOf(0) }
    var showSettings by remember { mutableStateOf(false) }
    val snackbarHostState = remember { SnackbarHostState() }

    // Go back to connection screen only on user-initiated disconnect
    LaunchedEffect(connectionState) {
        if (connectionState == ConnectionState.DISCONNECTED) {
            onDisconnect()
        }
    }
    if (connectionState == ConnectionState.DISCONNECTED) return

    // Snackbar events from ViewModel
    LaunchedEffect(snackbarMessage) {
        snackbarMessage?.let { msg ->
            snackbarHostState.showSnackbar(msg, duration = SnackbarDuration.Short)
        }
    }

    // Settings screen overlay
    if (showSettings) {
        SettingsScreen(
            viewModel = viewModel,
            onBack = { showSettings = false },
            onExitApp = onExitApp
        )
        return
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbarHostState) },
        topBar = {
            Column {
                TopAppBar(
                    title = {
                        Text("ClaudeRemote", fontWeight = FontWeight.Bold)
                    },
                    actions = {
                        val statusColor by animateColorAsState(
                            when (connectionState) {
                                ConnectionState.CONNECTED -> Color(0xFF10B981)
                                ConnectionState.RECONNECTING -> Color(0xFFF59E0B)
                                else -> Color(0xFFEF4444)
                            },
                            label = "statusDot"
                        )
                        Box(
                            modifier = Modifier
                                .padding(end = 4.dp)
                                .size(10.dp)
                                .clip(CircleShape)
                                .background(statusColor)
                        )

                        IconButton(onClick = { showSettings = true }) {
                            Icon(Icons.Default.Settings, contentDescription = "Settings")
                        }

                        IconButton(onClick = { viewModel.disconnect() }) {
                            Icon(Icons.Default.LinkOff, contentDescription = "Disconnect")
                        }
                    }
                )

                if (connectionState == ConnectionState.RECONNECTING) {
                    Surface(
                        modifier = Modifier.fillMaxWidth(),
                        color = Color(0xFFF59E0B)
                    ) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 16.dp, vertical = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.Center
                        ) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(14.dp),
                                strokeWidth = 2.dp,
                                color = Color.White
                            )
                            Spacer(modifier = Modifier.width(8.dp))
                            Text(
                                text = "Reconnecting to server...",
                                fontSize = 12.sp,
                                color = Color.White,
                                fontWeight = FontWeight.Medium,
                                textAlign = TextAlign.Center
                            )
                        }
                    }
                }
            }
        },
        bottomBar = {
            NavigationBar {
                NavigationBarItem(
                    icon = { Icon(Icons.Default.Chat, contentDescription = "Chat tab") },
                    label = { Text("Chat") },
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 }
                )
                NavigationBarItem(
                    icon = { Icon(Icons.Default.ManageAccounts, contentDescription = "Manage tab") },
                    label = { Text("Manage") },
                    selected = selectedTab == 1,
                    onClick = { selectedTab = 1 }
                )
            }
        }
    ) { padding ->
        Surface(modifier = Modifier.padding(padding)) {
            Crossfade(targetState = selectedTab, label = "tabContent") { tab ->
                when (tab) {
                    0 -> ChatScreen(viewModel)
                    1 -> ManageScreen(viewModel)
                }
            }
        }
    }
}
