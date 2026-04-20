package com.clauderemote.ui.screens

import android.widget.Toast
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clauderemote.data.model.ButtonInfo
import com.clauderemote.data.model.ChatMessage
import com.clauderemote.ui.components.MessageBubble
import com.clauderemote.viewmodel.MainViewModel

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ChatScreen(viewModel: MainViewModel) {
    val chatMessages by viewModel.chatMessages.collectAsState()
    val outputScope by viewModel.outputScope.collectAsState()
    val claudeStatus by viewModel.claudeStatus.collectAsState()
    val isLoading by viewModel.isLoading.collectAsState()
    val actionButtons by viewModel.actionButtons.collectAsState()
    val actionButtonsPrompt by viewModel.actionButtonsPrompt.collectAsState()
    val buttonCategory by viewModel.buttonCategory.collectAsState()
    val commandText by viewModel.commandText.collectAsState()
    val commandHistory by viewModel.commandHistory.collectAsState()

    val listState = rememberLazyListState()
    val clipboardManager = LocalClipboardManager.current
    val context = LocalContext.current
    val haptic = LocalHapticFeedback.current
    var clickedButtonId by remember { mutableStateOf<String?>(null) }
    var showQuickCommands by remember { mutableStateOf(true) }
    var showHistory by remember { mutableStateOf(false) }

    LaunchedEffect(actionButtons) { clickedButtonId = null }

    // Haptic feedback for permission requests
    LaunchedEffect(actionButtons, buttonCategory) {
        if (actionButtons.isNotEmpty() && buttonCategory == "permission") {
            haptic.performHapticFeedback(HapticFeedbackType.LongPress)
        }
    }

    // Auto-scroll to the newest message when the list grows
    LaunchedEffect(chatMessages.size) {
        if (chatMessages.isNotEmpty()) {
            listState.animateScrollToItem(chatMessages.lastIndex)
        }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(start = 12.dp, end = 12.dp, top = 8.dp)
    ) {
        // === HEADER ===
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column {
                Text("Claude Output", style = MaterialTheme.typography.titleMedium)
                Text(
                    text = buildString {
                        if (claudeStatus.claudeRunning) append("Claude is running")
                        else append("Claude not detected")
                        if (claudeStatus.isGenerating) append(" · Generating...")
                    },
                    style = MaterialTheme.typography.bodySmall,
                    color = if (claudeStatus.isGenerating) MaterialTheme.colorScheme.primary
                    else MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            Row {
                IconButton(onClick = { viewModel.requestOutput(outputScope) }) {
                    Icon(Icons.Default.Refresh, contentDescription = "Refresh")
                }
                IconButton(onClick = {
                    if (chatMessages.isNotEmpty()) {
                        val joined = chatMessages.joinToString("\n\n") {
                            "[${it.role}]\n${it.content}"
                        }
                        clipboardManager.setText(AnnotatedString(joined))
                        Toast.makeText(context, "Copied!", Toast.LENGTH_SHORT).show()
                    }
                }) {
                    Icon(Icons.Default.ContentCopy, contentDescription = "Copy")
                }
            }
        }

        // === SCOPE TOGGLE ===
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            listOf("full" to "Full", "latest" to "Latest", "summary" to "Summary").forEach { (scope, label) ->
                FilterChip(
                    selected = outputScope == scope,
                    onClick = { viewModel.requestOutput(scope) },
                    label = { Text(label, fontSize = 12.sp) },
                    shape = RoundedCornerShape(8.dp)
                )
            }
        }

        Spacer(modifier = Modifier.height(8.dp))

        // === CHAT MESSAGES ===
        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f),
            shape = RoundedCornerShape(12.dp),
            color = MaterialTheme.colorScheme.surface
        ) {
            when {
                isLoading && chatMessages.isEmpty() -> {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(36.dp),
                                strokeWidth = 3.dp
                            )
                            Spacer(modifier = Modifier.height(12.dp))
                            Text(
                                "Loading output...",
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                fontSize = 14.sp
                            )
                        }
                    }
                }
                chatMessages.isEmpty() -> {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(
                                "No messages yet",
                                fontSize = 16.sp,
                                fontWeight = FontWeight.Medium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                            Spacer(modifier = Modifier.height(8.dp))
                            Text(
                                "Tap Refresh to load",
                                fontSize = 13.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f)
                            )
                        }
                    }
                }
                else -> {
                    LazyColumn(
                        state = listState,
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 8.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        itemsIndexed(
                            items = chatMessages,
                            // index + role is stable for an append-only chat log
                            key = { index, msg -> "$index:${msg.role}" }
                        ) { _, msg ->
                            MessageBubble(role = msg.role, content = msg.content)
                        }
                    }
                }
            }
        }

        // === ACTION BUTTONS ===
        if (actionButtons.isNotEmpty()) {
            val isPermission = buttonCategory == "permission"
            Spacer(modifier = Modifier.height(8.dp))

            if (isPermission) {
                Text(
                    text = "Permission Required:",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.error
                )
                if (!actionButtonsPrompt.isNullOrEmpty()) {
                    Spacer(modifier = Modifier.height(4.dp))
                    Surface(
                        shape = RoundedCornerShape(8.dp),
                        color = MaterialTheme.colorScheme.errorContainer.copy(alpha = 0.3f),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Text(
                            text = actionButtonsPrompt!!,
                            fontFamily = FontFamily.Monospace,
                            fontSize = 12.sp,
                            lineHeight = 16.sp,
                            color = MaterialTheme.colorScheme.onErrorContainer,
                            modifier = Modifier.padding(8.dp)
                        )
                    }
                }
                Spacer(modifier = Modifier.height(6.dp))
                FlowRow(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    actionButtons.forEach { button ->
                        PermissionButton(
                            button = button,
                            isClicked = clickedButtonId == button.id,
                            enabled = clickedButtonId == null,
                            onClick = {
                                clickedButtonId = button.id
                                viewModel.clickButton(button.id)
                            }
                        )
                    }
                }
            } else {
                Text(
                    text = actionButtonsPrompt ?: "Claude is asking:",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Medium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(modifier = Modifier.height(6.dp))
                FlowRow(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    actionButtons.forEach { button ->
                        ActionButton(
                            button = button,
                            isClicked = clickedButtonId == button.id,
                            enabled = clickedButtonId == null,
                            onClick = {
                                clickedButtonId = button.id
                                viewModel.clickButton(button.id)
                            }
                        )
                    }
                }
            }
        }

        Spacer(modifier = Modifier.height(8.dp))

        // === QUICK COMMANDS (collapsible) ===
        AnimatedVisibility(visible = showQuickCommands) {
            Column {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    SuggestionChip(
                        onClick = { viewModel.sendQuickInput("continue") },
                        label = { Text("Continue", fontSize = 12.sp) }
                    )
                    SuggestionChip(
                        onClick = { viewModel.sendQuickInput("/stop") },
                        label = { Text("Stop", fontSize = 12.sp) }
                    )
                    SuggestionChip(
                        onClick = { viewModel.addSession() },
                        label = { Text("New", fontSize = 12.sp) }
                    )
                    Spacer(modifier = Modifier.weight(1f))
                    if (commandHistory.isNotEmpty()) {
                        IconButton(
                            onClick = { showHistory = !showHistory },
                            modifier = Modifier.size(32.dp)
                        ) {
                            Icon(
                                Icons.Default.History,
                                contentDescription = "History",
                                modifier = Modifier.size(18.dp),
                                tint = if (showHistory) MaterialTheme.colorScheme.primary
                                else MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    }
                    IconButton(
                        onClick = { showQuickCommands = false },
                        modifier = Modifier.size(32.dp)
                    ) {
                        Icon(
                            Icons.Default.ExpandMore,
                            contentDescription = "Collapse",
                            modifier = Modifier.size(18.dp)
                        )
                    }
                }

                AnimatedVisibility(visible = showHistory && commandHistory.isNotEmpty()) {
                    LazyRow(
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        modifier = Modifier.padding(bottom = 4.dp)
                    ) {
                        items(commandHistory) { cmd ->
                            SuggestionChip(
                                onClick = { viewModel.updateCommandText(cmd) },
                                label = {
                                    Text(
                                        cmd,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                        fontSize = 12.sp
                                    )
                                },
                                modifier = Modifier.widthIn(max = 160.dp)
                            )
                        }
                    }
                }
            }
        }

        // === INPUT ROW ===
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 4.dp),
            verticalAlignment = Alignment.Bottom,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            if (!showQuickCommands) {
                IconButton(
                    onClick = { showQuickCommands = true },
                    modifier = Modifier.size(40.dp)
                ) {
                    Icon(Icons.Default.ExpandLess, contentDescription = "Expand quick commands")
                }
            }
            OutlinedTextField(
                value = commandText,
                onValueChange = { viewModel.updateCommandText(it) },
                modifier = Modifier.weight(1f),
                placeholder = { Text("Enter command...", fontSize = 14.sp) },
                shape = RoundedCornerShape(12.dp),
                maxLines = 3
            )
            FilledIconButton(
                onClick = { viewModel.sendInput() },
                enabled = commandText.isNotBlank(),
                modifier = Modifier.size(44.dp)
            ) {
                Icon(
                    Icons.Default.Send,
                    contentDescription = "Send",
                    modifier = Modifier.size(20.dp)
                )
            }
        }
    }
}

// --- Action Button ---

@Composable
private fun ActionButton(
    button: ButtonInfo,
    isClicked: Boolean,
    enabled: Boolean,
    onClick: () -> Unit
) {
    FilledTonalButton(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(8.dp),
        contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp)
    ) {
        if (isClicked) {
            CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
            Spacer(modifier = Modifier.width(6.dp))
        }
        Text(button.text, fontSize = 14.sp)
    }
}

@Composable
private fun PermissionButton(
    button: ButtonInfo,
    isClicked: Boolean,
    enabled: Boolean,
    onClick: () -> Unit
) {
    val text = button.text.lowercase()
    val spinner: @Composable () -> Unit = {
        if (isClicked) {
            CircularProgressIndicator(modifier = Modifier.size(16.dp), strokeWidth = 2.dp)
            Spacer(modifier = Modifier.width(6.dp))
        }
    }

    when {
        text.contains("deny") || text.contains("reject") -> {
            OutlinedButton(
                onClick = onClick,
                enabled = enabled,
                shape = RoundedCornerShape(8.dp),
                colors = ButtonDefaults.outlinedButtonColors(
                    contentColor = MaterialTheme.colorScheme.error
                ),
                border = BorderStroke(
                    1.dp,
                    MaterialTheme.colorScheme.error.copy(alpha = if (enabled) 1f else 0.3f)
                )
            ) {
                spinner()
                Text(button.text, fontSize = 14.sp)
            }
        }
        text.contains("always") -> {
            Button(
                onClick = onClick,
                enabled = enabled,
                shape = RoundedCornerShape(8.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF10B981))
            ) {
                spinner()
                Text(button.text, fontSize = 14.sp, color = Color.White)
            }
        }
        else -> {
            FilledTonalButton(
                onClick = onClick,
                enabled = enabled,
                shape = RoundedCornerShape(8.dp)
            ) {
                spinner()
                Text(button.text, fontSize = 14.sp)
            }
        }
    }
}
