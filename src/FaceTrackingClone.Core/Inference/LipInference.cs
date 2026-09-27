using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using FaceTrackingClone.Vive;

namespace FaceTrackingClone.Inference;

internal sealed class LipInferenceOptions
{
    /// <summary>Path to the ONNX model. Defaults to models\babble.onnx beside the binary.</summary>
    public string? ModelPath { get; init; }

    /// <summary>
    /// Intra-op threads. Deliberately minimal: this runs alongside a VR title that is already
    /// dropping frames, and an inference engine that grabs cores makes the very stalls this
    /// project exists to survive more likely. At 224x224 the parallel split buys little.
    /// </summary>
    public int Threads { get; init; } = 1;

    /// <summary>
    /// One Euro filter smoothing. Reduces jitter without the lag of a plain moving average.
    /// </summary>
    public bool Smooth { get; init; } = true;

    public double MinCutoff { get; init; } = 1.0;
    public double Beta { get; init; } = 0.05;
}

/// <summary>
/// Runs the lip blendshape model over frames from <see cref="ViveLipTracker"/>.
///
/// Input is a single-channel 224x224 tensor: the left half of the tracker's stereo frame
/// (200x400) stretched to square, normalised to 0..1.
/// </summary>
internal sealed class LipInference : IDisposable
{
    public const int InputSize = 224;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;

    private readonly byte[] _scaled = new byte[InputSize * InputSize];
    private readonly DenseTensor<float> _input;
    private readonly NamedOnnxValue[] _inputs;
    private readonly float[] _raw = new float[BabbleShapes.Count];
    private readonly OneEuroFilter[] _filters = new OneEuroFilter[BabbleShapes.Count];
    private readonly LipInferenceOptions _options;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastInferenceMs;

    public LipInference(LipInferenceOptions? options = null)
    {
        _options = options ?? new LipInferenceOptions();

        string path = _options.ModelPath
                      ?? Path.Combine(AppContext.BaseDirectory, "models", "babble.onnx");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Inference model not found at '{path}'. Run tools/fetch-model.ps1.", path);

        var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = _options.Threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        // Stops ORT worker threads from busy-waiting between frames, which otherwise burns a
        // core continuously at 60Hz for no throughput gain.
        sessionOptions.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

        _session = new InferenceSession(path, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();
        _outputName = _session.OutputMetadata.Keys.First();

        _input = new DenseTensor<float>(new[] { 1, 1, InputSize, InputSize });
        _inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, _input) };

        for (int i = 0; i < _filters.Length; i++)
            _filters[i] = new OneEuroFilter(_options.MinCutoff, _options.Beta);
    }

    /// <summary>Milliseconds taken by the most recent inference.</summary>
    public double LastInferenceMs => _lastInferenceMs;

    /// <summary>Blendshape weights from the most recent <see cref="Run"/>, indexed by <see cref="BabbleShape"/>.</summary>
    public IReadOnlyList<float> Weights => _raw;

    public float this[BabbleShape shape] => _raw[(int)shape];

    /// <summary>
    /// Runs the model over one frame. Returns the 45 weights, clamped to 0..1.
    /// </summary>
    public IReadOnlyList<float> Run(LipFrame frame)
    {
        frame.CopyLeftViewScaled(_scaled, InputSize);

        // NCHW, single channel, 0..1.
        Span<float> dest = _input.Buffer.Span;
        for (int i = 0; i < _scaled.Length; i++)
            dest[i] = _scaled[i] * (1f / 255f);

        double started = _clock.Elapsed.TotalMilliseconds;
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
            _session.Run(_inputs);
        _lastInferenceMs = _clock.Elapsed.TotalMilliseconds - started;

        ReadOnlySpan<float> output = results.First(r => r.Name == _outputName)
            .AsTensor<float>().ToArray();

        double now = _clock.Elapsed.TotalSeconds;
        for (int i = 0; i < BabbleShapes.Count && i < output.Length; i++)
        {
            float value = output[i];
            if (_options.Smooth) value = (float)_filters[i].Filter(value, now);
            _raw[i] = Math.Clamp(value, 0f, 1f);
        }

        return _raw;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// One Euro filter: an adaptive low-pass that smooths hard when the signal is still and backs
/// off when it moves fast. Chosen over a fixed average because facial tracking needs both a
/// steady neutral pose and a responsive jaw.
/// </summary>
internal sealed class OneEuroFilter
{
    private readonly double _minCutoff;
    private readonly double _beta;
    private readonly double _dCutoff;

    private double _lastValue;
    private double _lastDerivative;
    private double _lastTime = -1;
    private bool _primed;

    public OneEuroFilter(double minCutoff, double beta, double dCutoff = 1.0)
    {
        _minCutoff = minCutoff;
        _beta = beta;
        _dCutoff = dCutoff;
    }

    public double Filter(double value, double timestampSeconds)
    {
        if (!_primed)
        {
            _primed = true;
            _lastValue = value;
            _lastTime = timestampSeconds;
            return value;
        }

        double dt = timestampSeconds - _lastTime;
        if (dt <= 0) dt = 1.0 / 60.0;
        _lastTime = timestampSeconds;

        double rate = 1.0 / dt;

        double derivative = (value - _lastValue) * rate;
        double dAlpha = Alpha(rate, _dCutoff);
        _lastDerivative = dAlpha * derivative + (1 - dAlpha) * _lastDerivative;

        double cutoff = _minCutoff + _beta * Math.Abs(_lastDerivative);
        double alpha = Alpha(rate, cutoff);
        _lastValue = alpha * value + (1 - alpha) * _lastValue;

        return _lastValue;
    }

    private static double Alpha(double rate, double cutoff)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau * rate);
    }
}
