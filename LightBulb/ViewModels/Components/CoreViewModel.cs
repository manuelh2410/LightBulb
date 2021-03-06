﻿using System;
using System.Linq;
using LightBulb.Domain;
using LightBulb.Internal.Extensions;
using LightBulb.Models;
using LightBulb.Services;
using LightBulb.WindowsApi.Timers;
using Stylet;

namespace LightBulb.ViewModels.Components
{
    public class CoreViewModel : PropertyChangedBase, IDisposable
    {
        private readonly SettingsService _settingsService;
        private readonly GammaService _gammaService;
        private readonly HotKeyService _hotKeyService;
        private readonly ExternalApplicationService _externalApplicationService;

        private readonly ITimer _updateInstantTimer;
        private readonly ITimer _updateConfigurationTimer;
        private readonly ITimer _updateIsPausedTimer;

        private IDisposable? _enableAfterDelayRegistration;

        public bool IsEnabled { get; set; } = true;

        public bool IsPaused { get; private set; }

        public bool IsCyclePreviewEnabled { get; set; }

        public bool IsActive => IsEnabled && !IsPaused || IsCyclePreviewEnabled;

        public DateTimeOffset Instant { get; private set; } = DateTimeOffset.Now;

        public SolarTimes SolarTimes =>
            !_settingsService.IsManualSunriseSunsetEnabled && _settingsService.Location != null
                ? SolarTimes.Calculate(_settingsService.Location.Value, Instant)
                : new SolarTimes(_settingsService.ManualSunrise, _settingsService.ManualSunset);

        public TimeOfDay SunriseStart => Cycle.GetSunriseStart(
            SolarTimes.Sunrise,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

        public TimeOfDay SunriseEnd => Cycle.GetSunriseEnd(
            SolarTimes.Sunrise,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

        public TimeOfDay SunsetStart => Cycle.GetSunsetStart(
            SolarTimes.Sunset,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

        public TimeOfDay SunsetEnd => Cycle.GetSunsetEnd(
            SolarTimes.Sunset,
            _settingsService.ConfigurationTransitionDuration,
            _settingsService.ConfigurationTransitionOffset
        );

        public double TemperatureOffset { get; set; }

        public double BrightnessOffset { get; set; }

        public ColorConfiguration TargetConfiguration => IsActive
            ? Cycle
                .InterpolateConfiguration(
                    SolarTimes,
                    _settingsService.DayConfiguration,
                    _settingsService.NightConfiguration,
                    _settingsService.ConfigurationTransitionDuration,
                    _settingsService.ConfigurationTransitionOffset,
                    Instant)
                .WithOffset(
                    TemperatureOffset,
                    BrightnessOffset)
            : _settingsService.IsDefaultToDayConfigurationEnabled
                ? _settingsService.DayConfiguration
                : ColorConfiguration.Default;

        public ColorConfiguration CurrentConfiguration { get; set; } = ColorConfiguration.Default;

        public ColorConfiguration AdjustedDayConfiguration => _settingsService.DayConfiguration.WithOffset(
            TemperatureOffset,
            BrightnessOffset
        );

        public ColorConfiguration AdjustedNightConfiguration => _settingsService.NightConfiguration.WithOffset(
            TemperatureOffset,
            BrightnessOffset
        );

        public CycleState CycleState => this switch
        {
            _ when CurrentConfiguration != TargetConfiguration => CycleState.Transition,
            _ when !IsEnabled => CycleState.Disabled,
            _ when IsPaused => CycleState.Paused,
            _ when CurrentConfiguration == AdjustedDayConfiguration => CycleState.Day,
            _ when CurrentConfiguration == AdjustedNightConfiguration => CycleState.Night,
            _ => CycleState.Transition
        };

        public CoreViewModel(
            SettingsService settingsService,
            GammaService gammaService,
            HotKeyService hotKeyService,
            ExternalApplicationService externalApplicationService)
        {
            _settingsService = settingsService;
            _gammaService = gammaService;
            _hotKeyService = hotKeyService;
            _externalApplicationService = externalApplicationService;

            _updateConfigurationTimer = Timer.Create(TimeSpan.FromMilliseconds(50), UpdateConfiguration);
            _updateInstantTimer = Timer.Create(TimeSpan.FromMilliseconds(50), UpdateInstant);
            _updateIsPausedTimer = Timer.Create(TimeSpan.FromSeconds(1), UpdateIsPaused);

            // Cancel 'disable temporarily' when switching to enabled
            this.Bind(o => o.IsEnabled, (sender, args) =>
            {
                if (IsEnabled)
                    _enableAfterDelayRegistration?.Dispose();
            });

            // Handle settings changes
            _settingsService.SettingsSaved += (sender, args) =>
            {
                Refresh();
                RegisterHotKeys();
            };
        }

        public void OnViewFullyLoaded()
        {
            _updateInstantTimer.Start();
            _updateConfigurationTimer.Start();
            _updateIsPausedTimer.Start();

            RegisterHotKeys();
        }

        private void RegisterHotKeys()
        {
            _hotKeyService.UnregisterAllHotKeys();

            if (_settingsService.ToggleHotKey != HotKey.None)
            {
                _hotKeyService.RegisterHotKey(_settingsService.ToggleHotKey, Toggle);
            }

            if (_settingsService.IncreaseTemperatureOffsetHotKey != HotKey.None)
            {
                _hotKeyService.RegisterHotKey(_settingsService.IncreaseTemperatureOffsetHotKey, () =>
                {
                    const double delta = +100;

                    // Avoid changing offset when it's already at its limit
                    if (TargetConfiguration.WithOffset(delta, 0) != TargetConfiguration)
                        TemperatureOffset += delta;
                });
            }

            if (_settingsService.DecreaseTemperatureOffsetHotKey != HotKey.None)
            {
                _hotKeyService.RegisterHotKey(_settingsService.DecreaseTemperatureOffsetHotKey, () =>
                {
                    const double delta = -100;

                    // Avoid changing offset when it's already at its limit
                    if (TargetConfiguration.WithOffset(delta, 0) != TargetConfiguration)
                        TemperatureOffset += delta;
                });
            }

            if (_settingsService.IncreaseBrightnessOffsetHotKey != HotKey.None)
            {
                _hotKeyService.RegisterHotKey(_settingsService.IncreaseBrightnessOffsetHotKey, () =>
                {
                    const double delta = +0.05;

                    // Avoid changing offset when it's already at its limit
                    if (TargetConfiguration.WithOffset(0, delta) != TargetConfiguration)
                        BrightnessOffset += delta;
                });
            }

            if (_settingsService.DecreaseBrightnessOffsetHotKey != HotKey.None)
            {
                _hotKeyService.RegisterHotKey(_settingsService.DecreaseBrightnessOffsetHotKey, () =>
                {
                    const double delta = -0.05;

                    // Avoid changing offset when it's already at its limit
                    if (TargetConfiguration.WithOffset(0, delta) != TargetConfiguration)
                        BrightnessOffset += delta;
                });
            }

            if (_settingsService.ResetConfigurationOffsetHotKey != HotKey.None)
            {
                _hotKeyService.RegisterHotKey(_settingsService.ResetConfigurationOffsetHotKey, ResetConfigurationOffset);
            }
        }

        private void UpdateInstant()
        {
            // If in cycle preview mode - advance quickly until full cycle
            if (IsCyclePreviewEnabled)
            {
                // Cycle is supposed to end 1 full day past current real time
                var targetInstant = DateTimeOffset.Now + TimeSpan.FromDays(1);

                Instant = Instant.StepTo(targetInstant, TimeSpan.FromMinutes(5));
                if (Instant >= targetInstant)
                    IsCyclePreviewEnabled = false;
            }
            // Otherwise - synchronize instant with system clock
            else
            {
                Instant = DateTimeOffset.Now;
            }
        }

        private void UpdateConfiguration()
        {
            var isSmooth = _settingsService.IsConfigurationSmoothingEnabled && !IsCyclePreviewEnabled;

            CurrentConfiguration = isSmooth
                ? CurrentConfiguration.StepTo(TargetConfiguration, 30, 0.008)
                : TargetConfiguration;

            _gammaService.SetGamma(CurrentConfiguration);
        }

        private void UpdateIsPaused()
        {
            bool IsPausedByFullScreen() =>
                _settingsService.IsPauseWhenFullScreenEnabled && _externalApplicationService.IsForegroundApplicationFullScreen();

            bool IsPausedByWhitelistedApplication() =>
                _settingsService.IsApplicationWhitelistEnabled && _settingsService.WhitelistedApplications != null &&
                _settingsService.WhitelistedApplications.Contains(_externalApplicationService.TryGetForegroundApplication());

            IsPaused = IsPausedByFullScreen() || IsPausedByWhitelistedApplication();
        }

        public void Enable() => IsEnabled = true;

        public void Disable() => IsEnabled = false;

        public void DisableTemporarily(TimeSpan duration)
        {
            _enableAfterDelayRegistration?.Dispose();
            _enableAfterDelayRegistration = Timer.QueueDelayedAction(duration, Enable);
            IsEnabled = false;
        }

        public void DisableTemporarilyUntilSunrise()
        {
            // Use real time here instead of Instant, because that's what the user likely wants
            var timeUntilSunrise = SolarTimes.Sunrise.Next() - DateTimeOffset.Now;
            DisableTemporarily(timeUntilSunrise);
        }

        public void Toggle() => IsEnabled = !IsEnabled;

        public void EnableCyclePreview() => IsCyclePreviewEnabled = true;

        public void DisableCyclePreview() => IsCyclePreviewEnabled = false;

        public bool CanResetConfigurationOffset =>
            Math.Abs(TemperatureOffset) + Math.Abs(BrightnessOffset) >= 0.01;

        public void ResetConfigurationOffset()
        {
            TemperatureOffset = 0;
            BrightnessOffset = 0;
        }

        public void Dispose()
        {
            _updateInstantTimer.Dispose();
            _updateConfigurationTimer.Dispose();
            _updateIsPausedTimer.Dispose();

            _enableAfterDelayRegistration?.Dispose();
        }
    }
}