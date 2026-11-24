using System.Runtime.ExceptionServices;
using LaunchFlow.Models;
using LaunchFlow.Services;

namespace LaunchFlow;

public partial class App
{
    /// <summary>
    /// False means an expected, pre-storage validation refusal. All operational failures propagate;
    /// their durable outcome is not inferred from the absence of a ready UI.
    /// </summary>
    internal static bool TryCreateAndVerifyVault(VaultDatabase db, string password, Configuration settings,
        Dictionary<Guid, SiteCredential>? credentials = null)
    {
        var admitted = PasswordStrength.MeetsMinimum(password);
        StartupDiagnostics.RecordAdmission(StartupDiagnostics.Admission.DbMeetsMinimum, admitted);
        if (!admitted)
        {
            StartupDiagnostics.Record(StartupDiagnostics.Stage.SetupValidationRejected);
            return false;
        }

        try
        {
            db.Initialize(password, settings, credentials);
            StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultRevokeCreatedSession);
            db.Lock();
            StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultColdVerify);
            var cold = new VaultDatabase(db.DatabasePath);
            Exception? verificationFailure = null;
            try
            {
                if (!cold.Unlock(password))
                    throw new InvalidOperationException("The newly created vault could not be independently authenticated. Its encrypted material was preserved.");
            }
            catch (Exception error) { verificationFailure = error; }
            if (verificationFailure is null)
                StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultColdRevoke);
            try { cold.Lock(); }
            catch (Exception cleanup)
            {
                if (verificationFailure is not null)
                    throw new AggregateException("Vault verification and key revocation failed.", verificationFailure, cleanup);
                throw;
            }
            if (verificationFailure is not null) ExceptionDispatchInfo.Capture(verificationFailure).Throw();
            StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultOpenVerifiedSession);
            if (!db.Unlock(password))
                throw new InvalidOperationException("The independently verified vault could not open a live session. Its encrypted material was preserved.");
            StartupDiagnostics.Record(StartupDiagnostics.Stage.VaultColdVerifyComplete);
            return true;
        }
        catch (Exception primary)
        {
            try { db.Lock(); }
            catch (Exception cleanup)
            {
                throw new AggregateException("Vault creation or verification failed and key revocation also failed.", primary, cleanup);
            }
            // Never reinitialize, quarantine or delete here: commit may already have completed.
            throw;
        }
    }
}
