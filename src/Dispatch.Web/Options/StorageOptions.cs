namespace Dispatch.Web.Options;

public class StorageOptions
{
    public string RootPath { get; set; } = "data";

    public string DatabasePath { get; set; } = "data/dispatch.db";

    public string RecordingsPath { get; set; } = "data/recordings";

    /// <summary>
    /// Where ASP.NET Core Data Protection persists its key ring. This must live on
    /// durable storage: the keys encrypt the auth cookie, so a fresh key ring on
    /// every restart signs out every user. Defaults under the storage root so it
    /// follows the mounted disk automatically.
    /// </summary>
    public string DataProtectionKeysPath { get; set; } = "data/keys";
}
