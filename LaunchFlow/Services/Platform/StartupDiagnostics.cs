using System.IO;
using System.Diagnostics;
using System.Text;

namespace LaunchFlow.Services;

/// <summary>Bounded, best-effort startup evidence. Never records inputs, messages, paths or raw stacks.</summary>
internal static class StartupDiagnostics
{
    internal enum Stage
    {
        ProcessStarted, ExistingInstance, ActivationFailed, DependencyProbeStarted, DependencyProbeRejected,
        VaultUnlockStarted, VaultUnlockCancelled, VaultReady, WindowConstructionStarted, WindowShown,
        OptionalUiInitializationFailed, StartupException, UiUnhandledException, FatalException, ProcessExited,
        SetupAssessment, SetupHandoff, SetupValidationRejected, VaultColdVerify, VaultColdVerifyComplete,
        VaultRevokeCreatedSession, VaultColdRevoke, VaultOpenVerifiedSession,
        VaultRecoveryStarted, VaultRecoveryFailed,
        VaultCreateAdmission, VaultCreateCloneConfig, VaultCreateSerializeConfig, VaultCreateRandomMaterial,
        VaultCreateDeriveKey, VaultCreateOpenConnection, VaultCreateBeginTransaction, VaultCreateEnsureSchema,
        VaultCreateSealVerifier, VaultCreateInsertMeta, VaultCreateWriteConfig, VaultCreatePersistCredentials,
        VaultCreateCommit, VaultCreateIdentityToken, VaultCreateSnapshotToken, VaultCreateDisposeResources,
        VaultCreatePublish, VaultCreatePublishCaches, VaultCreateCleanup, VaultCreateComplete
    }

    internal enum Admission { UiAcceptable, UiMeetsMinimum, ConfirmationEqual, HandoffOrdinalEqual, DbMeetsMinimum }

    internal static void RecordAdmission(Admission admission, bool value)
    {
        try { if (Enum.IsDefined(admission)) Write($"{admission}={value}"); }
        catch { /* Admission must not depend on the diagnostic sink. */ }
    }

    private static readonly object Gate = new();
    private const long MaximumBytes = 256 * 1024;
    internal static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaunchFlow", "startup-diagnostics.log");

    internal static Exception? RootCause(Exception? exception)
    {
        var current = exception;
        for (var i = 0; i < 8 && current?.InnerException is not null; i++)
        {
            if (current is System.Reflection.TargetInvocationException or TypeInitializationException or
                System.Windows.Markup.XamlParseException) current = current.InnerException;
            else break;
        }
        return current;
    }

    internal static void Record(Stage stage, Exception? exception = null)
    {
        try
        {
            if (!Enum.IsDefined(stage)) return;
            var identity = stage == Stage.ProcessStarted ? Identity() : "";
            Write($"{stage}{identity} {ExceptionGraph(exception)}");
        }
        catch { /* Diagnostics have no authority over startup or credential access. */ }
    }

    private static string Identity()
    {
        var assembly = typeof(StartupDiagnostics).Assembly;
        var text = $" runtime={Environment.Version} arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}" +
            $" assembly={assembly.GetName().Version} mvid={typeof(StartupDiagnostics).Module.ModuleVersionId:D}";
        foreach (var name in new[] { "Microsoft.Data.Sqlite", "SQLitePCLRaw.core", "Konscious.Security.Cryptography.Argon2" })
        {
            var dependency = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
            if (dependency is not null) text += $" {name}={dependency.GetName().Version}";
        }
        return text;
    }

    private static string ExceptionGraph(Exception? exception)
    {
        if (exception is null) return "None";
        var pending = new Queue<(Exception Error, int Parent, int Depth)>();
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Enqueue((exception, -1, 0));
        var text = new StringBuilder();
        var count = 0;
        while (pending.Count > 0 && count < 16)
        {
            var (error, parent, depth) = pending.Dequeue();
            if (!seen.Add(error)) continue;
            var id = count++;
            text.Append($" [{id}:{parent}:{ExceptionType(error)}:0x{unchecked((uint)error.HResult):X8}]");
            if (depth >= 7) { text.Append(" bounded"); continue; }
            if (error is AggregateException aggregate)
            {
                foreach (var child in aggregate.InnerExceptions.Take(8)) pending.Enqueue((child, id, depth + 1));
                if (aggregate.InnerExceptions.Count > 8) text.Append(" bounded");
            }
            else if (error.InnerException is { } inner) pending.Enqueue((inner, id, depth + 1));
        }
        if (pending.Count > 0) text.Append(" bounded");
        return text.ToString();
    }

    // Unknown exception type names are not trusted diagnostic strings.
    private static string ExceptionType(Exception error) => error switch
    {
        AggregateException => "AggregateException",
        ArgumentOutOfRangeException => "ArgumentOutOfRangeException",
        ArgumentNullException => "ArgumentNullException",
        ArgumentException => "ArgumentException",
        FileNotFoundException => "FileNotFoundException",
        DirectoryNotFoundException => "DirectoryNotFoundException",
        InvalidDataException => "InvalidDataException",
        IOException => "IOException",
        UnauthorizedAccessException => "UnauthorizedAccessException",
        ObjectDisposedException => "ObjectDisposedException",
        InvalidOperationException => "InvalidOperationException",
        System.Security.Cryptography.CryptographicException => "CryptographicException",
        System.Reflection.TargetInvocationException => "TargetInvocationException",
        TypeInitializationException => "TypeInitializationException",
        System.Windows.Markup.XamlParseException => "XamlParseException",
        DllNotFoundException => "DllNotFoundException",
        BadImageFormatException => "BadImageFormatException",
        TypeLoadException => "TypeLoadException",
        OutOfMemoryException => "OutOfMemoryException",
        OverflowException => "OverflowException",
        NotSupportedException => "NotSupportedException",
        OperationCanceledException => "OperationCanceledException",
        _ => error.GetType().FullName == "Microsoft.Data.Sqlite.SqliteException" ? "SqliteException" : "OtherException"
    };

    private static void Write(string entry)
    {
        var entered = false;
        try
        {
            entered = Monitor.TryEnter(Gate);
            if (!entered) return;
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var line = $"{DateTimeOffset.UtcNow:O} {entry}{Environment.NewLine}";
            if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(line) > MaximumBytes)
                File.Move(path, path + ".previous", overwrite: true);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch { /* A blocked or unwritable sink must never change the operation's outcome. */ }
        finally { if (entered) Monitor.Exit(Gate); }
    }
}
