package com.clauderemote.ui.screens

import android.widget.Toast
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clauderemote.data.model.ButtonInfo
import com.clauderemote.viewmodel.MainViewModel

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun ChatScreen(viewModel: MainViewModel) {
    val output by viewModel.claudeOutput.collectAsState()
    val outputScope by viewModel.outputScope.collectAsState()
    val claudeStatus by viewModel.claudeStatus.collectAsState()
    val isLoading by viewModel.isLoading.collectAsState()
    val actionButtons by viewModel.actionButtons.collectAsState()
    val actionButtonsPrompt by viewModel.actionButtonsPrompt.collectAsState()
    val buttonCategory by viewModel.buttonCategory.collectAsState()
    val commandText by viewModel.commandText.collectAsState()
    val commandHistory by viewModel.commandHistory.collectAsState()

    val scrollState = rememberScrollState()
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

    LaunchedEffect(output) {
        if (output.isNotEmpty()) scrollState.animateScrollTo(scrollState.maxValue)
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
                    if (output.isNotEmpty()) {
                        clipboardManager.setText(AnnotatedString(output))
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

        // === OUTPUT CONTENT ===
        Surface(
            modifier = Modifier
                .fillMaxWidth()
                .weight(1f),
            shape = RoundedCornerShape(12.dp),
            color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
        ) {
            when {
                isLoading -> {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            CircularProgressIndicator(modifier = Modifier.size(36.dp), strokeWidth = 3.dp)
                            Spacer(modifier = Modifier.height(12.dp))
                            Text("Loading output...", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 14.sp)
                        }
                    }
                }
                output.isEmpty() -> {
                    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text("No output yet", fontSize = 16.sp, fontWeight = FontWeight.Medium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Spacer(modifier = Modifier.height(8.dp))
                            Text("Tap Refresh to load", fontSize = 13.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f))
                        }
                    }
                }
                else -> {
                    MarkdownText(
                        text = output,
                        modifier = Modifier
                            .verticalScroll(scrollState)
                            .padding(12.dp)
                    )
                }
            }
        }

        // === ACTION BUTTONS ===
        if (actionButtons.isNotEmpty()) {
            val isPermission = buttonCategory == "permission"
            Spacer(modifier = Modifier.height(8.dp))

            if (isPermission) {
                // Permission header
                Text(
                    text = "Permission Required:",
                    fontSize = 13.sp,
                    fontWeight = FontWeight.Bold,
                    color = MaterialTheme.colorScheme.error
                )
                // Command preview
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
                // Selection UI
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
                        Icon(Icons.Default.ExpandMore, contentDescription = "Collapse",
                            modifier = Modifier.size(18.dp))
                    }
                }

                // History chips
                AnimatedVisibility(visible = showHistory && commandHistory.isNotEmpty()) {
                    LazyRow(
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        modifier = Modifier.padding(bottom = 4.dp)
                    ) {
                        items(commandHistory) { cmd ->
                            SuggestionChip(
                                onClick = { viewModel.updateCommandText(cmd) },
                                label = {
                                    Text(cmd, maxLines = 1, overflow = TextOverflow.Ellipsis, fontSize = 12.sp)
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
                Icon(Icons.Default.Send, contentDescription = "Send", modifier = Modifier.size(20.dp))
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
                colors = ButtonDefaults.buttonColors(
                    containerColor = Color(0xFF10B981)
                )
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

// --- Simple Markdown Renderer ---

@Composable
private fun MarkdownText(text: String, modifier: Modifier = Modifier) {
    val blocks = parseMarkdownBlocks(text)
    Column(modifier = modifier) {
        blocks.forEach { block ->
            when (block) {
                is MdBlock.CodeBlock -> CodeBlockView(block.language, block.code)
                is MdBlock.TextBlock -> {
                    Text(
                        text = parseInlineMarkdown(block.text),
                        fontFamily = FontFamily.Default,
                        fontSize = 13.sp,
                        color = MaterialTheme.colorScheme.onSurface,
                        lineHeight = 20.sp
                    )
                }
            }
            Spacer(modifier = Modifier.height(4.dp))
        }
    }
}

@Composable
private fun CodeBlockView(language: String, code: String) {
    val horizontalScroll = rememberScrollState()
    Column {
        if (language.isNotEmpty()) {
            Text(
                text = language,
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 8.dp, bottom = 2.dp)
            )
        }
        Surface(
            shape = RoundedCornerShape(8.dp),
            color = MaterialTheme.colorScheme.surface,
            tonalElevation = 2.dp,
            modifier = Modifier.fillMaxWidth()
        ) {
            Text(
                text = code,
                fontFamily = FontFamily.Monospace,
                fontSize = 12.sp,
                lineHeight = 18.sp,
                color = MaterialTheme.colorScheme.onSurface,
                modifier = Modifier
                    .horizontalScroll(horizontalScroll)
                    .padding(12.dp)
            )
        }
    }
}

// --- Markdown parsing helpers ---

private sealed class MdBlock {
    data class CodeBlock(val language: String, val code: String) : MdBlock()
    data class TextBlock(val text: String) : MdBlock()
}

private fun parseMarkdownBlocks(text: String): List<MdBlock> {
    val blocks = mutableListOf<MdBlock>()
    val lines = text.lines()
    var i = 0
    while (i < lines.size) {
        val line = lines[i]
        if (line.trimStart().startsWith("```")) {
            val lang = line.trimStart().removePrefix("```").trim()
            val codeLines = mutableListOf<String>()
            i++
            while (i < lines.size && !lines[i].trimStart().startsWith("```")) {
                codeLines.add(lines[i])
                i++
            }
            blocks.add(MdBlock.CodeBlock(lang, codeLines.joinToString("\n")))
            i++
        } else {
            val textLines = mutableListOf<String>()
            while (i < lines.size && !lines[i].trimStart().startsWith("```")) {
                textLines.add(lines[i])
                i++
            }
            val joined = textLines.joinToString("\n").trim()
            if (joined.isNotEmpty()) {
                blocks.add(MdBlock.TextBlock(joined))
            }
        }
    }
    return blocks
}

@Composable
private fun parseInlineMarkdown(text: String): AnnotatedString {
    val codeColor = MaterialTheme.colorScheme.primary.copy(alpha = 0.8f)
    val codeBg = MaterialTheme.colorScheme.primary.copy(alpha = 0.1f)

    return buildAnnotatedString {
        var i = 0
        val chars = text.toCharArray()
        while (i < chars.size) {
            when {
                // Bold: **text**
                i + 1 < chars.size && chars[i] == '*' && chars[i + 1] == '*' -> {
                    val end = text.indexOf("**", i + 2)
                    if (end != -1) {
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) {
                            append(text.substring(i + 2, end))
                        }
                        i = end + 2
                    } else { append(chars[i]); i++ }
                }
                // Italic: *text*
                chars[i] == '*' && (i == 0 || chars[i - 1] != '*') -> {
                    val end = text.indexOf('*', i + 1)
                    if (end != -1 && (end + 1 >= chars.size || chars[end + 1] != '*')) {
                        withStyle(SpanStyle(fontStyle = FontStyle.Italic)) {
                            append(text.substring(i + 1, end))
                        }
                        i = end + 1
                    } else { append(chars[i]); i++ }
                }
                // Inline code: `text`
                chars[i] == '`' -> {
                    val end = text.indexOf('`', i + 1)
                    if (end != -1) {
                        withStyle(SpanStyle(
                            fontFamily = FontFamily.Monospace,
                            color = codeColor,
                            background = codeBg,
                            fontSize = 12.sp
                        )) {
                            append(" ${text.substring(i + 1, end)} ")
                        }
                        i = end + 1
                    } else { append(chars[i]); i++ }
                }
                // Heading: # at start of line
                (i == 0 || chars[i - 1] == '\n') && chars[i] == '#' -> {
                    var level = 0
                    var j = i
                    while (j < chars.size && chars[j] == '#') { level++; j++ }
                    if (j < chars.size && chars[j] == ' ') {
                        val lineEnd = text.indexOf('\n', j).let { if (it == -1) chars.size else it }
                        val headingText = text.substring(j + 1, lineEnd)
                        val fontSize = when (level) { 1 -> 18.sp; 2 -> 16.sp; else -> 14.sp }
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold, fontSize = fontSize)) {
                            append(headingText)
                        }
                        append("\n")
                        i = lineEnd + 1
                    } else { append(chars[i]); i++ }
                }
                // List items: - or * at start of line
                (i == 0 || chars[i - 1] == '\n') && (chars[i] == '-' || chars[i] == '*')
                        && i + 1 < chars.size && chars[i + 1] == ' ' -> {
                    append("  \u2022 ")
                    i += 2
                }
                else -> { append(chars[i]); i++ }
            }
        }
    }
}
