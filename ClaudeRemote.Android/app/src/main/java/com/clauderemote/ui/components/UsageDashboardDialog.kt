package com.clauderemote.ui.components

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clauderemote.data.model.ContextWindow
import com.clauderemote.data.model.UsageDashboard
import com.clauderemote.data.model.UsageLimit

@Composable
fun UsageDashboardDialog(
    dashboard: UsageDashboard?,
    isLoading: Boolean,
    onRefresh: () -> Unit,
    onDismiss: () -> Unit
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = {
            Text(
                "Usage Dashboard",
                fontWeight = FontWeight.Bold,
                fontSize = 18.sp
            )
        },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(max = 520.dp)
                    .verticalScroll(rememberScrollState())
            ) {
                when {
                    isLoading && dashboard == null -> {
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(vertical = 40.dp),
                            contentAlignment = Alignment.Center
                        ) {
                            CircularProgressIndicator()
                        }
                    }
                    dashboard == null -> {
                        Text(
                            "No usage data available. Tap Refresh to load.",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 13.sp,
                            modifier = Modifier.padding(vertical = 16.dp)
                        )
                    }
                    else -> {
                        DashboardContent(dashboard, isLoading)
                    }
                }
            }
        },
        confirmButton = {
            TextButton(
                onClick = onRefresh,
                enabled = !isLoading
            ) {
                Icon(
                    Icons.Default.Refresh,
                    contentDescription = null,
                    modifier = Modifier.size(16.dp)
                )
                Spacer(Modifier.width(6.dp))
                Text("Refresh")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Close")
            }
        }
    )
}

@Composable
private fun DashboardContent(dashboard: UsageDashboard, isLoading: Boolean) {
    // Current usage section: context window + 5-hour limit
    if (dashboard.contextWindow != null || dashboard.fiveHourLimit != null) {
        SectionHeader("Current usage")
        dashboard.contextWindow?.let {
            ContextWindowRow(it)
            Spacer(Modifier.height(14.dp))
        }
        dashboard.fiveHourLimit?.let {
            UsageLimitRow(fallbackTitle = "5-hour limit", limit = it)
        }
        Spacer(Modifier.height(18.dp))
    }

    // Weekly limits section: all models + sonnet only
    if (dashboard.weeklyAllModels != null || dashboard.weeklySonnetOnly != null) {
        SectionHeader("Weekly limits")
        dashboard.weeklyAllModels?.let {
            UsageLimitRow(fallbackTitle = "All models", limit = it)
            Spacer(Modifier.height(14.dp))
        }
        dashboard.weeklySonnetOnly?.let {
            UsageLimitRow(fallbackTitle = "Sonnet only", limit = it)
        }
        Spacer(Modifier.height(18.dp))
    }

    // Footer: model + plan + fetched timestamp
    HorizontalDivider(
        color = MaterialTheme.colorScheme.outlineVariant,
        modifier = Modifier.padding(bottom = 10.dp)
    )
    dashboard.modelName?.let {
        FooterLine(label = "Model", value = it)
    }
    dashboard.planName?.let {
        FooterLine(label = "Plan", value = it)
    }

    if (dashboard.fetchedAt != null || isLoading) {
        Spacer(Modifier.height(4.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (isLoading) {
                CircularProgressIndicator(
                    modifier = Modifier.size(12.dp),
                    strokeWidth = 1.5f.dp
                )
                Spacer(Modifier.width(6.dp))
            }
            dashboard.fetchedAt?.let {
                Text(
                    text = "Fetched: $it",
                    fontSize = 10.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    }
}

@Composable
private fun SectionHeader(text: String) {
    Text(
        text = text,
        fontWeight = FontWeight.SemiBold,
        fontSize = 13.sp,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(bottom = 10.dp)
    )
}

@Composable
private fun ContextWindowRow(ctx: ContextWindow) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Text(
            text = "Context window",
            fontSize = 13.sp,
            fontWeight = FontWeight.Medium,
            color = MaterialTheme.colorScheme.onSurface
        )
        // "142k / 200k tokens" — combine usedText + totalText if present
        val detail = when {
            ctx.usedText != null && ctx.totalText != null -> "${ctx.usedText} / ${ctx.totalText}"
            ctx.usedText != null -> ctx.usedText
            ctx.totalText != null -> "of ${ctx.totalText}"
            else -> null
        }
        detail?.let {
            Text(
                text = it,
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        Spacer(Modifier.height(6.dp))

        val percent = (ctx.percentUsed ?: 0).coerceIn(0, 100)
        LinearProgressIndicator(
            progress = { percent / 100f },
            modifier = Modifier
                .fillMaxWidth()
                .height(6.dp),
            color = progressColorFor(percent),
            trackColor = MaterialTheme.colorScheme.surfaceVariant
        )

        Text(
            text = "$percent% used",
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 2.dp)
        )
    }
}

@Composable
private fun UsageLimitRow(fallbackTitle: String, limit: UsageLimit) {
    Column(modifier = Modifier.fillMaxWidth()) {
        Text(
            text = limit.label ?: fallbackTitle,
            fontSize = 13.sp,
            fontWeight = FontWeight.Medium,
            color = MaterialTheme.colorScheme.onSurface
        )
        limit.resetText?.let {
            Text(
                text = it,
                fontSize = 11.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        Spacer(Modifier.height(6.dp))

        val percent = (limit.percentUsed ?: 0).coerceIn(0, 100)
        LinearProgressIndicator(
            progress = { percent / 100f },
            modifier = Modifier
                .fillMaxWidth()
                .height(6.dp),
            color = progressColorFor(percent),
            trackColor = MaterialTheme.colorScheme.surfaceVariant
        )

        Text(
            text = "$percent% used",
            fontSize = 11.sp,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 2.dp)
        )
    }
}

@Composable
private fun FooterLine(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(
            text = "$label:",
            fontSize = 12.sp,
            fontWeight = FontWeight.Medium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.width(52.dp)
        )
        Text(
            text = value,
            fontSize = 12.sp,
            color = MaterialTheme.colorScheme.onSurface
        )
    }
}

@Composable
private fun progressColorFor(percent: Int): Color = when {
    percent >= 80 -> MaterialTheme.colorScheme.error
    percent >= 50 -> Color(0xFFF59E0B)
    else -> MaterialTheme.colorScheme.primary
}
