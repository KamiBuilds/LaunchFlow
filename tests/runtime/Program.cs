using System.Reflection;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

using var playwright = await Playwright.CreateAsync();
await WorkflowExecutionTests.RunAsync(playwright, args);
if (args.Contains("--execution-only")) return;
await DocumentBoundCredentialTests.RunAsync(playwright, args);
if (args.Contains("--document-bound-only")) return;
await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
await using var context = await browser.NewContextAsync();
await context.RouteAsync("**/*", route => route.FulfillAsync(new()
{
    ContentType = "text/html",
    Body = route.Request.Url.StartsWith("https://login.test/", StringComparison.Ordinal)
        ? "<input id='user'><button id='next' onclick=\"location.href='https://other.test/password'\">Next</button>"
        : "<input id='password' type='password'>"
}));
var page = await context.NewPageAsync();
var workflow = new WebsiteWorkflow
{
    StartUrl = "https://login.test/",
    UsernameSelector = "#user",
    UsernameContinueSelector = "#next",
    PasswordSelector = "#password",
    SubmitSelector = "",
    LoginWaitSeconds = 5
};
var vault = new CredentialVault();
await using var engine = new AutomationEngine(vault);
if (!args.Contains("--profile-only"))
{
    var prompts = new List<string>();
    engine.ApproveLoginOriginAsync = (_, origin) => { prompts.Add(origin); return Task.FromResult(false); };
    await page.GotoAsync(workflow.StartUrl);
    var login = typeof(AutomationEngine).GetMethod("TryAutoLoginAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    await (Task)login.Invoke(engine, new object?[] { workflow, page, CancellationToken.None, null, null })!;
    Assert(prompts.SequenceEqual(new[] { "https://other.test" }), "redirected password origin requires explicit approval even without a configured password origin");
    Assert(await page.InputValueAsync("#password") == "", "denied redirect never receives the test password");

    engine.ApproveLoginOriginAsync = (_, origin) => Task.FromResult(origin == "https://other.test");
    await page.GotoAsync(workflow.StartUrl);
    await (Task)login.Invoke(engine, new object?[] { workflow, page, CancellationToken.None, null, null })!;
    Assert(await page.InputValueAsync("#password") == vault.Credential.Password, "explicitly approved SSO origin still receives the login");
    await page.SetContentAsync("<h1>Signed in</h1><input type='password' hidden>");
    Assert(!(await LoginHealth.InspectAsync(page)).NeedsAttention, "hidden password fields do not stop an already completed login");
    await page.SetContentAsync("<input type='password'>");
    Assert((await LoginHealth.InspectAsync(page)).NeedsAttention, "visible password challenge still stops follow-up steps");
    var closed = await context.NewPageAsync();
    await closed.CloseAsync();
    Assert((await LoginHealth.InspectAsync(closed)).NeedsAttention, "failed login inspection does not report a successful sign-in");
}
var profileRoot = Path.Combine(Path.GetTempPath(), "launchflow-profile-test-" + Guid.NewGuid().ToString("N"));
try
{
    await using var owner = await playwright.Chromium.LaunchPersistentContextAsync(profileRoot, new() { Headless = true, Channel = "chromium" });
    var locks = Directory.GetFileSystemEntries(profileRoot).Where(p => Path.GetFileName(p).StartsWith("Singleton", StringComparison.Ordinal)).ToArray();
    if (OperatingSystem.IsLinux() && locks.Length == 0) throw new Exception("HARNESS: full Chromium must create Singleton coordination files");
    var recover = typeof(AutomationEngine).GetMethod("LaunchContextWithRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    bool rejected = false;
    try
    {
        var attempt = (Task<IBrowserContext>)recover.Invoke(engine, new object[] {
            playwright.Chromium, workflow, profileRoot, new BrowserTypeLaunchPersistentContextOptions { Headless = true, Channel = "chromium", Timeout = 3000 }, CancellationToken.None })!;
        await using var unexpectedSecondOwner = await attempt;
    }
    catch (Exception ex) when (ex is PlaywrightException or InvalidOperationException) { rejected = true; }
    Assert(rejected, "busy browser profiles are rejected instead of removing their live coordination locks");
    Assert(locks.All(p => Directory.GetFileSystemEntries(profileRoot).Contains(p)), "original browser retains every profile lock file");
}
finally
{
    if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, true);
    if (Directory.Exists(profileRoot + "-recovery")) Directory.Delete(profileRoot + "-recovery", true);
}
var parse = typeof(KeePassXcService).GetMethod("ParseShowOutput", BindingFlags.NonPublic | BindingFlags.Static)!;
var spacedCredential = (SiteCredential)parse.Invoke(null, new object[] { " user \r\n  test-secret  \r\n" })!;
Assert(spacedCredential == new SiteCredential(" user ", "  test-secret  "), "KeePass attribute parsing preserves exact username and password whitespace");
Assert((SiteCredential)parse.Invoke(null, new object[] { "\nPassword: literal\n" })! == new SiteCredential("", "Password: literal"),
    "empty KeePass usernames and label-like password text remain literal");
if (!OperatingSystem.IsWindows())
{
    var cliRoot = Path.Combine(Path.GetTempPath(), "launchflow-cli-test-" + Guid.NewGuid().ToString("N"));
    var oldPath = Environment.GetEnvironmentVariable("PATH");
    Directory.CreateDirectory(cliRoot);
    try
    {
        var cli = Path.Combine(cliRoot, "keepassxc-cli");
        File.WriteAllText(cli, "#!/bin/sh\nfor last do :; done\nprintf '%s\\n' \"$last\" 'test-password'\n");
        File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var db = Path.Combine(cliRoot, "fixture.kdbx");
        File.WriteAllText(db, "test fixture, not a real database");
        Environment.SetEnvironmentVariable("PATH", cliRoot + Path.PathSeparator + oldPath);
        var entry = "group/entry\"with trailing\\";
        var result = KeePassXcService.ReadEntry(db, entry);
        Assert(result.Username == entry, "KeePass child process receives the exact quoted/trailing-backslash entry argument");
        File.WriteAllText(cli, "#!/bin/sh\nprintf 'test-secret-from-cli' >&2\nexit 1\n");
        try
        {
            KeePassXcService.ReadEntry(db, "entry");
            throw new Exception("FAIL unsuccessful KeePass CLI must fail");
        }
        catch (InvalidOperationException ex)
        {
            Assert(!ex.Message.Contains("test-secret-from-cli"), "KeePass failure messages never echo raw child output");
        }
        File.WriteAllText(cli, "#!/bin/sh\ni=0\nwhile [ \"$i\" -lt 8192 ]; do printf 'diagnostic-padding-for-pipe-capacity-test\\n' >&2; i=$((i+1)); done\nprintf '%s\\n' 'test-user' 'test-password'\n");
        var noisy = KeePassXcService.ReadEntry(db, "entry");
        Assert(noisy.Username == "test-user", "KeePass drains stderr while waiting instead of deadlocking on a full pipe");
    }
    finally
    {
        Environment.SetEnvironmentVariable("PATH", oldPath);
        Directory.Delete(cliRoot, true);
    }
}
if (OperatingSystem.IsLinux())
{
    var xdg = Path.Combine(Path.GetTempPath(), "launchflow-restore-guard-" + Guid.NewGuid().ToString("N"));
    var oldXdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
    var oldData = VaultDatabase.Current.DataDirectory;
    Directory.CreateDirectory(xdg);
    try
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdg);
        if (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) != xdg)
            throw new Exception("HARNESS: application data must be inside the owned fixture");
        VaultDatabase.Current.DataDirectory = Path.Combine(xdg, "LaunchFlow");
        Directory.CreateDirectory(ProfileVaultService.SealedRoot);
        var blockedWorkflow = new WebsiteWorkflow { Browser = "Chromium" };
        File.WriteAllText(Path.Combine(ProfileVaultService.SealedRoot, blockedWorkflow.Id.ToString("N") + ".profile.aes"), "unrestored fixture");
        var playwrightField = typeof(AutomationEngine).GetField("_playwright", BindingFlags.NonPublic | BindingFlags.Instance)!;
        playwrightField.SetValue(engine, playwright);
        var getPage = typeof(AutomationEngine).GetMethod("GetOrCreatePageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var rejected = false;
        try { await (Task<IPage>)getPage.Invoke(engine, new object[] { blockedWorkflow, CancellationToken.None })!; }
        catch (InvalidOperationException ex) { rejected = ex.Message.Contains("restor", StringComparison.OrdinalIgnoreCase); }
        catch (PlaywrightException) { }
        finally { playwrightField.SetValue(engine, null); }
        Assert(rejected, "an unrestored sealed profile blocks browser creation instead of starting a replacement session");
        Assert(!Directory.Exists(Path.Combine(ProfileVaultService.ProfilesRoot, blockedWorkflow.Id.ToString("N"))), "failed restore never creates a replacement plaintext profile");
    }
    finally
    {
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", oldXdg);
        VaultDatabase.Current.DataDirectory = oldData;
        Directory.Delete(xdg, true);
    }
}
Console.WriteLine("Runtime regression tests passed.");

static void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}
