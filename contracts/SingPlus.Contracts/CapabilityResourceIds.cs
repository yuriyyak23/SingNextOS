using System.Globalization;
using System.Text;

namespace SingPlus.Contracts;

public readonly record struct MmioRegionResourceId(
    string DeviceResourceId,
    string RegionResourceId,
    long ByteLength);

public static class CapabilityResourceIds
{
    private const string MmioPrefix = "mmio-region:v1:";

    public static string MemoryRegion(RegionId regionId) =>
        $"memory-region:{regionId.Value.ToString(CultureInfo.InvariantCulture)}";

    public static string MmioRegion(
        string deviceResourceId,
        string regionResourceId,
        long byteLength)
    {
        ValidateMmioPart(deviceResourceId, nameof(deviceResourceId));
        ValidateMmioPart(regionResourceId, nameof(regionResourceId));
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

        if (!IsValidMmioPart(deviceResourceId) ||
            !IsValidMmioPart(regionResourceId) ||
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

    private static void ValidateMmioPart(string value, string parameterName)
    {
        if (!IsValidMmioPart(value))
            throw new ArgumentException(
                "MMIO semantic resource identities must be non-empty and at most 128 characters.",
                parameterName);
    }

    private static bool IsValidMmioPart(string? value) =>
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
