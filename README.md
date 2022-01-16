# CronTimer
Provides a cron timer similar to [`System.Threading.PeriodicTimer`](https://docs.microsoft.com/en-us/dotnet/api/system.threading.periodictimer?view=net-6.0) that enables waiting asynchronously for timer ticks.

## Usage

Normal usage:

```c#
// Every minute
using var timer = new CronTimer("* * * * *");

while (await timer.WaitForNextTickAsync())
{
    // Do work
}
```

Example [hosted service](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-6.0&tabs=visual-studio):

```c#
public class CronJob : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Every day at 8am local time
        using var timer = new CronTimer("0 8 * * *", TimeZoneInfo.Local);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Do work
        }
    }
}
```

Non-standard cron expression:

```c#
// Every 30 seconds
using var timer = new CronTimer(CronExpression.Parse("*/30 * * * * *", CronFormat.IncludeSeconds));
```

## Resources

* [Hanfire/Cronos](https://github.com/HangfireIO/Cronos) - Library for working with cron expressions.
* [Crontab.guru](https://crontab.guru/) - The cron schedule expression editor.
