using MultiFunPlayer.Common;
using MultiFunPlayer.Property;
using MultiFunPlayer.Shortcut;
using Newtonsoft.Json;
using PropertyChanged;
using Stylet;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Windows;

namespace MultiFunPlayer.MotionProvider.ViewModels;

[DisplayName("Custom Curve")]
[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
internal sealed class CustomCurveMotionProvider : AbstractMotionProvider
{
    private readonly Lock _stateLock = new();

    private int _index;
    private KeyframeCollection _keyframes;
    private bool _playing;
    private bool _pendingRefreshFlag;

    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public ObservableConcurrentCollection<Point> Points { get; set; }

    [JsonProperty] public InterpolationType InterpolationType { get; set; }
    [JsonProperty] public double Duration { get; set; } = 10;
    [JsonProperty] public bool IsLooping { get; set; } = true;
    [JsonProperty] public bool SyncOnEnd { get; set; } = true;

    [DependsOn(nameof(Duration))]
    public Rect Viewport => new(0, 0, Duration, 1);

    public double Time { get; private set; }

    public CustomCurveMotionProvider(DeviceAxis target, IEventAggregator eventAggregator)
        : base(target, eventAggregator)
    {
        Points = [new()];
        _pendingRefreshFlag = true;

        ResetState(true);
    }

    protected override bool ShouldSyncOnPropertyChanged(string propertyName)
        => propertyName != nameof(Time);

    public void OnPointsChanged(ObservableConcurrentCollection<Point> oldValue, ObservableConcurrentCollection<Point> newValue)
    {
        if (oldValue != null)
            oldValue.CollectionChanged -= OnPointsCollectionChanged;
        if (newValue != null)
            newValue.CollectionChanged += OnPointsCollectionChanged;

        Interlocked.Exchange(ref _pendingRefreshFlag, true);
    }

    public void OnViewportChanged()
        => Interlocked.Exchange(ref _pendingRefreshFlag, true);

    public void OnIsLoopingChanged()
    {
        lock (_stateLock)
            if (IsLooping)
                _playing = true;
    }

    [SuppressPropertyChangedWarnings]
    private void OnPointsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        => Interlocked.Exchange(ref _pendingRefreshFlag, true);

    public override void Update(double deltaTime)
    {
        if (Points == null || Points.Count == 0)
            return;

        var needsRefresh = Interlocked.CompareExchange(ref _pendingRefreshFlag, false, true);
        if (needsRefresh)
        {
            if (IsLooping && Points.Count != 1)
            {
                var minimumTilePointCount = InterpolationType switch
                {
                    InterpolationType.Makima => 3,
                    InterpolationType.Pchip => 2,
                    _ => 1
                };

                var tileCount = (int)Math.Ceiling(Math.Max(0, (minimumTilePointCount - Points.Count) / (double)Points.Count)) + 1;
                var takeCount = Math.Min(minimumTilePointCount, Points.Count);
                var newKeyframes = new KeyframeCollection(Points.Count + 2 * takeCount * tileCount);
                for (var i = tileCount; i >= 1; i--)
                    foreach (var point in Points.TakeLast(takeCount))
                        newKeyframes.Add(point.X - i * Viewport.Width, point.Y);

                foreach (var point in Points)
                    newKeyframes.Add(point.X, point.Y);

                for (var i = 1; i <= tileCount; i++)
                    foreach (var point in Points.Take(takeCount))
                        newKeyframes.Add(point.X + i * Viewport.Width, point.Y);

                _keyframes = newKeyframes;
            }
            else
            {
                var newKeyframes = new KeyframeCollection(Points.Count + 2)
                {
                    { Viewport.Left, Points[0].Y }
                };

                foreach (var point in Points)
                    newKeyframes.Add(point.X, point.Y);
                newKeyframes.Add(Viewport.Right, Points[^1].Y);

                _keyframes = newKeyframes;
            }
        }

        if (_keyframes == null)
            return;

        lock (_stateLock)
        {
            if (!_playing)
                return;

            if (needsRefresh)
                _index = _keyframes.SearchForIndexBefore(Time);

            if (Time >= Duration || _index + 1 >= _keyframes.Count)
            {
                ResetState(IsLooping);

                if (!IsLooping)
                {
                    if (SyncOnEnd)
                        RequestSync();

                    Value = double.NaN;
                    return;
                }
            }

            _index = _keyframes.AdvanceIndex(_index, Time);
            if (!_keyframes.ValidateIndex(_index) || !_keyframes.ValidateIndex(_index + 1))
                return;

            var newValue = MathUtils.Clamp01(_keyframes.Interpolate(_index, Time, InterpolationType));
            Value = MathUtils.Map(newValue, 0, 1, Minimum, Maximum);
            Time += Speed * deltaTime;
        }
    }

    public void Reset() => ResetState(true);
    private void ResetState(bool playing)
    {
        lock (_stateLock)
        {
            Time = 0;
            _index = -1;
            _playing = playing;
        }
    }

    public static void RegisterActions(IShortcutManager s, Func<DeviceAxis, CustomCurveMotionProvider> getInstance)
    {
        void UpdateProperty(DeviceAxis axis, Action<CustomCurveMotionProvider> callback)
        {
            var motionProvider = getInstance(axis);
            if (motionProvider != null)
                callback(motionProvider);
        }

        AbstractMotionProvider.RegisterActions(s, getInstance);
        var name = typeof(CustomCurveMotionProvider).GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName;

        #region CustomCurveMotionProvider::InterpolationType
        s.RegisterAction<DeviceAxis, InterpolationType>($"MotionProvider::{name}::InterpolationType::Set",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            s => s.WithLabel("插值类型").WithItemsSource(Enum.GetValues<InterpolationType>()),
            (axis, interpolationType) => UpdateProperty(axis, p => p.InterpolationType = interpolationType));
        #endregion

        #region CustomCurveMotionProvider::Duration
        s.RegisterAction<DeviceAxis, double>($"MotionProvider::{name}::Duration::Set",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            s => s.WithLabel("时长").AsNumericUpDown(1, 60, 1, "{0:F2}s"),
            (axis, duration) => UpdateProperty(axis, p => p.Duration = duration));
        #endregion

        #region CustomCurveMotionProvider::IsLooping
        s.RegisterAction<DeviceAxis, bool>($"MotionProvider::{name}::IsLooping::Set",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            s => s.WithLabel("启用循环"),
            (axis, enabled) => UpdateProperty(axis, p => p.IsLooping = enabled));

        s.RegisterAction<DeviceAxis>($"MotionProvider::{name}::IsLooping::Toggle",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            axis => UpdateProperty(axis, p => p.IsLooping = !p.IsLooping));
        #endregion

        #region CustomCurveMotionProvider::Reset
        s.RegisterAction<DeviceAxis, bool>($"MotionProvider::{name}::Reset",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            s => s.WithLabel("请求同步").WithDefaultValue(true),
            (axis, sync) => UpdateProperty(axis, p => {
                if (sync)
                    p.RequestSync();
                p.ResetState(true);
            }));
        #endregion

        #region CustomCurveMotionProvider::Points
        s.RegisterAction<DeviceAxis, PointsActionSettingsViewModel>($"MotionProvider::{name}::Points::Set",
            s => s.WithLabel("目标轴").WithItemsSource(DeviceAxis.All),
            s => s.WithDefaultValue(() => new PointsActionSettingsViewModel())
                  .WithTemplateName("CustomCurveMotionProviderPointsTemplate")
                  .WithCustomToString(vm => $"Points({vm.Points.Count})"),
            (axis, vm) => UpdateProperty(axis, p =>
            {
                p.Duration = vm.Duration;
                p.InterpolationType = vm.InterpolationType;
                p.Points.SetFrom(vm.Points);
            }));
        #endregion
    }

    public static void RegisterProperties(IPropertyManager p, Func<DeviceAxis, CustomCurveMotionProvider> getInstance)
    {
        TOut GetProperty<TOut>(DeviceAxis axis, Func<CustomCurveMotionProvider, TOut> callback)
        {
            var motionProvider = getInstance(axis);
            if (motionProvider != null)
                callback(motionProvider);

            return default;
        }

        AbstractMotionProvider.RegisterProperties(p, getInstance);
        var name = typeof(CustomCurveMotionProvider).GetCustomAttribute<DisplayNameAttribute>(inherit: false).DisplayName;

        p.RegisterProperty<DeviceAxis, InterpolationType>($"MotionProvider::{name}::InterpolationType", axis => GetProperty(axis, p => p.InterpolationType));
        p.RegisterProperty<DeviceAxis, double>($"MotionProvider::{name}::Duration", axis => GetProperty(axis, p => p.Duration));
        p.RegisterProperty<DeviceAxis, bool>($"MotionProvider::{name}::IsLooping", axis => GetProperty(axis, p => p.IsLooping));
        p.RegisterProperty<DeviceAxis, IReadOnlyCollection<Point>>($"MotionProvider::{name}::Points", axis => GetProperty(axis, p => p.Points.AsReadOnly()));
    }

    internal sealed class PointsActionSettingsViewModel : INotifyPropertyChanged
    {
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)] public ObservableConcurrentCollection<Point> Points { get; set; }
        public double Duration { get; set; }
        public InterpolationType InterpolationType { get; set; }

        public PointsActionSettingsViewModel() : this([new(0.5, 0.5)], 1, InterpolationType.Linear) { }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0290:Use primary constructor", Justification = "OnPointsChanged not called with primary constructor")]
        public PointsActionSettingsViewModel(ObservableConcurrentCollection<Point> points, double duration, InterpolationType interpolationType)
        {
            Points = points;
            Duration = duration;
            InterpolationType = interpolationType;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051:Remove unused private members")]
        private void OnPointsChanged(ObservableConcurrentCollection<Point> oldPoints, ObservableConcurrentCollection<Point> newPoints)
        {
            if (oldPoints != null) oldPoints.CollectionChanged -= OnPointsCollectionChanged;
            if (newPoints != null) newPoints.CollectionChanged += OnPointsCollectionChanged;
        }

        [SuppressPropertyChangedWarnings]
        private void OnPointsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Points)));

        [JsonIgnore]
        [DependsOn(nameof(Duration))]
        public Rect Viewport => new(0, 0, Duration, 1);

        public event PropertyChangedEventHandler PropertyChanged;
    }
}