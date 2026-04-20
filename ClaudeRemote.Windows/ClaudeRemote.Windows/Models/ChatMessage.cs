namespace ClaudeRemote.Windows.Models;

/// <summary>
/// Phase 13: structured chat message from the Claude Desktop conversation.
///
/// Produced by <c>GetChatMessagesAsync</c> instead of the flat string that
/// <c>GetOutputAsync</c> returned historically. Android renders these as
/// chat bubbles (user right-aligned, assistant left-aligned, tool as a
/// collapsible row) instead of a monolithic terminal-style text blob.
///
/// Content is "unwrapped prose" — paragraphs inside a single message are
/// separated by <c>\n\n</c> with no mid-paragraph line breaks. That contract
/// lets Android skip its regex postprocessor entirely.
/// </summary>
public class ChatMessage
{
    /// <summary>"user" | "assistant" | "tool"</summary>
    public string Role { get; set; } = "assistant";

    public string Content { get; set; } = string.Empty;
}
