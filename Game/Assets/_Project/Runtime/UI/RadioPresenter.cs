using HeroGame.Core.Audio;
using HeroGame.Runtime.Bootstrap;
using UnityEngine;

namespace HeroGame.Runtime.UI
{
    /// <summary>
    /// Plays the tuned station (GDD §53): the core decides what is on air; this shows a ticker with the song or a
    /// subtitle of what is being said, and plays the track's clip from Resources/Radio/&lt;trackId&gt; when one exists.
    /// No music has been commissioned yet, so it is currently silent (docs/ASSET_TRACKER.md).
    /// </summary>
    public sealed class RadioPresenter : MonoBehaviour
    {
        private const string PrefKey = "radio.station";
        private static string _tuned;

        private AudioSource _source;
        private RadioSegment _current;
        private float _nextCheck;
        private GUIStyle _style;

        public static string Tuned
        {
            get
            {
                if (_tuned == null)
                {
                    try { _tuned = PlayerPrefs.GetString(PrefKey, ""); }
                    catch (System.Exception) { _tuned = ""; }
                }
                return _tuned;
            }
        }

        public static void Tune(string stationId)
        {
            _tuned = stationId ?? "";
            try { PlayerPrefs.SetString(PrefKey, _tuned); }
            catch (System.Exception) { /* preference only */ }
        }

        private void Awake()
        {
            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.spatialBlend = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextCheck) return;
            _nextCheck = Time.unscaledTime + 0.5f;
            if (Tuned == "" || !ServiceRegistry.TryGet<GameSession>(out var session))
            {
                Stop();
                return;
            }
            var seg = session.World.Radio.OnAir(Tuned, session.World.Clock.Now);
            if (seg == null)
            {
                Stop();
                return;
            }
            if (_current != null && _current.Station == seg.Station && _current.StartSecond == seg.StartSecond) return;
            _current = seg;
            _source.Stop();
            if (seg.Kind != SegmentKind.Track) return;
            var clip = Resources.Load<AudioClip>("Radio/" + seg.TrackId);
            if (clip == null) return;
            _source.clip = clip;
            // Join the song where the station is, so everyone tuned in hears the same moment.
            var offset = session.World.Radio.RadioSecond(session.World.Clock.Now) - seg.StartSecond;
            _source.time = Mathf.Clamp(offset, 0f, Mathf.Max(0f, clip.length - 0.1f));
            _source.Play();
        }

        private void Stop()
        {
            _current = null;
            if (_source != null && _source.isPlaying) _source.Stop();
        }

        private void OnGUI()
        {
            if (_current == null) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            var text = _current.Kind == SegmentKind.Track ? "♪ " + _current.Title + " — " + _current.Text : _current.Title + ": " + _current.Text;
            GUI.Label(new Rect(12, Screen.height - 64, Mathf.Min(720, Screen.width - 24), 56), text, _style);
        }
    }
}
