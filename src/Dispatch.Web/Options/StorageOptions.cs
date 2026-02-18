namespace Dispatch.Web.Options;

public class StorageOptions
{
    public string RootPath { get; set; } = "data";

    public string DatabasePath { get; set; } = "data/dispatch.db";

    public string RecordingsPath { get; set; } = "data/recordings";
}
