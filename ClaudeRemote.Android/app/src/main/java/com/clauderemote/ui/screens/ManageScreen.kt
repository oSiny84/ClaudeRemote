package com.clauderemote.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clauderemote.data.model.ProjectInfo
import com.clauderemote.data.model.SessionInfo
import com.clauderemote.ui.components.FileBrowserDialog
import com.clauderemote.ui.components.UsageDashboardDialog
import com.clauderemote.viewmodel.MainViewModel

@Composable
fun ManageScreen(viewModel: MainViewModel) {
    val sessions by viewModel.sessions.collectAsState()
    val projects by viewModel.projects.collectAsState()
    val isLoading by viewModel.isLoading.collectAsState()
    val showUsageDashboard by viewModel.showUsageDashboard.collectAsState()
    val usageDashboard by viewModel.usageDashboard.collectAsState()
    val usageDashboardLoading by viewModel.usageDashboardLoading.collectAsState()
    val showFileBrowser by viewModel.showFileBrowser.collectAsState()
    val fileEntries by viewModel.fileEntries.collectAsState()
    val filePath by viewModel.currentPath.collectAsState()
    val fileParentPath by viewModel.parentPath.collectAsState()
    val fileBrowserLoading by viewModel.fileBrowserLoading.collectAsState()

    // There is at most one "focused" expanded project whose sessions are displayed.
    // If the server returns multiple expanded, we pick the first.
    val expandedProject = projects.firstOrNull { it.expanded }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(16.dp)
    ) {
        // View detailed usage — opens UsageDashboardDialog
        OutlinedButton(
            onClick = { viewModel.openUsageDashboard() },
            modifier = Modifier.fillMaxWidth()
        ) {
            Icon(
                Icons.Default.Analytics,
                contentDescription = null,
                modifier = Modifier.size(18.dp)
            )
            Spacer(Modifier.width(8.dp))
            Text("View detailed usage")
        }
        Spacer(Modifier.height(8.dp))

        // Browse files — opens FileBrowserDialog
        OutlinedButton(
            onClick = { viewModel.openFileBrowser() },
            modifier = Modifier.fillMaxWidth()
        ) {
            Icon(
                Icons.Default.FolderOpen,
                contentDescription = null,
                modifier = Modifier.size(18.dp)
            )
            Spacer(Modifier.width(8.dp))
            Text("Browse files")
        }
        Spacer(Modifier.height(16.dp))

        // Projects Section (moved to top — drives Sessions display below)
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text("Projects", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            IconButton(onClick = { viewModel.requestProjects() }) {
                Icon(Icons.Default.Refresh, contentDescription = "Refresh projects", modifier = Modifier.size(20.dp))
            }
        }

        if (projects.isEmpty()) {
            EmptyStateBox(
                message = if (isLoading) "Loading projects..." else "No projects. Tap refresh to load.",
                isLoading = isLoading
            )
        } else {
            LazyColumn(
                modifier = Modifier.weight(1f),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(projects) { project ->
                    ProjectCard(project) { viewModel.selectProject(project.id) }
                }
            }
        }

        // Sessions Section (below Projects — depends on expanded project)
        Spacer(modifier = Modifier.height(20.dp))

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = if (expandedProject != null) "Sessions - ${expandedProject.name}" else "Sessions",
                fontWeight = FontWeight.SemiBold,
                fontSize = 16.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f)
            )
            Row {
                IconButton(
                    onClick = { viewModel.requestSessions() },
                    enabled = expandedProject != null
                ) {
                    Icon(Icons.Default.Refresh, contentDescription = "Refresh sessions", modifier = Modifier.size(20.dp))
                }
                IconButton(
                    onClick = { viewModel.addSession() },
                    enabled = expandedProject != null
                ) {
                    Icon(Icons.Default.Add, contentDescription = "Add session", modifier = Modifier.size(20.dp))
                }
            }
        }

        when {
            expandedProject == null -> {
                EmptyStateBox(
                    message = "Select a project to view sessions",
                    isLoading = false
                )
            }
            sessions.isEmpty() -> {
                EmptyStateBox(
                    message = if (isLoading) "Loading sessions..." else "No sessions in this project.",
                    isLoading = isLoading
                )
            }
            else -> {
                LazyColumn(
                    modifier = Modifier.weight(1f),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    items(sessions) { session ->
                        SessionCard(session) { viewModel.selectSession(session.id) }
                    }
                }
            }
        }
    }

    // Usage Dashboard dialog (overlay — doesn't occupy layout space)
    if (showUsageDashboard) {
        UsageDashboardDialog(
            dashboard = usageDashboard,
            isLoading = usageDashboardLoading,
            onRefresh = { viewModel.requestUsageDashboard() },
            onDismiss = { viewModel.closeUsageDashboard() }
        )
    }

    // File Browser bottom sheet
    if (showFileBrowser) {
        FileBrowserDialog(
            currentPath = filePath,
            parentPath = fileParentPath,
            entries = fileEntries,
            isLoading = fileBrowserLoading,
            onBrowseDirectory = { viewModel.browseDirectory(it) },
            onBrowseParent = { viewModel.browseParent() },
            onDownload = { viewModel.requestDownload(it) },
            onDismiss = { viewModel.closeFileBrowser() }
        )
    }
}

@Composable
private fun EmptyStateBox(message: String, isLoading: Boolean = false) {
    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .height(80.dp),
        shape = RoundedCornerShape(12.dp),
        color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
    ) {
        Box(contentAlignment = Alignment.Center) {
            if (isLoading) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(20.dp),
                        strokeWidth = 2.dp
                    )
                    Text(
                        text = message,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        fontSize = 14.sp
                    )
                }
            } else {
                Text(
                    text = message,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    fontSize = 14.sp
                )
            }
        }
    }
}

@Composable
private fun SessionCard(session: SessionInfo, onClick: () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(
            containerColor = if (session.active)
                MaterialTheme.colorScheme.primaryContainer
            else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
        )
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                Icons.Default.ChatBubbleOutline,
                contentDescription = null,
                modifier = Modifier.size(20.dp),
                tint = if (session.active) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(modifier = Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = session.name,
                    fontWeight = FontWeight.Medium,
                    fontSize = 14.sp
                )
                if (session.lastMessage.isNotEmpty()) {
                    Text(
                        text = session.lastMessage,
                        fontSize = 12.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
            if (session.active) {
                Icon(
                    Icons.Default.Check,
                    contentDescription = "Active session",
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(18.dp)
                )
            }
        }
    }
}

@Composable
private fun ProjectCard(project: ProjectInfo, onClick: () -> Unit) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        shape = RoundedCornerShape(12.dp),
        colors = CardDefaults.cardColors(
            containerColor = if (project.expanded)
                MaterialTheme.colorScheme.primaryContainer
            else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
        ),
        border = if (project.expanded)
            BorderStroke(2.dp, MaterialTheme.colorScheme.primary)
        else null
    ) {
        Row(
            modifier = Modifier.padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                imageVector = if (project.expanded) Icons.Default.FolderOpen else Icons.Default.Folder,
                contentDescription = if (project.expanded) "Expanded" else "Collapsed",
                modifier = Modifier.size(20.dp),
                tint = if (project.expanded) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(modifier = Modifier.width(12.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = project.name,
                    fontWeight = if (project.expanded) FontWeight.Bold else FontWeight.Medium,
                    fontSize = 14.sp,
                    color = if (project.expanded) MaterialTheme.colorScheme.primary
                    else MaterialTheme.colorScheme.onSurface
                )
                if (project.path.isNotEmpty()) {
                    Text(
                        text = project.path,
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
            Icon(
                imageVector = if (project.expanded) Icons.Default.KeyboardArrowDown
                else Icons.Default.KeyboardArrowRight,
                contentDescription = null,
                tint = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.size(20.dp)
            )
        }
    }
}
