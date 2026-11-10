using System.Reflection;
using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static partial class IndependentStageProbes
{
    private static int failures;
    public static async Task RunAsync(IPlaywright pw)
    {
        await SsoAsync(pw, false);
        await SsoAsync(pw, true);
        await MissingPasswordAsync(pw);
        await ApprovalCancellationAsync(pw);
        await CustomResumedSubmitAsync(pw, "Next");
        await CustomResumedSubmitAsync(pw, "Unlock workspace");
        await VerificationShellAsync(pw, false);
        await VerificationShellAsync(pw, true);
        await CombinedIdpMappingAsync(pw, false, true);
        await CombinedIdpMappingAsync(pw, true, true);
        await CombinedIdpMappingAsync(pw, false, false);
        Console.WriteLine($"INDEPENDENT_SUMMARY failures={failures}");
        if (failures != 0) throw new Exception("Independent stage probes failed their acceptance assertions; see non-secret observations above.");
    }
    private static void Check(bool ok, string name, string observation)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + " " + observation);
        if (!ok) failures++;
    }
    private static WebsiteWorkflow Workflow(string url) => new()
    {
        StartUrl=url, Name="Independent synthetic review", LoginWaitSeconds=1,
        LoginSuccessSelector="", LoginSuccessUrlContains=""
    };
    private static async Task SsoAsync(IPlaywright pw, bool explicitSuccess)
    {
        await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync(); context.SetDefaultTimeout(2000);
        await context.RouteAsync("https://sso-proof.test/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body="<nav id='account'><button aria-label='Account'>Account</button><a href='/logout'>Sign out</a></nav><button id='follow' onclick=\"document.body.dataset.followed='yes'\">Follow</button>"}));
        var page=await context.NewPageAsync();await page.GotoAsync("https://sso-proof.test/application");
        var wf=Workflow(page.Url);wf.LoginMode="SSO";wf.Steps.Add(new(){Type="Click",Target="#follow"});
        if(explicitSuccess)wf.LoginSuccessSelector="#account";
        var vault=new CredentialVault();await using var engine=new AutomationEngine(vault);EngineBehaviorTests.Track(engine,wf,context,page);
        var proof=await LoginHealth.VerifiedApplicationSessionAsync(page,wf.StartUrl);
        var result=await engine.RunAsync(new[]{wf},CancellationToken.None);
        var followed=await page.GetAttributeAsync("body","data-followed")=="yes";
        Check(proof && result.FinishedCount==1 && result.LoginNeedsAttention.Count==0 && followed && vault.ReadCount==0,
            explicitSuccess?"SSO_EXPLICIT_CONTROL":"SSO_PROOF_WITH_FOLLOWUP",$"applicationProof={proof} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} followed={followed} vaultReads={vault.ReadCount}");
    }
    private static async Task MissingPasswordAsync(IPlaywright pw)
    {
        await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync();context.SetDefaultTimeout(2000);var submits=0;
        await context.RouteAsync("https://required-password.test/**",async r=>{
            if(new Uri(r.Request.Url).AbsolutePath=="/attempt"){Interlocked.Increment(ref submits);await r.FulfillAsync(new(){Status=204});return;}
            await r.FulfillAsync(new(){ContentType="text/html",Body="<form onsubmit=\"event.preventDefault();document.body.dataset.submitted='yes';fetch('/attempt',{method:'POST'})\"><input id='user'><button id='submit' type='submit'>Sign in</button></form>"});
        });
        var page=await context.NewPageAsync();await page.GotoAsync("https://required-password.test/login");
        var wf=Workflow(page.Url);wf.UsernameSelector="#user";wf.PasswordSelector="#missing-password";wf.SubmitSelector="#submit";
        await using var engine=new AutomationEngine(new CredentialVault());EngineBehaviorTests.Track(engine,wf,context,page);
        var result=await engine.RunAsync(new[]{wf},CancellationToken.None);
        Check(submits==0 && await page.GetAttributeAsync("body","data-submitted") is null && result.FinishedCount==0,
            "MISSING_REQUIRED_PASSWORD_NO_SUBMIT",$"submitRequests={submits} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count}");
    }
    private static async Task ApprovalCancellationAsync(IPlaywright pw)
    {
        await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync();context.SetDefaultTimeout(2000);
        await context.RouteAsync("**/*",r=>r.FulfillAsync(new(){ContentType="text/html",Body=r.Request.Url.Contains("identity.test")?
            "<form id='auth-password'><input id='password' type='password' autocomplete='current-password'><button type='button'>Next</button></form>":
            "<form id='login' action='/auth'><input id='email' type='email'><button type='button' onclick=\"location.href='https://identity.test/password'\">Log In</button></form>"}));
        var page=await context.NewPageAsync();await page.GotoAsync("https://app-review.test/login");var wf=Workflow(page.Url);wf.PasswordCredentialOrigin="https://identity.test";
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine=new AutomationEngine(new CredentialVault());EngineBehaviorTests.Track(engine,wf,context,page);
        engine.ApproveLoginOriginAsync=(_,_)=>{entered.TrySetResult();return release.Task;};
        using var cts=new CancellationTokenSource();var run=engine.RunAsync(new[]{wf},cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(8));cts.Cancel();
        var completed=await Task.WhenAny(run,Task.Delay(700))==run;
        var secretWasFilled=await page.InputValueAsync("#password")!="";
        release.TrySetResult(false);try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch(OperationCanceledException) { }
        Check(completed && !secretWasFilled,"CANCEL_DURING_ORIGIN_PREFLIGHT",$"completedBeforeApprovalRelease={completed} secretWasFilled={secretWasFilled}");
    }
}
