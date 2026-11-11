using LaunchFlow.Models;
using LaunchFlow.Services;
using Microsoft.Playwright;

internal static partial class IndependentStageProbes
{
    private static async Task CustomResumedSubmitAsync(IPlaywright pw,string label)
    {
        await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync();context.SetDefaultTimeout(2000);var submits=0;
        await context.RouteAsync("https://custom-action.test/**",async r=>{
            var path=new Uri(r.Request.Url).AbsolutePath;
            if(path=="/submit"){Interlocked.Increment(ref submits);await r.FulfillAsync(new(){Status=204});return;}
            await r.FulfillAsync(new(){ContentType="text/html",Body=path=="/password"?
                "<form id='login'><input id='password' type='password' autocomplete='current-password'><button id='finish' type='button' onclick=\"fetch('/submit',{method:'POST'}).then(()=>location.href='/application')\">"+label+"</button></form>":
                "<nav><button aria-label='Account'>Account</button><a href='/logout'>Sign out</a></nav>"});
        });
        var page=await context.NewPageAsync();await page.GotoAsync("https://custom-action.test/password");
        var wf=Workflow(page.Url);wf.SubmitSelector="#finish";var vault=new CredentialVault();
        await using var engine=new AutomationEngine(vault);EngineBehaviorTests.Track(engine,wf,context,page);
        var result=await engine.RunAsync(new[]{wf},CancellationToken.None);
        Check(submits==1 && result.FinishedCount==1 && result.LoginNeedsAttention.Count==0,
            label=="Next"?"CUSTOM_RESUMED_NEXT_CONTROL":"CUSTOM_RESUMED_EXPLICIT_ACTION",$"submitRequests={submits} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} vaultReads={vault.ReadCount}");
    }
    private static async Task VerificationShellAsync(IPlaywright pw,bool semanticOtp)
    {
        await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync();context.SetDefaultTimeout(2000);
        await context.RouteAsync("https://verification-shell.test/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body=
            "<title>Application</title><nav><button aria-label='Account'>Account</button><a href='/logout'>Sign out</a></nav>"+
            "<form id='verification'><div>Enter verification code</div><label>Code<input name='verificationCode' inputmode='numeric' "+(semanticOtp?"autocomplete='one-time-code'":"")+"></label></form>"+
            "<button id='follow' onclick=\"document.body.dataset.acted='yes'\">Sensitive follow-up</button>"}));
        var page=await context.NewPageAsync();await page.GotoAsync("https://verification-shell.test/login");
        var wf=Workflow(page.Url);wf.Steps.Add(new(){Type="Click",Target="#follow"});var vault=new CredentialVault();
        await using var engine=new AutomationEngine(vault);EngineBehaviorTests.Track(engine,wf,context,page);
        var proof=await LoginHealth.VerifiedApplicationSessionAsync(page,wf.StartUrl);
        var result=await engine.RunAsync(new[]{wf},CancellationToken.None);
        var acted=await page.GetAttributeAsync("body","data-acted")=="yes";
        Check(!proof && !acted && result.FinishedCount==0 && result.LoginNeedsAttention.Count==1,
            semanticOtp?"SEMANTIC_OTP_BLOCK_CONTROL":"PLAIN_VERIFICATION_MUST_BLOCK",$"applicationProof={proof} followed={acted} finished={result.FinishedCount} attention={result.LoginNeedsAttention.Count} vaultReads={vault.ReadCount}");
    }
    private static async Task CombinedIdpMappingAsync(IPlaywright pw,bool mapped,bool approved)
    {
        await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using var context=await browser.NewContextAsync();context.SetDefaultTimeout(2000);var submits=0;var matched=false;
        await context.RouteAsync("https://mapping-app.test/**",r=>r.FulfillAsync(new(){ContentType="text/html",Body="<nav><button aria-label='Account'>Account</button><a href='/logout'>Sign out</a></nav>"}));
        await context.RouteAsync("https://mapping-idp.test/**",async r=>{
            if(new Uri(r.Request.Url).AbsolutePath=="/submit"){
                Interlocked.Increment(ref submits);matched=r.Request.PostData=="matched=true";await r.FulfillAsync(new(){Status=204});return;
            }
            await r.FulfillAsync(new(){ContentType="text/html",Body="""
                <form id="login" action="/session" onsubmit="event.preventDefault();fetch('/submit',{method:'POST',body:document.getElementById('password').value==='owned-synthetic-secret'?'matched=true':'matched=false'}).then(()=>location.href='https://mapping-app.test/application')">
                  <input id="email" type="email"><input id="password" type="password" autocomplete="current-password"><button type="submit">Sign in</button>
                </form>
                """});
        });
        // Start at the foreign combined-form landing and exercise the real private login pipeline.
        // This probe does not claim to test the preceding HTTP redirect or RunOne navigation.
        var page=await context.NewPageAsync();await page.GotoAsync("https://mapping-idp.test/login");
        var wf=Workflow("https://mapping-app.test/login");
        if(mapped)wf.PasswordCredentialOrigin="https://mapping-idp.test";
        if(approved)wf.ApprovedLoginOrigins.Add("https://mapping-idp.test");
        var vault=new CredentialVault(){Credential=new SiteCredential("fixture@example.invalid","owned-synthetic-secret")};
        await using var engine=new AutomationEngine(vault);engine.ApproveLoginOriginAsync=(_,_)=>Task.FromResult(false);EngineBehaviorTests.Track(engine,wf,context,page);
        var login=typeof(AutomationEngine).GetMethod("TryAutoLoginAsync",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var flow=await (Task<LoginFlowResult>)login.Invoke(engine,new object?[]{wf,page,CancellationToken.None,null,null})!;
        var completed=flow.Succeeded;
        var expectSuccess=mapped && approved;
        Check(expectSuccess?matched&&submits==1&&completed:!matched&&submits==0&&!completed,
            mapped?"COMBINED_MAPPED_IDP_CONTROL":approved?"COMBINED_UNMAPPED_IDP_MUST_BLOCK":"COMBINED_DENIED_IDP_CONTROL",$"mapped={mapped} originApproved={approved} syntheticPasswordMatchedAtIdp={matched} submitRequests={submits} completed={completed}");
    }
}
