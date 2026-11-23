using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Xunit;

namespace Guardian.Contracts.Tests;

public class ProtocolSerializationTests
{
    [Fact]
    public void RequestMessage_RoundTrips()
    {
        var request = new RequestMessage
        {
            Id = Guid.NewGuid(),
            Command = Commands.EnableProtection,
            SentAt = DateTimeOffset.Now,
            ClientProcessName = "Guardian.App.exe",
            Payload = ProtocolJson.ToElement(new EnableProtectionPayload
            {
                Mode = ProtectionMode.InputIsolation,
                Source = "test",
                DisplayPrivacyCheckAcknowledged = true,
            }),
        };

        var json = ProtocolJson.Serialize(request);
        var back = ProtocolJson.Deserialize<RequestMessage>(json);

        Assert.NotNull(back);
        Assert.Equal(request.Id, back.Id);
        Assert.Equal(request.Command, back.Command);
        var payload = ProtocolJson.FromElement<EnableProtectionPayload>(back.Payload);
        Assert.Equal(ProtectionMode.InputIsolation, payload!.Mode);
    }

    [Fact]
    public void ResponseMessage_RoundTrips_WithData()
    {
        var snapshot = new ProtectionSnapshot
        {
            State = ProtectionState.Active,
            ActiveMode = ProtectionMode.Privacy,
            DisplayPrivacyActive = true,
            AcOnline = true,
            MonitorCount = 2,
            ServiceVersion = ProductInfo.Version,
        };
        var response = ResponseMessage.Ok(Guid.NewGuid(), ProtocolJson.ToElement(snapshot));

        var json = ProtocolJson.Serialize(response);
        var back = ProtocolJson.Deserialize<ResponseMessage>(json);
        var data = ProtocolJson.FromElement<ProtectionSnapshot>(back!.Data);

        Assert.Equal(ProtectionState.Active, data!.State);
        Assert.Equal(2, data.MonitorCount);
    }

    [Fact]
    public void Enums_SerializeAsCamelCaseStrings()
    {
        var json = ProtocolJson.Serialize(new SessionInfo
        {
            Name = "x",
            RequiredMode = ProtectionMode.InputIsolation,
            WorkloadType = WorkloadType.AiAgent,
            Status = SessionStatus.Active,
        });

        Assert.Contains("\"requiredMode\":\"inputIsolation\"", json);
        Assert.Contains("\"workloadType\":\"aiAgent\"", json);

        var back = ProtocolJson.Deserialize<SessionInfo>(json);
        Assert.Equal(ProtectionMode.InputIsolation, back!.RequiredMode);
    }

    [Fact]
    public void GuardianSettings_RoundTrips()
    {
        var settings = new GuardianSettings
        {
            EmergencyHotkeyKey = "F8",
            UiLossAction = UiLossAction.KeepProtection,
            ResumePolicy = ResumePolicy.ResumeLast,
            ClientLostThresholdSeconds = 30,
            OnlyOnAcPower = true,
            MaxManualProtectionMinutes = 60,
            AllowIsolationDuringRemoteSession = true,
            LogLevel = GuardianLogLevel.Debug,
        };

        var back = ProtocolJson.Deserialize<GuardianSettings>(ProtocolJson.Serialize(settings));

        Assert.NotNull(back);
        Assert.Equal("F8", back.EmergencyHotkeyKey);
        Assert.Equal(UiLossAction.KeepProtection, back.UiLossAction);
        Assert.Equal(ResumePolicy.ResumeLast, back.ResumePolicy);
        Assert.Equal(30, back.ClientLostThresholdSeconds);
        Assert.True(back.OnlyOnAcPower);
        Assert.Equal(60, back.MaxManualProtectionMinutes);
        Assert.True(back.AllowIsolationDuringRemoteSession);
        Assert.Equal(GuardianLogLevel.Debug, back.LogLevel);
    }

    [Fact]
    public void CapabilityReport_RoundTrips_WithNullValidation()
    {
        var report = new CapabilityReport
        {
            GeneratedAt = DateTimeOffset.Now,
            InputDevices = { new InputDeviceCapability { DeviceType = InputDeviceType.Keyboard, Count = 1 } },
            RemoteInputDetection = RemoteInputDetectionCapability.Supported,
            DisplayPrivacyValidated = null,
            InputIsolationExperimentalAvailable = true,
            EnhancedIsolationAvailable = false,
        };

        var back = ProtocolJson.Deserialize<CapabilityReport>(ProtocolJson.Serialize(report));

        Assert.Null(back!.DisplayPrivacyValidated);
        Assert.Single(back.InputDevices);
    }
}
