using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgenticSystem.Data;

namespace AgenticSystem.Services;

public class AgentSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(15);

    public AgentSchedulerService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("AgentSchedulerService background worker has started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateSchedulesAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error checking schedules in AgentSchedulerService: {ex.Message}");
            }

            await Task.Delay(_checkInterval, stoppingToken);
        }

        Console.WriteLine("AgentSchedulerService background worker is stopping.");
    }

    private async Task EvaluateSchedulesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var profileService = scope.ServiceProvider.GetRequiredService<FirebaseProfileService>();
        var searchSocialService = scope.ServiceProvider.GetRequiredService<FirebaseSearchSocialService>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<SearchAndSocialOrchestrator>();

        // 1. Get all active business profiles
        var profiles = await profileService.GetAllProfilesAsync();
        if (!profiles.Any()) return;

        // 2. Process each profile schedule
        foreach (var profile in profiles)
        {
            var schedule = await searchSocialService.GetScheduleAsync(profile.UserId);

            if (schedule == null)
            {
                // Create default schedule: 24 hour interval, starts now.
                schedule = new AgentSchedule(
                    profile.UserId,
                    24, // 24 hours default
                    DateTime.MinValue,
                    DateTime.UtcNow, // Trigger immediately on first run
                    true
                );
                await searchSocialService.SaveScheduleAsync(schedule);
            }

            if (schedule.IsEnabled && DateTime.UtcNow >= schedule.NextRunTime)
            {
                Console.WriteLine($"[Scheduler] Triggering autonomous run for User: {profile.UserId} (Business: {profile.BusinessName})");

                // Update schedule timings first to prevent overlapping concurrent evaluations
                var nextRun = DateTime.UtcNow.AddHours(schedule.IntervalHours);
                var updatedSchedule = schedule with {
                    LastRunTime = DateTime.UtcNow,
                    NextRunTime = nextRun
                };
                await searchSocialService.SaveScheduleAsync(updatedSchedule);

                // Run workflow in the background to avoid blocking other checks
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var runScope = _serviceProvider.CreateScope();
                        var runOrchestrator = runScope.ServiceProvider.GetRequiredService<SearchAndSocialOrchestrator>();
                        await runOrchestrator.RunWorkflowAsync(profile.UserId, profile);
                    }
                    catch (Exception runEx)
                    {
                        Console.WriteLine($"Error running autonomous agent job for user {profile.UserId}: {runEx.Message}");
                    }
                });
            }
        }
    }
}
