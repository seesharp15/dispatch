namespace Dispatch.Web.Options;

public class SegmentationOptions
{
    public double ActivationDeltaDb { get; set; } = 10;

    public double SilenceDeltaDb { get; set; } = 3;

    public double InitialNoiseFloorDb { get; set; } = -55;

    public double NoiseFloorEmaAlpha { get; set; } = 0.08;

    public double HangoverSeconds { get; set; } = 2.0;

    public double MinimumRecordingSeconds { get; set; } = 1.0;

    public double PreRollSeconds { get; set; } = 1.0;
}
