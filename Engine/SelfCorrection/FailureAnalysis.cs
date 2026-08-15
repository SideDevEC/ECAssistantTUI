namespace ECAssistant.Engine;

public class FailureAnalysis
{
    public FailurePattern Pattern { get; set; }
    public string Recommendation { get; set; } = "";
    public bool ShouldEscalate { get; set; }
}