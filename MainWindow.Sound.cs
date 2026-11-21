using Microsoft.Web.WebView2.Core;

namespace Bro;

/// <summary>
/// Which tabs are playing sound, mute, and a volume level per tab.
/// Chromium only offers mute per tab, so the volume is applied inside the page: every audio/video
/// element and Web Audio context is scaled by the tab's level (the page's own volume still works on top).
/// </summary>
public partial class MainWindow
{
    void SetupSound(BrowserTab tab, CoreWebView2 core)
    {
        core.IsDocumentPlayingAudioChanged += (_, _) => { tab.Audible = core.IsDocumentPlayingAudio; QueueState(); };
        core.IsMutedChanged += (_, _) => { tab.Muted = core.IsMuted; QueueState(); };
        core.FrameCreated += (_, e) =>
        {
            var frame = e.Frame;
            tab.Frames.Add(frame);
            frame.Destroyed += (_, _) => tab.Frames.Remove(frame);
        };
    }

    void SetMuted(BrowserTab tab, bool muted)
    {
        if (tab.View.CoreWebView2 is { } core) core.IsMuted = muted;
        tab.Muted = muted;
        QueueState();
    }

    async void SetVolume(BrowserTab tab, double level)
    {
        level = Math.Clamp(Math.Round(level, 2), 0, 1);
        if (tab.View.CoreWebView2 is not { } core || Math.Abs(level - tab.Volume) < 0.001) return;
        tab.Volume = level;
        if (level > 0 && tab.Muted) SetMuted(tab, false);
        QueueState();
        try
        {
            // The page that's open now, and its frames (embedded players)
            var script = VolumeScript(level);
            _ = core.ExecuteScriptAsync(script);
            foreach (var frame in tab.Frames.ToList())
            {
                try { _ = frame.ExecuteScriptAsync(script); }
                catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
            }

            // Pages opened later in this tab
            if (tab.VolumeScriptId != null)
            {
                core.RemoveScriptToExecuteOnDocumentCreated(tab.VolumeScriptId);
                tab.VolumeScriptId = null;
            }
            if (level < 1)
            {
                var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
                if (Math.Abs(tab.Volume - level) < 0.001 && tab.VolumeScriptId == null) tab.VolumeScriptId = id;
                else core.RemoveScriptToExecuteOnDocumentCreated(id); // changed again meanwhile
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // Tab closed meanwhile.
        }
    }

    /// <summary>Installs the volume hook once per document (safe to run again) and sets the level.</summary>
    static string VolumeScript(double level) => $$"""
        (() => {
          if (!window.__broVolume) {
            const state = { level: 1 };
            const media = Object.getOwnPropertyDescriptor(HTMLMediaElement.prototype, 'volume');
            const wanted = new WeakMap(), seen = new WeakSet(), refs = [], gains = [];
            const actual = (el) => Math.max(0, Math.min(1, (wanted.has(el) ? wanted.get(el) : 1) * state.level));
            const track = (el) => {
              if (!(el instanceof HTMLMediaElement)) return;
              if (!seen.has(el)) {
                seen.add(el);
                refs.push(new WeakRef(el));
                if (!wanted.has(el)) wanted.set(el, media.get.call(el));
              }
              media.set.call(el, actual(el));
            };
            Object.defineProperty(HTMLMediaElement.prototype, 'volume', {
              configurable: true, enumerable: media.enumerable,
              get() { return wanted.has(this) ? wanted.get(this) : media.get.call(this); },
              set(v) {
                const n = Number(v);
                if (!(n >= 0 && n <= 1)) return media.set.call(this, v); // let the browser report the error
                wanted.set(this, n);
                track(this);
              },
            });
            const play = HTMLMediaElement.prototype.play;
            HTMLMediaElement.prototype.play = function () { track(this); return play.apply(this, arguments); };
            document.addEventListener('play', (e) => track(e.target), true);
            document.querySelectorAll('audio, video').forEach(track);

            // Web Audio: route each context through a gain node
            const BAC = window.BaseAudioContext;
            const dest = BAC && Object.getOwnPropertyDescriptor(BAC.prototype, 'destination');
            if (dest && dest.get) {
              const own = new WeakMap();
              Object.defineProperty(BAC.prototype, 'destination', {
                configurable: true, enumerable: dest.enumerable,
                get() {
                  const real = dest.get.call(this);
                  if (typeof OfflineAudioContext !== 'undefined' && this instanceof OfflineAudioContext) return real;
                  let g = own.get(this);
                  if (!g) {
                    g = this.createGain();
                    g.gain.value = state.level;
                    g.connect(real);
                    Object.defineProperty(g, 'maxChannelCount', { get: () => real.maxChannelCount });
                    own.set(this, g);
                    gains.push(new WeakRef(g));
                  }
                  return g;
                },
              });
            }

            Object.defineProperty(window, '__broVolume', {
              value: (v) => {
                state.level = v;
                for (const r of refs) { const el = r.deref(); if (el) media.set.call(el, actual(el)); }
                for (const r of gains) { const g = r.deref(); if (g) g.gain.value = v; }
              },
            });
          }
          window.__broVolume({{level.ToString(System.Globalization.CultureInfo.InvariantCulture)}});
        })();
        """;
}
