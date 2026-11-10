using System.Text.Json;
using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class WebsiteEditMapperTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("shared website mapper covers every field for detached draft and staged navigation", () =>
        {
            var expectedId = Guid.NewGuid();
            var draft = new WebsiteEditDraft(0, expectedId, false, "  Dashboard A  ",
                " https://start.example.invalid ", " https://end.example.invalid ", "Edge",
                true, 4, "DISPLAY-4", "Auto", true, " #next ", " https://id.example.invalid ",
                " Synthetic/KeePass/Entry ", " #username ", " #password ", " #sign ",
                " https://login.example.invalid ", " #completed ", " /dashboard ", 300);
            var durable = new WebsiteWorkflow { Id = expectedId, MonitorIndex = 0, MonitorDeviceName = "before" };
            var staged = new WebsiteWorkflow { Id = expectedId, MonitorIndex = 0, MonitorDeviceName = "before" };
            draft.Apply(durable);
            WebsiteEditMapper.ApplyLeading(staged, draft.Enabled, draft.Name, draft.StartUrl, draft.DestinationUrl);
            WebsiteEditMapper.ApplyBrowser(staged, draft.Browser, draft.ReplaceMonitor, draft.MonitorIndex, draft.MonitorDeviceName);
            WebsiteEditMapper.ApplyLogin(staged, draft.LoginMode, draft.UsernameContinueSelector,
                draft.PasswordCredentialOrigin, draft.AutoLogin);
            WebsiteEditMapper.ApplyDetails(staged, draft.KeepassEntryPath, draft.UsernameSelector,
                draft.PasswordSelector, draft.SubmitSelector, draft.CredentialOrigin,
                draft.LoginSuccessSelector, draft.LoginSuccessUrlContains, draft.LoginWaitSeconds);
            Check(JsonSerializer.Serialize(durable) == JsonSerializer.Serialize(staged),
                "Durable and staged mapper assigned different website properties");
            Check(durable.Name == "Dashboard A" && !durable.Enabled &&
                durable.StartUrl == "https://start.example.invalid" &&
                durable.DestinationUrl == "https://end.example.invalid" &&
                durable.MonitorIndex == 4 && durable.MonitorDeviceName == "DISPLAY-4" &&
                durable.UsernameContinueSelector == "#next" &&
                durable.PasswordCredentialOrigin == "https://id.example.invalid" &&
                durable.KeepassEntryPath == "Synthetic/KeePass/Entry" &&
                durable.UsernameSelector == "#username" && durable.PasswordSelector == "#password" &&
                durable.SubmitSelector == "#sign" && durable.CredentialOrigin == "https://login.example.invalid" &&
                durable.LoginSuccessSelector == "#completed" && durable.LoginSuccessUrlContains == "/dashboard" &&
                durable.LoginWaitSeconds == 180 && durable.AutoLogin,
                "One or more explicitly editable properties were lost or not normalized");
        });
        yield return ("missing browser preserves legacy navigation partial mutation but does not commit login fields", () =>
        {
            var w = new WebsiteWorkflow { Name = "original", Browser = "Edge", LoginMode = "None" };
            WebsiteEditMapper.ApplyLeading(w, true, "  changed  ", " https://a.invalid ", " https://b.invalid ");
            var rejected = false;
            try { WebsiteEditMapper.ApplyBrowser(w, "", true, 3, "DISPLAY-3"); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && w.Name == "changed" && w.StartUrl == "https://a.invalid" &&
                w.Browser == "Edge" && w.LoginMode == "None" && w.MonitorDeviceName != "DISPLAY-3",
                "Prior partial-failure ordering changed");
        });
        yield return ("disconnected monitor identity survives implicit selection and explicit replacement writes it", () =>
        {
            var id = Guid.NewGuid();
            var w = new WebsiteWorkflow { Id = id, MonitorIndex = 5, MonitorDeviceName = "UNPLUGGED" };
            WebsiteEditMapper.ApplyBrowser(w, "Edge", false, 1, "DISPLAY-1");
            Check(w.MonitorIndex == 5 && w.MonitorDeviceName == "UNPLUGGED", "implicit monitor replacement");
            WebsiteEditMapper.ApplyBrowser(w, "Edge", true, 1, "DISPLAY-1");
            Check(w.MonitorIndex == 1 && w.MonitorDeviceName == "DISPLAY-1", "explicit monitor not stored");
        });
        yield return ("shared normalization keeps null/blank names and integer wait bounds", () =>
        {
            Check(WebsiteEditMapper.NormalizeName(null) == "Unnamed website" &&
                WebsiteEditMapper.NormalizeName("  ") == "Unnamed website" &&
                WebsiteEditMapper.NormalizeName("  A  ") == "A", "name fallback");
            foreach (var pair in new[] { ("invalid", 60), ("", 60), ("-1", 5),
                ("5", 5), ("180", 180), ("181", 180), ("9999999999999999999", 60) })
                Check(WebsiteEditMapper.NormalizeWait(pair.Item1) == pair.Item2, "wait " + pair.Item1);
        });
        yield return ("identity guard and unknown fields remain on original workflow", () =>
        {
            var source = new WebsiteWorkflow { Id = Guid.NewGuid(), Name = "Old" };
            var draft = new WebsiteEditDraft(0, source.Id, true, " New ", "https://a.invalid", "",
                "Edge", false, 0, "", "SSO", false, "", "", "", "", "", "", "", "", "", 60);
            var replacement = new WebsiteWorkflow { Id = Guid.NewGuid(), Name = "Replacement" };
            var blocked = false;
            try { draft.Apply(replacement); } catch (InvalidOperationException) { blocked = true; }
            Check(blocked && replacement.Name == "Replacement", "Detached identity guard bypass");
            var node = JsonSerializer.SerializeToNode(source)!;
            node["FutureField"] = "preserve";
            var roundTrip = node.Deserialize<WebsiteWorkflow>()!;
            draft.Apply(roundTrip);
            var after = JsonSerializer.SerializeToNode(roundTrip)!;
            Check(after["FutureField"]?.ToString() == "preserve", "Unknown extension data was dropped");
        });
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }
}
