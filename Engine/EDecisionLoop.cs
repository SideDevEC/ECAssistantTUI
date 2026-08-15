using System.Text;
using ECAssistant.Memory;
using ECAssistant.Interfaces;
using ECAssistant.Session;

namespace ECAssistant.Engine;

/// <summary>
/// Interactive Decision Loop — lets the agent ask clarifying questions,
/// present options, and wait for user input before proceeding.
///
/// The loop:
///   1. Send task to LLM → LLM analyzes and either gives an answer or asks a question
///   2. If LLM asks a question (detected via <output> containing "?"), present to user
///   3. User responds → feed answer back to LLM → repeat until LLM gives final answer
///   4. Or user types "cancel" to abort
///
/// All output goes through ISessionOutput — no direct UI calls.
/// </summary>
public class EDecisionLoop : IDisposable
{
    private readonly EAgentEngine _engine;
    private readonly IColorFormatter _color;
    private readonly ISessionOutput? _out;
    private bool _running = false;

    /// <summary>Create decision loop with engine reference and session output.</summary>
    public EDecisionLoop(EAgentEngine engine, IColorFormatter color, ISessionOutput? sessionOutput = null)
    {
        _engine = engine;
        _color = color;
        _out = sessionOutput;
    }

    /// <summary>
    /// Execute interactive loop — sends task to LLM, relays questions to user,
    /// feeds answers back until the LLM produces a final answer or user cancels.
    /// </summary>
    public async Task<DecisionResult> ExecuteInteractiveLoop(string taskDescription)
    {
        _color.TagBold(_color.Cyan, "Decision", $"Interactive loop for: {taskDescription}");
        _out?.BlankLine();

        _running = true;
        var maxRounds = 5;

        for (int round = 1; round <= maxRounds && _running; round++)
        {
            _color.TagBold(_color.Cyan, "Round", $"{round}/{maxRounds}");

            var llmResponse = await _engine.GenerateAsync(taskDescription);

            _out?.BlankLine();

            if (llmResponse.Contains("<output>", StringComparison.OrdinalIgnoreCase))
            {
                var answer = ExtractOutputContent(llmResponse);
                _color.TagBold(_color.Green, "Decision", "Final answer received.");
                _out?.WriteLine(answer, OutputState.Bold);

                return new DecisionResult
                {
                    Success = true,
                    OptionChosen = "final",
                    Outcome = answer
                };
            }

            _out?.WriteLine($"[Decision] LLM says: {llmResponse}", OutputState.Info);
            _out?.BlankLine();

            if (round < maxRounds)
            {
                // Use RequestApproval as a continue/cancel prompt
                var approved = _out?.RequestApproval("Continue? (approve to proceed, deny to cancel)") ?? false;

                if (!approved)
                {
                    _color.Tag(_color.Cyan, "Decision", "Cancelled by user.");
                    _running = false;
                    return new DecisionResult
                    {
                        Success = false,
                        OptionChosen = "cancelled",
                        Outcome = "User cancelled the decision loop."
                    };
                }

                taskDescription = "User approved. Continue with the original task.";
            }
        }

        _color.Tag(_color.Cyan, "Decision", "Max rounds reached.");
        return new DecisionResult
        {
            Success = false,
            OptionChosen = "max_rounds",
            Outcome = "Decision loop reached maximum rounds without a final answer."
        };
    }

    /// <summary>Extract content between <output> and </output> tags.</summary>
    private string ExtractOutputContent(string response)
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
        _color.Tag(_color.Cyan, "Feedback", $"Saved: {userFeedback.Substring(0, Math.Min(userFeedback.Length, 100))}");
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