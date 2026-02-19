namespace Dispatch.Web.Services;

public static class LocalFeedUri
{
    public static string Build(string backend, string input)
    {
        if (string.IsNullOrWhiteSpace(backend))
        {
            throw new ArgumentException("Backend is required.", nameof(backend));
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Input is required.", nameof(input));
        }

        var normalizedBackend = backend.Trim().ToLowerInvariant();
        return $"local://{normalizedBackend}/{Uri.EscapeDataString(input)}";
    }

    public static bool TryParse(string streamUrl, out string backend, out string input)
    {
        backend = string.Empty;
        input = string.Empty;

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            return false;
        }

        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!uri.Scheme.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        backend = uri.Host.Trim().ToLowerInvariant();
        input = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        return !string.IsNullOrWhiteSpace(backend) && !string.IsNullOrWhiteSpace(input);
    }
}
