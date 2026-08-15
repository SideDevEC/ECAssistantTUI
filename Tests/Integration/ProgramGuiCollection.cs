namespace ECAssistant.Tests.Integration;

/// <summary>
/// xUnit test collection that serializes tests sharing the static Program.Gui field.
/// Without this, parallel test execution causes Program.Gui to be overwritten between tests.
/// </summary>
[CollectionDefinition("ProgramGuiCollection", DisableParallelization = true)]
public class ProgramGuiCollection
{
}