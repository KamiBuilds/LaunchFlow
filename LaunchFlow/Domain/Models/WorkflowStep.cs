using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchFlow.Models;

public sealed class WorkflowStep
{
    public string Type { get; set; } = "Navigate";
    public string Target { get; set; } = "";
    public string? Value { get; set; }
    public string? Description { get; set; }
    // Preserve unknown/forward-compatible action fields during edits and
    // encrypted saves; never implicitly inspect or log their contents.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }

    // Targets, descriptions and even unknown action types may contain credentials.
    // Editors can present them deliberately; implicit formatting must not disclose them.
    /// <summary>Safe list label; never echoes Target/Value (may hold secrets).</summary>
    [JsonIgnore]
    public string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Description))
                return Description.Trim();
            return Type switch
            {
                "Navigate" => "Open a page",
                "Click" => "Clicked something on the page",
                "Fill" => "Fill a field",
                "Wait" => "Wait",
                "Press" => "Pressed a key",
                "AssertVisible" => "Verify element",
                "WaitForURL" => "Wait for page",
                _ => "Recorded action"
            };
        }
    }

    public override string ToString() => DisplayLabel;
}
