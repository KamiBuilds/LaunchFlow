using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;

static void Check(bool ok, string message)
{
    if (!ok) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}

var admin = new EnterprisePolicy
{
    DisableCredentialFill = false,
    RequireHttpsForCredentials = true,
    AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "portal.example.org" }
};
Check(admin.AllowsUrl("https://portal.example.org/dashboard"), "exact host allowed");
Check(!admin.AllowsUrl("https://evilportal.example.org"), "lookalike host denied");
Check(!admin.AllowsUrl("https://portal.example.org.evil.test"), "suffix confusion denied");
Check(!admin.AllowsUrl("file:///C:/temp/test"), "file scheme denied");
Check(!admin.AllowsCredentialFill("http://portal.example.org"), "HTTPS policy enforced");
Check(admin.AllowsCredentialFill("https://portal.example.org/login"), "permitted HTTPS login");
var blocked = new EnterprisePolicy { DisableCredentialFill = true };
Check(!blocked.AllowsCredentialFill("https://example.org"), "admin credential block enforced");
Check(new EnterprisePolicy { DisableCredentialStorage = true }.DisableCredentialStorage,
      "separate managed credential-storage restriction represented");

var workflow = new WebsiteWorkflow { Name = "Monitoring", Enabled = true };
var disabled = new WebsiteWorkflow { Name = "Disabled", Enabled = false };
var config = new Configuration { Workflows = new() { workflow, disabled } };
var group = new WorkspaceProfile
{
    Name = "Morning", WorkflowIds = new() { workflow.Id, workflow.Id, disabled.Id, Guid.NewGuid() }
};
config.Workspaces.Add(group);
Check(group.Resolve(config).Count() == 1, "workspace skips duplicate, deleted and disabled sites");
var copy = JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(config))!;
Check(copy.Workspaces.Single().WorkflowIds.Count == 4, "workspace survives encrypted-config JSON serialization");
Check(LocalizationService.Text("Settings", "nl") == "Instellingen", "Dutch translation");
Check(LocalizationService.Text("Settings", "en") == "Settings", "English fallback");
Console.WriteLine("LaunchFlow safety smoke tests passed.");
