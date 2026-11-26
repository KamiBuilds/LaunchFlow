using LaunchFlow;
using LaunchFlow.Services;
using System.Windows;

static string Sample(int length) => new(Enumerable.Range(0, length).Select(i => "aZ7!"[i % 4]).ToArray());
static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static SetupWindow Ready(string password, string? confirmation = null)
{
    var window = new SetupWindow();
    window.PasswordBox.Password = password;
    window.ConfirmBox.Password = confirmation ?? password;
    window.UpdateStrengthForTest();
    return window;
}

var root = Path.Combine(Path.GetTempPath(), "launchflow-setup-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("XDG_DATA_HOME", root);
try
{
    var closed = Ready(Sample(16)); closed.CloseForTest();
    Require(closed.PasswordBox.Password == "" && closed.ConfirmBox.Password == "" && closed.ChosenPassword == "", "Window close retained secret input.");
    Console.WriteLine("PASS window_close_clears_unsubmitted_secret_input");
    // Actual production assessment and handlers; explicit adapters replace native WPF.
    foreach (var value in new string?[] { null, "", Sample(8), Sample(10), Sample(11), Sample(12), Sample(13), Sample(16),
        "é漢🙂e\u0301Z7!é漢🙂e\u0301Z7!", "   Mixed7!   ", "password123!XYZ", new('a', 12), new('a', 16),
        new(' ', 16), "test01020345", "welkom010203", "abcdefghijklmnop", "Abcdefgh12!?", "0102030405060708", "               1" })
    {
        var assessment = PasswordStrength.Evaluate(value);
        Require(assessment.Acceptable == PasswordStrength.MeetsMinimum(value), "Assessment/storage admission disagreement.");
        if (value is null || value.Length < PasswordStrength.MinimumLength)
            Require(!assessment.Acceptable && assessment.Label == "Too short" && assessment.Score == 0 &&
                assessment.Hint.Contains(PasswordStrength.MinimumLength.ToString()), "Short guidance is untruthful.");
        var window = Ready(value ?? "");
        if (!assessment.Acceptable)
        {
            var color = ((System.Windows.Media.SolidColorBrush)window.StrengthLabel.Foreground!).Color;
            Require(color != System.Windows.Media.Color.FromRgb(0x6E, 0xE7, 0xD7), "Rejected input was presented in the accepted strength color.");
            if (assessment.Label == "Too short") Require(color == System.Windows.Media.Color.FromRgb(0xF8, 0x71, 0x71), "Short-input color was not a refusal.");
        }
        System.Windows.MessageBox.Reset();
        window.SubmitForTest();
        Require((window.DialogResult == true) == assessment.Acceptable, "Actual setup admission disagreement.");
        if (!assessment.Acceptable)
        {
            Require(System.Windows.MessageBox.Count == 1 &&
                System.Windows.MessageBox.LastTitle == "Choose a stronger master password" &&
                System.Windows.MessageBox.LastMessage?.Contains(assessment.Hint) == true &&
                System.Windows.MessageBox.LastMessage?.Contains("stronger") == true &&
                window.PasswordBox.FocusCount > 0 &&
                window.PasswordBox.Password == (value ?? "") &&
                window.DialogResult is null && window.ChosenPassword == "",
                "Rejected setup password did not show actionable modal advice and stay correctable.");
        }
        else Require(System.Windows.MessageBox.Count == 0,
            "Accepted setup password unexpectedly showed a weakness popup.");
        if (assessment.Acceptable)
            Require(window.ChosenPassword == value && window.PasswordBox.Password == "" && window.ConfirmBox.Password == "", "Snapshot changed or controls retained.");
    }
    Console.WriteLine("PASS assessment_and_actual_setup_boundary_unicode_common_matrix");
    foreach (var weak in new[] { "test01020345", "welkom010203", "aaaaaaaaaaaaaaaa",
        "010101010101", new string(' ', 16), "Abcdef12!?", "0102030405060708", "               1" })
    {
        var assessment = PasswordStrength.Evaluate(weak);
        Require(!assessment.Acceptable && !PasswordStrength.MeetsMinimum(weak) &&
            assessment.Label is not ("Good" or "Strong"),
            "Known guessable or short input was approved.");
    }
    var numericCases = new (string Id, string Value)[]
    {
        ("trailing_whitespace", "123456789012 "),
        ("leading_whitespace", " 123456789012"),
        ("embedded_whitespace", "1234 5678 9012"),
        ("nonbreaking_whitespace", "123456789012\u00a0"),
        ("supplementary_digits", string.Concat(Enumerable.Range(0, 8).Select(i => char.ConvertFromUtf32(0x1D7CE + i)))),
        ("supplementary_digits_with_whitespace", " " + string.Concat(Enumerable.Range(0, 8).Select(i => char.ConvertFromUtf32(0x1D7CE + i))) + " ")
    };
    foreach (var (id, value) in numericCases)
    {
        Require(!PasswordStrength.Evaluate(value).Acceptable && !PasswordStrength.MeetsMinimum(value),
            "Numeric-only new-password admission bypass: " + id);
        System.Windows.MessageBox.Reset();
        var numericSetup = Ready(value); numericSetup.SubmitForTest();
        Require(numericSetup.DialogResult is null && numericSetup.ChosenPassword == "" &&
            numericSetup.PasswordBox.Password == value && System.Windows.MessageBox.Count == 1,
            "Numeric setup refusal did not remain correctable: " + id);
        var numericPrompt = new SecretPromptWindow("New password", "Choose one") { RequireStrongPassword = true };
        numericPrompt.SecretBox.Password = value;
        System.Windows.MessageBox.Reset(); numericPrompt.SubmitForTest();
        Require(numericPrompt.DialogResult is null && numericPrompt.Secret == "" &&
            numericPrompt.SecretBox.Password == value && System.Windows.MessageBox.Count == 1,
            "Numeric standalone new-password refusal failed: " + id);
        var legacyNumeric = new SecretPromptWindow("Existing password", "Use original input");
        legacyNumeric.SecretBox.Password = value;
        System.Windows.MessageBox.Reset(); legacyNumeric.SubmitForTest();
        Require(legacyNumeric.DialogResult == true && legacyNumeric.Secret == value && System.Windows.MessageBox.Count == 0,
            "Legacy numeric-password prompt was incorrectly revalidated: " + id);
    }
    Console.WriteLine("PASS numeric_only_whitespace_and_unicode_new_password_refusal_legacy_prompt_unchanged");
    foreach (var value in new[] { "quiet river amber lantern", "  quiet River7! amber Lantern  ", "\u6f22\u00e9\U0001f642e\u0301Z7!\u6f22\u00e9\U0001f642e\u0301Z7!" })
    {
        Require(PasswordStrength.MeetsMinimum(value), "Healthy mixed input was rejected by numeric guard.");
        var exactSetup = Ready(value); exactSetup.SubmitForTest();
        Require(exactSetup.DialogResult == true && exactSetup.TakeChosenPassword() == value,
            "Healthy input representation changed at setup handoff.");
    }
    Console.WriteLine("PASS spaced_passphrase_and_unicode_exact_snapshot_controls");
    Require(PasswordStrength.MeetsMinimum("Abcdefgh12!?") &&
        PasswordStrength.Evaluate("Abcdefgh12!?").Acceptable,
        "Healthy 12-character password was rejected.");
    Console.WriteLine("PASS explicit weak_password_denial_and_accepted_creation_boundary");

    // The shared NEW-password popup must be shown BEFORE the prompt closes,
    // with guidance rather than a silent refusal. Existing-unlock prompts are
    // deliberately NOT strength-gated, for backwards compatibility.
    var newPasswordPrompt = new SecretPromptWindow("New master password", "Choose one")
        { RequireStrongPassword = true };
    newPasswordPrompt.SecretBox.Password = "welkom010203";
    System.Windows.MessageBox.Reset();
    newPasswordPrompt.SubmitForTest();
    Require(newPasswordPrompt.DialogResult is null &&
        newPasswordPrompt.Secret == "" &&
        newPasswordPrompt.SecretBox.Password == "welkom010203" &&
        newPasswordPrompt.SecretBox.FocusCount > 0 &&
        System.Windows.MessageBox.Count == 1 &&
        System.Windows.MessageBox.LastTitle == "Choose a stronger password" &&
        System.Windows.MessageBox.LastMessage?.Contains("predictable") == true,
        "Weak new-password prompt did not display guidance before admission.");
    newPasswordPrompt.SecretBox.Password = "Abcdefgh12!?";
    System.Windows.MessageBox.Reset();
    newPasswordPrompt.SubmitForTest();
    Require(newPasswordPrompt.DialogResult == true &&
        newPasswordPrompt.Secret == "Abcdefgh12!?" &&
        newPasswordPrompt.SecretBox.Password == "" &&
        System.Windows.MessageBox.Count == 0,
        "Corrected new-password prompt was not admitted and cleared.");
    var existingPasswordPrompt = new SecretPromptWindow("Unlock existing vault", "Existing password");
    existingPasswordPrompt.SecretBox.Password = "welkom010203";
    System.Windows.MessageBox.Reset();
    existingPasswordPrompt.SubmitForTest();
    Require(existingPasswordPrompt.DialogResult == true &&
        existingPasswordPrompt.Secret == "welkom010203" &&
        System.Windows.MessageBox.Count == 0,
        "Existing vault unlock was incorrectly strength-gated.");
    Console.WriteLine("PASS new_password_popups_and_existing_password_compatibility");
    var random = new Random(71023);
    for (var n = 0; n < 2000; n++)
    {
        const string alphabet = "aZ7! 漢é\u0301";
        var value = new string(Enumerable.Range(0, random.Next(0, 49)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
        Require(PasswordStrength.Evaluate(value).Acceptable == PasswordStrength.MeetsMinimum(value), "Random assessment disagreement.");
    }
    Console.WriteLine("PASS 2000_random_assessment_equivalence");
    var setup = Ready(Sample(10)); setup.SubmitForTest();
    Require(setup.DialogResult is null && setup.ChosenPassword == "" && setup.PasswordBox.Password == Sample(10), "Short rejection not correctable.");
    setup.PasswordBox.Password = Sample(16); setup.ConfirmBox.Password = Sample(16); setup.SubmitForTest();
    Require(setup.DialogResult == true && setup.ChosenPassword == Sample(16), "Correction failed.");
    Console.WriteLine("PASS short_rejection_and_correction");
    var mismatch = Ready(Sample(16), Sample(12)); mismatch.SubmitForTest();
    Require(mismatch.DialogResult is null && mismatch.ChosenPassword == "", "Mismatch accepted.");
    mismatch.CancelForTest();
    Require(mismatch.DialogResult == false && mismatch.PasswordBox.Password == "" && mismatch.ConfirmBox.Password == "" && mismatch.ChosenPassword == "", "Cancellation retained secrets.");
    Console.WriteLine("PASS confirmation_and_cancel");
    var accepted = Ready(Sample(16)); accepted.SubmitForTest(); accepted.CloseForTest();
    Require(accepted.ChosenPassword == Sample(16) && accepted.PasswordBox.Password == "", "Successful native-close seam lost the pending handoff.");
    accepted.PasswordBox.Password = Sample(12); accepted.ConfirmBox.Password = Sample(12); accepted.SubmitForTest();
    Require(accepted.ChosenPassword == Sample(16), "Duplicate submission replaced admitted snapshot.");
    var consume = typeof(SetupWindow).GetMethod("TakeChosenPassword", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    Require(consume is not null, "Setup must provide one-shot secret handoff.");
    Require((string?)consume!.Invoke(accepted, null) == Sample(16) && accepted.ChosenPassword == "", "Handoff did not release secret.");
    Require((string?)consume.Invoke(accepted, null) == "", "Secret handoff can be repeated.");
    Console.WriteLine("PASS duplicate_submission_and_one_shot_handoff");
    Require(File.Exists(StartupDiagnostics.FilePath), "Actual setup admission emitted no diagnostic.");
    var diagnostic = File.ReadAllText(StartupDiagnostics.FilePath);
    Require(diagnostic.Contains("UiAcceptable=False") && diagnostic.Contains("UiAcceptable=True") &&
        diagnostic.Contains("UiMeetsMinimum=False") && diagnostic.Contains("ConfirmationEqual=False"), "Actual setup diagnostic booleans missing.");
    Require(!diagnostic.Contains(Sample(16)), "Input escaped into actual diagnostic output.");
    Console.WriteLine("PASS actual_setup_secret_safe_admission_diagnostics");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
finally { Directory.Delete(root, recursive: true); }
