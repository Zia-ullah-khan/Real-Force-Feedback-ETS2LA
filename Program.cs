using ETS2LA.Shared;
using ETS2LA.Logging;
using ETS2LA.Game.Telemetry;
using ETS2LA.Backend.Events;
using ETS2LA.Notifications;
using ETS2LA.Settings;
using ETS2LA.Controls.Windows;
using SharpDX.DirectInput;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ForceFeedback
{
    [Serializable]
    public class FfbSettings
    {
        public float Kp { get; set; } = 5.0f;
        public float Ki { get; set; } = 0.2f;
        public float Kd { get; set; } = 2.5f;
        public float MaxIntegral { get; set; } = 0.35f;

        public bool InvertSteering { get; set; } = true;
        public float CenterBoost { get; set; } = 0.0f;
        public float MaxPidForce { get; set; } = 0.45f;
        public float MaxTotalForce { get; set; } = 1.0f;
        public float Smoothing { get; set; } = 0.05f;
        public bool EnableEngineVibration { get; set; } = true;
        public float EngineVibrationScale { get; set; } = 1.0f;
        public bool EnableBumpEffects { get; set; } = true;
        public float BumpSensitivity { get; set; } = 0.1f;
        public float MaxBumpForce { get; set; } = 0.3f;
    }

    public class WheelDevice
    {
        public required Joystick Joystick;
        public required string DeviceName;
        public required Guid InstanceGuid;
        public EffectInfo? ConstantForceInfo;
        public Effect? ConstantForceEffect;
        public Effect? EngineEffect;
        public bool IsInitialized = false;
        public bool IsLowPower = true;

        private readonly object _lock = new object();
        private float _last = 0;
        private float _lastError = 0;
        private float _integralError = 0;
        private FfbSettings _settings;
        private DateTime _lastForceErrorLog = DateTime.MinValue;
        private bool _effectUpdateDisabled = false;
        private EffectParameters? _cachedEffectParams;
        private ConstantForce _cachedConstantForce = new ConstantForce();
        private EffectParameters? _cachedEngineParams;
        private PeriodicForce _cachedPeriodic = new PeriodicForce();

        public WheelDevice(FfbSettings settings)
        {
            _settings = settings;
        }

        public void ApplySettings(FfbSettings settings)
        {
            _settings = settings;
        }

        public void CenterToNeutral()
        {
            lock (_lock)
            {
                if (!IsInitialized || ConstantForceEffect == null || _cachedEffectParams == null)
                    return;

                try
                {
                    var state = Joystick.GetCurrentState();
                    float currentAngle = (32767.5f - state.X) / 32767.5f;
                    float force = -currentAngle * 0.15f;
                    force = Math.Clamp(force, -_settings.MaxPidForce, _settings.MaxPidForce);

                    _last = 0f;
                    _lastError = 0f;
                    _integralError = 0f;

                    _cachedConstantForce.Magnitude = (int)(Math.Clamp(force, -1f, 1f) * 10000f);
                    _cachedEffectParams.Parameters = _cachedConstantForce;
                    ConstantForceEffect.SetParameters(_cachedEffectParams, EffectParameterFlags.TypeSpecificParameters);
                }
                catch (Exception)
                {
                    
                }
            }
        }


        private EffectParameters CreateLegacyEffectParams()
        {
            var effectParams = new EffectParameters
            {
                Flags = EffectFlags.Cartesian | EffectFlags.ObjectOffsets,
                Duration = int.MaxValue,
                Gain = 10000,
                SamplePeriod = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                StartDelay = 0
            };
            effectParams.SetAxes(new int[] { 0 }, new int[] { 0 });
            return effectParams;
        }

        private EffectParameters CreateDriverSafeEffectParams()
        {
            var effectParams = new EffectParameters
            {
                Flags = EffectFlags.Cartesian,
                Duration = 10000,
                Gain = 10000,
                SamplePeriod = 0,
                TriggerButton = -1,
                TriggerRepeatInterval = 0,
                StartDelay = 0
            };
            effectParams.SetAxes(new int[] { 0 }, new int[] { 0 });
            return effectParams;
        }

        public void UpdateToAngle(float targetAngle)
        {
            lock (_lock)
            {
                if (!IsInitialized || ConstantForceEffect == null || _effectUpdateDisabled) return;

                try
                {
                    var state = Joystick.GetCurrentState();
                    float rawAngle = (state.X - 32767.5f) / 32767.5f;
                    float currentAngle = _settings.InvertSteering ? -rawAngle : rawAngle;
                    targetAngle = Math.Clamp(targetAngle, -1f, 1f);

                    float error = targetAngle - currentAngle;

                    _integralError += error;
                    _integralError = Math.Clamp(_integralError, -_settings.MaxIntegral, _settings.MaxIntegral);
                    if (Math.Sign(error) != Math.Sign(_lastError))
                    {
                        _integralError *= 0.5f;
                    }

                    float derivative = error - _lastError;
                    _lastError = error;

                    float force = error * _settings.Kp + _integralError * _settings.Ki + derivative * _settings.Kd;
                    float absError = Math.Abs(error);
                    if (_settings.CenterBoost > 0.0f && absError > 0.0f && absError < 0.18f)
                    {
                        float ramp = absError / 0.18f;
                        float assist = Math.Sign(error) * (ramp * _settings.CenterBoost);
                        force += assist;
                    }

                    force = Math.Clamp(force, -_settings.MaxPidForce, _settings.MaxPidForce);
                    force += _bumpForce;

                    force = Math.Clamp(force, -_settings.MaxTotalForce, _settings.MaxTotalForce);
                    force = _last * _settings.Smoothing + force * (1.0f - _settings.Smoothing);
                    _last = force;
                    int diForce = (int)(force * 10000);
                    if (_cachedEffectParams == null)
                    {
                        _cachedEffectParams = CreateDriverSafeEffectParams();
                    }
                    _cachedConstantForce.Magnitude = diForce;
                    _cachedEffectParams.Parameters = _cachedConstantForce;
                    ConstantForceEffect.SetParameters(_cachedEffectParams, EffectParameterFlags.TypeSpecificParameters);
                }
                catch (Exception ex)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastForceErrorLog).TotalSeconds > 10)
                    {
                        Logger.Warn($"Driver rejected force update for {DeviceName}: {ex.Message}");
                        _lastForceErrorLog = now;
                    }
                    _effectUpdateDisabled = true;
                }
            }
        }

        public bool InitializeEffect()
        {
            try
            {
                var effects = Joystick.GetEffects();
                foreach (var effectInfo in effects)
                {
                    if (effectInfo.Guid == EffectGuid.ConstantForce)
                    {
                        ConstantForceInfo = effectInfo;
                        break;
                    }
                }

                if (ConstantForceInfo == null)
                {
                    Logger.Warn($"No constant force effect found for {DeviceName}");
                    return false;
                }

                var profiles = new[]
                {
                    ("legacy", CreateLegacyEffectParams()),
                    ("safe", CreateDriverSafeEffectParams())
                };

                foreach (var (profileName, effectParams) in profiles)
                {
                    try
                    {
                        var constantForce = new ConstantForce { Magnitude = 0 };
                        effectParams.Parameters = constantForce;

                        var effect = new Effect(Joystick, EffectGuid.ConstantForce, effectParams);
                        effect.Start();

                        _cachedEffectParams = effectParams;
                        ConstantForceEffect = effect;
                        IsInitialized = true;
                        Logger.Success($"Initialized force feedback for {DeviceName} using {profileName} DirectInput profile");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Device {DeviceName} rejected {profileName} DirectInput profile: {ex.Message}");
                        ConstantForceEffect?.Dispose();
                        ConstantForceEffect = null;
                    }
                }

                IsInitialized = false;
                return false;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Device {DeviceName} rejected DirectInput constant force effect: {ex.Message}");
                IsInitialized = false;
                ConstantForceEffect?.Dispose();
                ConstantForceEffect = null;
                return false;
            }
        }

        public void InitializeEngineEffect()
        {
            try
            {
                var effects = Joystick.GetEffects();
                if (!effects.Any(x => x.Guid == EffectGuid.Sine))
                {
                    Logger.Warn($"Sine wave effect not supported by {DeviceName} - engine vibration disabled");
                    return;
                }

                var periodic = new PeriodicForce
                {
                    Magnitude = 0,
                    Period = 100000, 
                    Phase = 0
                };

                _cachedEngineParams = CreateDriverSafeEffectParams();
                _cachedEngineParams.Parameters = periodic;

                EngineEffect = new Effect(Joystick, EffectGuid.Sine, _cachedEngineParams);
                EngineEffect.Start();
                Logger.Info($"Initialized engine vibration for {DeviceName}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to initialize engine vibration: {ex.Message}");
            }
        }

        private Vector3 _lastAccel = Vector3.Zero;
        private float _bumpForce = 0;

        public void UpdateTelemetry(float rpm, float speed, Vector3 acceleration)
        {
            lock (_lock)
            {
                if (_settings.EnableBumpEffects)
                {
                    float currentAccelY = acceleration.Y;
                    float distY = Math.Abs(currentAccelY - _lastAccel.Y);
                    if (distY > 0.05f) 
                    {
                        float bumpMagnitude = Math.Clamp(distY * _settings.BumpSensitivity, 0, 0.2f);
                        if ((DateTime.Now.Ticks % 2) == 0) bumpMagnitude = -bumpMagnitude;
                        _bumpForce += bumpMagnitude;
                        _bumpForce = Math.Clamp(_bumpForce, -_settings.MaxBumpForce, _settings.MaxBumpForce);
                    }
                }
                
                _bumpForce *= 0.85f;
                _lastAccel = acceleration;
                if (!_settings.EnableEngineVibration || EngineEffect == null || _cachedEngineParams == null) return;
                try
                {
                    int magnitude = 0;
                    int period = 100000;

                    if (rpm > 100)
                    {
                        period = (int)Math.Clamp(60000000.0f / rpm, 1000, 200000);
                        float vibrationScale = 0.05f;
                        if (rpm < 700) 
                            vibrationScale += 0.15f * (1.0f - (rpm - 300f)/400f);
                        if (rpm > 1500)
                            vibrationScale += 0.1f * ((rpm - 1500f)/1000f);

                        magnitude = (int)(Math.Clamp(vibrationScale, 0, 0.4f) * 10000 * _settings.EngineVibrationScale);
                    }

                    _cachedPeriodic.Magnitude = magnitude;
                    _cachedPeriodic.Period = period;
                    _cachedEngineParams.Parameters = _cachedPeriodic;

                    EngineEffect.SetParameters(_cachedEngineParams, EffectParameterFlags.TypeSpecificParameters);
                }
                catch { }
            }
        }

        public void StopEffect()
        {
            lock (_lock)
            {
                try
                {
                    if (IsInitialized && ConstantForceEffect != null && _cachedEffectParams != null)
                    {
                        _cachedConstantForce.Magnitude = 0;
                        _cachedEffectParams.Parameters = _cachedConstantForce;
                        ConstantForceEffect.SetParameters(_cachedEffectParams, EffectParameterFlags.TypeSpecificParameters);
                    }
                    
                    IsInitialized = false;
                    ConstantForceEffect?.Stop();
                    ConstantForceEffect?.Dispose();
                    ConstantForceEffect = null;

                    EngineEffect?.Stop();
                    EngineEffect?.Dispose();
                    EngineEffect = null;
                }
                catch (Exception) { }
            }
        }

        public void Dispose()
        {
            StopEffect();
            try
            {
                if (Joystick != null)
                {
                    try 
                    { 
                        Joystick.Properties.AutoCenter = true;
                        Logger.Info($"Re-enabled auto-center for {DeviceName}");
                    } 
                    catch { }
                }

                Joystick?.Unacquire();
                Joystick?.Dispose();
                Logger.Info($"Disposed wheel: {DeviceName}");
            }
            catch (Exception) { }
        }
    }

    public class ForceFeedback : Plugin
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        public override float TickRate => 60f;
        public override PluginInformation Info => new PluginInformation
        {
            Id = "zkhan4509.realforcefeedback",
            Name = "Real Force Feedback",
            Description = "Recreates force feedback effects from ETS2/ATS As well as it fixes the wheel turning the opposite direction when using ETS2LA",
            Version = "1.0.0",
            SupportedETS2LA = "*",
            AuthorName = "Zkhan4509",
        };

        private List<WheelDevice> _wheels = new List<WheelDevice>();
        public float TargetAngle = 0;
        private float _lastRpm = 0;
        private float _lastSpeed = 0;
        private Vector3 _lastAcceleration = Vector3.Zero;
        private IntPtr _windowHandle = IntPtr.Zero;
        private int _scanRetryCount = 0;
        private const int MaxScanRetries = 300;
        private bool _isScanning = false;
        private readonly object _scanLock = new object();

        private SettingsHandler? _settingsHandler;
        private FfbSettings _settings = new FfbSettings();
        private const string SettingsFilename = "realffb_settings.json";

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        private IntPtr GetWindowHandle()
        {
            var currentProcessId = Process.GetCurrentProcess().Id;
            var consoleHandle = GetConsoleWindow();
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                if (hWnd == consoleHandle) return true;

                GetWindowThreadProcessId(hWnd, out int pid);
                if (pid == currentProcessId)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero)
            {
                Logger.Info($"Using app window handle: {found}");
                return found;
            }

            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero) return fg;
            var dt = GetDesktopWindow();
            if (dt != IntPtr.Zero) return dt;

            Logger.Warn("No valid window handle found for force feedback. Will retry on next scan.");
            return IntPtr.Zero;
        }

        private void ScanForWheels()
        {
            lock (_scanLock)
            {
                if (_isScanning) return;
                _isScanning = true;
            }

            try
            {
                var connected = SharpDXControlsBackend.Current._connectedJoysticks;
                Logger.Info($"Found {connected.Count} connected joysticks in ETS2LA controls backend.");

                foreach (var joystick in connected)
                {
                    if (_wheels.Any(w => w.InstanceGuid == joystick.Information.InstanceGuid))
                        continue;

                    try
                    {
                        if (_windowHandle == IntPtr.Zero)
                        {
                            _windowHandle = GetWindowHandle();
                            if (_windowHandle == IntPtr.Zero)
                            {
                                Logger.Warn("Cannot initialize force feedback: no valid window handle available.");
                                continue;
                            }
                            Logger.Info($"Using window handle: {_windowHandle}");
                        }

                        try { joystick.Unacquire(); } catch { }

                        try
                        {
                            joystick.SetCooperativeLevel(_windowHandle, CooperativeLevel.Background | CooperativeLevel.Exclusive);
                        }
                        catch (SharpDX.SharpDXException sdxEx)
                        {
                            Logger.Warn($"Exclusive access failed (0x{sdxEx.HResult:X8}), retrying in non-exclusive mode");
                            try
                            {
                                try { joystick.Unacquire(); } catch { }
                                joystick.SetCooperativeLevel(_windowHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
                            }
                            catch (SharpDX.SharpDXException fallbackEx)
                            {
                                Logger.Warn($"Non-exclusive access also failed (0x{fallbackEx.HResult:X8}), skipping device until retry");
                                _windowHandle = IntPtr.Zero;
                                continue;
                            }
                        }

                        joystick.Acquire();

                        try
                        {
                            joystick.Properties.AutoCenter = false;
                        }
                        catch (Exception) { }

                        var wheel = new WheelDevice(_settings)
                        {
                            Joystick = joystick,
                            DeviceName = joystick.Information.InstanceName,
                            InstanceGuid = joystick.Information.InstanceGuid
                        };

                        if (!wheel.InitializeEffect())
                        {
                            Logger.Warn($"Skipping unsupported wheel: {wheel.DeviceName}");
                            continue;
                        }

                        wheel.InitializeEngineEffect();
                        _wheels.Add(wheel);
                        wheel.CenterToNeutral();
                        Logger.Info($"Added wheel: {wheel.DeviceName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not add device {joystick.Information.InstanceName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error scanning for wheels: {ex.Message}");
            }
            finally
            {
                lock (_scanLock)
                {
                    _isScanning = false;
                }
            }
        }

        public override void Init()
        {
            base.Init();
            Logger.Info("ForceFeedback plugin Init() called");
            ScanForWheels();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            Logger.Info("ForceFeedback plugin OnEnable() called");

            _settingsHandler = new SettingsHandler();
            _settings = _settingsHandler.Load<FfbSettings>(SettingsFilename);
            _settingsHandler.RegisterListener<FfbSettings>(SettingsFilename, OnSettingsChanged);
            Events.Current.Subscribe<float>("ForceFeedback.Output", OnControlEvent);
            Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);

            ScanForWheels();
        }

        public override void OnDisable()
        {
            base.OnDisable();
            Events.Current.Unsubscribe<float>("ForceFeedback.Output", OnControlEvent);
            Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
            _settingsHandler?.UnregisterListener<FfbSettings>(SettingsFilename, OnSettingsChanged);
            _settingsHandler?.Dispose();
            _settingsHandler = null;
            NotificationHandler.Current.CloseNotification("ForceFeedback.Debug");
            DisposeResources();
        }

        public void OnControlEvent(float steering)
        {
            TargetAngle = steering;
        }

        public void OnTelemetry(GameTelemetryData data)
        {
            _lastRpm = data.truckFloat.engineRpm;
            _lastSpeed = data.truckFloat.speed;
            _lastAcceleration = data.truckVector.acceleration;
            if (data.truckFloat != null)
            {
                TargetAngle = data.truckFloat.gameSteer;
            }
        }

        private void OnSettingsChanged(FfbSettings data)
        {
            _settings = data;
            foreach (var wheel in _wheels)
            {
                wheel.ApplySettings(data);
            }
            Logger.Info("RealFFB settings updated");
        }

        public override void Tick()
        {
            try
            {
                if (_wheels.Count == 0 && _scanRetryCount < MaxScanRetries && !_isScanning)
                {
                    _scanRetryCount++;
                    _windowHandle = IntPtr.Zero;
                    Logger.Info($"Retrying wheel scan (attempt {_scanRetryCount}/{MaxScanRetries})...");
                    ScanForWheels();
                }
                var wheels = _wheels.ToList();
                foreach (var wheel in wheels)
                {
                    wheel.UpdateToAngle(TargetAngle);
                    wheel.UpdateTelemetry(_lastRpm, _lastSpeed, _lastAcceleration);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in ForceFeedback Tick: {ex.Message}");
            }
        }

        private void DisposeResources()
        {
            Logger.Info("Disposing force feedback resources - centering and unlocking wheels...");
            foreach (var wheel in _wheels)
            {
                wheel.Dispose();
            }
            _wheels.Clear();
            _windowHandle = IntPtr.Zero;
            _scanRetryCount = 0;
            Logger.Info("Force feedback resources disposed.");
        }
    }
}