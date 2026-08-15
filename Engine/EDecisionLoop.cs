using ECAssistant.Session;

namespace ECAssistant.Engine;

/// <summary>
/// Interactive Decision Loop — lets the agent ask clarifying questions,
/// present options, and wait for user input before proceeding.
///
/// All output goes through ISessionOutput — no direct UI calls.
/// </summary>
public class EDecisionLoop : IDisposable
{
    private readonly EAgentEngine _engine;
    private readonly ISessionOutput? _out;
    private bool _running = false;

    public EDecisionLoop(EAgentEngine engine, ISessionOutput? sessionOutput = null)
    {
        _engine = engine;
        _out = sessionOutput;
    }

    public async Task<DecisionResult> ExecuteInteractiveLoop(string taskDescription)
    {
        _out?.WriteTag("Decision", $"Interactive loop for: {taskDescription}", OutputState.Info);
        _out?.BlankLine();

        _running = true;
        var maxRounds = 5;

        for (int round = 1; round <= maxRounds && _running; round++)
        {
            _out?.WriteTag("Round", $"{round}/{maxRounds}", OutputState.Info);

            var llmResponse = await _engine.GenerateAsync(taskDescription);

            _out?.BlankLine();

            if (llmResponse.Contains("<output>", StringComparison.OrdinalIgnoreCase))
            {
                var answer = ExtractOutputContent(llmResponse);
                _out?.WriteTag("Decision", "Final answer received.", OutputState.Success);
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
                var approved = _out?.RequestApproval("Continue? (approve to proceed, deny to cancel)") ?? false;

                if (!approved)
                {
                    _out?.WriteTag("Decision", "Cancelled by user.", OutputState.Warning);
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

        _out?.WriteTag("Decision", "Max rounds reached.", OutputState.Warning);
        return new DecisionResult
        {
            Success = false,
            OptionChosen = "max_rounds",
            Outcome = "Decision loop reached maximum rounds without a final answer."
        };
    }

    private string ExtractOutputContent(string response)
    {
        var startIdx = response.IndexOf("<output>", StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return response;
        startIdx += "<output>".Length;
        var endIdx = response.IndexOf("</output>", startIdx, StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0) endIdx = response.Length;
        return response.Substring(startIdx, endIdx - startIdx).Trim();
    }

    public void ProcessFeedback(string userFeedback)
    {
        _engine.SaveMemory("feedback", userFeedback, "decision_loop");
        _out?.WriteTag("Feedback", $"Saved: {userFeedback.Substring(0, Math.Min(userFeedback.Length, 100))}", OutputState.Info);
    }

    public void Dispose() => _running = false;
}

public class DecisionResult
{
    public bool Success { get; set; }
    public string? OptionChosen { get; set; }
    public string Outcome { get; set; } = "";
}