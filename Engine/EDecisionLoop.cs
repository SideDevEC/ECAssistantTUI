using static ECAssistant.EColor;

using System.Text;
using ECAssistant.Memory;

namespace ECAssistant.Engine;

/// <summary>
/// Interactive Decision Loop — lets the agent ask clarifying questions,
/// present options, and wait for user input before proceeding.
/// 
/// v2: Real user input via EGuiBase, LLM-driven analysis, no hardcoded defaults.
/// The loop:
///   1. Send task to LLM → LLM analyzes and either gives an answer or asks a question
///   2. If LLM asks a question (detected via <output> containing "?"), present to user
///   3. User responds → feed answer back to LLM → repeat until LLM gives final answer
///   4. Or user types "cancel" to abort
/// </summary>
public class EDecisionLoop : IDisposable
{
    private readonly EAgentEngine _engine;
    private bool _running = false;

    /// <summary>Create decision loop with engine reference.</summary>
    public EDecisionLoop(EAgentEngine engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Execute interactive loop — sends task to LLM, relays questions to user,
    /// feeds answers back until the LLM produces a final answer or user cancels.
    /// </summary>
    public async Task<DecisionResult> ExecuteInteractiveLoop(string taskDescription)
    {
        EColor.TagBold(Cyan, "Decision", $"Interactive loop for: {taskDescription}");
        Program.Gui.BlankLine();

        _running = true;
        var maxRounds = 5; // Prevent infinite loops

        for (int round = 1; round <= maxRounds && _running; round++)
        {
            EColor.TagBold(Info(), "Round", $"{round}/{maxRounds}");
            
            // Ask the LLM to process the task (or continue from user's answer)
            var llmResponse = await _engine.GenerateAsync(taskDescription);
            
            Program.Gui.BlankLine();

            // Check if LLM gave a final answer (<output> tag)
            if (llmResponse.Contains("<output>", StringComparison.OrdinalIgnoreCase))
            {
                // Extract the answer
                var answer = ExtractOutputContent(llmResponse);
                EColor.TagBold(Success(), "Decision", "Final answer received.");
                Program.Gui.WriteLineColored(answer);
                
                return new DecisionResult
                {
                    Success = true,
                    OptionChosen = "final",
                    Outcome = answer
                };
            }

            // If no <output>, the LLM might be asking a question or calling a tool
            // Present whatever it said to the user and ask for input
            Program.Gui.WriteLineColored($"[Decision] LLM says: {llmResponse}");
            Program.Gui.BlankLine();

            if (round < maxRounds)
            {
                Program.Gui.WriteRaw($"{Yellow}Your response (or 'cancel'): {Reset}");
                var userInput = Program.Gui.PromptRaw("")?.Trim();

                if (string.IsNullOrEmpty(userInput) || userInput.Equals("cancel", StringComparison.OrdinalIgnoreCase))
                {
                    EColor.Tag(Info(), "Decision", "Cancelled by user.");
                    _running = false;
                    return new DecisionResult
                    {
                        Success = false,
                        OptionChosen = "cancelled",
                        Outcome = "User cancelled the decision loop."
                    };
                }

                // Feed user's answer back as the next task for the LLM
                taskDescription = $"User answered: {userInput}\n\nNow continue with the original task.";
            }
        }

        EColor.Tag(Info(), "Decision", "Max rounds reached.");
        return new DecisionResult
        {
            Success = false,
            OptionChosen = "max_rounds",
            Outcome = "Decision loop reached maximum rounds without a final answer."
        };
    }

    /// <summary>Extract content between <output> and </output> tags.</summary>
    private static string ExtractOutputContent(string response)
    {
        var startIdx = response.IndexOf("<output>", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return response;

        startIdx += "<output>".Length;
        var endIdx = response.IndexOf("</output>", startIdx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0) endIdx = response.Length;

        return response.Substring(startIdx, endIdx - startIdx).Trim();
    }

    /// <summary>Process feedback from completed action — saves to memory for future reference.</summary>
    public void ProcessFeedback(string userFeedback)
    {
        _engine.SaveMemory("feedback", userFeedback, "decision_loop");
        EColor.Tag(Info(), "Feedback", $"Saved: {userFeedback.Substring(0, Math.Min(userFeedback.Length, 100))}");
    }

    public void Dispose()
    {
        _running = false;
    }
}

// === Decision Loop Data Structures ===

/// <summary>Result of the interactive decision process.</summary>
public class DecisionResult
{
    public bool Success { get; set; }
    public string? OptionChosen { get; set; }
    public string Outcome { get; set; } = "";
}