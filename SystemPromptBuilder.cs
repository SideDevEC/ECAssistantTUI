namespace ECAssistant;

/// <summary>
/// Builds a system prompt for ECAssistant.Core that includes the required
/// &lt;lm&gt; tag format rules, OS awareness, and custom domain context.
///
/// The OS is auto-detected if WithPlatform() is not called.
/// The &lt;lm&gt; tag rules are always included — the engine parser depends on them.
///
/// Usage:
///   var prompt = SystemPromptBuilder.Create()
///       .WithAgentName("ECSQL Assistant")
///       .WithDescription("You help users manage and query SQL databases.")
///       .Build();
///   // OS is auto-detected: "macOS", "Windows", or "Linux"
///   session.Engine.SystemPromptText = prompt;
///
/// Override OS if needed:
///   .WithPlatform("Linux")
///
/// Add domain rules:
///   .WithCustomRules("Always explain SQL before executing. Use SqlQuery for SELECT statements.")
/// </summary>
public class SystemPromptBuilder
{
    private string _agentName = "ECAssistant";
    private string _description = "a local AI agent with multiple tools and persistent memory";
    private string? _platform = null;  // null = auto-detect
    private string _customRules = "";

    private SystemPromptBuilder() { }

    /// <summary>Start building a system prompt.</summary>
    public static SystemPromptBuilder Create() => new();

    /// <summary>Name the agent appears as in the prompt. Default: "ECAssistant".</summary>
    public SystemPromptBuilder WithAgentName(string name) { _agentName = name; return this; }

    /// <summary>Describe what the agent does. Appended after the name.</summary>
    public SystemPromptBuilder WithDescription(string desc) { _description = desc; return this; }

    /// <summary>
    /// Specify the platform explicitly. If not called, auto-detected:
    /// macOS → "macOS", Windows → "Windows", otherwise → "Linux".
    /// </summary>
    public SystemPromptBuilder WithPlatform(string platform) { _platform = platform; return this; }

    /// <summary>
    /// Add custom rules/instructions. Appended after the tag format rules.
    /// Use this for domain-specific guidance (e.g., "Always explain SQL before executing").
    /// </summary>
    public SystemPromptBuilder WithCustomRules(string rules) { _customRules = rules; return this; }

    /// <summary>
    /// Auto-detect the OS. Returns "macOS", "Windows", or "Linux".
    /// </summary>
    private static string DetectPlatform()
    {
        if (OperatingSystem.IsMacOS()) return "macOS";
        if (OperatingSystem.IsWindows()) return "Windows";
        return "Linux";
    }

    /// <summary>
    /// Get OS-specific shell guidance for the platform.
    /// </summary>
    private static string GetShellGuidance(string platform)
    {
        return platform switch
        {
            "macOS" => "Use bash/zsh commands. Shell is case-sensitive.",
            "Windows" => "Use PowerShell commands. Shell is case-insensitive.",
            "Linux" => "Use bash commands. Shell is case-sensitive.",
            _ => "Use shell commands appropriate for your platform."
        };
    }

    /// <summary>Build the complete system prompt string.</summary>
    public string Build()
    {
        var platform = _platform ?? DetectPlatform();
        var shellGuidance = GetShellGuidance(platform);

        var sb = new System.Text.StringBuilder();

        // ── Agent identity + OS ──
        sb.AppendLine($"# {_agentName} — System Prompt");
        sb.AppendLine();
        sb.AppendLine($"You are **{_agentName}** — {_description}");
        sb.AppendLine();
        sb.AppendLine($"You work on {platform}. {shellGuidance}");
        sb.AppendLine();

        // ── REQUIRED: <lm> tag format rules (engine parser depends on these) ──
        sb.AppendLine("## RESPONSE FORMAT — STRICT");
        sb.AppendLine();
        sb.AppendLine("Every response MUST be wrapped in an `<lm>` container. No exceptions.");
        sb.AppendLine();
        sb.AppendLine("**When you need to run a tool:**");
        sb.AppendLine("```");
        sb.AppendLine("<lm><thinking>Brief reasoning about what to do</thinking><toolcall>ToolName<argname>value</argname></toolcall></lm>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("**When you have the answer for the user:**");
        sb.AppendLine("```");
        sb.AppendLine("<lm><thinking>Brief reasoning</thinking><output>Your answer to the user</output></lm>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### CRITICAL RULES — NO EXCEPTIONS");
        sb.AppendLine("1. Your FIRST token is always `<lm>`. Your LAST token is always `</lm>`. Nothing comes before or after.");
        sb.AppendLine("2. Inside `<lm>`: ONE `<thinking>`, then ONE `<toolcall>` OR ONE `<output>`. Then `</lm>`. Then STOP.");
        sb.AppendLine("3. Never write a second `<thinking>` or `<toolcall>`.");
        sb.AppendLine("4. Never write text outside `<lm>...</lm>`.");
        sb.AppendLine("5. Never write `<user>`, `<tooloutput>`, `<result>` tags — host only.");
        sb.AppendLine("6. After a tool result in history, respond with `<output>` (if done) or another `<toolcall>` (if you need more data).");
        sb.AppendLine("7. Keep `<thinking>` SHORT — 1-2 sentences max.");
        sb.AppendLine("8. For simple questions, still use the full format: `<lm><thinking>brief</thinking><output>answer</output></lm>`.");
        sb.AppendLine("9. After the `<assistant>` tag, start with `<lm>` immediately. Do NOT echo `<assistant>` back.");
        sb.AppendLine("10. You can include MULTIPLE `<toolcall>` tags in one `<lm>` response for independent operations.");
        sb.AppendLine();
        sb.AppendLine("### EXAMPLE: Simple question after tool result");
        sb.AppendLine("Tool returned: \"Wednesday\"");
        sb.AppendLine("Your response MUST be:");
        sb.AppendLine("```");
        sb.AppendLine("<lm><thinking>The tool returned Wednesday. I'll give this to the user.</thinking><output>Today is Wednesday.</output></lm>");
        sb.AppendLine("```");
        sb.AppendLine("NEVER just write \"Today is Wednesday\" without tags. The host will reject it.");
        sb.AppendLine();

        // ── Custom domain rules ──
        if (!string.IsNullOrEmpty(_customRules))
        {
            sb.AppendLine("## DOMAIN RULES");
            sb.AppendLine();
            sb.AppendLine(_customRules);
            sb.AppendLine();
        }

        // ── Error handling (generic, always included) ──
        sb.AppendLine("## ERROR HANDLING");
        sb.AppendLine();
        sb.AppendLine("When a tool returns errors:");
        sb.AppendLine("1. Read the error message carefully");
        sb.AppendLine("2. Identify the root cause");
        sb.AppendLine("3. Fix the issue with a new tool call — don't just retry the same command");
        sb.AppendLine("4. After 3 failed attempts, ask the user for help");
        sb.AppendLine();

        // ── Conversation history format ──
        sb.AppendLine("## CONVERSATION HISTORY");
        sb.AppendLine();
        sb.AppendLine("When you see history from previous turns:");
        sb.AppendLine("- `<user>...text...</user>` = what the user asked");
        sb.AppendLine("- `<tooloutput>ToolName<result>text</result></tooloutput>` = tool result from a previous turn");
        sb.AppendLine("- Your past `<thinking>` and `<toolcall>`/`<output>` blocks");
        sb.AppendLine();
        sb.AppendLine("If you see a `<tooloutput>` in history, the tool ALREADY RAN. Read the result and give your `<output>` answer. Do NOT repeat the same tool call.");
        sb.AppendLine();

        // ── Operating rules ──
        sb.AppendLine("## OPERATING RULES");
        sb.AppendLine();
        sb.AppendLine("1. Keep responses concise — don't over-explain");
        sb.AppendLine("2. Use the right tool for the job");
        sb.AppendLine("3. If a tool fails, read the error carefully and fix the command");
        sb.AppendLine();

        // ── Memory ──
        sb.AppendLine("## MEMORY");
        sb.AppendLine();
        sb.AppendLine("Save important patterns for future sessions:");
        sb.AppendLine("- Bugs and their fixes");
        sb.AppendLine("- Solutions that worked");
        sb.AppendLine("- User preferences");
        sb.AppendLine();

        // ── Tools section (engine appends tool definitions at runtime) ──
        sb.AppendLine("## AVAILABLE TOOLS");
        sb.AppendLine();
        sb.AppendLine("Tools are registered at runtime below. Use the tool name exactly as shown.");

        return sb.ToString();
    }
}