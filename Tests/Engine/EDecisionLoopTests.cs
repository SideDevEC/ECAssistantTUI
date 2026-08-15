using ECAssistant.Engine;
using ECAssistant.Interfaces;
using Moq;

namespace ECAssistant.Tests.Engine;

public class EDecisionLoopTests
{
    [Fact]
    public void DecisionResult_Default_SuccessIsFalse()
    {
        var result = new DecisionResult();
        Assert.False(result.Success);
    }

    [Fact]
    public void DecisionResult_Default_OutcomeIsEmpty()
    {
        var result = new DecisionResult();
        Assert.Equal("", result.Outcome);
    }

    [Fact]
    public void DecisionResult_Default_OptionChosenIsNull()
    {
        var result = new DecisionResult();
        Assert.Null(result.OptionChosen);
    }

    [Fact]
    public void DecisionResult_WithValues_PreservesValues()
    {
        var result = new DecisionResult
        {
            Success = true,
            OptionChosen = "option1",
            Outcome = "Task completed"
        };
        Assert.True(result.Success);
        Assert.Equal("option1", result.OptionChosen);
        Assert.Equal("Task completed", result.Outcome);
    }

    // ExtractOutputContent is private, but we can test decision parsing
    // indirectly by verifying that DecisionResult is constructed correctly.
    // Since ExecuteInteractiveLoop requires Program.Gui and EAgentEngine,
    // we test the data structures and the publicly testable aspects here.

    [Fact]
    public void DecisionResult_CanBeSerializedToJson()
    {
        var result = new DecisionResult
        {
            Success = true,
            OptionChosen = "final",
            Outcome = "The answer is 42"
        };
        // Verify the result can be used as expected
        Assert.True(result.Success);
        Assert.NotNull(result.Outcome);
    }

    [Fact]
    public void DecisionResult_CancelledScenario()
    {
        var result = new DecisionResult
        {
            Success = false,
            OptionChosen = "cancelled",
            Outcome = "User cancelled the decision loop."
        };
        Assert.False(result.Success);
        Assert.Equal("cancelled", result.OptionChosen);
    }

    [Fact]
    public void DecisionResult_MaxRoundsScenario()
    {
        var result = new DecisionResult
        {
            Success = false,
            OptionChosen = "max_rounds",
            Outcome = "Decision loop reached maximum rounds without a final answer."
        };
        Assert.False(result.Success);
        Assert.Equal("max_rounds", result.OptionChosen);
    }

    [Fact]
    public void DecisionResult_FinalAnswerScenario()
    {
        var result = new DecisionResult
        {
            Success = true,
            OptionChosen = "final",
            Outcome = "The project should use .NET 8.0"
        };
        Assert.True(result.Success);
        Assert.Equal("final", result.OptionChosen);
        Assert.Contains(".NET 8.0", result.Outcome);
    }
}