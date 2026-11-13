using LaunchFlow.Services;

internal static class ExitSafetyTests
{
    public static IEnumerable<(string Name, Action Body)> Cases()
    {
        yield return ("clean locked vault may exit without accessing encrypted storage", () =>
        {
            int saves = 0;
            Check(ExitSafety.TryPersistForExit(() => false, () => false, () => { saves++; return true; })
                && saves == 0, "clean locked exit was blocked or attempted a save");
        });
        yield return ("clean unlocked exit skips unnecessary encrypted configuration write", () =>
        {
            int saves = 0;
            Check(ExitSafety.TryPersistForExit(() => true, () => false, () => { saves++; return false; })
                && saves == 0, "clean exit rewrote encrypted data or failed without pending edits");
        });
        yield return ("clean exit does not need to query a locked vault", () =>
        {
            Check(ExitSafety.TryPersistForExit(
                    () => throw new Exception("clean exit unexpectedly queried vault"),
                    () => false,
                    () => throw new Exception("clean exit unexpectedly saved")),
                "clean exit was not fast-pathed");
        });
        yield return ("dirty locked vault refuses explicit exit without losing edits", () =>
        {
            int saves = 0;
            Check(!ExitSafety.TryPersistForExit(() => false, () => true, () => { saves++; return true; })
                && saves == 0, "dirty locked exit silently discarded pending edits");
        });
        yield return ("unlocked exit requires durable save and clean state", () =>
        {
            bool dirty = true;
            int saves = 0;
            Check(ExitSafety.TryPersistForExit(() => true, () => dirty,
                () => { saves++; dirty = false; return true; }) && saves == 1 && !dirty,
                "unlocked save did not preserve changes exactly once");
        });
        yield return ("failed write never authorizes close", () =>
        {
            Check(!ExitSafety.TryPersistForExit(() => true, () => true, () => false),
                "failed persistent write was treated as saved");
        });
        yield return ("reentrant edits after a reported save block close", () =>
        {
            bool dirty = true;
            Check(!ExitSafety.TryPersistForExit(() => true, () => dirty,
                () => { dirty = false; dirty = true; return true; }),
                "reentrant change disappeared after a successful save result");
        });
    }

    private static void Check(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
