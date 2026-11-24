using LaunchFlow.Models;
using LaunchFlow.Services;

internal static class PasswordStrengthTests
{
    public static void Run(Action<string, Action> test)
    {
        var cases = new (string Name, string? Password, bool Accepted)[]
        {
            ("empty", "", false),
            ("null", null, false),
            ("10 characters previously labelled Strong", "Abcdef12!?", false),
            ("11 characters previously labelled Strong", "Abcdefg12!?", false),
            ("12 characters with good complexity", "Abcdefgh12!?", true),
            ("16 identical letters", "aaaaaaaaaaaaaaaa", false),
            ("16 whitespace characters", new string(' ', 16), false),
            ("repeated mixed pattern", "Ab1!Ab1!Ab1!", false),
            ("common password inside a longer string", "MyPassword123!ABC", false),
            ("long mixed-case password", "NoSpacesOnly123", true),
            ("long passphrase containing spaces", "correct horse battery staple", true),
            ("non-ASCII passphrase", "Zażółć gęślą jaźń 2049!", true)
        };
        foreach (var (name, password, expected) in cases)
        {
            test("new-password acceptance is consistent: " + name, () =>
            {
                var assessment = PasswordStrength.Evaluate(password);
                VaultTests.Check(assessment.Acceptable == expected,
                    "Password approval differed from expected policy: " + name);
                VaultTests.Check(PasswordStrength.MeetsMinimum(password) == assessment.Acceptable,
                    "Strength indicator and vault approval disagree: " + name);
                if (!expected)
                    VaultTests.Check(assessment.Label != "Good" && assessment.Label != "Strong",
                        "Rejected password was labelled Good or Strong: " + name);
            });
        }

        test("strict vault rejects a short password that the old UI called Strong", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "lf-password-gate-" +
                Guid.NewGuid().ToString("N"), "launchflow.db");
            try
            {
                var db = new VaultDatabase(path);
                var declined = false;
                try { db.Initialize("Abcdef12!?", new Configuration()); }
                catch (ArgumentException) { declined = true; }
                VaultTests.Check(declined && !db.Exists,
                    "Vault accepted an invalid new password or wrote an unusable vault.");
                const string approved = "Abcdefgh12!?";
                VaultTests.Check(PasswordStrength.Evaluate(approved).Acceptable, "UI approval missing.");
                db.Initialize(approved, new Configuration());
                db.Lock();
                VaultTests.Check(db.Unlock(approved), "UI-approved vault password could not cold-unlock.");
                db.Lock();
            }
            finally
            {
                if (Directory.Exists(Path.GetDirectoryName(path)!))
                    Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
        });

        test("portable export refuses weak passwords without creating a file", () =>
        {
            var path = Path.Combine(Path.GetTempPath(),
                "lf-password-export-" + Guid.NewGuid().ToString("N") + ".launchflow");
            try
            {
                foreach (var weak in new[] { "Abcdef12!?", "aaaaaaaaaaaaaaaa", new string(' ', 16) })
                {
                    var denied = false;
                    try { PortableExport.Write(path, weak, new PortableExport.PortablePackage()); }
                    catch (ArgumentException) { denied = true; }
                    VaultTests.Check(denied && !File.Exists(path), "Weak export password created an archive.");
                }
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
    }
}
