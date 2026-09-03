using System.Globalization;
using System.Text;

namespace SingPlus.Contracts;

public readonly record struct MmioRegionResourceId(
    string DeviceResourceId,
    string RegionResourceId,
    long ByteLength);

public enum IrqTriggerMode : byte
{
    Edge = 0,
    Level = 1,
}

public readonly record struct IrqResourceId(
    string DeviceResourceId,
    string SourceResourceId,
    IrqTriggerMode Trigger);

public static class CapabilityResourceIds
{
    private const string MmioPrefix = "mmio-region:v1:";
    private const string IrqPrefix = "irq:v1:";

    public static string MemoryRegion(RegionId regionId) =>
        $"memory-region:{regionId.Value.ToString(CultureInfo.InvariantCulture)}";

    public static string MmioRegion(
        string deviceResourceId,
        string regionResourceId,
        long byteLength)
    {
        ValidateSemanticPart(deviceResourceId, nameof(deviceResourceId), "MMIO");
        ValidateSemanticPart(regionResourceId, nameof(regionResourceId), "MMIO");
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));

        return $"{MmioPrefix}{Encode(deviceResourceId)}:{Encode(regionResourceId)}:{byteLength.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseMmioRegion(
        string? resourceId,
        out MmioRegionResourceId parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(resourceId) ||
            !resourceId.StartsWith(MmioPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = resourceId[MmioPrefix.Length..];
        var parts = payload.Split(':');
        if (parts.Length != 3 ||
            !TryDecode(parts[0], out var deviceResourceId) ||
            !TryDecode(parts[1], out var regionResourceId) ||
            !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var byteLength) ||
            byteLength <= 0)
        {
            return false;
        }

        if (!IsValidSemanticPart(deviceResourceId) ||
            !IsValidSemanticPart(regionResourceId) ||
            !string.Equals(parts[0], Encode(deviceResourceId), StringComparison.Ordinal) ||
            !string.Equals(parts[1], Encode(regionResourceId), StringComparison.Ordinal))
        {
            return false;
        }

        parsed = new MmioRegionResourceId(
            deviceResourceId,
            regionResourceId,
            byteLength);
        return true;
    }

    public static string Irq(
        string deviceResourceId,
        string sourceResourceId,
        IrqTriggerMode trigger)
    {
        ValidateSemanticPart(deviceResourceId, nameof(deviceResourceId), "IRQ");
        ValidateSemanticPart(sourceResourceId, nameof(sourceResourceId), "IRQ");
        if (!Enum.IsDefined(trigger))
            throw new ArgumentOutOfRangeException(nameof(trigger));

        return $"{IrqPrefix}{Encode(deviceResourceId)}:{Encode(sourceResourceId)}:{((byte)trigger).ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseIrq(
        string? resourceId,
        out IrqResourceId parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(resourceId) ||
            !resourceId.StartsWith(IrqPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var payload = resourceId[IrqPrefix.Length..];
        var parts = payload.Split(':');
        if (parts.Length != 3 ||
            !TryDecode(parts[0], out var deviceResourceId) ||
            !TryDecode(parts[1], out var sourceResourceId) ||
            !byte.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var triggerValue))
        {
            return false;
        }

        var trigger = (IrqTriggerMode)triggerValue;
        if (!Enum.IsDefined(trigger) ||
            !IsValidSemanticPart(deviceResourceId) ||
            !IsValidSemanticPart(sourceResourceId) ||
            !string.Equals(parts[0], Encode(deviceResourceId), StringComparison.Ordinal) ||
            !string.Equals(parts[1], Encode(sourceResourceId), StringComparison.Ordinal) ||
            !string.Equals(parts[2], triggerValue.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return false;
        }

        parsed = new IrqResourceId(deviceResourceId, sourceResourceId, trigger);
        return true;
    }

    private static void ValidateSemanticPart(
        string value,
        string parameterName,
        string authorityName)
    {
        if (!IsValidSemanticPart(value))
            throw new ArgumentException(
                $"{authorityName} semantic resource identities must be non-empty and at most 128 characters.",
                parameterName);
    }

    private static bool IsValidSemanticPart(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128;

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static bool TryDecode(string encoded, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(encoded) || encoded.Length % 4 == 1)
            return false;

        var base64 = encoded.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };

        try
        {
            value = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
