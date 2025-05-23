// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting.Devcontainers.Codespaces;
using Aspire.Hosting.ConsoleLogs;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;
using Aspire.Hosting.Devcontainers;

namespace Aspire.Hosting.Tests.Dashboard;

public class DashboardLifecycleHookTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [MemberData(nameof(Data))]
    public async Task WatchDashboardLogs_WrittenToHostLoggerFactory(DateTime? timestamp, string logMessage, string expectedMessage, string expectedCategory, LogLevel expectedLevel)
    {
        // Arrange
        var testSink = new TestSink();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddProvider(new TestLoggerProvider(testSink));
            b.AddXunit(testOutputHelper);
        });
        var logChannel = Channel.CreateUnbounded<WriteContext>();
        testSink.MessageLogged += c => logChannel.Writer.TryWrite(c);

        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create(logger: factory.CreateLogger<ResourceNotificationService>());
        var configuration = new ConfigurationBuilder().Build();
        var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration, loggerFactory: factory);

        var model = new DistributedApplicationModel(new ResourceCollection());
        await hook.BeforeStartAsync(model, CancellationToken.None).DefaultTimeout();

        await resourceNotificationService.PublishUpdateAsync(model.Resources.Single(), s => s).DefaultTimeout();

        string resourceId = default!;
        await foreach (var item in resourceLoggerService.WatchAnySubscribersAsync().DefaultTimeout())
        {
            if (item.Name.StartsWith(KnownResourceNames.AspireDashboard) && item.AnySubscribers)
            {
                resourceId = item.Name;
                break;
            }
        }

        // Act
        var dashboardLoggerState = resourceLoggerService.GetResourceLoggerState(resourceId);
        dashboardLoggerState.AddLog(LogEntry.Create(timestamp, logMessage, isErrorMessage: false), inMemorySource: true);

        // Assert
        while (true)
        {
            var logContext = await logChannel.Reader.ReadAsync().DefaultTimeout();
            if (logContext.LoggerName == expectedCategory)
            {
                Assert.Equal(expectedMessage, logContext.Message);
                Assert.Equal(expectedLevel, logContext.LogLevel);
                break;
            }
        }
    }

    [Fact]
    public async Task BeforeStartAsync_ExcludeLifecycleCommands_CommandsNotAddedToDashboard()
    {
        // Arrange
        var resourceLoggerService = new ResourceLoggerService();
        var resourceNotificationService = ResourceNotificationServiceTestHelpers.Create();
        var configuration = new ConfigurationBuilder().Build();
        var hook = CreateHook(resourceLoggerService, resourceNotificationService, configuration);

        var model = new DistributedApplicationModel(new ResourceCollection());

        // Act
        await hook.BeforeStartAsync(model, CancellationToken.None).DefaultTimeout();
        var dashboardResource = model.Resources.Single(r => string.Equals(r.Name, KnownResourceNames.AspireDashboard, StringComparisons.ResourceName));
        dashboardResource.AddLifeCycleCommands();

        // Assert
        Assert.Single(dashboardResource.Annotations.OfType<ExcludeLifecycleCommandsAnnotation>());
        Assert.Empty(dashboardResource.Annotations.OfType<ResourceCommandAnnotation>());
    }

    private static DashboardLifecycleHook CreateHook(
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService,
        IConfiguration configuration,
        ILoggerFactory? loggerFactory = null,
        IOptions<CodespacesOptions>? codespacesOptions = null,
        IOptions<DevcontainersOptions>? devcontainersOptions = null
        )
    {
        codespacesOptions ??= Options.Create(new CodespacesOptions());
        devcontainersOptions ??= Options.Create(new DevcontainersOptions());
        var settingsWriter = new DevcontainerSettingsWriter(NullLogger<DevcontainerSettingsWriter>.Instance, codespacesOptions, devcontainersOptions);
        var rewriter = new CodespacesUrlRewriter(codespacesOptions);

        return new DashboardLifecycleHook(
            configuration,
            Options.Create(new DashboardOptions { DashboardPath = "test.dll" }),
            NullLogger<DistributedApplication>.Instance,
            new TestDashboardEndpointProvider(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
            resourceNotificationService,
            resourceLoggerService,
            loggerFactory ?? NullLoggerFactory.Instance,
            new DcpNameGenerator(configuration, Options.Create(new DcpOptions())),
            new TestHostApplicationLifetime(),
            rewriter,
            codespacesOptions,
            devcontainersOptions,
            settingsWriter
            );
    }

    public static IEnumerable<object?[]> Data()
    {
        var timestamp = new DateTime(2001, 12, 29, 23, 59, 59, DateTimeKind.Utc);
        var message = new DashboardLogMessage
        {
            LogLevel = LogLevel.Error,
            Category = "TestCategory",
            Message = "Hello world",
            Timestamp = timestamp.ToString(KnownFormats.ConsoleLogsTimestampFormat, CultureInfo.InvariantCulture),
        };
        var messageJson = JsonSerializer.Serialize(message, DashboardLogMessageContext.Default.DashboardLogMessage);

        yield return new object?[]
        {
            DateTime.UtcNow,
            messageJson,
            "Hello world",
            "Aspire.Hosting.Dashboard.TestCategory",
            LogLevel.Error
        };
        yield return new object?[]
        {
            null,
            messageJson,
            "Hello world",
            "Aspire.Hosting.Dashboard.TestCategory",
            LogLevel.Error
        };

        message = new DashboardLogMessage
        {
            LogLevel = LogLevel.Critical,
            Category = "TestCategory.TestSubCategory",
            Message = "Error message",
            Exception = new InvalidOperationException("Error!").ToString(),
            Timestamp = timestamp.ToString(KnownFormats.ConsoleLogsTimestampFormat, CultureInfo.InvariantCulture),
        };
        messageJson = JsonSerializer.Serialize(message, DashboardLogMessageContext.Default.DashboardLogMessage);

        yield return new object?[]
        {
            null,
            messageJson,
            $"Error message{Environment.NewLine}System.InvalidOperationException: Error!",
            "Aspire.Hosting.Dashboard.TestCategory.TestSubCategory",
            LogLevel.Critical
        };
    }

    private sealed class TestDashboardEndpointProvider : IDashboardEndpointProvider
    {
        public Task<string> GetResourceServiceUriAsync(CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; }
        public CancellationToken ApplicationStopped { get; }
        public CancellationToken ApplicationStopping { get; }

        public void StopApplication()
        {
        }
    }
}
