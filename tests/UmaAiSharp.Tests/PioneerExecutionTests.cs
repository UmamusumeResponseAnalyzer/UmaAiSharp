using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using UmaAiSharp.MCP;

namespace UmaAiSharp.Tests;

public sealed class PioneerExecutionTests
{
    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("eventId")]
    public void ReplayRejectsRemovedInputFields(string field)
    {
        var input = JsonNode.Parse(File.ReadAllText(Example("event-choice.json")))!.AsObject();
        var target = field == "eventId" ? input["script"]![0]!["action"]!.AsObject() : input;
        target[field] = 1;
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, input.ToJsonString());
            var error = Assert.Throws<JsonException>(() => PioneerExecution.Load(path, "unused-rules.br"));
            Assert.Contains(field, error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AutonomousInputRejectsAnExplicitActionScript()
    {
        var input = JsonNode.Parse(File.ReadAllText(Example("event-choice.json")))!;
        input["mode"] = "autonomous";
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, input.ToJsonString());
            var error = Assert.Throws<InvalidDataException>(() => PioneerExecution.Load(path, "unused-rules.br"));
            Assert.Contains("script", error.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExistingReportIsNeverOverwritten()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "existing output");
            Assert.Throws<InvalidDataException>(() => PioneerExecution.Run(Example("event-choice.json"),
                1234, path, "unused-rules.br", PioneerExecutionMode.Conditional));
            Assert.Equal("existing output", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }

    internal static string Example(string name, [CallerFilePath] string source = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../examples/pioneer", name));
}
