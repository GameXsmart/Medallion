using NAudio.CoreAudioApi;
using Medallion.Core.Diagnostics;

namespace Medallion.Core.Audio;

public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault, bool IsCapture)
{
    public override string ToString() => IsDefault ? Name + " (Default)" : Name;
}

/// <summary>Enumerates WASAPI endpoints for the settings UI.</summary>
public static class AudioDevices
{
    public static IReadOnlyList<AudioDeviceInfo> Render() => Enumerate(DataFlow.Render, isCapture: false);

    public static IReadOnlyList<AudioDeviceInfo> Capture() => Enumerate(DataFlow.Capture, isCapture: true);

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DataFlow flow, bool isCapture)
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            try
            {
                using var def = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                defaultId = def.ID;
            }
            catch (Exception ex)
            {
                Log.Debug($"No default {flow} endpoint: {ex.Message}");
            }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try
                {
                    list.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, device.ID == defaultId, isCapture));
                }
                finally
                {
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Audio endpoint enumeration failed ({flow})", ex);
        }

        return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
    }

    /// <summary>Resolves a stored device id, falling back to the system default.</summary>
    public static MMDevice? Resolve(string? deviceId, DataFlow flow)
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                try
                {
                    var device = enumerator.GetDevice(deviceId);
                    if (device is not null && device.State == DeviceState.Active) return device;
                    device?.Dispose();
                    Log.Warn($"Audio device {deviceId} is unavailable; using default");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Audio device {deviceId} could not be opened ({ex.Message}); using default");
                }
            }

            return enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        }
        catch (Exception ex)
        {
            Log.Error($"No usable {flow} audio device", ex);
            return null;
        }
    }
}
