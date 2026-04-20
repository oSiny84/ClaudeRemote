package com.clauderemote.ui.components

import android.util.Log
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.clauderemote.data.model.FileEntry

private const val TAG = "FileBrowser"
private val DRIVE_REGEX = Regex("^[A-Za-z]:\\\\?$")

/**
 * 드라이브/디렉토리 판별.
 * 서버가 "directory" 외에 "drive", "dir", "folder" 등으로 보내는 경우와
 * type 필드가 잘못 와도 name이 드라이브 패턴(C:, D:\)이면 디렉토리로 취급.
 */
private fun FileEntry.isDirectoryLike(): Boolean {
    val t = type.lowercase()
    return t == "directory" || t == "dir" || t == "drive" || t == "folder"
        || DRIVE_REGEX.matches(name)
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FileBrowserDialog(
    currentPath: String,
    parentPath: String?,
    entries: List<FileEntry>,
    isLoading: Boolean,
    onBrowseDirectory: (fullPath: String) -> Unit,
    onBrowseParent: () -> Unit,
    onDownload: (fullPath: String) -> Unit,
    onDismiss: () -> Unit
) {
    val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)

    // Download confirmation
    var fileToDownload by remember { mutableStateOf<FileEntry?>(null) }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = sheetState,
        modifier = Modifier.fillMaxHeight(0.85f)
    ) {
        Column(modifier = Modifier.fillMaxSize()) {
            // Header: title + path
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(
                    Icons.Default.Folder,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(24.dp)
                )
                Spacer(Modifier.width(10.dp))
                Text(
                    text = "Files",
                    fontWeight = FontWeight.Bold,
                    fontSize = 18.sp,
                    modifier = Modifier.weight(1f)
                )
            }

            // Current path display
            Surface(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp),
                shape = RoundedCornerShape(8.dp),
                color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.5f)
            ) {
                Text(
                    text = currentPath.ifEmpty { "Drives" },
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)
                )
            }

            // Back button
            if (parentPath != null) {
                TextButton(
                    onClick = onBrowseParent,
                    modifier = Modifier.padding(horizontal = 8.dp),
                    enabled = !isLoading
                ) {
                    Icon(
                        Icons.AutoMirrored.Filled.ArrowBack,
                        contentDescription = null,
                        modifier = Modifier.size(18.dp)
                    )
                    Spacer(Modifier.width(6.dp))
                    Text("Back")
                }
            }

            HorizontalDivider(modifier = Modifier.padding(horizontal = 16.dp))

            // File list
            when {
                isLoading -> {
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .fillMaxWidth(),
                        contentAlignment = Alignment.Center
                    ) {
                        CircularProgressIndicator()
                    }
                }
                entries.isEmpty() -> {
                    Box(
                        modifier = Modifier
                            .weight(1f)
                            .fillMaxWidth(),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            "Empty directory",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 14.sp
                        )
                    }
                }
                else -> {
                    LazyColumn(
                        modifier = Modifier.weight(1f),
                        contentPadding = PaddingValues(horizontal = 8.dp, vertical = 4.dp)
                    ) {
                        items(entries) { entry ->
                            FileEntryRow(
                                entry = entry,
                                onClick = {
                                    val isDir = entry.isDirectoryLike()
                                    Log.d(
                                        TAG,
                                        "tap: name='${entry.name}' path='${entry.path}' " +
                                            "type='${entry.type}' size=${entry.size} " +
                                            "isDir=$isDir currentPath='$currentPath'"
                                    )
                                    if (isDir) {
                                        onBrowseDirectory(entry.path)
                                    } else {
                                        fileToDownload = entry
                                    }
                                }
                            )
                        }
                    }
                }
            }

            // Close button
            HorizontalDivider(modifier = Modifier.padding(horizontal = 16.dp))
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(16.dp),
                contentAlignment = Alignment.Center
            ) {
                TextButton(onClick = onDismiss) {
                    Text("Close")
                }
            }
        }
    }

    // Download confirmation dialog
    fileToDownload?.let { file ->
        AlertDialog(
            onDismissRequest = { fileToDownload = null },
            icon = {
                Icon(
                    Icons.Default.Download,
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary
                )
            },
            title = { Text("Download File") },
            text = {
                Text("Download ${file.name} (${formatFileSize(file.size)})?")
            },
            confirmButton = {
                TextButton(
                    onClick = {
                        onDownload(file.path)
                        fileToDownload = null
                    }
                ) {
                    Text("Download")
                }
            },
            dismissButton = {
                TextButton(onClick = { fileToDownload = null }) {
                    Text("Cancel")
                }
            }
        )
    }
}

@Composable
private fun FileEntryRow(entry: FileEntry, onClick: () -> Unit) {
    val isDir = entry.isDirectoryLike()

    Surface(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.surface
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                imageVector = fileIconFor(entry.name, isDir),
                contentDescription = null,
                modifier = Modifier.size(24.dp),
                tint = if (isDir) MaterialTheme.colorScheme.primary
                else MaterialTheme.colorScheme.onSurfaceVariant
            )
            Spacer(Modifier.width(14.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = entry.name + if (isDir) "/" else "",
                    fontWeight = if (isDir) FontWeight.Medium else FontWeight.Normal,
                    fontSize = 14.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
                if (!isDir && entry.size > 0) {
                    Text(
                        text = formatFileSize(entry.size),
                        fontSize = 11.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
            if (!isDir) {
                Icon(
                    Icons.Default.Download,
                    contentDescription = "Download",
                    tint = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.size(18.dp)
                )
            }
        }
    }
}

private fun fileIconFor(name: String, isDirectory: Boolean): ImageVector {
    if (isDirectory) return Icons.Default.Folder
    val ext = name.substringAfterLast('.', "").lowercase()
    return when (ext) {
        "apk" -> Icons.Default.Android
        "zip", "rar", "7z", "tar", "gz", "tgz" -> Icons.Default.Archive
        "jpg", "jpeg", "png", "gif", "bmp", "webp", "svg" -> Icons.Default.Image
        "mp4", "avi", "mkv", "mov", "webm" -> Icons.Default.VideoFile
        "mp3", "wav", "flac", "ogg", "aac" -> Icons.Default.AudioFile
        "pdf" -> Icons.Default.PictureAsPdf
        else -> Icons.Default.InsertDriveFile
    }
}

private fun formatFileSize(bytes: Long): String = when {
    bytes >= 1_073_741_824 -> "%.1f GB".format(bytes / 1_073_741_824.0)
    bytes >= 1_048_576 -> "%.1f MB".format(bytes / 1_048_576.0)
    bytes >= 1_024 -> "%.1f KB".format(bytes / 1_024.0)
    else -> "$bytes B"
}
