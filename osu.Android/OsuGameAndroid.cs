<<<<<<< SEARCH
            lowLatencyAudio.BindValueChanged(e =>
            {
                try
                {
                    if (e.NewValue)
                    {
                        audioRedirector?.RefreshMixers();

                        startOboeBridge(latency =>
                        {
                            // Only auto-suggest when the user hasn't already configured a manual offset.
                            if (Math.Abs(audioOffset.Value) >= 0.01)
                                return;

                            double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                            audioOffset.Value = suggested;
                            Debug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
                        }, audioRedirector?.Provider);
                    }
                    else if (nativeBridges != null)
                    {
                        stopOboeBridge();
                        audioRedirector?.Dispose();
                        audioRedirector = new OboeAudioRedirector(Audio);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to toggle Oboe bridge: {ex.Message}");
                }
            }, true);
=======
            lowLatencyAudio.BindValueChanged(e =>
            {
                try
                {
                    if (e.NewValue)
                    {
                        startOboeBridge(latency =>
                        {
                            // Only auto-suggest when the user hasn't already configured a manual offset.
                            if (Math.Abs(audioOffset.Value) >= 0.01)
                                return;

                            double suggested = Math.Clamp(-latency, audioOffset.MinValue, audioOffset.MaxValue);
                            audioOffset.Value = suggested;
                            Debug.WriteLine($"[osu!] Audio offset auto-suggested: {suggested:F1}ms (hardware latency={latency:F1}ms)");
                        }, audioRedirector?.Provider, sampleRate =>
                        {
                            // Initialise BASS mixers at the hardware sample rate to eliminate resampling latency.
                            audioRedirector?.RefreshMixers(sampleRate);
                        });
                    }
                    else if (nativeBridges != null)
                    {
                        stopOboeBridge();
                        audioRedirector?.Dispose();
                        audioRedirector = new OboeAudioRedirector(Audio);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[osu!] Failed to toggle Oboe bridge: {ex.Message}");
                }
            }, true);
>>>>>>> REPLACE
<<<<<<< SEARCH
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startOboeBridge(Action<double> onLatencyMeasured, OboeAudioBridge.OboeAudioProvider? provider = null)
        {
            nativeBridges ??= new AndroidNativeBridgeManager();

            if (nativeBridges is AndroidNativeBridgeManager mgr)
                mgr.StartOboeBridge(Scheduler, onLatencyMeasured, provider);
        }
=======
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void startOboeBridge(Action<double> onLatencyMeasured, OboeAudioBridge.OboeAudioProvider? provider = null, Action<int>? onStarted = null)
        {
            nativeBridges ??= new AndroidNativeBridgeManager();

            if (nativeBridges is AndroidNativeBridgeManager mgr)
                mgr.StartOboeBridge(Scheduler, onLatencyMeasured, provider, onStarted);
        }
>>>>>>> REPLACE
