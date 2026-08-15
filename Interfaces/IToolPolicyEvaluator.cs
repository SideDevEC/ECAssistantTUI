namespace ECAssistant.Interfaces;

/// <summary>
/// Evaluates tool permissions.
/// </summary>
public interface IToolPolicyEvaluator
{
    bool IsToolAllowed(string toolName);
    ToolPolicy GetPolicy(string toolName);
}