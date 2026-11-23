using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Infrastructure.Ipc;
using Guardian.Storage;
using Guardian.Storage.Repositories;
using Guardian.Windows.Display;
using Guardian.Windows.Input;
using Guardian.Windows.Power;
using Guardian.Windows.SystemState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace Guardian.Service;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var paths = AppPaths.Resolve();
        Directory.CreateDirectory(paths.LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "guardian-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Category}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddWindowsService(options => options.ServiceName = ProtocolConstants.ServiceName);
            builder.Services.AddSerilog();

            builder.Services.AddSingleton(paths);
            builder.Services.AddSingleton(new GuardianDb(paths.DatabasePath));

            builder.Services.AddSingleton<IGuardianLog>(sp =>
                new SerilogGuardianLog(new LogRepository(sp.GetRequiredService<GuardianDb>())));

            builder.Services.AddSingleton<IClock, SystemClock>();
            builder.Services.AddSingleton<IDisplayPrivacyController, DisplayPrivacyController>();
            builder.Services.AddSingleton<IPowerRequestController, PowerRequestController>();
            builder.Services.AddSingleton<IInputIsolationController, InputIsolationController>();
            builder.Services.AddSingleton<ISystemStateReader, SystemStateReader>();

            builder.Services.AddSingleton<SessionRepository>(sp =>
                new SessionRepository(sp.GetRequiredService<GuardianDb>()));
            builder.Services.AddSingleton<SettingsRepository>(sp =>
                new SettingsRepository(sp.GetRequiredService<GuardianDb>()));
            builder.Services.AddSingleton<RecoveryRepository>(sp =>
                new RecoveryRepository(sp.GetRequiredService<GuardianDb>()));
            builder.Services.AddSingleton<CapabilityRepository>(sp =>
                new CapabilityRepository(sp.GetRequiredService<GuardianDb>()));
            builder.Services.AddSingleton<LogRepository>(sp =>
                new LogRepository(sp.GetRequiredService<GuardianDb>()));

            builder.Services.AddSingleton<IGuardianEventSink>(sp =>
                new PersistentEventSink(
                    sp.GetRequiredService<IGuardianLog>(),
                    sp.GetRequiredService<SessionRepository>(),
                    sp.GetRequiredService<SettingsRepository>(),
                    sp.GetRequiredService<RecoveryRepository>(),
                    sp.GetRequiredService<CapabilityRepository>()));

            builder.Services.AddSingleton(sp =>
            {
                var settings = sp.GetRequiredService<SettingsRepository>()
                    .LoadGuardianSettings(new GuardianSettings());
                return new ProtectionEngine(
                    sp.GetRequiredService<IGuardianLog>(),
                    new SystemClock(),
                    sp.GetRequiredService<IDisplayPrivacyController>(),
                    sp.GetRequiredService<IPowerRequestController>(),
                    sp.GetRequiredService<IInputIsolationController>(),
                    sp.GetRequiredService<ISystemStateReader>(),
                    sp.GetRequiredService<IGuardianEventSink>(),
                    settings);
            });

            builder.Services.AddSingleton<StreamingTopologyStore>();
            builder.Services.AddSingleton(sp =>
            {
                var repository = sp.GetRequiredService<SettingsRepository>();
                // 设置必须现场读：用户可能先改设置再点一键，锁在构造期会读到旧值。
                // 旧版本保存的设置里这项可能还是空串，此时回退到安装根 offline 目录（发布包内置产物放在那里）。
                string Offline()
                {
                    var configured = repository.LoadGuardianSettings(new GuardianSettings()).StreamingEnvOfflineDirectory;
                    return string.IsNullOrWhiteSpace(configured) ? GuardianSettings.DefaultOfflineDirectory : configured;
                }

                return StreamingEnvOrchestrator.Create(
                    sp.GetRequiredService<StreamingTopologyStore>(),
                    new StreamingEnvSettings(
                        Offline,
                        () => repository.LoadGuardianSettings(new GuardianSettings()).VirtualOutputGuid,
                        guid =>
                        {
                            var current = repository.LoadGuardianSettings(new GuardianSettings()).Clone();
                            if (string.Equals(current.VirtualOutputGuid, guid, StringComparison.OrdinalIgnoreCase))
                            {
                                return;
                            }

                            current.VirtualOutputGuid = guid;
                            current.VirtualOutputGuidAt = DateTimeOffset.Now;
                            repository.SaveGuardianSettings(current);
                        }),
                    sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<IGuardianLog>());
            });

            builder.Services.AddSingleton<CommandRouter>();
            builder.Services.AddSingleton(sp =>
            {
                var router = sp.GetRequiredService<CommandRouter>();
                var appPaths = sp.GetRequiredService<AppPaths>();
                var log = sp.GetRequiredService<IGuardianLog>();
                return new GuardianPipeServer(
                    router.Handle,
                    pid => CallerVerifier.IsAuthorized(
                        pid,
                        new[] { "Guardian.App.exe", "Guardian.Cli.exe" },
                        appPaths.InstallDirectory),
                    message => log.Write(GuardianLogLevel.Error, "ipc", message));
            });

            builder.Services.AddHostedService<GuardianHost>();

            var host = builder.Build();
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Guardian Service 启动失败");
            throw;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
