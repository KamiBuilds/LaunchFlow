namespace LaunchFlow.Models;

public sealed class WorkflowStep
{
    public string Type { get; set; } = "Navigate";
    public string Target { get; set; } = "";
    public string? Value { get; set; }
    public string? Description { get; set; }

    public override string ToString()
    {
        if (!string.IsNullOrWhiteSpace(Description))
            return Description;

        return Type switch
        {
            "Navigate" => $"Navigate → {Target}",
            "Click" => $"Click → {Target}",
            "Fill" => $"Fill → {Target}" + (string.IsNullOrEmpty(Value) ? "" : " (value hidden)"),
            "Wait" => $"Wait → {Target}",
            "Press" => $"Press → {Target}",
            _ => $"{Type} → {Target}"
        };
    }
}
