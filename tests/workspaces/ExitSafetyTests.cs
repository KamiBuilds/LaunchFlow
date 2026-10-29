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
