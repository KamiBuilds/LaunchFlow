using System.Diagnostics;
using LaunchFlow.Services;

if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("Synthetic schtasks executable is a Linux-only fixture.");
var sandbox = Path.Combine(Path.GetTempPath(), "launchflow-scheduler-080-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sandbox);
var previousPath = Environment.GetEnvironmentVariable("PATH") ?? "";
try
{
    // The shell produces more than a redirected pipe's OS buffer. We use
    // production TaskSchedulerService/Process code but never Windows tasks.
    var script = Path.Combine(sandbox, "schtasks.exe");
    File.WriteAllText(script, """
        #!/bin/sh
        case "$*" in
          *"/Query /TN"*) exit 1 ;;
          *"/Query /FO CSV /NH"*)
             i=0
             while [ "$i" -lt 18000 ]; do
               printf '"\\Task%06d","Never","Ready"\n' "$i"
               i=$((i + 1))
             done
             exit 0 ;;
          *) exit 91 ;;
        esac
        """ + "\n");
    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    Environment.SetEnvironmentVariable("PATH", sandbox + Path.PathSeparator + previousPath);
    var watch = Stopwatch.StartNew();
    TaskSchedulerService.Apply(enabled: false);
    watch.Stop();
    Check(watch.Elapsed < TimeSpan.FromSeconds(12),
        "full scheduled-task CSV output drains concurrently without the former 15-second pipe timeout");
    Console.WriteLine("Scheduler redirected-pipe regression passed.");
}
finally
{
    Environment.SetEnvironmentVariable("PATH", previousPath);
    if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    Console.WriteLine("PASS " + name);
}
