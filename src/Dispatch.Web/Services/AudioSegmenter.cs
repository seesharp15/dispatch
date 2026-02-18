using Dispatch.Web.Options;

namespace Dispatch.Web.Services;

public enum SegmentEvent
{
    None,
    Started,
    Stopped
}

public class AudioSegmenter
{
    private readonly SegmentationOptions _options;
    private double _noiseFloorDb;
    private double _silenceSeconds;
    private double _recordingSeconds;

    public AudioSegmenter(SegmentationOptions options)
    {
        _options = options;
        _noiseFloorDb = _options.InitialNoiseFloorDb;
    }

    public bool IsRecording { get; private set; }

    public double RecordingSeconds => _recordingSeconds;

    public double NoiseFloorDb => _noiseFloorDb;

    public SegmentEvent ProcessFrame(double dbLevel, double frameSeconds)
    {
        if (!IsRecording)
        {
            UpdateNoiseFloor(dbLevel);
            if (dbLevel > _noiseFloorDb + _options.ActivationDeltaDb)
            {
                IsRecording = true;
                _recordingSeconds = 0;
                _silenceSeconds = 0;
                return SegmentEvent.Started;
            }

            return SegmentEvent.None;
        }

        _recordingSeconds += frameSeconds;

        if (dbLevel < _noiseFloorDb + _options.SilenceDeltaDb)
        {
            _silenceSeconds += frameSeconds;
        }
        else
        {
            _silenceSeconds = 0;
        }

        if (_silenceSeconds >= _options.HangoverSeconds && _recordingSeconds >= _options.MinimumRecordingSeconds)
        {
            IsRecording = false;
            _silenceSeconds = 0;
            return SegmentEvent.Stopped;
        }

        return SegmentEvent.None;
    }

    private void UpdateNoiseFloor(double dbLevel)
    {
        if (dbLevel < _noiseFloorDb + _options.ActivationDeltaDb)
        {
            _noiseFloorDb = (_options.NoiseFloorEmaAlpha * dbLevel) + ((1 - _options.NoiseFloorEmaAlpha) * _noiseFloorDb);
        }
    }
}
