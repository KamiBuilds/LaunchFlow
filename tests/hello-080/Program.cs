using LaunchFlow.Services;
using Windows.Security.Credentials.UI;

var root = Path.Combine(Path.GetTempPath(), "launchflow-hello-080-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
VaultDatabase.Current.DataDirectory = root;
try
{
    UserConsentVerifier.ResetPending();
    var admissionA = WindowsHelloService.BeginEnable();
    var awaitingA = WindowsHelloService.EnableAsync("synthetic-A", admissionA);
    var admissionB = WindowsHelloService.BeginEnable();
    var awaitingB = WindowsHelloService.EnableAsync("synthetic-B", admissionB);
    Check(UserConsentVerifier.PendingCount == 2, "both enables admitted consent before completion");
    UserConsentVerifier.Approve(1);
    var publishedB = await awaitingB;
    Check(publishedB is > 0 && WindowsHelloService.IsEnabled, "newer B publishes successfully");
    UserConsentVerifier.Approve(0);
    Check(await awaitingA is null && WindowsHelloService.PublicationGeneration == publishedB,
        "A consent completing after B cannot overwrite B's publication owner");
    UserConsentVerifier.AutoApprove = true;
    Check(await WindowsHelloService.TryUnlockPasswordAsync() == "synthetic-B",
        "late A consent retains B's actual protected unlock material");

    WindowsHelloService.DisableStrict();
    UserConsentVerifier.ResetPending();
    var oldAdmission = WindowsHelloService.BeginEnable();
    var waitingAtConsent = WindowsHelloService.EnableAsync("synthetic-stale", oldAdmission);
    Check(UserConsentVerifier.PendingCount == 1, "old enable reached the asynchronous consent gate");
    WindowsHelloService.DisableStrict(); // same strict removal boundary used by vault reset
    UserConsentVerifier.Approve(0);
    Check(await waitingAtConsent is null && !WindowsHelloService.IsEnabled,
        "strict disable/reset invalidates enable BEFORE pending consent may publish");

    UserConsentVerifier.AutoApprove = true;
    var first = await WindowsHelloService.EnableAsync("synthetic-C", WindowsHelloService.BeginEnable());
    var second = await WindowsHelloService.EnableAsync("synthetic-D", WindowsHelloService.BeginEnable());
    Check(first is not null && second is not null &&
          WindowsHelloService.DisableIfPublication(first.Value) ==
              WindowsHelloService.CleanupResult.Superseded &&
          WindowsHelloService.IsEnabled &&
          await WindowsHelloService.TryUnlockPasswordAsync() == "synthetic-D",
        "cleanup of superseded owner cannot delete newer Hello generation");

    WindowsHelloService.DisableStrict();
    Check(!WindowsHelloService.IsEnabled, "strict disable clears current publication");

    var task = true;
    var runKey = true;
    var startup = new StartupRegistrationService(on => task = on, on => runKey = on);
    startup.Apply(false, preferTaskScheduler: false);
    Check(!task && !runKey, "fresh setup off reconciles old Scheduled Task and Run key");
    task = true;
    startup.Apply(true, preferTaskScheduler: false);
    Check(!task && runKey, "fresh setup on cannot preserve a prior Scheduled Task alongside the Run key");
    Console.WriteLine("Hello owner admission and startup-registration regressions passed.");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Check(bool value, string test)
{
    if (!value) throw new Exception("FAIL " + test);
    Console.WriteLine("PASS " + test);
}
