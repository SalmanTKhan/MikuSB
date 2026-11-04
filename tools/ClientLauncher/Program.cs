using MikuSB.Loader;
try
{
    Console.WriteLine($"Local client launched with PID {GameLaunchService.Launch(args)}.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
