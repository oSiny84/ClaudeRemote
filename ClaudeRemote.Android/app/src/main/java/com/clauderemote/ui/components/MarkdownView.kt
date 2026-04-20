package com.clauderemote.ui.components

import android.graphics.Typeface
import android.text.method.LinkMovementMethod
import android.util.TypedValue
import android.view.ViewGroup
import android.widget.TextView
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import io.noties.markwon.AbstractMarkwonPlugin
import io.noties.markwon.Markwon
import io.noties.markwon.MarkwonVisitor
import io.noties.markwon.core.MarkwonTheme
import io.noties.markwon.ext.strikethrough.StrikethroughPlugin
import io.noties.markwon.ext.tables.TablePlugin
import io.noties.markwon.ext.tables.TableTheme
import io.noties.markwon.ext.tasklist.TaskListPlugin
import io.noties.markwon.html.HtmlPlugin
import io.noties.markwon.image.ImagesPlugin
import io.noties.markwon.linkify.LinkifyPlugin
import org.commonmark.node.SoftLineBreak

/**
 * Rich markdown renderer.
 *
 * Uses Markwon (Android-native commonmark) via AndroidView — chosen for stability
 * against Compose version drift. Plugins cover:
 *  - Tables (GFM)
 *  - Strikethrough (~~text~~)
 *  - Task lists ([ ] / [x])
 *  - Linkify (auto-detect URLs)
 *  - HTML (<b>, <i>, <code>, <br>, etc.)
 *  - Images (loaded synchronously if data URI; network images noop without a loader)
 *
 * Theming pulls colors from [MaterialTheme] at call time so light/dark automatically adapt.
 */
@Composable
fun MarkdownView(
    markdown: String,
    modifier: Modifier = Modifier,
    textColor: Color = MaterialTheme.colorScheme.onSurface,
    linkColor: Color = MaterialTheme.colorScheme.primary,
    codeBgColor: Color = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.08f),
    codeTextColor: Color = MaterialTheme.colorScheme.primary,
    blockquoteColor: Color = MaterialTheme.colorScheme.primary,
    tableBorderColor: Color = MaterialTheme.colorScheme.outlineVariant,
    headingBreakColor: Color = MaterialTheme.colorScheme.outlineVariant,
    fontSizeSp: Float = 14f
) {
    val context = LocalContext.current
    val density = LocalDensity.current

    // Re-create Markwon only when colors change (theme swap). For a stable theme,
    // this resolves once per color-scheme and is reused across recompositions.
    val markwon = remember(
        textColor, linkColor, codeBgColor, codeTextColor,
        blockquoteColor, tableBorderColor, headingBreakColor, density
    ) {
        buildMarkwon(
            context = context,
            density = density,
            textColorArgb = textColor.toArgb(),
            linkColorArgb = linkColor.toArgb(),
            codeBgArgb = codeBgColor.toArgb(),
            codeTextArgb = codeTextColor.toArgb(),
            blockquoteArgb = blockquoteColor.toArgb(),
            tableBorderArgb = tableBorderColor.toArgb(),
            headingBreakArgb = headingBreakColor.toArgb()
        )
    }

    // Unwrap terminal-style line wrapping BEFORE Markwon parses it. Claude Code
    // emits real `\n` at terminal-width breakpoints inside sentences, which the
    // soft-break override would otherwise render as visible mid-sentence breaks.
    val normalized = remember(markdown) { unwrapTerminalLines(markdown) }

    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            TextView(ctx).apply {
                layoutParams = ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
                setTextIsSelectable(true)
                movementMethod = LinkMovementMethod.getInstance()
                setTextSize(TypedValue.COMPLEX_UNIT_SP, fontSizeSp)
                setLineSpacing(0f, 1.25f) // 1.25x line height
                typeface = Typeface.DEFAULT
            }
        },
        update = { tv ->
            tv.setTextColor(textColor.toArgb())
            tv.setLinkTextColor(linkColor.toArgb())
            tv.setTextSize(TypedValue.COMPLEX_UNIT_SP, fontSizeSp)
            markwon.setMarkdown(tv, normalized)
        }
    )
}

// --- Terminal wrap normalization --------------------------------------------

/**
 * Collapse terminal-wrap newlines to spaces while keeping intentional breaks.
 *
 * A single `\n` is treated as:
 *  - preserved when either side is blank (paragraph boundary) or the NEXT line
 *    starts a block element (list item, heading, blockquote, table row,
 *    ordered list, code fence).
 *  - replaced with a single space (terminal wrap) otherwise.
 *
 * Fenced code block contents are passed through verbatim.
 *
 * NOTE: The previous version also preserved `\n` after sentence-ending
 * punctuation (`.`, `?`, `:`, etc.). That heuristic has been removed — the
 * server now normalizes paragraph breaks authoritatively (`\n\n`), so the
 * client should not second-guess by inserting breaks after periods.
 * Combined with the SoftLineBreak visitor override, this yields:
 *   terminal-wrap → joined with space (no visible break)
 *   paragraph     → full paragraph break (from server `\n\n`)
 */
private fun unwrapTerminalLines(md: String): String {
    if (md.isEmpty()) return md
    val lines = md.split('\n')
    val sb = StringBuilder(md.length + 16)
    var inFence = false

    for (i in lines.indices) {
        val line = lines[i]
        sb.append(line)

        val isFenceToggle = line.trimStart().startsWith("```")
        if (isFenceToggle) inFence = !inFence

        if (i == lines.lastIndex) continue
        val next = lines[i + 1]

        val sep: Char = when {
            // Preserve everything inside fenced code blocks
            inFence || isFenceToggle -> '\n'
            // Blank line ⇒ paragraph break (preserve)
            line.isBlank() || next.isBlank() -> '\n'
            // Next line starts a block element (list/heading/quote/table/fence)
            isBlockStart(next) -> '\n'
            // Otherwise, treat as terminal wrap — join lines with a single space
            else -> ' '
        }
        sb.append(sep)
    }
    return sb.toString()
}

private fun isBlockStart(line: String): Boolean {
    val t = line.trimStart()
    if (t.isEmpty()) return false
    if (t.startsWith("#")) return true
    if (t.startsWith("```")) return true
    if (t.startsWith(">")) return true
    if (t.startsWith("|")) return true
    if (t.length >= 2 && t[0] in "-*+•" && t[1] == ' ') return true
    return ORDERED_LIST_REGEX.containsMatchIn(t)
}

private val ORDERED_LIST_REGEX = Regex("""^\d+[.)]\s""")

private fun buildMarkwon(
    context: android.content.Context,
    density: Density,
    textColorArgb: Int,
    linkColorArgb: Int,
    codeBgArgb: Int,
    codeTextArgb: Int,
    blockquoteArgb: Int,
    tableBorderArgb: Int,
    headingBreakArgb: Int
): Markwon {
    val p = { d: Dp -> with(density) { d.toPx().toInt() } }

    val tableTheme = TableTheme.Builder()
        .tableBorderColor(tableBorderArgb)
        .tableBorderWidth(p(1.dp))
        .tableCellPadding(p(8.dp))
        .tableHeaderRowBackgroundColor(codeBgArgb)
        .tableEvenRowBackgroundColor(0x00000000) // transparent
        .tableOddRowBackgroundColor(0x00000000)
        .build()

    return Markwon.builder(context)
        .usePlugin(StrikethroughPlugin.create())
        .usePlugin(TablePlugin.create(tableTheme))
        .usePlugin(TaskListPlugin.create(context))
        .usePlugin(LinkifyPlugin.create())
        .usePlugin(HtmlPlugin.create())
        .usePlugin(ImagesPlugin.create())
        // CommonMark collapses single `\n` into a space (SoftLineBreak). Claude
        // Code output uses real newlines for visual wrapping, so override the
        // visitor to render soft breaks as actual newlines instead of spaces.
        .usePlugin(object : AbstractMarkwonPlugin() {
            override fun configureVisitor(builder: MarkwonVisitor.Builder) {
                builder.on(SoftLineBreak::class.java) { visitor, _ ->
                    visitor.ensureNewLine()
                }
            }
        })
        .usePlugin(object : AbstractMarkwonPlugin() {
            override fun configureTheme(builder: MarkwonTheme.Builder) {
                builder
                    // Inline code
                    .codeTextColor(codeTextArgb)
                    .codeBackgroundColor(codeBgArgb)
                    // Fenced code blocks
                    .codeBlockTextColor(textColorArgb)
                    .codeBlockBackgroundColor(codeBgArgb)
                    .codeTypeface(Typeface.MONOSPACE)
                    .codeBlockTypeface(Typeface.MONOSPACE)
                    .codeTextSize(p(13.dp))
                    .codeBlockTextSize(p(13.dp))
                    .codeBlockMargin(p(8.dp))
                    // Block quote
                    .blockQuoteColor(blockquoteArgb)
                    .blockQuoteWidth(p(4.dp))
                    .blockMargin(p(8.dp))
                    // Lists
                    .bulletListItemStrokeWidth(p(1.dp))
                    .bulletWidth(p(6.dp))
                    .listItemColor(textColorArgb)
                    // Headings
                    .headingBreakColor(headingBreakArgb)
                    .headingBreakHeight(p(1.dp))
                    .headingTextSizeMultipliers(
                        floatArrayOf(1.6f, 1.35f, 1.2f, 1.1f, 1.0f, 0.95f)
                    )
                    // Links
                    .linkColor(linkColorArgb)
                    // Thematic break (---)
                    .thematicBreakColor(headingBreakArgb)
                    .thematicBreakHeight(p(1.dp))
            }
        })
        .build()
}
