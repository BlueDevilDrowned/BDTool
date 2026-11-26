using BDTool.Cli;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    // 保留进程控制权，让 Application 有机会写 Cancelled 终态 Activity。
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

Console.CancelKeyPress += cancelHandler;
try
{
    return await CliEntrypoint.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}
